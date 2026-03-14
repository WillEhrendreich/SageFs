namespace SageFs.Features

open System
open System.IO
open System.Text
open SageFs

/// Domain types for .sagetc v1 binary format (test cache).
module TestCacheTypes =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Pass = 0uy
    | Fail = 1uy
    | Skip = 2uy
    | Error = 3uy

  module Outcome =
    let tryParse (b: byte) : Result<Outcome, string> =
      match b <= 3uy with
      | true -> Ok (LanguagePrimitives.EnumOfValue<byte, Outcome> b)
      | false -> Error (sprintf "Unknown Outcome value: %d" b)

  type CoverageEntry = {
    TestId: string
    BitmapWordCount: uint32
    BitmapWords: uint64[]
  }

  type ResultEntry = {
    TestId: string
    Outcome: Outcome
    DurationMs: uint32
    Message: string option
  }

  type StcData = {
    CoverageEntries: CoverageEntry list
    ResultEntries: ResultEntry list
    ImapGeneration: uint32
    CreatedAtMs: int64
  }

  module StcData =
    let empty = {
      CoverageEntries = []
      ResultEntries = []
      ImapGeneration = 0u
      CreatedAtMs = 0L
    }


/// Writer for .sagetc v1 binary format.
module TestCacheWriter =
  open TestCacheTypes

  let private writeImap (entries: CoverageEntry list) : byte[] =
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    bw.Write(uint32 (List.length entries))
    for e in entries do
      BinaryPrimitives.writeLpString bw e.TestId
      bw.Write(e.BitmapWordCount)
      for w in e.BitmapWords do
        bw.Write(w)
    bw.Flush()
    ms.ToArray()

  let private writeTcov (entries: CoverageEntry list) : byte[] =
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    bw.Write(uint32 (List.length entries))
    for e in entries do
      BinaryPrimitives.writeLpString bw e.TestId
      bw.Write(e.BitmapWordCount * 64u)
    bw.Flush()
    ms.ToArray()

  let private writeTres (entries: ResultEntry list) : byte[] =
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    bw.Write(uint32 (List.length entries))
    for e in entries do
      BinaryPrimitives.writeLpString bw e.TestId
      bw.Write(byte e.Outcome)
      bw.Write(e.DurationMs)
      BinaryPrimitives.writeLpStringOption bw e.Message
    bw.Flush()
    ms.ToArray()

  let write (data: StcData) : byte[] =
    let imapPayload = writeImap data.CoverageEntries
    let tcovPayload = writeTcov data.CoverageEntries
    let tresPayload = writeTres data.ResultEntries

    let sectionCount = 3u
    let headerSize = 64
    let dirEntrySize = 16
    let dirSize = int sectionCount * dirEntrySize

    let imapOffset = uint64 (headerSize + dirSize)
    let tcovOffset = imapOffset + uint64 imapPayload.Length
    let tresOffset = tcovOffset + uint64 tcovPayload.Length
    let totalSize = tresOffset + uint64 tresPayload.Length

    let imapCrc = Crc32.computeAll imapPayload
    let tcovCrc = Crc32.computeAll tcovPayload
    let tresCrc = Crc32.computeAll tresPayload

    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)

    // Header (64 bytes) — matches spec §3.2
    bw.Write([| 0x53uy; 0x54uy; 0x43uy; 0x31uy |]) // "STC1"
    bw.Write(1us)                                     // format_version
    bw.Write(1us)                                     // min_reader_version
    bw.Write(sectionCount)                            // section_count (u32)
    bw.Write(0u)                                      // flags
    bw.Write(data.CreatedAtMs)                        // created_at_ms
    bw.Write(totalSize)                               // total_file_size
    bw.Write(uint32 (List.length data.ResultEntries)) // test_count
    bw.Write(0u)                                      // header_crc placeholder @36
    bw.Write(data.ImapGeneration)                     // imap_generation
    bw.Write(Array.zeroCreate<byte> 20)               // reserved to 64

    // Directory (3 × 16 bytes: tag:u32 + offset:u64 + crc:u32)
    bw.Write(0x494D4150u); bw.Write(imapOffset); bw.Write(imapCrc) // IMAP
    bw.Write(0x54434F56u); bw.Write(tcovOffset); bw.Write(tcovCrc) // TCOV
    bw.Write(0x54524553u); bw.Write(tresOffset); bw.Write(tresCrc) // TRES

    // Payloads
    bw.Write(imapPayload)
    bw.Write(tcovPayload)
    bw.Write(tresPayload)
    bw.Flush()

    // Patch header CRC — covers entire file (header + TOC + payloads)
    let result = ms.ToArray()
    let forCrc = Array.copy result
    forCrc.[36] <- 0uy; forCrc.[37] <- 0uy; forCrc.[38] <- 0uy; forCrc.[39] <- 0uy
    let hcrc = Crc32.computeAll forCrc
    let cb = BitConverter.GetBytes(hcrc)
    Array.Copy(cb, 0, result, 36, 4)
    result


