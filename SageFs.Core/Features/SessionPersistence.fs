namespace SageFs.Features

open System
open System.IO
open System.Text
open SageFs

/// Domain types for .sagefs v3 binary format (session persistence).
module SessionBinaryTypes =

  [<RequireQualifiedAccess>]
  type InteractionKind =
    | Interaction = 0us
    | Expression = 1us
    | Directive = 2us
    | ScriptLoad = 3us

  module InteractionKind =
    let tryParse (v: uint16) : Result<InteractionKind, string> =
      match v <= 3us with
      | true -> Ok (LanguagePrimitives.EnumOfValue<uint16, InteractionKind> v)
      | false -> Error (sprintf "Unknown InteractionKind value: %d" v)

  [<System.Flags>]
  type EntryFlags =
    | None = 0us
    | Failed = 1us
    | HasSideEffects = 2us
    | HasOutput = 4us

  [<RequireQualifiedAccess>]
  type RefKind =
    | DllPath = 0uy
    | NuGet = 1uy
    | IncludePath = 2uy
    | LoadedScript = 3uy

  module RefKind =
    let tryParse (b: byte) : Result<RefKind, string> =
      match b <= 3uy with
      | true -> Ok (LanguagePrimitives.EnumOfValue<byte, RefKind> b)
      | false -> Error (sprintf "Unknown RefKind value: %d" b)

  type Interaction = {
    Code: string
    Output: string
    TimestampMs: int64
    Kind: InteractionKind
    Flags: EntryFlags
    DurationMicros: uint32
  }

  type Reference = {
    Kind: RefKind
    Path: string
  }

  type SessionMeta = {
    SageFsVersion: string
    FSharpVersion: string
    DotNetVersion: string
    ProjectPath: string
    WorkingDirectory: string
    EvalCount: uint32
    FailedEvalCount: uint32
    SessionId: string
  }

  type SfsData = {
    Meta: SessionMeta
    Interactions: Interaction list
    References: Reference list
    CreatedAtMs: int64
  }

  module SessionMeta =
    let empty = {
      SageFsVersion = ""; FSharpVersion = ""; DotNetVersion = ""
      ProjectPath = ""; WorkingDirectory = ""
      EvalCount = 0u; FailedEvalCount = 0u; SessionId = ""
    }

  module SfsData =
    let empty = {
      Meta = SessionMeta.empty
      Interactions = []
      References = []
      CreatedAtMs = 0L
    }


