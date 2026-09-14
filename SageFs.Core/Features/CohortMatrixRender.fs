namespace SageFs.Features

open System
open System.IO
open System.IO.Compression
open System.Text

/// Phase 2 item 16 of sagefs-multiagent-vision.md (§6.5 "Matrix view — a
/// picture, with a text fallback"; the data shape is §5.7's `CohortFrame`).
/// Pure rendering of the cohort test matrix — the three bitplanes
/// `Cohort.CohortFrame<'m>.Pass` / `.Fail` / `.Stale` (`Cohort.fs:973-975`),
/// each `bool[][]` index-aligned to `Cohort.CohortFrame<'m>.TestIds`
/// (`Cohort.fs:972`): outer index = row (one per `SessionSnapshot` folded by
/// `Cohort.project`, `Cohort.fs:983-1029` — the integration session is
/// whichever snapshot has `Member = None`, §5.3), inner index = column,
/// aligned 1:1 with `TestIds`.
///
/// No IO, no daemon, no session/member resolution: callers pass the frame's
/// own `Pass`/`Fail`/`Stale` arrays and get back PNG bytes or a character
/// grid. This module has zero dependency on `Cohort.CohortFrame<'m>` itself
/// (no `'m` type parameter to thread through) — it only ever reads the three
/// bitplane arrays, so it composes with any caller's member type.
module CohortMatrixRender =

  /// One cell's settled outcome, derived from the three bitplanes rather than
  /// carried as its own array. `NotRun` covers every status the bitplanes
  /// don't represent — `Detected`/`Queued`/`Running`/`Skipped`/
  /// `PolicyDisabled` per `CohortTestProjection.SessionTestProjection.
  /// ofStatusEntries` — and a test this row's session has simply never seen.
  [<RequireQualifiedAccess>]
  type CellOutcome =
    | NotRun
    | Pass
    | Fail
    | Stale

  /// A cell is Pass/Fail/Stale only if its plane says so; a cell set in more
  /// than one plane (which `Cohort.project` never produces, since a test's
  /// `TestRunStatus` is a single DU case) resolves Pass > Fail > Stale, the
  /// same "most informative wins" order `TestSummary.fromStatuses` uses.
  let outcomeAt (pass: bool[][]) (fail: bool[][]) (stale: bool[][]) (row: int) (col: int) : CellOutcome =
    if pass.[row].[col] then CellOutcome.Pass
    elif fail.[row].[col] then CellOutcome.Fail
    elif stale.[row].[col] then CellOutcome.Stale
    else CellOutcome.NotRun

  /// Bounds-safe outcome lookup: a row shorter than `col`, or a row index
  /// past `pass.Length`, reads as `NotRun` rather than throwing. Real frames
  /// never produce ragged rows (`Cohort.project`'s `bitmapFor` maps every
  /// snapshot over the same `testIds` array), but a renderer must not crash
  /// on a malformed one — it should draw a blank cell instead.
  let private outcomeAtSafe (pass: bool[][]) (fail: bool[][]) (stale: bool[][]) (row: int) (col: int) : CellOutcome =
    match row < pass.Length && col < pass.[row].Length with
    | true -> outcomeAt pass fail stale row col
    | false -> CellOutcome.NotRun

  let glyphFor (outcome: CellOutcome) : char =
    match outcome with
    | CellOutcome.NotRun -> '.'
    | CellOutcome.Pass -> '✓' // ✓
    | CellOutcome.Fail -> '✗' // ✗
    | CellOutcome.Stale -> '~'

  /// Accessible-fallback legend, appended to every character grid.
  let legend = "Legend: . not run   ✓ pass   ✗ fail   ~ stale"

  /// The character-grid fallback (§6.5): one row per session (the frame's
  /// own row order — integration plus members), one glyph per test column,
  /// newline-separated, with a trailing legend line. Pure, total: an empty
  /// matrix renders as just the legend.
  let toCharGrid (pass: bool[][]) (fail: bool[][]) (stale: bool[][]) : string =
    let sb = StringBuilder()
    for row in 0 .. pass.Length - 1 do
      let colCount = pass.[row].Length
      for col in 0 .. colCount - 1 do
        sb.Append(glyphFor (outcomeAt pass fail stale row col)) |> ignore
      sb.Append('\n') |> ignore
    sb.Append(legend) |> ignore
    sb.ToString()

  // ── PNG encoding — no dependency (§6.5: "a PNG encoder ... is ~60 lines
  //    and no dependency") ─────────────────────────────────────────────────

  let private pngSignature : byte[] = [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]

  let private crc32Table : uint32[] =
    Array.init 256 (fun n ->
      let mutable c = uint32 n
      for _ in 0 .. 7 do
        c <- (if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1)
      c)

  let private crc32 (bytes: byte[]) : uint32 =
    let mutable c = 0xFFFFFFFFu
    for b in bytes do
      c <- crc32Table.[int ((c ^^^ uint32 b) &&& 0xFFu)] ^^^ (c >>> 8)
    c ^^^ 0xFFFFFFFFu

  let private beBytes (v: uint32) : byte[] =
    [| byte (v >>> 24); byte (v >>> 16); byte (v >>> 8); byte v |]

  let private writeChunk (stream: MemoryStream) (chunkType: string) (data: byte[]) =
    stream.Write(beBytes (uint32 data.Length), 0, 4)
    let typeBytes = Encoding.ASCII.GetBytes(chunkType: string)
    stream.Write(typeBytes, 0, typeBytes.Length)
    stream.Write(data, 0, data.Length)
    let crcInput = Array.append typeBytes data
    let crcBytes = beBytes (crc32 crcInput)
    stream.Write(crcBytes, 0, 4)

  /// index -> (r,g,b). Order matches `paletteIndex` and the PLTE chunk below.
  let private paletteColor (outcome: CellOutcome) : byte * byte * byte =
    match outcome with
    | CellOutcome.NotRun -> 200uy, 200uy, 200uy // grey
    | CellOutcome.Pass -> 40uy, 180uy, 80uy // green
    | CellOutcome.Fail -> 220uy, 50uy, 50uy // red
    | CellOutcome.Stale -> 230uy, 160uy, 30uy // amber

  let private paletteIndex (outcome: CellOutcome) : byte =
    match outcome with
    | CellOutcome.NotRun -> 0uy
    | CellOutcome.Pass -> 1uy
    | CellOutcome.Fail -> 2uy
    | CellOutcome.Stale -> 3uy

  /// Encodes the matrix as a 4-color paletted PNG (color type 3, bit depth
  /// 2 — 4 pixels/byte before compression): width = test count, height = row
  /// count, exactly §6.5's "width = tests, height = rows". Palette index 0
  /// not-run/grey, 1 pass/green, 2 fail/red, 3 stale/amber.
  ///
  /// The pixel data is real-DEFLATE-compressed via the BCL's `ZLibStream`
  /// (RFC 1950 zlib framing — precisely what a PNG IDAT chunk requires), not
  /// stored uncompressed: a cohort's outcomes are overwhelmingly repeated
  /// across both tests and members, so genuine compression is what gets a
  /// 7,000-test x 10-row frame down to a few KB (§6.5: "~2 KB"); storing raw
  /// 2-bit pixels alone (17.5 KB for that shape) would not clear the "well
  /// under 4 KB" bar. `System.IO.Compression` is part of the BCL — no new
  /// NuGet dependency.
  ///
  /// Total dimensions are clamped to at least 1x1 (a PNG's width/height must
  /// be positive per spec) — an empty frame renders as one grey pixel rather
  /// than an invalid file.
  let toPng (pass: bool[][]) (fail: bool[][]) (stale: bool[][]) : byte[] =
    let rowCount = max 1 pass.Length
    let colCount = max 1 (if pass.Length = 0 then 0 else pass.[0].Length)
    let outcomeOf row col = outcomeAtSafe pass fail stale row col

    let ihdr =
      Array.concat [
        beBytes (uint32 colCount)
        beBytes (uint32 rowCount)
        [| 2uy; 3uy; 0uy; 0uy; 0uy |] // bit depth 2, color type 3 (palette), compression 0, filter method 0, no interlace
      ]

    let plte =
      [| CellOutcome.NotRun; CellOutcome.Pass; CellOutcome.Fail; CellOutcome.Stale |]
      |> Array.collect (fun o ->
        let r, g, b = paletteColor o
        [| r; g; b |])

    // Raw scanlines: one filter byte (0 = None) then the row's pixels packed
    // 4-per-byte, MSB-first, each row byte-padded per the PNG spec.
    let bytesPerRow = (colCount * 2 + 7) / 8
    let raw = Array.zeroCreate<byte> (rowCount * (1 + bytesPerRow))
    let mutable pos = 0
    for row in 0 .. rowCount - 1 do
      raw.[pos] <- 0uy // filter: None
      let rowStart = pos + 1
      let mutable bitPos = 0
      for col in 0 .. colCount - 1 do
        let idx = paletteIndex (outcomeOf row col)
        let byteOffset = bitPos / 8
        let shift = 6 - (bitPos % 8)
        raw.[rowStart + byteOffset] <- raw.[rowStart + byteOffset] ||| (idx <<< shift)
        bitPos <- bitPos + 2
      pos <- rowStart + bytesPerRow

    let idatPayload =
      use ms = new MemoryStream()
      do
        use zs = new ZLibStream(ms, CompressionLevel.SmallestSize, true)
        zs.Write(raw, 0, raw.Length)
      ms.ToArray()

    use out = new MemoryStream()
    out.Write(pngSignature, 0, pngSignature.Length)
    writeChunk out "IHDR" ihdr
    writeChunk out "PLTE" plte
    writeChunk out "IDAT" idatPayload
    writeChunk out "IEND" [||]
    out.ToArray()

  /// `toPng` inlined as a `data:image/png;base64,...` URI — the form §6.5's
  /// `<img>` embedding uses (no served route, no extra HTTP round trip).
  let toPngDataUri (pass: bool[][]) (fail: bool[][]) (stale: bool[][]) : string =
    "data:image/png;base64," + Convert.ToBase64String(toPng pass fail stale)