/// Reader for .sagetc v1 binary format.
module TestCacheReader =
  open TestCacheTypes

  /// The format version this reader supports.
  let readerVersion = 1us

  let private ok v : Result<_, string> = Ok v
  let private err msg : Result<_, string> = FSharp.Core.Error msg

  type DirEntry = { Tag: uint32; Offset: uint64; Crc: uint32 }

  let private findSection (tag: uint32) (entries: DirEntry list) =
    entries |> List.tryFind (fun e -> e.Tag = tag)

  let rec private readItems (remaining: int) (acc: 'T list) (readOne: unit -> Result<'T, string>) : Result<'T list, string> =
    match remaining with
    | 0 -> Ok (List.rev acc)
    | n ->
      match readOne () with
      | Error e -> Error e
      | Ok v -> readItems (n - 1) (v :: acc) readOne

  let private parseImap (payload: byte[]) : Result<CoverageEntry list, string> =
    try
      use ms = new MemoryStream(payload)
      use br = new BinaryReader(ms)
      let count = br.ReadUInt32() |> int
      readItems count [] (fun () ->
        let tid = BinaryPrimitives.readLpString br
        let wc = br.ReadUInt32()
        let remaining = ms.Length - ms.Position
        match int64 wc * 8L > remaining with
        | true ->
          Error (sprintf "IMAP parse error: Bitmap word count %d requires %d bytes but only %d remain" wc (wc * 8u) remaining)
        | false ->
        let words =
          match wc with
          | 0u -> [||]
          | n -> [| for _ in 1u .. n -> br.ReadUInt64() |]
        Ok { TestId = tid; BitmapWordCount = wc; BitmapWords = words })
    with ex -> err (sprintf "IMAP parse error: %s" ex.Message)

  let private parseTres (payload: byte[]) : Result<ResultEntry list, string> =
    try
      use ms = new MemoryStream(payload)
      use br = new BinaryReader(ms)
      let count = br.ReadUInt32() |> int
      readItems count [] (fun () ->
        let tid = BinaryPrimitives.readLpString br
        let rawOutcome = br.ReadByte()
        match Outcome.tryParse rawOutcome with
        | Error msg -> Error (sprintf "TRES parse error: %s" msg)
        | Ok outcome ->
        let dur = br.ReadUInt32()
        let msg = BinaryPrimitives.readLpStringOption br
        Ok { TestId = tid; Outcome = outcome; DurationMs = dur; Message = msg })
    with ex -> err (sprintf "TRES parse error: %s" ex.Message)

  let read (data: byte[]) : Result<StcData, string> =
    if data.Length < 64 then err "File too short for STC1 header"
    elif data.[0] <> 0x53uy || data.[1] <> 0x54uy || data.[2] <> 0x43uy || data.[3] <> 0x31uy then
      err "Invalid magic: expected STC1"
    else
      let storedCrc = BitConverter.ToUInt32(data, 36)
      let forCrc = Array.copy data
      forCrc.[36] <- 0uy; forCrc.[37] <- 0uy; forCrc.[38] <- 0uy; forCrc.[39] <- 0uy
      let computed = Crc32.computeAll forCrc
      if storedCrc <> computed then
        Instrumentation.persistenceCrcErrors.Add(
          1L, System.Collections.Generic.KeyValuePair("format", box "stc1"))
        err (sprintf "Header CRC mismatch: stored=%08X computed=%08X" storedCrc computed)
      else
        let minVersion = BitConverter.ToUInt16(data, 6)
        if minVersion > readerVersion then
          err (sprintf "File requires reader version %d but this reader is version %d" minVersion readerVersion)
        else
        let sectionCount = BitConverter.ToUInt32(data, 8) |> int
        let createdAtMs = BitConverter.ToInt64(data, 16)
        let imapGen = BitConverter.ToUInt32(data, 40)

        let dirEntries = [
          for i in 0 .. sectionCount - 1 do
            let o = 64 + i * 16
            yield {
              Tag = BitConverter.ToUInt32(data, o)
              Offset = BitConverter.ToUInt64(data, o + 4)
              Crc = BitConverter.ToUInt32(data, o + 12)
            } ]

        // Compute section sizes from offset gaps
        let sorted = dirEntries |> List.sortBy (fun e -> e.Offset)
        let totalSize = BitConverter.ToUInt64(data, 24)
        let fileLen = uint64 data.Length

        // Bounds check: all offsets must be within the file
        let oob = sorted |> List.tryFind (fun e -> e.Offset >= fileLen)
        match oob with
        | Some e -> err (sprintf "Section offset %d exceeds file length %d" e.Offset fileLen)
        | None ->

        let sectionPayloads =
          sorted |> List.mapi (fun i e ->
            let nextOff =
              match List.tryItem (i + 1) sorted with
              | Some next -> next.Offset
              | None -> totalSize
            let endPos = int e.Offset + int (nextOff - e.Offset)
            match endPos > data.Length with
            | true -> (e, [||], false)
            | false ->
            let size = int (nextOff - e.Offset)
            let payload = data.[int e.Offset .. int e.Offset + size - 1]
            let computed = Crc32.computeAll payload
            (e, payload, computed = e.Crc))

        let crcOk = sectionPayloads |> List.forall (fun (_, _, ok) -> ok)
        if not crcOk then
          Instrumentation.persistenceCrcErrors.Add(
            1L, System.Collections.Generic.KeyValuePair("format", box "stc1"))
          err "Section CRC mismatch"
        else
          let payloadMap = sectionPayloads |> List.map (fun (e, p, _) -> (e.Tag, p)) |> Map.ofList
          match Map.tryFind 0x494D4150u payloadMap, Map.tryFind 0x54524553u payloadMap with
          | Some imapP, Some tresP ->
            match parseImap imapP with
            | Result.Error e -> err e
            | Result.Ok coverage ->
            match parseTres tresP with
            | Result.Error e -> err e
            | Result.Ok results ->
            ok {
              CoverageEntries = coverage
              ResultEntries = results
              ImapGeneration = imapGen
              CreatedAtMs = createdAtMs
            }
          | _ -> err "Missing required IMAP or TRES section"


