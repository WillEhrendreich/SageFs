module SageFs.Tests.FileAnnotationsBasisTests

// `FileAnnotations.projectWithCoverage` rebuilt, for every file it was asked about, things that do not depend
// on the file: it sorted every test entry, merged every instrumentation map, ORed every test's coverage
// bitmap, computed the line coverage of every file to keep one, and built a map of every test's name.
// On `SageFs.Tests` (1,629 files, 17,944 tests) that was about 0.3 s of CPU a file, so the connect-time
// catch-up, which projects every file, took minutes and kept a core busy the whole time.
//
// A `ProjectionBasis` holds those things once. These tests pin that a file projected from a basis is the
// file projected the long way, and that a basis keeps each file's data to itself.

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting

let private fileOf (i: int) = sprintf "/repo/Tests/File%d.fs" i

let private entryIn (file: int) (line: int) (status: TestRunStatus) : TestStatusEntry =
  let name = sprintf "Tests.File%d.case%d" file line
  { TestId = TestId.TestId name
    DisplayName = name
    FullName = name
    Origin = TestOrigin.SourceMapped (fileOf file, line)
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit
    CurrentPolicy = RunPolicy.OnEveryChange
    Status = status
    PreviousStatus = TestRunStatus.Detected
    Provenance = ResultProvenance.Compiled }

let private coverageIn (file: int) (line: int) : CoverageAnnotation =
  { Symbol = sprintf "File%d.f%d" file line
    FilePath = fileOf file
    DefinitionLine = line
    Status = CoverageStatus.Covered (1, CoverageHealth.AllPassing)
    BranchCoverage = BranchCoverage.Unknown }

let private sequencePoint (file: int) (line: int) : SequencePoint =
  { File = fileOf file; Line = line; Column = 2; EndLine = line + 3; EndColumn = 9; BranchId = 0 }

/// Three files; file 2 has a failing test; files 1 and 2 have sequence points; file 3 has none.
let private cycle : LiveTestCycleState =
  let entries =
    [| entryIn 1 10 (TestRunStatus.Passed TimeSpan.Zero)
       entryIn 1 20 (TestRunStatus.Passed TimeSpan.Zero)
       entryIn 2 5 (TestRunStatus.Failed (TestFailure.AssertionFailed "boom", TimeSpan.Zero))
       entryIn 3 7 TestRunStatus.Detected |]
  let state =
    { LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        CoverageAnnotations = [| coverageIn 1 10; coverageIn 1 20; coverageIn 2 5 |] }
    |> LiveTestState.withStatusEntries entries
  let slots = [| sequencePoint 1 10; sequencePoint 1 20; sequencePoint 2 5 |]
  let map : InstrumentationMap =
    { Slots = slots; TotalProbes = slots.Length; TrackerTypeName = "T"; HitsFieldName = "H"; Source = MapSource.none }
  { LiveTestCycleState.empty with
      TestState = state
      InstrumentationMaps = Map.ofList [ "s1", [| map |] ] }

let private files = [ fileOf 1; fileOf 2; fileOf 3; "/repo/Nowhere.fs" ]

[<Tests>]
let tests =
  testList "file annotations from a projection basis" [

    test "WHY — a file projected from a basis is the file projected the long way, for every file" {
      let basis = FileAnnotations.prepare cycle
      for file in files do
        let fromBasis = FileAnnotations.projectFile basis file
        let longWay = FileAnnotations.project file (Some cycle.DepGraph) cycle.TestState
        fromBasis.TestAnnotations |> Expect.equal (sprintf "test annotations of %s" file) longWay.TestAnnotations
        fromBasis.CodeLenses |> Expect.equal (sprintf "code lenses of %s" file) longWay.CodeLenses
        fromBasis.InlineFailures |> Expect.equal (sprintf "inline failures of %s" file) longWay.InlineFailures
        (fromBasis.CoverageAnnotations |> Array.map (fun c -> c.Line))
        |> Expect.equal (sprintf "coverage lines of %s" file) (longWay.CoverageAnnotations |> Array.map (fun c -> c.Line))
    }

    test "one basis answers many files, and each gets only its own" {
      let basis = FileAnnotations.prepare cycle
      let one = FileAnnotations.projectFile basis (fileOf 1)
      let two = FileAnnotations.projectFile basis (fileOf 2)
      let three = FileAnnotations.projectFile basis (fileOf 3)
      one.TestAnnotations.Length |> Expect.equal "file 1 has its two tests" 2
      one.CoverageAnnotations.Length |> Expect.equal "and its two coverage annotations" 2
      two.InlineFailures.Length |> Expect.equal "file 2 has the failure" 1
      one.InlineFailures.Length |> Expect.equal "file 1 has none of file 2's" 0
      three.CoverageAnnotations.Length |> Expect.equal "file 3 has no coverage annotation" 0
      three.TestAnnotations.Length |> Expect.equal "and its one test" 1
    }

    test "a file nothing is known about projects to nothing, and does not throw" {
      let projected = FileAnnotations.projectFile (FileAnnotations.prepare cycle) "/repo/Nowhere.fs"
      projected.TestAnnotations |> Expect.isEmpty "no tests"
      projected.CoverageAnnotations |> Expect.isEmpty "no coverage"
      projected.CodeLenses |> Expect.isEmpty "no lenses"
    }

    test "the range of a sequence point reaches the coverage annotation on its line" {
      let projected = FileAnnotations.projectFile (FileAnnotations.prepare cycle) (fileOf 1)
      let at10 = projected.CoverageAnnotations |> Array.find (fun c -> c.Line = 10)
      at10.EndLine |> Expect.equal "from the sequence point" 13
      at10.EndColumn |> Expect.equal "from the sequence point" 9
    }

    test "projectWithCoverage is the basis for one file" {
      let single = FileAnnotations.projectWithCoverage (fileOf 1) cycle
      single |> Expect.equal "the same as preparing and projecting" (FileAnnotations.projectFile (FileAnnotations.prepare cycle) (fileOf 1))
    }

    test "the coverage views of a file from its own annotations are the views found by scanning for them" {
      let basis = FileAnnotations.prepare cycle
      for file in files do
        let viaBasis =
          FileAnnotationsInternals.projectViewsOf
            CoverageViewMode.defaults (FileAnnotations.coverageAnnotationsOf basis file) file cycle.DepGraph cycle.TestState
        let scanned =
          FileAnnotationsInternals.projectViewsForFile CoverageViewMode.defaults file cycle.DepGraph cycle.TestState
        viaBasis |> Expect.equal (sprintf "views of %s" file) scanned
    }

    test "the slots of a merged map are found by file in one pass, and match a scan per file" {
      let merged = InstrumentationMap.merge (cycle.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq)
      let byFile = CoverageByFile.slotsByFile merged
      for file in files do
        let scanned = [| for i in 0 .. merged.Slots.Length - 1 do if merged.Slots.[i].File = file then yield i |]
        Map.tryFind file byFile |> Option.defaultValue [||] |> Expect.equal (sprintf "slots of %s" file) scanned
    }

    test "a file with no probe has no covering tests, whatever the tests recorded" {
      let merged = InstrumentationMap.merge (cycle.InstrumentationMaps |> Map.values |> Seq.collect id |> Array.ofSeq)
      CoverageByFile.coveringTestsByLineIn merged [||] Map.empty [| TestId.TestId "a" |]
      |> Expect.isEmpty "nothing to walk the tests for"
    }
  ]
