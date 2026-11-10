namespace SageFs.Features

open System
open System.IO

/// Reads the allocation events of a `.nettrace` file, the format `dotnet-trace collect --format NetTrace` writes, into allocation
/// samples: the type each sampled allocation was of, the bytes it stands for, and the stack of the thread that made it, resolved to
/// method names from the method events the trace carries. Nothing here touches a process or a disk.
///
/// The file is a list of blocks. A metadata block says which provider and event id each metadata id means. A stack block holds
/// stacks as lists of code addresses, by id. An event block holds events whose headers are compressed (varints, with a field left
/// out when it equals the one before it) followed by their payloads. The runtime samples allocations about once for every
/// 100 KB (event 10 of `Microsoft-Windows-DotNETRuntime`, with the GC keyword at verbose level), so each tick stands for that many
/// bytes, which is the figure its payload carries. Method names come from the method load events (143 at run time, 144 of the
/// rundown the tool asks for when it stops); module names from the module load events (152, and 154 of the rundown).
module NetTrace =
  /// The largest trace a report is built from, in bytes.
  let maxBytes : int64 = 128L * 1024L * 1024L

  let runtimeProvider = "Microsoft-Windows-DotNETRuntime"
  let rundownProvider = "Microsoft-Windows-DotNETRuntimeRundown"

  [<Literal>]
  let AllocationTickEvent = 10
  [<Literal>]
  let MethodLoadVerboseEvent = 143
  [<Literal>]
  let MethodRundownVerboseEvent = 144
  [<Literal>]
  let ModuleLoadEvent = 152
  [<Literal>]
  let ModuleRundownEvent = 154

  let i32 (b: byte[]) (o: int) : int = BitConverter.ToInt32(b, o)
  let u16 (b: byte[]) (o: int) : int = int (BitConverter.ToUInt16(b, o))
  let u32 (b: byte[]) (o: int) : uint32 = BitConverter.ToUInt32(b, o)
  let u64 (b: byte[]) (o: int) : uint64 = BitConverter.ToUInt64(b, o)

  /// A variable-length integer: seven bits at a time, low bits first, the high bit saying there is more. The value and the
  /// position after it.
  let varintAt (b: byte[]) (pos: int) : uint64 * int =
    let rec go (at: int) (shift: int) (acc: uint64) : uint64 * int =
      let x = b.[at]
      let next = acc ||| (uint64 (x &&& 0x7Fuy) <<< shift)
      match x &&& 0x80uy with
      | 0uy -> next, at + 1
      | _ -> go (at + 1) (shift + 7) next
    go pos 0 0UL

  /// A null-terminated UTF-16 string and the position after its terminator.
  let utf16z (b: byte[]) (pos: int) : string * int =
    let rec findEnd (at: int) : int =
      match b.[at] = 0uy && b.[at + 1] = 0uy with
      | true -> at
      | false -> findEnd (at + 2)
    let stop = findEnd pos
    Text.Encoding.Unicode.GetString(b, pos, stop - pos), stop + 2

  type Block = { Kind: string; Start: int; Size: int }

  /// The serialization header, then objects: each is a typed object holding a block (a size, padding to a four-byte boundary
  /// from the start of the file, then the content) or the trace header. A null reference ends the list.
  let blocksOf (b: byte[]) : Block list * int =
    let afterHeader = 8 + 4 + i32 b 8
    let rec objects (pos: int) (pointerSize: int) (acc: Block list) : Block list * int =
      match b.[pos] with
      | 1uy -> List.rev acc, pointerSize
      | _ ->
        // BeginPrivateObject, BeginPrivateObject, NullReference, version, minimum reader version, name length, name, EndObject.
        let nameLength = i32 b (pos + 1 + 1 + 1 + 4 + 4)
        let nameStart = pos + 1 + 1 + 1 + 4 + 4 + 4
        let kind = Text.Encoding.UTF8.GetString(b, nameStart, nameLength)
        let payload = nameStart + nameLength + 1
        match kind with
        | "Trace" ->
          // Eight 16-bit time fields, two 64-bit clock values, then the pointer size, process id, processor count and sampling rate.
          let size = i32 b (payload + 32)
          objects (payload + 48 + 1) size acc
        | _ ->
          let size = i32 b payload
          let content = (payload + 4 + 3) &&& ~~~3
          objects (content + size + 1) pointerSize ({ Kind = kind; Start = content; Size = size } :: acc)
    objects afterHeader 8 []

  /// One event of a block: which metadata describes it, the stack it was taken on, and where its payload is.
  type RawEvent = { MetadataId: int; StackId: int; PayloadOffset: int; PayloadSize: int }

  type Decoder = { Pos: int; MetadataId: int; StackId: int; PayloadSize: int }

  /// Decode the next event header. A field a header leaves out keeps the value of the event before it.
  let nextEvent (b: byte[]) (d: Decoder) : RawEvent * Decoder =
    let flags = int b.[d.Pos]
    let p0 = d.Pos + 1
    let metadataId, p1 =
      match flags &&& 1 with
      | 0 -> d.MetadataId, p0
      | _ -> let v, n = varintAt b p0 in int v, n
    // Sequence number, capture thread, processor number: three varints we do not use.
    let p2 =
      match flags &&& 2 with
      | 0 -> p1
      | _ -> p1 |> (fun p -> snd (varintAt b p)) |> (fun p -> snd (varintAt b p)) |> (fun p -> snd (varintAt b p))
    let p3 =
      match flags &&& 4 with
      | 0 -> p2
      | _ -> snd (varintAt b p2)
    let stackId, p4 =
      match flags &&& 8 with
      | 0 -> d.StackId, p3
      | _ -> let v, n = varintAt b p3 in int v, n
    let p5 = snd (varintAt b p4) // the time since the event before
    let p6 = (match flags &&& 16 with | 0 -> p5 | _ -> p5 + 16)
    let p7 = (match flags &&& 32 with | 0 -> p6 | _ -> p6 + 16)
    let payloadSize, p8 =
      match flags &&& 128 with
      | 0 -> d.PayloadSize, p7
      | _ -> let v, n = varintAt b p7 in int v, n
    // A size read from a damaged file may be absurd. Clamped, every event still moves the position forward, so decoding ends.
    let size = max 0 payloadSize
    { MetadataId = metadataId; StackId = stackId; PayloadOffset = p8; PayloadSize = size },
    { Pos = p8 + size; MetadataId = metadataId; StackId = stackId; PayloadSize = size }

  /// Whether every block of events has compressed headers, which is what the runtime has written since .NET 5 and the only form read.
  let hasCompressedHeaders (b: byte[]) (blocks: Block list) : bool =
    blocks
    |> List.filter (fun block -> block.Kind = "EventBlock" || block.Kind = "MetadataBlock")
    |> List.forall (fun block -> (u16 b (block.Start + 2)) &&& 1 = 1)

  /// The events of one event or metadata block, whose headers are compressed (`hasCompressedHeaders`).
  let eventsOfBlock (b: byte[]) (block: Block) : RawEvent list =
    let headerSize = u16 b block.Start
    let stop = block.Start + block.Size
    { Pos = block.Start + headerSize; MetadataId = 0; StackId = 0; PayloadSize = 0 }
    |> List.unfold (fun d ->
      match d.Pos >= stop with
      | true -> None
      | false -> Some(nextEvent b d))

  /// Which provider and event id a metadata id means.
  let metadataOf (b: byte[]) (blocks: Block list) : Map<int, string * int> =
    blocks
    |> List.filter (fun block -> block.Kind = "MetadataBlock")
    |> List.collect (eventsOfBlock b)
    |> List.map (fun e ->
      let provider, afterProvider = utf16z b (e.PayloadOffset + 4)
      i32 b e.PayloadOffset, (provider, i32 b afterProvider))
    |> Map.ofList

  /// Stacks by id, each a list of code addresses with the innermost first.
  let stacksOf (b: byte[]) (pointerSize: int) (blocks: Block list) : Map<int, uint64 list> =
    let addressAt (o: int) : uint64 =
      match pointerSize with
      | 4 -> uint64 (u32 b o)
      | _ -> u64 b o
    blocks
    |> List.filter (fun block -> block.Kind = "StackBlock")
    |> List.collect (fun block ->
      let first = i32 b block.Start
      let count = i32 b (block.Start + 4)
      (block.Start + 8, 0)
      |> List.unfold (fun (pos, index) ->
        match index >= count with
        | true -> None
        | false ->
          let size = i32 b pos
          let addresses = [ for k in 0 .. size / pointerSize - 1 -> addressAt (pos + 4 + k * pointerSize) ]
          Some((first + index, addresses), (pos + 4 + size, index + 1))))
    |> Map.ofList

  /// A method the trace knows by code address.
  type Method = { Start: uint64; End: uint64; Name: string; ModuleId: uint64 }

  let isEvent (metadata: Map<int, string * int>) (provider: string) (eventIds: int list) (e: RawEvent) : bool =
    match Map.tryFind e.MetadataId metadata with
    | Some(p, id) -> p = provider && List.contains id eventIds
    | None -> false

  /// The methods of the trace, sorted by address: those loaded while it ran and those the rundown listed when it stopped.
  let methodsOf (b: byte[]) (metadata: Map<int, string * int>) (events: RawEvent list) : Method[] =
    let isMethod (e: RawEvent) =
      isEvent metadata runtimeProvider [ MethodLoadVerboseEvent ] e
      || isEvent metadata rundownProvider [ MethodRundownVerboseEvent; MethodLoadVerboseEvent ] e
    events
    |> List.filter isMethod
    |> List.map (fun e ->
      let o = e.PayloadOffset
      let ns, afterNamespace = utf16z b (o + 36)
      let name, _ = utf16z b afterNamespace
      { Start = u64 b (o + 16)
        End = u64 b (o + 16) + uint64 (u32 b (o + 24))
        Name = (match ns with | "" -> name | _ -> ns + "." + name)
        ModuleId = u64 b (o + 8) })
    |> List.filter (fun m -> m.End > m.Start)
    |> List.distinct
    |> List.sortBy (fun m -> m.Start)
    |> Array.ofList

  /// Module names by module id: the file name of the module's path, without its extension.
  let modulesOf (b: byte[]) (metadata: Map<int, string * int>) (events: RawEvent list) : Map<uint64, string> =
    events
    |> List.filter (fun e ->
      isEvent metadata runtimeProvider [ ModuleLoadEvent ] e || isEvent metadata rundownProvider [ ModuleRundownEvent ] e)
    |> List.map (fun e ->
      let path, _ = utf16z b (e.PayloadOffset + 24)
      u64 b e.PayloadOffset, Path.GetFileNameWithoutExtension path)
    |> Map.ofList

  /// The method that holds a code address: the last one that starts at or before it, if the address falls inside it.
  let methodAt (methods: Method[]) (address: uint64) : Method option =
    let rec search (lo: int) (hi: int) (best: int) : int =
      match lo > hi with
      | true -> best
      | false ->
        let mid = (lo + hi) / 2
        match methods.[mid].Start <= address with
        | true -> search (mid + 1) hi mid
        | false -> search lo (mid - 1) best
    match search 0 (methods.Length - 1) -1 with
    | -1 -> None
    | i -> (match address < methods.[i].End with | true -> Some methods.[i] | false -> None)

  /// The allocation ticks of a trace as samples.
  let samplesOf (b: byte[]) (blocks: Block list) (pointerSize: int) : AllocSample list =
    let metadata = metadataOf b blocks
    let events = blocks |> List.filter (fun block -> block.Kind = "EventBlock") |> List.collect (eventsOfBlock b)
    let stacks = stacksOf b pointerSize blocks
    let methods = methodsOf b metadata events
    let modules = modulesOf b metadata events
    let frameOf (m: Method) : Frame =
      { Module = (match Map.tryFind m.ModuleId modules with | Some name -> name | None -> ""); Method = m.Name }
    let sampleOf (e: RawEvent) : AllocSample =
      let o = e.PayloadOffset
      // Amount (4), kind (4), instance (2), amount as 64 bits (8), type id (8), then the type name.
      let typeName, _ = utf16z b (o + 26)
      let addresses = Map.tryFind e.StackId stacks |> Option.defaultValue []
      { TypeName = typeName
        Bytes = int64 (u64 b (o + 10))
        Frames = addresses |> List.choose (methodAt methods) |> List.map frameOf }
    events |> List.filter (fun e -> isEvent metadata runtimeProvider [ AllocationTickEvent ] e && e.PayloadSize >= 28) |> List.map sampleOf

  let isNetTrace (b: byte[]) : bool =
    b.Length > 64 && Text.Encoding.ASCII.GetString(b, 0, 8) = "Nettrace"

  /// The blocks and the samples, or what is wrong with the file.
  let read (b: byte[]) : Result<AllocSample list, ProfileFailure> =
    let blocks, pointerSize = blocksOf b
    match hasCompressedHeaders b blocks with
    | true -> Result.Ok(samplesOf b blocks pointerSize)
    | false -> Result.Error(ProfileFailure.TraceUnreadable "its events have uncompressed headers, which this reader does not read")

  /// Read the allocation samples of a trace. A file that is not a nettrace, or is cut off, is a named failure.
  let parse (bytes: byte[]) : Result<AllocSample list, ProfileFailure> =
    match int64 bytes.Length > maxBytes, isNetTrace bytes with
    | true, _ -> Result.Error(ProfileFailure.TraceTooLarge(int64 bytes.Length, maxBytes))
    | false, false -> Result.Error(ProfileFailure.TraceUnreadable "the file does not start like a nettrace file")
    | false, true ->
      match SageFs.Guarded.attempt (fun () -> read bytes) with
      | SageFs.Guarded.Attempt.Completed outcome -> outcome
      | SageFs.Guarded.Attempt.Threw ex ->
        Result.Error(ProfileFailure.TraceUnreadable(sprintf "the trace could not be read as a nettrace file (%s)" ex.Message))

  /// The allocation report of a trace, or why there is none. A trace with no allocation sample is `NoSamples`.
  let allocReport (projectModules: Set<string>) (top: int) (bytes: byte[]) : Result<AllocReport, ProfileFailure> =
    parse bytes
    |> Result.bind (fun samples ->
      match List.isEmpty samples with
      | true -> Result.Error ProfileFailure.NoSamples
      | false -> Result.Ok(ProfileReport.alloc projectModules top samples))