/// Maps between LiveTestState and StcData for binary persistence.
module TestCacheMapping =
  open TestCacheTypes
  open SageFs.Features.LiveTesting

  /// Convert LiveTestState coverage/results to binary-serializable StcData.
  let fromLiveTestState (state: LiveTestState) : StcData =
    let coverageEntries =
      state.TestCoverageBitmaps
      |> Map.toList
      |> List.map (fun (TestId.TestId tid, bm) ->
        { TestId = tid
          BitmapWordCount = uint32 bm.Bits.Length
          BitmapWords = bm.Bits })

    let resultEntries =
      state.LastResults
      |> Map.toList
      |> List.map (fun (TestId.TestId tid, runResult) ->
        let outcome, durationMs, message =
          match runResult.Result with
          | TestResult.Passed duration ->
            Outcome.Pass, uint32 duration.TotalMilliseconds, None
          | TestResult.Failed (failure, duration) ->
            let msg =
              match failure with
              | TestFailure.AssertionFailed m -> m
              | TestFailure.ExceptionThrown (m, _) -> m
              | TestFailure.TimedOut ts -> sprintf "Timed out after %A" ts
            Outcome.Fail, uint32 duration.TotalMilliseconds, Some msg
          | TestResult.Skipped reason ->
            Outcome.Skip, 0u, Some reason
          | TestResult.NotRun ->
            Outcome.Skip, 0u, None
        { TestId = tid
          Outcome = outcome
          DurationMs = durationMs
          Message = message })

    let generation =
      match state.LastGeneration with
      | RunGeneration g -> uint32 g

    { ImapGeneration = generation
      CoverageEntries = coverageEntries
      ResultEntries = resultEntries
      CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }

  /// Restore LiveTestState from deserialized StcData (lossy reverse mapping).
  let toLiveTestState (data: StcData) : LiveTestState =
    let coverageBitmaps =
      data.CoverageEntries
      |> List.map (fun e ->
        let tid = TestId.TestId e.TestId
        let bm : CoverageBitmap = {
          Bits = e.BitmapWords
          Count = int e.BitmapWordCount * 64
        }
        tid, bm)
      |> Map.ofList

    let lastResults =
      data.ResultEntries
      |> List.map (fun e ->
        let tid = TestId.TestId e.TestId
        let duration = TimeSpan.FromMilliseconds(float e.DurationMs)
        let result =
          match e.Outcome with
          | Outcome.Pass -> TestResult.Passed duration
          | Outcome.Fail ->
            let msg = e.Message |> Option.defaultValue "Unknown failure"
            TestResult.Failed (TestFailure.AssertionFailed msg, duration)
          | Outcome.Skip ->
            let reason = e.Message |> Option.defaultValue "Skipped"
            TestResult.Skipped reason
          | _ -> TestResult.NotRun
        let runResult : TestRunResult = {
          TestId = tid
          TestName = e.TestId
          Result = result
          Timestamp = DateTimeOffset.UtcNow
          Output = None
        }
        tid, runResult)
      |> Map.ofList

    let gen = RunGeneration (int data.ImapGeneration)
    { LiveTestState.empty with
        TestCoverageBitmaps = coverageBitmaps
        LastResults = lastResults
        LastGeneration = gen }


