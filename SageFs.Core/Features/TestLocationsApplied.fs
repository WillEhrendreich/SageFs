/// The edge of the marker pipeline: what `TestLocationsDetected` does to a
/// session's live-testing state. The two inputs are the tree-sitter locations
/// the event always carried, and the cases `ExpectoCases.discover` read from the
/// same file. The merge keeps its old behaviour first, then `place` decides the
/// mark for every leaf the text verifies — and because `place` and `matchedCase`
/// share one selection, the category recomputed here reads the very case the
/// mark came from, body included. A leaf with no matching case keeps whatever
/// reflection said: no source, no opinion.
namespace SageFs.Features.LiveTesting

module TestLocationsApplied =

  /// Merge the locations into the discovered tests, then apply placement and
  /// category to every test whose source case the text verifies. The locations
  /// pass through untouched: placement changes marks, not the file's own list.
  let apply
    (locations: SourceTestLocation array)
    (cases: SourceCase array)
    (tests: TestCase array)
    : SourceTestLocation array * TestCase array =
    let merged =
      match Array.isEmpty tests with
      | true -> tests
      | false -> SourceMapping.mergeSourceLocations locations tests
    let reflected = merged |> Array.map (fun testCase -> testCase.FullName, testCase.NamePath) |> Array.toList
    let placements = ExpectoCases.place cases reflected |> Map.ofList
    let applied =
      merged
      |> Array.map (fun testCase ->
        match Map.tryFind testCase.FullName placements with
        | None -> testCase
        | Some CasePlacement.NotInSource -> testCase
        | Some (CasePlacement.Exact (file, line)) | Some (CasePlacement.NearestAncestor (file, line, _)) ->
          match ExpectoCases.matchedCase cases testCase.NamePath with
          | None -> testCase
          | Some caseEntry ->
            let labels =
              match List.rev testCase.NamePath with
              | [] -> []
              | _ :: rest -> List.rev rest
            { testCase with
                Origin = TestOrigin.SourceMapped (file, line)
                Category =
                  fst (LiveTestingCategories.categoryOf
                        { ListLabels = labels
                          FilePath = caseEntry.FilePath
                          Body = caseEntry.Body }) })
    locations, applied
