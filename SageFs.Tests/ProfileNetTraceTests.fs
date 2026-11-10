module SageFs.Tests.ProfileNetTraceTests

open System
open System.IO
open System.Text
open Expecto
open Expecto.Flip
open SageFs.Features

/// The nettrace reader, against traces built here in the format the runtime writes (compressed event headers, metadata blocks,
/// stack blocks), and against a real recording by hand: the allocation numbers of the plan's outcome gate were checked on
/// 2026-11-10 against a trace of a real FSI host that allocated 64,000,000 bytes of one record type, which the reader reported as
/// 64,008,288 bytes of that type at the function that allocated it. The builder is the other half of the contract: a reader that
/// agrees with a builder written from the same notes proves nothing alone, which is why the outcome gate records the real host.

/// Builds `.nettrace` bytes. Every field an event repeats from the one before it is left out of its header, as the runtime does,
/// so the reader's rule for a field that is not there is under test too.
module Fixture =
  let utf16z (text: string) : byte[] = Array.append (Encoding.Unicode.GetBytes text) [| 0uy; 0uy |]
  let i32 (value: int) : byte[] = BitConverter.GetBytes value
  let u16 (value: int) : byte[] = BitConverter.GetBytes(uint16 value)
  let u32 (value: uint32) : byte[] = BitConverter.GetBytes value
  let u64 (value: uint64) : byte[] = BitConverter.GetBytes value
  let join (parts: byte[] list) : byte[] = Array.concat parts

  let rec varint (value: uint64) : byte[] =
    match value < 0x80UL with
    | true -> [| byte value |]
    | false -> Array.append [| byte (value &&& 0x7FUL) ||| 0x80uy |] (varint (value >>> 7))

  /// One event: the metadata that describes it, the stack it was taken on, its payload.
  type Event = { MetadataId: int; StackId: int; Payload: byte[] }

  /// An event block whose headers leave out what repeats.
  let eventBlock (events: Event list) : byte[] =
    let header = join [ u16 20; u16 1; u64 0UL; u64 0UL ]
    let body =
      events
      |> List.fold
        (fun (previous: Event option, bytes: byte[] list) e ->
          let sameMetadata = previous |> Option.exists (fun p -> p.MetadataId = e.MetadataId)
          let sameStack = previous |> Option.exists (fun p -> p.StackId = e.StackId)
          let sameSize = previous |> Option.exists (fun p -> p.Payload.Length = e.Payload.Length)
          let flags =
            (match sameMetadata with | true -> 0 | false -> 1)
            ||| (match sameStack with | true -> 0 | false -> 8)
            ||| (match sameSize with | true -> 0 | false -> 128)
          let encoded =
            join
              [ [| byte flags |]
                (match sameMetadata with | true -> [||] | false -> varint (uint64 e.MetadataId))
                (match sameStack with | true -> [||] | false -> varint (uint64 e.StackId))
                varint 7UL // the time since the event before
                (match sameSize with | true -> [||] | false -> varint (uint64 e.Payload.Length))
                e.Payload ]
          Some e, encoded :: bytes)
        (None, [])
      |> snd
      |> List.rev
    join (header :: body)

  /// A metadata block: which provider and event id each metadata id means.
  let metadataBlock (entries: (int * string * int) list) : byte[] =
    let metadataEvent (id, provider, eventId) =
      join [ i32 id; utf16z provider; i32 eventId; utf16z ""; u64 0UL; i32 0; i32 5; i32 0 ]
    let header = join [ u16 20; u16 1; u64 0UL; u64 0UL ]
    let body =
      entries |> List.map (fun entry -> let payload = metadataEvent entry in join [ [| 128uy |]; varint 0UL; varint (uint64 payload.Length); payload ])
    join (header :: body)

  let stackBlock (first: int) (stacks: uint64 list list) : byte[] =
    let one (ips: uint64 list) = join (i32 (ips.Length * 8) :: (ips |> List.map u64))
    join (i32 first :: i32 stacks.Length :: (stacks |> List.map one))

  /// The whole file: the header, the trace object, then each block as a typed object, padded to four bytes from the start.
  let file (blocks: (string * byte[]) list) : byte[] =
    use stream = new MemoryStream()
    let write (bytes: byte[]) = stream.Write(bytes, 0, bytes.Length)
    write (Encoding.ASCII.GetBytes "Nettrace")
    write (i32 20)
    write (Encoding.ASCII.GetBytes "!FastSerialization.1")
    let typed (name: string) =
      write [| 5uy; 5uy; 1uy |]
      write (join [ i32 4; i32 4; i32 name.Length ])
      write (Encoding.ASCII.GetBytes name)
      write [| 6uy |]
    typed "Trace"
    // Eight 16-bit time fields, two clock values, the pointer size (8), a process id, a processor count and a sampling rate.
    write (join [ Array.zeroCreate 16; u64 0UL; u64 0UL; i32 8; i32 1; i32 1; i32 1000 ])
    write [| 6uy |]
    for kind, content in blocks do
      typed kind
      write (i32 content.Length)
      while stream.Position % 4L <> 0L do write [| 0uy |]
      write content
      write [| 6uy |]
    write [| 1uy |]
    stream.ToArray()

  let methodPayload (moduleId: uint64) (start: uint64) (size: int) (ns: string, name: string) : byte[] =
    join [ u64 1UL; u64 moduleId; u64 start; u32 (uint32 size); u32 0u; u32 0u; utf16z ns; utf16z name; utf16z ""; u16 1 ]

  let modulePayload (moduleId: uint64) (path: string) : byte[] =
    join [ u64 moduleId; u64 0UL; u32 0u; u32 0u; utf16z path; utf16z ""; u16 1 ]

  let tickPayload (typeName: string) (bytes: int) : byte[] =
    join [ u32 (uint32 bytes); u32 0u; u16 1; u64 (uint64 bytes); u64 0UL; utf16z typeName; u32 0u; u64 0UL; u64 0UL ]

  let metadata : (int * string * int) list =
    [ 1, "Microsoft-Windows-DotNETRuntime", 10 // allocation tick
      2, "Microsoft-Windows-DotNETRuntime", 143 // method load
      3, "Microsoft-Windows-DotNETRuntime", 152 // module load
      4, "Microsoft-Windows-DotNETRuntimeRundown", 144 // method, from the rundown
      5, "Microsoft-Windows-DotNETRuntimeRundown", 154 ] // module, from the rundown

  let eval = 200UL
  let project = 100UL
  let corelib = 300UL

  /// Three methods (one typed into the session, one in a project, one in the runtime), three modules, three stacks and the ticks.
  let traceWith (ticks: (string * int * int) list) : byte[] =
    let events =
      [ { MetadataId = 3; StackId = 0; Payload = modulePayload eval "FSI-ASSEMBLY" }
        { MetadataId = 5; StackId = 0; Payload = modulePayload project "/work/MyProject/bin/MyProject.dll" }
        { MetadataId = 5; StackId = 0; Payload = modulePayload corelib "/dotnet/System.Private.CoreLib.dll" }
        { MetadataId = 2; StackId = 0; Payload = methodPayload eval 0x1000UL 0x100 ("FSI_0001", "makeThings") }
        { MetadataId = 4; StackId = 0; Payload = methodPayload corelib 0x2000UL 0x80 ("System.String", "Concat") }
        { MetadataId = 4; StackId = 0; Payload = methodPayload project 0x3000UL 0x40 ("MyProject.Lib", "build") } ]
      @ (ticks |> List.map (fun (typeName, bytes, stackId) -> { MetadataId = 1; StackId = stackId; Payload = tickPayload typeName bytes }))
    file
      [ "MetadataBlock", metadataBlock metadata
        // Stack 1: Concat called from makeThings. Stack 2: Concat called from the project. Stack 3: an address no method holds.
        "StackBlock", stackBlock 1 [ [ 0x2010UL; 0x1010UL ]; [ 0x2020UL; 0x3010UL ]; [ 0x9999UL ] ]
        "EventBlock", eventBlock events ]