/// Writer for .sagefs v3 binary format.
/// Uses string pool architecture for INPT section (deduplicates code/output strings).
module SessionBinaryWriter =
  open SessionBinaryTypes

  let private buildStringPool (strings: string list) : byte[] * Map<string, uint32> =
    use ms = new MemoryStream()
    let mutable offsets = Map.empty<string, uint32>
    for s in strings do
      match Map.tryFind s offsets with
      | Some _ -> ()
      | None ->
        let off = uint32 ms.Position
        let bytes = Encoding.UTF8.GetBytes(s)
        ms.Write(BitConverter.GetBytes(uint32 bytes.Length), 0, 4)
        ms.Write(bytes, 0, bytes.Length)
        offsets <- Map.add s off offsets
    (ms.ToArray(), offsets)

  let private writeMeta (meta: SessionMeta) : byte[] =
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    BinaryPrimitives.writeLpString bw meta.SageFsVersion
    BinaryPrimitives.writeLpString bw meta.FSharpVersion
    BinaryPrimitives.writeLpString bw meta.DotNetVersion
    BinaryPrimitives.writeLpString bw meta.ProjectPath
    BinaryPrimitives.writeLpString bw meta.WorkingDirectory
    BinaryPrimitives.writeLpString bw meta.SessionId
    bw.Write(meta.EvalCount)
    bw.Write(meta.FailedEvalCount)
    bw.Flush()
    ms.ToArray()

  let private writeInpt (interactions: Interaction list) : byte[] =
    let allStrings = interactions |> List.collect (fun i -> [i.Code; i.Output])
    let (poolBytes, poolMap) = buildStringPool allStrings
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    bw.Write(uint32 (List.length interactions))
    bw.Write(48us) // toc_entry_stride
    for ix in interactions do
      bw.Write(Map.find ix.Code poolMap)
      bw.Write(Map.find ix.Output poolMap)
      bw.Write(ix.TimestampMs)
      bw.Write(uint16 ix.Kind)
      bw.Write(uint16 ix.Flags)
      bw.Write(ix.DurationMicros)
      bw.Write(Array.zeroCreate<byte> 24) // reserved pad to stride=48
    bw.Write(uint32 poolBytes.Length)
    bw.Write(poolBytes)
    bw.Flush()
    ms.ToArray()

  let private writeRefs (refs: Reference list) : byte[] =
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    bw.Write(uint32 (List.length refs))
    for r in refs do
      bw.Write(byte r.Kind)
      BinaryPrimitives.writeLpString bw r.Path
    bw.Flush()
    ms.ToArray()

  let write (data: SfsData) : byte[] =
    let metaP = writeMeta data.Meta
    let inptP = writeInpt data.Interactions
    let refsP = writeRefs data.References

    let headerSize = 64
    let dirSize = 3 * 20
    let metaOff = uint64 (headerSize + dirSize)
    let inptOff = metaOff + uint64 metaP.Length
    let refsOff = inptOff + uint64 inptP.Length
    let totalSize = refsOff + uint64 refsP.Length

    let metaCrc = Crc32.computeAll metaP
    let inptCrc = Crc32.computeAll inptP
    let refsCrc = Crc32.computeAll refsP

    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)

    // Header (64 bytes) — matches spec §2.2
    bw.Write([| 0x53uy; 0x46uy; 0x53uy; 0x33uy |]) // "SFS3"
    bw.Write(3us)                                     // format_version
    bw.Write(3us)                                     // min_reader_version
    bw.Write(3u)                                      // section_count (u32)
    bw.Write(0u)                                      // flags
    bw.Write(data.CreatedAtMs)                        // created_at_ms
    bw.Write(totalSize)                               // total_file_size
    bw.Write(uint32 (List.length data.Interactions))  // interaction_count
    bw.Write(0u)                                      // header_crc32 placeholder
    bw.Write(0u)                                      // string_dedup_count
    bw.Write(0u)                                      // reserved_1
    bw.Write(0UL)                                     // reserved_2
    bw.Write(0UL)                                     // reserved_3

    // Directory: 3 × (tag:u16 + flags:u16 + offset:u64 + size:u32 + crc:u32)
    bw.Write(0x4D45us); bw.Write(0us); bw.Write(metaOff); bw.Write(uint32 metaP.Length); bw.Write(metaCrc)
    bw.Write(0x494Eus); bw.Write(0us); bw.Write(inptOff); bw.Write(uint32 inptP.Length); bw.Write(inptCrc)
    bw.Write(0x5245us); bw.Write(0us); bw.Write(refsOff); bw.Write(uint32 refsP.Length); bw.Write(refsCrc)

    // Payloads
    bw.Write(metaP)
    bw.Write(inptP)
    bw.Write(refsP)
    bw.Flush()

    // Patch header CRC — covers entire file (header + TOC + payloads)
    let result = ms.ToArray()
    let forCrc = Array.copy result
    forCrc.[36] <- 0uy; forCrc.[37] <- 0uy; forCrc.[38] <- 0uy; forCrc.[39] <- 0uy
    let hcrc = Crc32.computeAll forCrc
    let cb = BitConverter.GetBytes(hcrc)
    Array.Copy(cb, 0, result, 36, 4)
    result


