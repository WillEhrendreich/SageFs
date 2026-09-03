namespace SageFs.Features

open System
open System.IO
open System.Text
open SageFs

/// Domain types for the .sagetc test-cache binary format.
module TestCacheTypes =

  /// A test outcome byte in the TRES section. Values 0-4 are the STC v1
  /// codes. Values 5-7 are the STC v2 failure-kind refinement: v1 collapsed
  /// every TestFailure kind into Fail (1); v2 gives AssertionFailed (5),
  /// ExceptionThrown (6) and TimedOut (7) distinct bytes so write→read
  /// round-trips preserve the failure kind across restarts. The reader treats
  /// byte 1 (Fail) as AssertionFailed, which is exactly what v1 writers meant.
  [<RequireQualifiedAccess>]
  type Outcome =
    | Pass = 0uy
    | Fail = 1uy
    | Skip = 2uy
    | Error = 3uy
    | NotRun = 4uy
    | AssertionFailed = 5uy
    | ExceptionThrown = 6uy
    | TimedOut = 7uy

  module Outcome =
    let tryParse (b: byte) : Result<Outcome, string> =
      match b <= 7uy with
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
    bw.Write(2us)                                     // format_version
    bw.Write(1us)                                     // min_reader_version — a v1 reader can
                                                      //   still parse v2 files (bytes 5-7 merely
                                                      //   refine what byte 1 already meant)
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
      let payloadLen = ms.Length
      // Each IMAP entry needs a tid lp-string (>=4 bytes) plus a u32 word
      // count — floor of 4 bytes per plausible entry is impossible; use a
      // generous physical cap: every entry must contain >= 5 bytes (4 prefix
      // + 1 word-count byte) so no more than payload/5 entries can exist.
      let rawCount = br.ReadUInt32() |> int64
      let maxPossible = max 0L (payloadLen - 4L) / 5L
      match rawCount > maxPossible with
      | true -> err (sprintf "IMAP parse error: count %d exceeds section payload capacity %d" rawCount maxPossible)
      | false ->
      let count = int rawCount
      readItems count [] (fun () ->
        let tid = BinaryPrimitives.readLpStringBounded br (max 0L (ms.Length - ms.Position))
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
      let payloadLen = ms.Length
      // Each TRES entry needs a tid lp-string (>=4 bytes) + outcome byte +
      // duration u32 + optional message — floor of 9 bytes per entry.
      let rawCount = br.ReadUInt32() |> int64
      let maxPossible = max 0L (payloadLen - 4L) / 9L
      match rawCount > maxPossible with
      | true -> err (sprintf "TRES parse error: count %d exceeds section payload capacity %d" rawCount maxPossible)
      | false ->
      let count = int rawCount
      readItems count [] (fun () ->
        let tid = BinaryPrimitives.readLpStringBounded br (max 0L (ms.Length - ms.Position))
        let rawOutcome = br.ReadByte()
        match Outcome.tryParse rawOutcome with
        | Error msg -> Error (sprintf "TRES parse error: %s" msg)
        | Ok outcome ->
        let dur = br.ReadUInt32()
        let msg = BinaryPrimitives.readLpStringOptionBounded br (max 0L (ms.Length - ms.Position))
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
        let rawSectionCount = BitConverter.ToUInt32(data, 8)
        let createdAtMs = BitConverter.ToInt64(data, 16)
        let imapGen = BitConverter.ToUInt32(data, 40)

        // Directory capacity: after the 64-byte header, each entry is 16 bytes
        // (tag:u32 + offset:u64 + crc:u32). A consistent hostile rewrite that
        // inflates section_count must fail cleanly here — never walk the loop
        // past the directory into the payload and misparse it as entries.
        let maxSections = uint32 ((data.Length - 64) / 16)
        match rawSectionCount > maxSections with
        | true -> err (sprintf "Header section count %d exceeds directory capacity %d" rawSectionCount maxSections)
        | false ->
        let sectionCount = int rawSectionCount
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

        // Declared total size is a section boundary for the LAST payload; it
        // must match the actual file length or section slicing walks nowhere.
        match totalSize <> fileLen with
        | true -> err (sprintf "Declared total size %d does not match actual file size %d" totalSize fileLen)
        | false ->

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

  // ── TimedOut message format helpers ─────────────────────────────
  // A TimedOut failure's TimeSpan lives on the wire only as a string (the
  // TRES entry has a single lp-string-option message). The message is written
  // in a parseable form so kind AND timeout duration round-trip losslessly.

  let private timedOutPrefix = "Timed out after "

  let private tryParseTimedOutMessage (msg: string) : TimeSpan option =
    match msg.StartsWith(timedOutPrefix, StringComparison.Ordinal) with
    | false -> None
    | true ->
      let rest = msg.Substring(timedOutPrefix.Length).Trim()
      let trimmed =
        match rest.EndsWith("s", StringComparison.Ordinal) with
        | true -> rest.Substring(0, rest.Length - 1).Trim()
        | false -> rest
      match Double.TryParse(trimmed, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
      | true, seconds when seconds >= 0.0 -> Some (TimeSpan.FromSeconds seconds)
      | _ -> None

  let private formatTimeSpan (ts: TimeSpan) : string =
    let seconds = Math.Round(ts.TotalSeconds, 3)
    sprintf "%s%ss" timedOutPrefix (seconds.ToString(Globalization.CultureInfo.InvariantCulture))

  /// A failure's kind alongside the message persisted for it. Rehydrating a
  /// TimedOut needs the duration ("Timed out after X") as the elapsed span —
  /// exact kind + duration both survive, which v1's single Fail byte erased.
  let private failureInfo (failure: TestFailure) (duration: TimeSpan) : Outcome * string =
    match failure with
    | TestFailure.AssertionFailed m -> Outcome.AssertionFailed, m
    | TestFailure.ExceptionThrown (m, _) -> Outcome.ExceptionThrown, m
    | TestFailure.TimedOut after ->
      // Persist the timeout span, rounded to whole seconds when it does not
      // already land on a whole millisecond. Live-testing timeouts are built
      // from whole seconds (Timeouts.perTestDefault), and duration_ms on the
      // wire is whole milliseconds anyway, so this keeps the round-trip exact
      // for every value a real executor can produce.
      let ts =
        match TimeSpan.FromMilliseconds(float (int64 after.TotalMilliseconds)) = after with
        | true -> after
        | false -> TimeSpan.FromSeconds after.TotalSeconds
      Outcome.TimedOut, formatTimeSpan ts

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
            let kind, msg = failureInfo failure duration
            kind, uint32 duration.TotalMilliseconds, Some msg
          | TestResult.Skipped reason ->
            Outcome.Skip, 0u, Some reason
          | TestResult.NotRun ->
            Outcome.NotRun, 0u, None
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

  /// Restore LiveTestState from deserialized StcData.
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
          // STC v1 byte — Fail was the only failure code and every v1 writer
          // produced AssertionFailed, so legacy files decode exactly as written.
          | Outcome.Fail ->
            let msg = e.Message |> Option.defaultValue "Unknown failure"
            TestResult.Failed (TestFailure.AssertionFailed msg, duration)
          | Outcome.AssertionFailed ->
            let msg = e.Message |> Option.defaultValue "Unknown failure"
            TestResult.Failed (TestFailure.AssertionFailed msg, duration)
          | Outcome.ExceptionThrown ->
            let msg = e.Message |> Option.defaultValue "Unknown exception"
            TestResult.Failed (TestFailure.ExceptionThrown (msg, ""), duration)
          | Outcome.TimedOut ->
            // v2 writers stored the original timeout span in the message
            // ("Timed out after <N>s") so the kind round-trips losslessly even
            // though the wire carries only one string.
            let after =
              e.Message
              |> Option.bind tryParseTimedOutMessage
              |> Option.defaultValue duration
            TestResult.Failed (TestFailure.TimedOut after, duration)
          | Outcome.Skip ->
            let reason = e.Message |> Option.defaultValue "Skipped"
            TestResult.Skipped reason
          | Outcome.NotRun -> TestResult.NotRun
          | Outcome.Error -> TestResult.NotRun  // legacy code 3; never written by current writers
          | _ -> TestResult.NotRun  // unknown enum value — treat as not run, never crash
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