/// File I/O for .sagetc binary cache files.
module TestCacheFile =
  open TestCacheTypes

  /// Get the cache directory, creating it if needed.
  let cacheDir (sageFsDir: string) =
    let dir = IO.Path.Combine(sageFsDir, "cache")
    IO.Directory.CreateDirectory(dir) |> ignore
    dir

  let private cachePath sageFsDir (projectHash: string) =
    IO.Path.Combine(cacheDir sageFsDir, sprintf "%s.sagetc" projectHash)

  /// Save StcData to a .sagetc file with atomic write.
  let save (sageFsDir: string) (projectHash: string) (data: StcData) : Result<string, string> =
    try
      let path = cachePath sageFsDir projectHash
      let tmpPath = path + ".tmp"
      let bytes = TestCacheWriter.write data
      IO.File.WriteAllBytes(tmpPath, bytes)
      IO.File.Move(tmpPath, path, overwrite = true)
      Ok path
    with ex ->
      Error (sprintf "Failed to save test cache: %s" ex.Message)

  /// Load StcData from a .sagetc file.
  let load (sageFsDir: string) (projectHash: string) : Result<StcData, string> =
    let path = cachePath sageFsDir projectHash
    match IO.File.Exists(path) with
    | false -> Error "No cache file found"
    | true ->
      try
        let bytes = IO.File.ReadAllBytes(path)
        TestCacheReader.read bytes
      with ex ->
        Error (sprintf "Failed to read test cache: %s" ex.Message)

  /// Remove orphaned .sagetc.tmp files left by interrupted writes.
  let cleanupOrphanedTmpFiles (sageFsDir: string) : int =
    let dir = IO.Path.Combine(sageFsDir, "cache")
    match IO.Directory.Exists(dir) with
    | false -> 0
    | true ->
      let tmpFiles = IO.Directory.GetFiles(dir, "*.sagetc.tmp")
      tmpFiles |> Array.iter (fun f -> try IO.File.Delete(f) with _ -> ())
      tmpFiles.Length