/// Reader for .sagefs v3 binary format.
module SessionBinaryReader =
  open SessionBinaryTypes

  /// The format version this reader supports.
  let readerVersion = 3us

  let private ok v : Result<_, string> = Ok v
  let private err msg : Result<_, string> = FSharp.Core.Error msg

  type DirEntry = { Tag: uint16; Flags: uint16; Offset: uint64; Size: uint32; Crc: uint32 }

  let private findSection (tag: uint16) (entries: DirEntry list) =
    entries |> List.tryFind (fun e -> e.Tag = tag)

  let private readPoolString (pool: byte[]) (offset: uint32) : Result<string, string> =
    let off = int offset
    match off + 4 > pool.Length with
    | true -> Error (sprintf "String pool offset %d exceeds pool size %d" off pool.Length)
    | false ->
      let len = BitConverter.ToUInt32(pool, off) |> int
      match off + 4 + len > pool.Length with
      | true -> Error (sprintf "String pool entry at %d (len %d) exceeds pool size %d" off len pool.Length)
      | false -> Ok (Encoding.UTF8.GetString(pool, off + 4, len))

  let rec private readItems (remaining: int) (acc: 'T list) (readOne: unit -> Result<'T, string>) : Result<'T list, string> =
    match remaining with
    | 0 -> Ok (List.rev acc)
    | n ->
      match readOne () with
      | Error e -> Error e
      | Ok v -> readItems (n - 1) (v :: acc) readOne

  let private parseMeta (payload: byte[]) : Result<SessionMeta, string> =
    try
      use ms = new MemoryStream(payload)
      use br = new BinaryReader(ms)
      // All reads are bounded by the section payload size, never the enclosing stream.
      let budget () = max 0L (ms.Length - ms.Position)
      let m = {
        SageFsVersion = BinaryPrimitives.readLpStringBounded br (budget ())
        FSharpVersion = BinaryPrimitives.readLpStringBounded br (budget ())
        DotNetVersion = BinaryPrimitives.readLpStringBounded br (budget ())
        ProjectPath = BinaryPrimitives.readLpStringBounded br (budget ())
        WorkingDirectory = BinaryPrimitives.readLpStringBounded br (budget ())
        SessionId = BinaryPrimitives.readLpStringBounded br (budget ())
        EvalCount = br.ReadUInt32()
        FailedEvalCount = br.ReadUInt32()
      }
      ok m
    with ex -> err (sprintf "META parse error: %s" ex.Message)

  let private parseInpt (payload: byte[]) : Result<Interaction list, string> =
    try
      use ms = new MemoryStream(payload)
      use br = new BinaryReader(ms)
      // All bounds derive from the SECTION PAYLOAD (ms.Length), which is the
      // declared section size (e.Size). Header count/stride fields must stay
      // inside the payload — a consistent rewrite that inflates them must fail
      // cleanly instead of walking out of the section or exhausting memory.
      let payloadLen = ms.Length
      if payloadLen < 8L then
        err "INPT parse error: payload too short for header (count + stride)"
      else
      let count = br.ReadUInt32() |> int64
      let stride = br.ReadUInt16() |> int
      let tocStart = ms.Position
      let tocBytes = payloadLen - tocStart
      match stride = 0 && count > 0L with
      | true -> err "INPT parse error: zero toc_entry_stride with nonzero entry count"
      | false ->
      match count > 0L && int64 stride > tocBytes with
      | true -> err (sprintf "INPT parse error: toc_entry_stride %d exceeds payload %d" stride tocBytes)
      | false ->
      // The whole TOC (count fixed-size entries at stride) plus the u32 pool
      // size must fit inside the section payload. Checked in int64 to avoid
      // any int overflow on hostile count/stride combinations.
      match count > 0L && count * int64 stride + 4L > tocBytes with
      | true -> err (sprintf "INPT parse error: count %d * stride %d exceeds section payload %d" count stride tocBytes)
      | false ->
      let entries =
        match count with
        | 0L -> []
        | n -> List.init (int n) (fun i ->
          let off = int64 i * int64 stride
          ms.Position <- tocStart + off
          (br.ReadUInt32(), br.ReadUInt32(), br.ReadInt64(), br.ReadUInt16(), br.ReadUInt16(), br.ReadUInt32()))
      ms.Position <- tocStart + count * int64 stride
      let poolSize = br.ReadUInt32() |> int64
      match poolSize < 0L || poolSize > payloadLen - ms.Position with
      | true -> err (sprintf "INPT parse error: string pool size %d exceeds remaining payload %d" poolSize (payloadLen - ms.Position))
      | false ->
      let pool = br.ReadBytes(int poolSize)
      let rec build (remaining: (uint32 * uint32 * int64 * uint16 * uint16 * uint32) list) acc =
        match remaining with
        | [] -> Ok (List.rev acc)
        | (codeOff, outputOff, tsMs, kind, flags, durMicros) :: rest ->
          match InteractionKind.tryParse kind with
          | Error msg -> Error (sprintf "INPT parse error: %s" msg)
          | Ok validKind ->
          match readPoolString pool codeOff with
          | Error msg -> Error (sprintf "INPT parse error: %s" msg)
          | Ok code ->
          match readPoolString pool outputOff with
          | Error msg -> Error (sprintf "INPT parse error: %s" msg)
          | Ok output ->
          build rest ({
            Code = code
            Output = output
            TimestampMs = tsMs
            Kind = validKind
            Flags = LanguagePrimitives.EnumOfValue<uint16, EntryFlags> flags
            DurationMicros = durMicros
          } :: acc)
      build entries []
    with ex -> err (sprintf "INPT parse error: %s" ex.Message)

  let private parseRefs (payload: byte[]) : Result<Reference list, string> =
    try
      use ms = new MemoryStream(payload)
      use br = new BinaryReader(ms)
      // Cap declared count by the section payload: a ref entry needs at least
      // one kind byte plus a u32 length prefix, so no more than payload/5 entries
      // can physically exist. Anything more is a consistent-but-hostile header.
      let payloadLen = ms.Length
      let rawCount = br.ReadUInt32() |> int64
      let maxPossible = max 0L (payloadLen - 4L) / 5L
      let count =
        match rawCount > maxPossible with
        | true -> err (sprintf "REFS parse error: count %d exceeds section payload capacity %d" rawCount maxPossible)
        | false -> ok (int rawCount)
      match count with
      | Error e -> Error e
      | Ok n ->
      readItems n [] (fun () ->
        let rawKind = br.ReadByte()
        match RefKind.tryParse rawKind with
        | Error msg -> Error (sprintf "REFS parse error: %s" msg)
        | Ok validKind ->
          Ok {
            Kind = validKind
            Path = BinaryPrimitives.readLpStringBounded br (ms.Length - ms.Position)
          })
    with ex -> err (sprintf "REFS parse error: %s" ex.Message)

  let read (data: byte[]) : Result<SfsData, string> =
    if data.Length < 64 then err "File too short for SFS3 header"
    elif data.[0] <> 0x53uy || data.[1] <> 0x46uy || data.[2] <> 0x53uy || data.[3] <> 0x33uy then
      err "Invalid magic: expected SFS3"
    else
      let storedCrc = BitConverter.ToUInt32(data, 36)
      let forCrc = Array.copy data
      forCrc.[36] <- 0uy; forCrc.[37] <- 0uy; forCrc.[38] <- 0uy; forCrc.[39] <- 0uy
      let computed = Crc32.computeAll forCrc
      if storedCrc <> computed then
        Instrumentation.persistenceCrcErrors.Add(
          1L, System.Collections.Generic.KeyValuePair("format", box "sfs3"))
        err (sprintf "Header CRC mismatch: stored=%08X computed=%08X" storedCrc computed)
      else
        let minVersion = BitConverter.ToUInt16(data, 6)
        if minVersion > readerVersion then
          err (sprintf "File requires reader version %d but this reader is version %d" minVersion readerVersion)
        else
        let sectionCount = BitConverter.ToUInt32(data, 8)
        let createdAtMs = BitConverter.ToInt64(data, 16)
        let fileLen = uint64 data.Length
        // Section directory is 20 bytes per entry; cap section_count by the
        // bytes that actually exist after the 64-byte header so a consistent
        // rewrite with a huge count fails cleanly (no int overflow, no OOM).
        let maxSections = uint32 ((data.Length - 64) / 20)
        let dirEntries =
          match sectionCount > maxSections with
          | true -> err (sprintf "Header section count %d exceeds directory capacity %d" sectionCount maxSections)
          | false ->
          let count = int sectionCount
          let entries = [
            for i in 0 .. count - 1 do
              let o = 64 + i * 20
              yield {
                Tag = BitConverter.ToUInt16(data, o)
                Flags = BitConverter.ToUInt16(data, o + 2)
                Offset = BitConverter.ToUInt64(data, o + 4)
                Size = BitConverter.ToUInt32(data, o + 12)
                Crc = BitConverter.ToUInt32(data, o + 16)
              } ]
          ok entries
        match dirEntries with
        | Error e -> err e
        | Ok dirEntries ->
        // Bounds check: all section offset+size must be within the file.
        // Arithmetic on u64 is checked against fileLen; entry offset+size is
        // re-checked with overflow-safe comparisons before slicing.
        let oob = dirEntries |> List.tryFind (fun e ->
          e.Offset >= fileLen
          || uint64 e.Size > fileLen
          || e.Size > 0u && e.Offset > fileLen - uint64 e.Size)
        match oob with
        | Some e -> err (sprintf "Section offset %d + size %d exceeds file length %d" e.Offset e.Size fileLen)
        | None ->
        let crcOk = dirEntries |> List.forall (fun e ->
          let eOff = int e.Offset
          let eEnd = int e.Offset + int e.Size
          let p = data.[eOff .. eEnd - 1]
          Crc32.computeAll p = e.Crc)
        if not crcOk then
          Instrumentation.persistenceCrcErrors.Add(
            1L, System.Collections.Generic.KeyValuePair("format", box "sfs3"))
          err "Section CRC mismatch"
        else
          let getP tag =
            match findSection tag dirEntries with
            | Some e ->
              let eOff = int e.Offset
              let eSize = int e.Size
              ok data.[eOff .. eOff + eSize - 1]
            | None -> err (sprintf "Missing section 0x%04X" tag)
          match getP 0x4D45us, getP 0x494Eus, getP 0x5245us with
          | Result.Ok mp, Result.Ok ip, Result.Ok rp ->
            match parseMeta mp, parseInpt ip, parseRefs rp with
            | Result.Ok m, Result.Ok ints, Result.Ok refs ->
              ok { Meta = m; Interactions = ints; References = refs; CreatedAtMs = createdAtMs }
            | Result.Error e, _, _ | _, Result.Error e, _ | _, _, Result.Error e -> err e
          | Result.Error e, _, _ | _, Result.Error e, _ | _, _, Result.Error e -> err e


