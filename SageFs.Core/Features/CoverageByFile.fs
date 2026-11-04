namespace SageFs.Features.LiveTesting

/// What the file projections need from the tests' recorded coverage, in two halves: what does not depend
/// on which file is asked about, found once for a session (`slotsByFile`, `computeLineCoverageAllFiles`),
/// and what does (`coveringTestsByLineIn`, given the file's slots).
///
/// These were `CoverageBitmap.coveringTestsByLine` and `CoverageBitmap.computeLineCoverageForFile`, which
/// did both halves on every call: merge every instrumentation map, OR every test's bitmap, compute the
/// line coverage of EVERY file to keep one, and scan every slot for the file's. Called once per file over
/// the 1,629 files of `SageFs.Tests` that was about 0.3 s of CPU a file. The old names stay, as the
/// one-file form of the same two halves.
module CoverageByFile =

  /// The slots of the merged map, by the file each belongs to, found for every file in one pass instead
  /// of one scan of every slot per file.
  let slotsByFile (merged: InstrumentationMap) : Map<string, int array> =
    merged.Slots
    |> Array.mapi (fun index slot -> slot.File, index)
    |> Array.groupBy fst
    |> Array.map (fun (file, indexed) -> file, indexed |> Array.map snd)
    |> Map.ofArray

  /// The tests whose own recorded coverage reaches `line`, by line, for the lines of the file whose slots
  /// these are. Tests are in discovery order. A file with no probe has no line a test can reach, so the
  /// tests are not walked for it.
  let coveringTestsByLineIn
    (merged: InstrumentationMap)
    (fileSlots: int array)
    (bitmaps: Map<TestId, CoverageBitmap>)
    (discovered: TestId array)
    : Map<int, TestId array> =
    match merged.TotalProbes, fileSlots.Length with
    | 0, _
    | _, 0 -> Map.empty
    | total, _ ->
      let testsOfLine = System.Collections.Generic.Dictionary<int, ResizeArray<TestId>>()
      for id in discovered do
        match Map.tryFind id bitmaps with
        | Some bm when bm.Count = total ->
          for slot in fileSlots do
            match CoverageBitmap.isSet slot bm with
            | false -> ()
            | true ->
              let sp = merged.Slots.[slot]
              for line in sp.Line .. max sp.Line sp.EndLine do
                match testsOfLine.TryGetValue line with
                | true, tests ->
                  // `discovered` is walked once, in order, so a test is only ever the last one in a line's list.
                  match tests.Count > 0 && tests.[tests.Count - 1] = id with
                  | true -> ()
                  | false -> tests.Add id
                | false, _ -> testsOfLine.[line] <- ResizeArray [ id ]
        | _ -> ()
      testsOfLine |> Seq.map (fun kv -> kv.Key, kv.Value.ToArray()) |> Map.ofSeq

  /// The tests whose own recorded coverage reaches `line` of `filePath`, by line, for every line
  /// that has a sequence point some test hit. Tests are in discovery order.
  let coveringTestsByLine
    (filePath: string)
    (maps: InstrumentationMap array)
    (bitmaps: Map<TestId, CoverageBitmap>)
    (discovered: TestId array)
    : Map<int, TestId array> =
    let merged = InstrumentationMap.merge maps
    let fileSlots =
      [| for i in 0 .. merged.Slots.Length - 1 do
           match merged.Slots.[i].File = filePath with
           | true -> yield i
           | false -> () |]
    coveringTestsByLineIn merged fileSlots bitmaps discovered

  /// Merge all test bitmaps via OR and compute LineCoverage per line, for EVERY file at once. This is the
  /// expensive part (it ORs every test's bitmap and walks every slot), and it does not depend on which
  /// file is asked about, so a caller projecting many files does it once.
  let computeLineCoverageAllFiles
    (merged: InstrumentationMap)
    (bitmaps: Map<TestId, CoverageBitmap>)
    : Map<string, Map<int, LineCoverage>> =
    match merged.TotalProbes = 0 || merged.Slots.Length = 0 with
    | true -> Map.empty
    | false ->
      let compatBitmaps =
        bitmaps |> Map.values |> Seq.filter (fun bm -> bm.Count = merged.TotalProbes) |> Seq.toArray
      match compatBitmaps.Length = 0 with
      | true -> Map.empty
      | false ->
        let combined =
          compatBitmaps
          |> Array.reduce (fun acc bm ->
            let bits = Array.init acc.Bits.Length (fun i -> acc.Bits.[i] ||| bm.Bits.[i])
            { Bits = bits; Count = acc.Count })
        let hits = CoverageBitmap.toBoolArray combined
        let state = InstrumentationMap.toCoverageState hits merged
        ILCoverage.computeLineCoverage state

  /// Merge all test bitmaps via OR, compute LineCoverage per line for a file.
  let computeLineCoverageForFile
    (filePath: string)
    (maps: InstrumentationMap array)
    (bitmaps: Map<TestId, CoverageBitmap>)
    : Map<int, LineCoverage> =
    computeLineCoverageAllFiles (InstrumentationMap.merge maps) bitmaps
    |> Map.tryFind filePath
    |> Option.defaultValue Map.empty