let private usual : byte[] =
  Fixture.traceWith [ "System.String", 100000, 1; "System.String", 100000, 2; "Foo.Bar", 50000, 3 ]

let private rowFor (label: string) (rows: Row list) : Row option = rows |> List.tryFind (fun r -> r.Label = label)

[<Tests>]
let tests =
  testList "Profile nettrace reader" [

    testCase "WHY - allocation ticks are summed by type, with the bytes each tick stands for" <| fun _ ->
      match NetTrace.allocReport Set.empty 10 usual with
      | Result.Error failure -> failtestf "should read: %s" (ProfileFailure.describe failure)
      | Result.Ok report ->
        report.TotalBytes |> Expect.equal "all the sampled bytes" 250000L
        (rowFor "System.String" report.ByType |> Option.map (fun r -> r.Weight)) |> Expect.equal "two ticks of strings" (Some 200000.0)
        (rowFor "Foo.Bar" report.ByType |> Option.map (fun r -> r.Weight)) |> Expect.equal "one tick of the other type" (Some 50000.0)

    testCase "WHY - a tick is placed at the nearest frame of the user's code, typed into the session or in a project" <| fun _ ->
      match NetTrace.allocReport (Set.ofList [ "MyProject" ]) 10 usual with
      | Result.Error failure -> failtestf "should read: %s" (ProfileFailure.describe failure)
      | Result.Ok report ->
        let labels = report.BySite |> List.map (fun r -> r.Label)
        labels |> Expect.contains "an eval-defined function owns the first stack" "System.String at FSI-ASSEMBLY!FSI_0001.makeThings"
        labels |> Expect.contains "a project function owns the second" "System.String at MyProject!MyProject.Lib.build"
        labels |> Expect.contains "a stack nothing resolves says so" "Foo.Bar (no frame of yours)"

    testCase "WHY - a project the session did not load is the runtime's, not the user's" <| fun _ ->
      match NetTrace.allocReport Set.empty 10 usual with
      | Result.Error failure -> failtestf "should read: %s" (ProfileFailure.describe failure)
      | Result.Ok report ->
        (report.BySite |> List.map (fun r -> r.Label)) |> Expect.contains "second stack has no frame of the user's" "System.String at FSI-ASSEMBLY!FSI_0001.makeThings"
        (report.BySite |> List.exists (fun r -> r.Label.Contains "MyProject")) |> Expect.isFalse "MyProject is not named as the user's"

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
      "WHY - for any ticks the type rows plus the other row weigh what the ticks carried"
      <| fun (seed: int) (topSeed: byte) ->
        let random = Random seed
        let ticks = [ for _ in 1 .. random.Next(1, 30) -> sprintf "Type%d" (random.Next 6), random.Next(1, 200000), random.Next(1, 4) ]
        match NetTrace.allocReport Set.empty (int topSeed % 8) (Fixture.traceWith ticks) with
        | Result.Error failure -> failtestf "should read: %s" (ProfileFailure.describe failure)
        | Result.Ok report ->
          let carried = ticks |> List.sumBy (fun (_, bytes, _) -> int64 bytes)
          report.TotalBytes |> Expect.equal "the total is the sum of the ticks" carried
          (report.ByType |> List.sumBy (fun r -> r.Weight)) |> Expect.equal "the rows weigh the total" (float carried)

    testCase "WHY - a trace with no allocation tick is NoSamples, never an empty table" <| fun _ ->
      NetTrace.allocReport Set.empty 10 (Fixture.traceWith []) |> Expect.equal "refused by name" (Result.Error ProfileFailure.NoSamples)

    testCase "WHY - an address is inside a method only up to its end" <| fun _ ->
      let methods : NetTrace.Method[] =
        [| { Start = 0x1000UL; End = 0x1100UL; Name = "a"; ModuleId = 1UL }; { Start = 0x1100UL; End = 0x1180UL; Name = "b"; ModuleId = 1UL } |]
      (NetTrace.methodAt methods 0x10FFUL |> Option.map (fun m -> m.Name)) |> Expect.equal "last byte of a" (Some "a")
      (NetTrace.methodAt methods 0x1100UL |> Option.map (fun m -> m.Name)) |> Expect.equal "first byte of b" (Some "b")
      NetTrace.methodAt methods 0x1180UL |> Expect.isNone "one past b is no method"
      NetTrace.methodAt methods 0x0FFFUL |> Expect.isNone "before the first method is no method"

    testCase "WHY - a varint round trips" <| fun _ ->
      for value in [ 0UL; 1UL; 127UL; 128UL; 300UL; 16384UL; 0xFFFFFFFFUL; UInt64.MaxValue ] do
        NetTrace.varintAt (Fixture.varint value) 0 |> Expect.equal "value and length" (value, (Fixture.varint value).Length)

    testList "named failures" [
      testCase "WHY - text that is not a nettrace file is TraceUnreadable" <| fun _ ->
        match NetTrace.parse (Encoding.ASCII.GetBytes(String.replicate 50 "not a trace ")) with
        | Result.Error(ProfileFailure.TraceUnreadable detail) -> detail |> Expect.stringContains "says what is wrong" "nettrace"
        | other -> failtestf "got %A" other

      testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
        "WHY - a trace cut off anywhere is a named failure or a smaller report, and never an exception"
        <| fun (cut: uint16) ->
          let length = int cut % usual.Length
          match NetTrace.parse (Array.sub usual 0 length) with
          | Result.Ok _
          | Result.Error(ProfileFailure.TraceUnreadable _) -> ()
          | Result.Error other -> failtestf "cut at %d gave %A" length other

      testCase "WHY - events with uncompressed headers are refused by name" <| fun _ ->
        let bytes = Array.copy usual
        // The first metadata block's content starts after the headers; its second 16-bit field is the flags.
        let blocks, _ = NetTrace.blocksOf usual
        let metadata = blocks |> List.find (fun b -> b.Kind = "MetadataBlock")
        bytes.[metadata.Start + 2] <- 0uy
        match NetTrace.parse bytes with
        | Result.Error(ProfileFailure.TraceUnreadable detail) -> detail |> Expect.stringContains "names the headers" "uncompressed"
        | other -> failtestf "got %A" other
    ]
  ]