/// Maps between SessionReplayState and SfsData for binary persistence.
module SessionMapping =
  open SessionBinaryTypes
  open SageFs.Features.Replay

  /// Convert SessionReplayState to binary-serializable SfsData.
  let fromReplayState
    (sessionId: string)
    (projectPath: string)
    (workDir: string)
    (refs: string list)
    (state: SessionReplayState) : SfsData =

    let ver =
      match System.Reflection.Assembly.GetEntryAssembly() with
      | null -> "0.0.0"
      | a -> string (a.GetName().Version)

    let meta : SessionMeta = {
      SageFsVersion = ver
      FSharpVersion = ""
      DotNetVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
      ProjectPath = projectPath
      WorkingDirectory = workDir
      EvalCount = uint32 state.EvalCount
      FailedEvalCount = uint32 state.FailedEvalCount
      SessionId = sessionId
    }

    let interactions =
      state.EvalHistory
      |> List.map (fun e ->
        let kind =
          match e.Code.TrimStart() with
          | c when c.StartsWith("#r ") || c.StartsWith("#load ") -> InteractionKind.Directive
          | c when c.StartsWith("#") -> InteractionKind.ScriptLoad
          | _ -> InteractionKind.Expression
        let flags =
          let mutable f = EntryFlags.None
          match e.Result.StartsWith("error") || e.Result.StartsWith("Error") with
          | true -> f <- f ||| EntryFlags.Failed
          | false -> ()
          match e.Result.Length > 0 with
          | true -> f <- f ||| EntryFlags.HasOutput
          | false -> ()
          f
        { Code = e.Code
          Output = e.Result
          TimestampMs = e.Timestamp.ToUnixTimeMilliseconds()
          Kind = kind
          Flags = flags
          DurationMicros = uint32 (e.Duration.TotalMicroseconds) })

    let references =
      refs
      |> List.map (fun r ->
        let kind =
          match r with
          | r when r.EndsWith(".dll") -> RefKind.DllPath
          | r when r.Contains("nuget:") -> RefKind.NuGet
          | r when r.EndsWith(".fsx") -> RefKind.LoadedScript
          | _ -> RefKind.IncludePath
        { Kind = kind; Path = r })

    { Meta = meta
      Interactions = interactions
      References = references
      CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }

  /// Restore SessionReplayState from deserialized SfsData (partial).
  let toReplayState (data: SfsData) : SessionReplayState =
    let evalHistory =
      data.Interactions
      |> List.map (fun i ->
        { Code = i.Code
          Result = i.Output
          TypeSignature = None
          Duration = TimeSpan.FromMicroseconds(float i.DurationMicros)
          Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(i.TimestampMs) })

    let status =
      match data.Interactions with
      | [] -> ReplayStatus.NotStarted
      | _ -> ReplayStatus.Ready

    { SessionReplayState.empty with
        Status = status
        EvalCount = int data.Meta.EvalCount
        FailedEvalCount = int data.Meta.FailedEvalCount
        EvalHistory = evalHistory
        StartedAt = Some (DateTimeOffset.FromUnixTimeMilliseconds(data.CreatedAtMs))
        LastActivity =
          data.Interactions
          |> List.tryLast
          |> Option.map (fun i -> DateTimeOffset.FromUnixTimeMilliseconds(i.TimestampMs)) }


/// File I/O for .sagefs binary session files.
module SessionFile =
  open SessionBinaryTypes

  /// Get the sessions directory, creating it if needed.
  let sessionDir (sageFsDir: string) =
    let dir = IO.Path.Combine(sageFsDir, "sessions")
    IO.Directory.CreateDirectory(dir) |> ignore
    dir

  let private sessionPath sageFsDir (sessionId: string) =
    IO.Path.Combine(sessionDir sageFsDir, sprintf "%s.sagefs" sessionId)

  /// Save SfsData to a .sagefs file with atomic write.
  let save (sageFsDir: string) (sessionId: string) (data: SfsData) : Result<string, string> =
    try
      let path = sessionPath sageFsDir sessionId
      let tmpPath = path + ".tmp"
      let bytes = SessionBinaryWriter.write data
      IO.File.WriteAllBytes(tmpPath, bytes)
      IO.File.Move(tmpPath, path, overwrite = true)
      Ok path
    with ex ->
      Error (sprintf "Failed to save session: %s" ex.Message)

  /// Load SfsData from a .sagefs file.
  let load (sageFsDir: string) (sessionId: string) : Result<SfsData, string> =
    let path = sessionPath sageFsDir sessionId
    match IO.File.Exists(path) with
    | false -> Error "No session file found"
    | true ->
      try
        let bytes = IO.File.ReadAllBytes(path)
        SessionBinaryReader.read bytes
      with ex ->
        Error (sprintf "Failed to read session: %s" ex.Message)

  /// Delete the .sagefs replay file for a session id.
  /// Missing file is Ok (idempotent) — mirrors "delete obj/bin" semantics.
  let delete (sageFsDir: string) (sessionId: string) : Result<unit, string> =
    let path = sessionPath sageFsDir sessionId
    match IO.File.Exists(path) with
    | false -> Ok ()
    | true ->
      try
        IO.File.Delete(path)
        Ok ()
      with ex ->
        Error (sprintf "Failed to delete session file: %s" ex.Message)

  /// List all saved session IDs.
  let listSaved (sageFsDir: string) : string list =
    let dir = sessionDir sageFsDir
    match IO.Directory.Exists(dir) with
    | false -> []
    | true ->
      IO.Directory.GetFiles(dir, "*.sagefs")
      |> Array.map IO.Path.GetFileNameWithoutExtension
      |> Array.toList

  /// Remove orphaned .sagefs.tmp files left by interrupted writes.
  let cleanupOrphanedTmpFiles (sageFsDir: string) : int =
    let dir = IO.Path.Combine(sageFsDir, "sessions")
    match IO.Directory.Exists(dir) with
    | false -> 0
    | true ->
      let tmpFiles = IO.Directory.GetFiles(dir, "*.sagefs.tmp")
      tmpFiles |> Array.iter (fun f -> try IO.File.Delete(f) with _ -> ())
      tmpFiles.Length
