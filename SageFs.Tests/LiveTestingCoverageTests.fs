module SageFs.Tests.LiveTestingCoverageTests

open System
open System.Reflection
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

// --- TestDependencyGraph Tests (RED — stub returns empty) ---

[<Tests>]
let dependencyGraphTests = testList "TestDependencyGraph" [
  test "findAffected returns tests that reference changed symbol" {
    let t1 = mkTestId "test1" (TestFramework.Unknown "x")
    let t2 = mkTestId "test2" (TestFramework.Unknown "x")
    let graph = {
      SymbolToTests = Map.ofList [ "MyModule.add", [| t1 |]; "MyModule.sub", [| t2 |] ]
      TransitiveCoverage = Map.ofList [ "MyModule.add", [| t1 |]; "MyModule.sub", [| t2 |] ]; SourceVersion = 1
      PerFileIndex = Map.empty
    }
    TestDependencyGraph.findAffected ["MyModule.add"] graph
    |> Expect.hasLength "one affected test" 1
  }

  test "findAffected returns union of tests for multiple symbols" {
    let t1 = mkTestId "test1" (TestFramework.Unknown "x")
    let t2 = mkTestId "test2" (TestFramework.Unknown "x")
    let graph = {
      SymbolToTests = Map.ofList [ "A.f", [| t1 |]; "B.g", [| t2 |] ]
      TransitiveCoverage = Map.ofList [ "A.f", [| t1 |]; "B.g", [| t2 |] ]; SourceVersion = 1
      PerFileIndex = Map.empty
    }
    TestDependencyGraph.findAffected ["A.f"; "B.g"] graph
    |> Expect.hasLength "both tests" 2
  }

  test "findAffected deduplicates when test references multiple changed symbols" {
    let t1 = mkTestId "test1" (TestFramework.Unknown "x")
    let graph = {
      SymbolToTests = Map.ofList [ "A.f", [| t1 |]; "A.g", [| t1 |] ]
      TransitiveCoverage = Map.ofList [ "A.f", [| t1 |]; "A.g", [| t1 |] ]; SourceVersion = 1
      PerFileIndex = Map.empty
    }
    TestDependencyGraph.findAffected ["A.f"; "A.g"] graph
    |> Expect.hasLength "deduplicated to one" 1
  }

  test "findAffected returns empty for unknown symbols" {
    let graph = {
      SymbolToTests = Map.ofList [ "A.f", [| mkTestId "t" (TestFramework.Unknown "x") |] ]
      TransitiveCoverage = Map.ofList [ "A.f", [| mkTestId "t" (TestFramework.Unknown "x") |] ]; SourceVersion = 1
      PerFileIndex = Map.empty
    }
    TestDependencyGraph.findAffected ["Unknown.sym"] graph
    |> Expect.hasLength "no matches" 0
  }

  test "empty graph returns empty for any symbol" {
    TestDependencyGraph.findAffected ["anything"] TestDependencyGraph.empty
    |> Expect.hasLength "empty graph" 0
  }
]

// --- CoverageProjection Tests (RED — stub returns Pending) ---

[<Tests>]
let coverageProjectionTests = testList "CoverageProjection" [
  test "symbol with no tests is NotCovered" {
    let result = CoverageProjection.symbolCoverage TestDependencyGraph.empty Map.empty "MyModule.add"
    match result with
    | CoverageStatus.NotCovered -> ()
    | other -> failtestf "expected NotCovered, got %A" other
  }

  test "symbol with empty test array is NotCovered" {
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "MyModule.add", Array.empty ]
    }
    let result = CoverageProjection.symbolCoverage graph Map.empty "MyModule.add"
    match result with
    | CoverageStatus.NotCovered -> ()
    | other -> failtestf "expected NotCovered, got %A" other
  }

  test "symbol reachable by passing tests is Covered with allPassing=true" {
    let tid = mkTestId "t1" (TestFramework.Unknown "x")
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "MyModule.add", [| tid |] ]
    }
    let results = Map.ofList [ tid, mkResult tid (TestResult.Passed (ts 5.0)) ]
    match CoverageProjection.symbolCoverage graph results "MyModule.add" with
    | CoverageStatus.Covered (1, CoverageHealth.AllPassing) -> ()
    | other -> failtestf "expected Covered(1,AllPassing), got %A" other
  }

  test "symbol reachable by failing test is Covered with allPassing=false" {
    let tid = mkTestId "t1" (TestFramework.Unknown "x")
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "MyModule.add", [| tid |] ]
    }
    let failure = TestFailure.AssertionFailed "nope"
    let results = Map.ofList [ tid, mkResult tid (TestResult.Failed (failure, ts 3.0)) ]
    match CoverageProjection.symbolCoverage graph results "MyModule.add" with
    | CoverageStatus.Covered (1, CoverageHealth.SomeFailing) -> ()
    | other -> failtestf "expected Covered(1,SomeFailing), got %A" other
  }

  test "symbol reachable by mixed results has allPassing=false" {
    let t1 = mkTestId "t1" (TestFramework.Unknown "x")
    let t2 = mkTestId "t2" (TestFramework.Unknown "x")
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "MyModule.add", [| t1; t2 |] ]
    }
    let failure = TestFailure.AssertionFailed "nope"
    let results = Map.ofList [
      t1, mkResult t1 (TestResult.Passed (ts 5.0))
      t2, mkResult t2 (TestResult.Failed (failure, ts 3.0))
    ]
    match CoverageProjection.symbolCoverage graph results "MyModule.add" with
    | CoverageStatus.Covered (2, CoverageHealth.SomeFailing) -> ()
    | other -> failtestf "expected Covered(2,SomeFailing), got %A" other
  }

  test "symbol reachable by tests with no results yet has allPassing=false" {
    let tid = mkTestId "t1" (TestFramework.Unknown "x")
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "MyModule.add", [| tid |] ]
    }
    match CoverageProjection.symbolCoverage graph Map.empty "MyModule.add" with
    | CoverageStatus.Covered (1, CoverageHealth.SomeFailing) -> ()
    | other -> failtestf "expected Covered(1,SomeFailing), got %A" other
  }

  test "computeAll produces coverage for every symbol in graph" {
    let t1 = mkTestId "t1" (TestFramework.Unknown "x")
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "A.f", [| t1 |]; "B.g", Array.empty ]
    }
    let result = CoverageProjection.computeAll graph Map.empty
    result |> Map.count
    |> Expect.equal "covers all symbols" 2
  }
]

// --- CoverageComputation Tests (RED — stub returns NotCovered) ---

[<Tests>]
let coverageComputationTests = testList "CoverageComputation" [
  test "line with all branches hit is FullyCovered" {
    let state = {
      Slots = [|
        { File = "a.fs"; Line = 10; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
        { File = "a.fs"; Line = 10; Column = 5; EndLine = 0; EndColumn = 0; BranchId = 1 }
        { File = "a.fs"; Line = 10; Column = 10; EndLine = 0; EndColumn = 0; BranchId = 2 }
      |]
      Hits = [| true; true; true |]
    }
    match CoverageComputation.computeLineCoverage state "a.fs" 10 with
    | LineCoverage.FullyCovered -> ()
    | other -> failtestf "expected FullyCovered, got %A" other
  }

  test "line with some branches hit is PartiallyCovered" {
    let state = {
      Slots = [|
        { File = "a.fs"; Line = 10; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
        { File = "a.fs"; Line = 10; Column = 5; EndLine = 0; EndColumn = 0; BranchId = 1 }
        { File = "a.fs"; Line = 10; Column = 10; EndLine = 0; EndColumn = 0; BranchId = 2 }
      |]
      Hits = [| true; false; true |]
    }
    match CoverageComputation.computeLineCoverage state "a.fs" 10 with
    | LineCoverage.PartiallyCovered (2, 3) -> ()
    | other -> failtestf "expected PartiallyCovered(2,3), got %A" other
  }

  test "line with no branches hit is NotCovered" {
    let state = {
      Slots = [|
        { File = "a.fs"; Line = 10; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
        { File = "a.fs"; Line = 10; Column = 5; EndLine = 0; EndColumn = 0; BranchId = 1 }
      |]
      Hits = [| false; false |]
    }
    match CoverageComputation.computeLineCoverage state "a.fs" 10 with
    | LineCoverage.NotCovered -> ()
    | other -> failtestf "expected NotCovered, got %A" other
  }

  test "line with no slots is NotCovered" {
    let state = {
      Slots = [|
        { File = "a.fs"; Line = 20; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
      |]
      Hits = [| true |]
    }
    match CoverageComputation.computeLineCoverage state "a.fs" 10 with
    | LineCoverage.NotCovered -> ()
    | other -> failtestf "expected NotCovered for untracked line, got %A" other
  }

  test "single slot line fully covered returns FullyCovered" {
    let state = {
      Slots = [| { File = "a.fs"; Line = 5; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 } |]
      Hits = [| true |]
    }
    match CoverageComputation.computeLineCoverage state "a.fs" 5 with
    | LineCoverage.FullyCovered -> ()
    | other -> failtestf "expected FullyCovered, got %A" other
  }

  test "empty state returns NotCovered" {
    let state = { Slots = Array.empty; Hits = Array.empty }
    match CoverageComputation.computeLineCoverage state "a.fs" 1 with
    | LineCoverage.NotCovered -> ()
    | other -> failtestf "expected NotCovered, got %A" other
  }
]

[<Tests>]
let instrumentationTests = testList "LiveTestingInstrumentation" [
  test "ActivitySource name is SageFs.LiveTesting" {
    LiveTestingInstrumentation.activitySource.Name
    |> Expect.equal "activity source name" "SageFs.LiveTesting"
  }

  test "Meter name is SageFs.LiveTesting" {
    LiveTestingInstrumentation.meter.Name
    |> Expect.equal "meter name" "SageFs.LiveTesting"
  }

  test "traced returns same value as wrapped function" {
    LiveTestingInstrumentation.traced "test.op" [] (fun () -> 42)
    |> Expect.equal "should return 42" 42
  }

  test "traced preserves exceptions" {
    Expect.throwsT<System.InvalidOperationException>
      "should rethrow"
      (fun () ->
        LiveTestingInstrumentation.traced
          "test.fail" [] (fun () ->
            raise (System.InvalidOperationException "boom")) |> ignore)
  }

  test "traced works with string return" {
    LiveTestingInstrumentation.traced
      "test.string" [("key", box "val")] (fun () -> "hello")
    |> Expect.equal "should return hello" "hello"
  }

  test "treeSitterHistogram is created" {
    LiveTestingInstrumentation.treeSitterHistogram
    |> Expect.isNotNull "should not be null"
  }

  test "fcsHistogram is created" {
    LiveTestingInstrumentation.fcsHistogram
    |> Expect.isNotNull "should not be null"
  }

  test "executionHistogram is created" {
    LiveTestingInstrumentation.executionHistogram
    |> Expect.isNotNull "should not be null"
  }
]

[<Tests>]
let transitiveClosureTests = testList "TransitiveCoverage" [
  test "single symbol with direct test stays in result" {
    let t1 = TestId.create "test1" (TestFramework.Unknown "x")
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.empty<string, string array>
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    Map.find "A" result
    |> Expect.equal "A should have t1" [| t1 |]
  }

  test "callee of tested symbol is transitively covered" {
    let t1 = TestId.create "test1" (TestFramework.Unknown "x")
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.ofList [ "A", [| "B" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    Map.find "B" result
    |> Expect.equal "B transitively covered by T1" [| t1 |]
  }

  test "two-hop transitive coverage" {
    let t1 = TestId.create "test1" (TestFramework.Unknown "x")
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.ofList [ "A", [| "B" |]; "B", [| "C" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    Map.find "C" result
    |> Expect.equal "C transitively covered by T1" [| t1 |]
  }

  test "multiple tests merge at shared callee" {
    let t1 = TestId.create "test1" (TestFramework.Unknown "x")
    let t2 = TestId.create "test2" (TestFramework.Unknown "x")
    let direct = Map.ofList [ "A", [| t1 |]; "B", [| t2 |] ]
    let callGraph = Map.ofList [ "A", [| "C" |]; "B", [| "C" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    let coveringC = Map.find "C" result |> Array.sort
    let expected = [| t1; t2 |] |> Array.sort
    coveringC |> Expect.equal "C covered by both" expected
  }

  test "cycle in call graph terminates" {
    let t1 = TestId.create "test1" (TestFramework.Unknown "x")
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.ofList [ "A", [| "B" |]; "B", [| "A" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    Map.find "B" result
    |> Expect.equal "B covered despite cycle" [| t1 |]
  }

  test "empty call graph returns direct mapping" {
    let t1 = TestId.create "test1" (TestFramework.Unknown "x")
    let direct = Map.ofList [ "A", [| t1 |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage Map.empty direct
    result |> Expect.equal "same as direct" direct
  }

  test "callee symbol with no direct tests gets attributed" {
    let t1 = TestId.create "test1" (TestFramework.Unknown "x")
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.ofList [ "A", [| "B" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    Map.containsKey "B" result
    |> Expect.isTrue "B should appear in result"
  }

  test "diamond call graph merges correctly" {
    // A calls B and C, both B and C call D
    let t1 = TestId.create "test1" (TestFramework.Unknown "x")
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.ofList [ "A", [| "B"; "C" |]; "B", [| "D" |]; "C", [| "D" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    Map.find "D" result
    |> Expect.equal "D covered by T1 (once, not duplicated)" [| t1 |]
  }

  test "symbols only in call graph but not tested are included" {
    let t1 = TestId.create "test1" (TestFramework.Unknown "x")
    let direct = Map.ofList [ "A", [| t1 |] ]
    let callGraph = Map.ofList [ "A", [| "B" |]; "B", [| "C" |]; "C", [| "D" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    [ "A"; "B"; "C"; "D" ]
    |> List.forall (fun s -> Map.containsKey s result)
    |> Expect.isTrue "all reachable symbols should be in result"
  }

  test "empty direct map produces empty result" {
    let callGraph = Map.ofList [ "A", [| "B" |] ]
    let result = TestDependencyGraph.computeTransitiveCoverage callGraph Map.empty
    result |> Expect.equal "empty result" Map.empty
  }
]


[<Tests>]
let sourceMappingTests = testList "SourceMapping" [
  test "extractMethodName gets last segment from dotted name" {
    SourceMapping.extractMethodName "MyModule.Tests.shouldAdd"
    |> Expect.equal "last segment" "shouldAdd"
  }

  test "extractMethodName gets property before slash for Expecto" {
    SourceMapping.extractMethodName "Ns.Mod.allMyTests/group/should add"
    |> Expect.equal "before slash" "allMyTests"
  }

  test "attributeMatchesFramework matches Fact to xunit" {
    SourceMapping.attributeMatchesFramework TestFramework.XUnit "Fact"
    |> Expect.isTrue "Fact is xunit"
  }

  test "attributeMatchesFramework does not match Fact to nunit" {
    SourceMapping.attributeMatchesFramework TestFramework.NUnit "Fact"
    |> Expect.isFalse "Fact is not nunit"
  }

  test "mergeSourceLocations maps xunit test by function name" {
    let locations = [|
      { AttributeName = "Fact"; FunctionName = "shouldAdd"
        FilePath = "Tests.fs"; Line = 10; Column = 0 }
    |]
    let tests = [|
      { Id = TestId.create "Mod.Tests.shouldAdd" TestFramework.XUnit
        FullName = "Mod.Tests.shouldAdd"; DisplayName = "shouldAdd"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = TestFramework.XUnit; Category = TestCategory.Unit }
    |]
    let result = SourceMapping.mergeSourceLocations locations tests
    result.[0].Origin
    |> Expect.equal "mapped" (TestOrigin.SourceMapped("Tests.fs", 10))
  }

  test "mergeSourceLocations maps Expecto hierarchical tests" {
    let locations = [|
      { AttributeName = "Tests"; FunctionName = "allMyTests"
        FilePath = "MyTests.fs"; Line = 5; Column = 0 }
    |]
    let tests = [|
      { Id = TestId.create "Ns.Mod.allMyTests/group/test1" TestFramework.Expecto
        FullName = "Ns.Mod.allMyTests/group/test1"; DisplayName = "test1"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = TestFramework.Expecto; Category = TestCategory.Unit }
      { Id = TestId.create "Ns.Mod.allMyTests/group/test2" TestFramework.Expecto
        FullName = "Ns.Mod.allMyTests/group/test2"; DisplayName = "test2"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]
    let result = SourceMapping.mergeSourceLocations locations tests
    result.[0].Origin
    |> Expect.equal "first mapped" (TestOrigin.SourceMapped("MyTests.fs", 5))
    result.[1].Origin
    |> Expect.equal "second mapped" (TestOrigin.SourceMapped("MyTests.fs", 5))
  }

  test "mergeSourceLocations preserves already-mapped tests" {
    let locations = [|
      { AttributeName = "Tests"; FunctionName = "myTests"
        FilePath = "New.fs"; Line = 99; Column = 0 }
    |]
    let tests = [|
      { Id = TestId.create "Mod.myTests" TestFramework.Expecto
        FullName = "Mod.myTests"; DisplayName = "myTests"
        Origin = TestOrigin.SourceMapped("Old.fs", 42); Labels = []
        Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]
    let result = SourceMapping.mergeSourceLocations locations tests
    result.[0].Origin
    |> Expect.equal "kept original" (TestOrigin.SourceMapped("Old.fs", 42))
  }

  test "mergeSourceLocations returns tests unchanged when no locations" {
    let tests = [|
      { Id = TestId.create "T.x" TestFramework.XUnit
        FullName = "T.x"; DisplayName = "x"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = TestFramework.XUnit; Category = TestCategory.Unit }
    |]
    let result = SourceMapping.mergeSourceLocations [||] tests
    result.[0].Origin
    |> Expect.equal "unchanged" TestOrigin.ReflectionOnly
  }

  test "mergeSourceLocations disambiguates by framework" {
    let locations = [|
      { AttributeName = "Test"; FunctionName = "validate"
        FilePath = "Nunit.fs"; Line = 10; Column = 0 }
      { AttributeName = "Fact"; FunctionName = "validate"
        FilePath = "Xunit.fs"; Line = 20; Column = 0 }
    |]
    let tests = [|
      { Id = TestId.create "Suite.validate" TestFramework.XUnit
        FullName = "Suite.validate"; DisplayName = "validate"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = TestFramework.XUnit; Category = TestCategory.Unit }
    |]
    let result = SourceMapping.mergeSourceLocations locations tests
    result.[0].Origin
    |> Expect.equal "picked xunit location" (TestOrigin.SourceMapped("Xunit.fs", 20))
  }
]

[<Tests>]
let annotationTests = testList "Gutter Annotations" [
  let test1 =
    { Id = TestId.create "Module.Tests.test1" TestFramework.Expecto; FullName = "Module.Tests.test1"
      DisplayName = "test1"; Origin = TestOrigin.SourceMapped ("test.fs", 10)
      Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
  let mkResult tid res =
    { TestId = tid; TestName = ""; Result = res; Timestamp = DateTimeOffset.UtcNow; Output = None }

  test "annotationsForFile returns tree-sitter locations when no results" {
    let state = {
      LiveTestState.empty with
        SourceLocations = [|
          { AttributeName = "Test"; FunctionName = "test1"; FilePath = "test.fs"; Line = 10; Column = 0 }
          { AttributeName = "Test"; FunctionName = "test2"; FilePath = "other.fs"; Line = 5; Column = 0 }
        |]
        Activation = LiveTestingActivation.Active
    }
    let annotations = LiveTesting.annotationsForFile "test.fs" state
    annotations.Length |> Expect.equal "1 annotation" 1
    annotations.[0].Line |> Expect.equal "line 10" 10
    annotations.[0].Icon |> Expect.equal "detected glyph" GutterIcon.TestDiscovered
  }

  test "annotationsForFile prefers result annotations over tree-sitter" {
    let baseState = {
      LiveTestState.empty with
        SourceLocations = [|
          { AttributeName = "Test"; FunctionName = "test1"; FilePath = "test.fs"; Line = 10; Column = 0 }
        |]
        DiscoveredTests = [| test1 |]
        LastResults = Map.ofList [
          test1.Id, mkResult test1.Id (TestResult.Passed (TimeSpan.FromMilliseconds 5.0))
        ]
        Activation = LiveTestingActivation.Active
    }
    let state = { baseState with StatusEntries = LiveTesting.computeStatusEntries baseState }
    let annotations = LiveTesting.annotationsForFile "test.fs" state
    annotations.Length |> Expect.equal "1 annotation" 1
    annotations.[0].Icon |> Expect.equal "passed glyph" GutterIcon.TestPassed
  }

  test "GutterIcon chars are correct" {
    GutterIcon.toChar GutterIcon.TestPassed |> Expect.equal "check" '\u2713'
    GutterIcon.toChar GutterIcon.TestFailed |> Expect.equal "cross" '\u2717'
    GutterIcon.toChar GutterIcon.TestDiscovered |> Expect.equal "diamond" '\u25C6'
  }

  test "tooltip includes duration for passed tests" {
    let status = TestRunStatus.Passed (TimeSpan.FromMilliseconds 12.5)
    let tip = StatusToGutter.tooltip "test1" status
    tip |> Expect.stringContains "check mark" "\u2713"
    tip |> Expect.stringContains "duration" "12"
  }

  test "tooltip shows failure message" {
    let status = TestRunStatus.Failed (TestFailure.AssertionFailed "expected 42 got 0", TimeSpan.FromMilliseconds 1.0)
    let tip = StatusToGutter.tooltip "test1" status
    tip |> Expect.stringContains "cross mark" "\u2717"
    tip |> Expect.stringContains "message" "expected 42 got 0"
  }

  test "recomputeEditorAnnotations returns annotations when enabled" {
    let filePath = "Tests.fs"
    let state = { LiveTestState.empty with
                    Activation = LiveTestingActivation.Active
                    SourceLocations = [|
                      { AttributeName = "Fact"; FunctionName = "test1"; FilePath = filePath; Line = 5; Column = 0 }
                      { AttributeName = "Test"; FunctionName = "test2"; FilePath = filePath; Line = 10; Column = 0 }
                    |] }
    let cached = LiveTesting.recomputeEditorAnnotations (Some filePath) state
    cached.Length |> Expect.equal "two annotations" 2
  }

  test "recomputeEditorAnnotations returns empty when disabled" {
    let filePath = "Tests.fs"
    let state = { LiveTestState.empty with
                    Activation = LiveTestingActivation.Inactive
                    SourceLocations = [|
                      { AttributeName = "Fact"; FunctionName = "test1"; FilePath = filePath; Line = 5; Column = 0 }
                    |] }
    LiveTesting.recomputeEditorAnnotations (Some filePath) state
    |> Array.length |> Expect.equal "no annotations" 0
  }

  test "recomputeEditorAnnotations matches annotationsForFile" {
    let filePath = "Tests.fs"
    let state = { LiveTestState.empty with
                    Activation = LiveTestingActivation.Active
                    SourceLocations = [|
                      { AttributeName = "Fact"; FunctionName = "test1"; FilePath = filePath; Line = 5; Column = 0 }
                    |] }
    let cached = LiveTesting.recomputeEditorAnnotations (Some filePath) state
    let direct = LiveTesting.annotationsForFile filePath state
    cached |> Expect.equal "cache matches direct" direct
  }

  test "recomputeEditorAnnotations returns empty when no active file" {
    let state = { LiveTestState.empty with
                    SourceLocations = [|
                      { AttributeName = "Fact"; FunctionName = "test1"; FilePath = "Tests.fs"; Line = 5; Column = 0 }
                    |] }
    LiveTesting.recomputeEditorAnnotations None state
    |> Array.length |> Expect.equal "no annotations without active file" 0
  }

  test "CachedEditorAnnotations defaults to empty" {
    LiveTestState.empty.CachedEditorAnnotations
    |> Expect.equal "default empty" [||]
  }
]

// ============================================================
// Coverage Projection Tests
// ============================================================

[<Tests>]
let coverageProjectionExtendedTests = testList "Coverage Projection Extended" [
  let test1 =
    { Id = TestId.create "Module.Tests.test1" TestFramework.Expecto; FullName = "Module.Tests.test1"
      DisplayName = "test1"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
  let test2 =
    { Id = TestId.create "Module.Tests.test2" TestFramework.Expecto; FullName = "Module.Tests.test2"
      DisplayName = "test2"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
  let mkResult tid res =
    { TestId = tid; TestName = ""; Result = res; Timestamp = DateTimeOffset.UtcNow; Output = None }
  let results = Map.ofList [
    test1.Id, mkResult test1.Id (TestResult.Passed (TimeSpan.FromMilliseconds 5.0))
    test2.Id, mkResult test2.Id (TestResult.Passed (TimeSpan.FromMilliseconds 3.0))
  ]

  test "symbolCoverage returns NotCovered for unknown symbol" {
    let graph = { TestDependencyGraph.empty with TransitiveCoverage = Map.empty }
    let cov = CoverageProjection.symbolCoverage graph results "Unknown.symbol"
    cov |> Expect.equal "not covered" CoverageStatus.NotCovered
  }

  test "symbolCoverage returns Covered with all passing" {
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "Module.add", [| test1.Id |] ]
    }
    let cov = CoverageProjection.symbolCoverage graph results "Module.add"
    match cov with
    | CoverageStatus.Covered (count, health) ->
      count |> Expect.equal "1 test" 1
      health |> Expect.equal "all passing" CoverageHealth.AllPassing
    | other -> failwithf "Expected Covered, got %A" other
  }

  test "symbolCoverage returns Covered with not all passing when test fails" {
    let failedResults =
      Map.add test1.Id (mkResult test1.Id (TestResult.Failed (TestFailure.AssertionFailed "bad", TimeSpan.FromMilliseconds 1.0))) results
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [ "Module.add", [| test1.Id |] ]
    }
    let cov = CoverageProjection.symbolCoverage graph failedResults "Module.add"
    match cov with
    | CoverageStatus.Covered (_, health) ->
      health |> Expect.equal "not all passing" CoverageHealth.SomeFailing
    | other -> failwithf "Expected Covered, got %A" other
  }

  test "computeAll returns coverage for all symbols" {
    let graph = {
      TestDependencyGraph.empty with
        TransitiveCoverage = Map.ofList [
          "Module.add", [| test1.Id |]
          "Module.validate", [| test1.Id; test2.Id |]
          "Module.unused", [||]
        ]
    }
    let all = CoverageProjection.computeAll graph results
    all.Count |> Expect.equal "3 symbols" 3
    match Map.find "Module.unused" all with
    | CoverageStatus.NotCovered -> ()
    | other -> failwithf "Expected NotCovered for unused, got %A" other
    match Map.find "Module.validate" all with
    | CoverageStatus.Covered (count, _) -> count |> Expect.equal "2 tests" 2
    | other -> failwithf "Expected Covered for validate, got %A" other
  }

  test "IL line coverage computes correctly" {
    let covState = {
      Slots = [|
        { File = "test.fs"; Line = 10; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
        { File = "test.fs"; Line = 10; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 1 }
        { File = "test.fs"; Line = 10; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 2 }
        { File = "test.fs"; Line = 20; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
      |]
      Hits = [| true; true; false; false |]
    }
    let line10 = CoverageComputation.computeLineCoverage covState "test.fs" 10
    match line10 with
    | LineCoverage.PartiallyCovered (covered, total) ->
      covered |> Expect.equal "2 covered" 2
      total |> Expect.equal "3 total" 3
    | other -> failwithf "Expected PartiallyCovered, got %A" other

    let line20 = CoverageComputation.computeLineCoverage covState "test.fs" 20
    line20 |> Expect.equal "line 20 not covered" LineCoverage.NotCovered

    let line30 = CoverageComputation.computeLineCoverage covState "test.fs" 30
    line30 |> Expect.equal "line 30 not covered (no slots)" LineCoverage.NotCovered
  }
]

// ============================================================
// Dependency Graph Tests
// ============================================================

[<Tests>]
let depGraphBfsTests = testList "TestDependencyGraph BFS" [
  let test1 =
    { Id = TestId.create "Module.Tests.test1" TestFramework.Expecto; FullName = "Module.Tests.test1"
      DisplayName = "test1"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
  let test2 =
    { Id = TestId.create "Module.Tests.test2" TestFramework.Expecto; FullName = "Module.Tests.test2"
      DisplayName = "test2"; Origin = TestOrigin.ReflectionOnly
      Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
  let depGraph = {
    SymbolToTests = Map.ofList [
      "Module.add", [| test1.Id |]
      "Module.validate", [| test1.Id; test2.Id |]
    ]
    TransitiveCoverage = Map.ofList [
      "Module.add", [| test1.Id |]
      "Module.validate", [| test1.Id; test2.Id |]
    ]; SourceVersion = 1
    PerFileIndex = Map.empty
  }

  test "findAffected returns empty for unknown symbols" {
    let result = TestDependencyGraph.findAffected [ "Unknown" ] depGraph
    result |> Expect.isEmpty "no affected tests"
  }

  test "findAffected returns union of affected tests for multiple symbols" {
    let result = TestDependencyGraph.findAffected [ "Module.add"; "Module.validate" ] depGraph
    result.Length |> Expect.equal "2 unique affected" 2
  }

  test "computeTransitiveCoverage propagates through call chain" {
    let callGraph = Map.ofList [
      "testFunc", [| "helperA" |]
      "helperA", [| "helperB" |]
    ]
    let directSymbolToTests = Map.ofList [
      "testFunc", [| test1.Id |]
    ]
    let transitive = TestDependencyGraph.computeTransitiveCoverage callGraph directSymbolToTests
    transitive.ContainsKey "helperA" |> Expect.isTrue "helperA reachable"
    transitive.ContainsKey "helperB" |> Expect.isTrue "helperB reachable"
    let helperBTests = Map.find "helperB" transitive
    helperBTests |> Expect.contains "helperB covered by test1" test1.Id
  }

  test "computeTransitiveCoverage handles diamond dependency" {
    let callGraph = Map.ofList [
      "test1Func", [| "A" |]
      "test2Func", [| "B" |]
      "A", [| "C" |]
      "B", [| "C" |]
    ]
    let direct = Map.ofList [
      "test1Func", [| test1.Id |]
      "test2Func", [| test2.Id |]
    ]
    let transitive = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    let cTests = Map.find "C" transitive
    cTests.Length |> Expect.equal "C covered by 2 tests" 2
  }

  test "computeTransitiveCoverage handles cycles without infinite loop" {
    let callGraph = Map.ofList [
      "A", [| "B" |]
      "B", [| "A" |]
    ]
    let direct = Map.ofList [
      "A", [| test1.Id |]
    ]
    let transitive = TestDependencyGraph.computeTransitiveCoverage callGraph direct
    transitive.ContainsKey "B" |> Expect.isTrue "B reachable from A"
    transitive.ContainsKey "A" |> Expect.isTrue "A reachable from itself"
  }
]

// ============================================================
// Category Detection Tests
// ============================================================

[<Tests>]
let coverageCorrelationTests = testList "CoverageCorrelation" [

  test "testsForSymbol returns NotCovered when symbol not in graph" {
    let graph = TestDependencyGraph.empty
    CoverageCorrelation.testsForSymbol graph Array.empty Map.empty "MyModule.add"
    |> Expect.equal "not in graph" CoverageDetail.NotCovered
  }

  test "testsForSymbol returns NotCovered when testIds array is empty" {
    let graph = { TestDependencyGraph.empty with TransitiveCoverage = Map.ofList ["MyModule.add", Array.empty] }
    CoverageCorrelation.testsForSymbol graph Array.empty Map.empty "MyModule.add"
    |> Expect.equal "empty array" CoverageDetail.NotCovered
  }

  test "testsForSymbol returns Covered with test info when tests exist" {
    let tid1 = TestId.create "test1" TestFramework.Expecto
    let tid2 = TestId.create "test2" TestFramework.Expecto
    let graph = { TestDependencyGraph.empty with TransitiveCoverage = Map.ofList ["MyModule.add", [| tid1; tid2 |]] }
    let tests = [|
      { Id = tid1; FullName = "Tests.test1"; DisplayName = "test1"; Origin = TestOrigin.ReflectionOnly
        Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
      { Id = tid2; FullName = "Tests.test2"; DisplayName = "test2"; Origin = TestOrigin.ReflectionOnly
        Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]
    let results = Map.ofList [
      tid1, { TestId = tid1; TestName = "test1"; Result = TestResult.Passed (TimeSpan.FromMilliseconds 5.0); Timestamp = DateTimeOffset.UtcNow; Output = None }
    ]
    match CoverageCorrelation.testsForSymbol graph tests results "MyModule.add" with
    | CoverageDetail.Covered infos ->
      infos.Length |> Expect.equal "2 tests" 2
      infos.[0].DisplayName |> Expect.equal "name1" "test1"
      Expect.isSome "has result" infos.[0].Result
      Expect.isNone "no result yet" infos.[1].Result
    | other -> failwithf "expected Covered, got %A" other
  }

  test "testsForSymbol uses TestId hash when test not in discovered" {
    let tid = TestId.create "orphan" TestFramework.XUnit
    let graph = { TestDependencyGraph.empty with TransitiveCoverage = Map.ofList ["Mod.fn", [| tid |]] }
    match CoverageCorrelation.testsForSymbol graph Array.empty Map.empty "Mod.fn" with
    | CoverageDetail.Covered infos ->
      infos.[0].DisplayName |> Expect.equal "falls back to hash" (TestId.value tid)
    | other -> failwithf "expected Covered, got %A" other
  }

  test "testsForLine returns NotCovered when no annotation matches" {
    let annotations = [| { Symbol = "Other.fn"; FilePath = "other.fs"; DefinitionLine = 5; Status = CoverageStatus.NotCovered; BranchCoverage = BranchCoverage.Unknown } |]
    CoverageCorrelation.testsForLine annotations TestDependencyGraph.empty Array.empty Map.empty "prod.fs" 10
    |> Expect.equal "no match" CoverageDetail.NotCovered
  }

  test "testsForLine chains through annotation to graph" {
    let tid = TestId.create "lineTest" TestFramework.Expecto
    let graph = { TestDependencyGraph.empty with TransitiveCoverage = Map.ofList ["Prod.validate", [| tid |]] }
    let annotations = [| { Symbol = "Prod.validate"; FilePath = "prod.fs"; DefinitionLine = 42; Status = CoverageStatus.Covered (1, CoverageHealth.AllPassing); BranchCoverage = BranchCoverage.Unknown } |]
    let tests = [| { Id = tid; FullName = "Tests.lineTest"; DisplayName = "lineTest"; Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto; Category = TestCategory.Unit } |]
    let results = Map.ofList [ tid, { TestId = tid; TestName = "lineTest"; Result = TestResult.Passed (TimeSpan.FromMilliseconds 3.0); Timestamp = DateTimeOffset.UtcNow; Output = None } ]
    match CoverageCorrelation.testsForLine annotations graph tests results "prod.fs" 42 with
    | CoverageDetail.Covered infos ->
      infos.Length |> Expect.equal "1 test" 1
      infos.[0].DisplayName |> Expect.equal "correct name" "lineTest"
      match infos.[0].Result with
      | Some (TestResult.Passed _) -> ()
      | other -> failwithf "expected Passed, got %A" other
    | other -> failwithf "expected Covered, got %A" other
  }

  test "testsForLine returns NotCovered when annotation exists but graph has no entry" {
    let annotations = [| { Symbol = "Prod.orphan"; FilePath = "prod.fs"; DefinitionLine = 10; Status = CoverageStatus.Pending; BranchCoverage = BranchCoverage.Unknown } |]
    CoverageCorrelation.testsForLine annotations TestDependencyGraph.empty Array.empty Map.empty "prod.fs" 10
    |> Expect.equal "annotation but no graph entry" CoverageDetail.NotCovered
  }
]

// --- CoverageBitmap cycle Wiring Tests ---

[<Tests>]
let coverageBitmapWiringTests = testList "CoverageBitmap cycle Wiring" [
  test "CoverageBitmapCollected populates TestCoverageBitmaps map" {
    let tid1 = mkTestId "ns" (TestFramework.Unknown "t1")
    let tid2 = mkTestId "ns" (TestFramework.Unknown "t2")
    let hits = [| true; false; true; true; false; false; true |]
    let bitmap = CoverageBitmap.ofBoolArray hits
    let model', _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.CoverageBitmapCollected ([| tid1; tid2 |], bitmap)))
        (SageFsModel.initial())
    let bitmaps = model'.LiveTesting.TestState.TestCoverageBitmaps
    Map.count bitmaps
    |> Expect.equal "should have 2 entries" 2
    Map.find tid1 bitmaps
    |> CoverageBitmap.equivalent bitmap
    |> Expect.isTrue "tid1 bitmap should match"
    Map.find tid2 bitmaps
    |> CoverageBitmap.equivalent bitmap
    |> Expect.isTrue "tid2 bitmap should match"
  }

  test "subsequent CoverageBitmapCollected merges into existing map" {
    let tid1 = mkTestId "ns" (TestFramework.Unknown "t1")
    let tid2 = mkTestId "ns" (TestFramework.Unknown "t2")
    let tid3 = mkTestId "ns" (TestFramework.Unknown "t3")
    let bm1 = CoverageBitmap.ofBoolArray [| true; false |]
    let bm2 = CoverageBitmap.ofBoolArray [| false; true |]
    let model1, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.CoverageBitmapCollected ([| tid1; tid2 |], bm1)))
        (SageFsModel.initial())
    let model2, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.CoverageBitmapCollected ([| tid3 |], bm2)))
        model1
    let bitmaps = model2.LiveTesting.TestState.TestCoverageBitmaps
    Map.count bitmaps
    |> Expect.equal "should have 3 entries" 3
    Map.find tid1 bitmaps |> CoverageBitmap.equivalent bm1 |> Expect.isTrue "tid1 still bm1"
    Map.find tid3 bitmaps |> CoverageBitmap.equivalent bm2 |> Expect.isTrue "tid3 is bm2"
  }

  test "CoverageBitmapCollected overwrites stale entry for same test" {
    let tid = mkTestId "ns" (TestFramework.Unknown "t1")
    let bm1 = CoverageBitmap.ofBoolArray [| true; false; false |]
    let bm2 = CoverageBitmap.ofBoolArray [| false; true; true |]
    let model1, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.CoverageBitmapCollected ([| tid |], bm1)))
        (SageFsModel.initial())
    let model2, _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.CoverageBitmapCollected ([| tid |], bm2)))
        model1
    let bitmaps = model2.LiveTesting.TestState.TestCoverageBitmaps
    Map.count bitmaps
    |> Expect.equal "should still have 1 entry" 1
    Map.find tid bitmaps |> CoverageBitmap.equivalent bm2 |> Expect.isTrue "should be overwritten to bm2"
  }

  test "empty TestCoverageBitmaps on initial model" {
    LiveTestState.empty.TestCoverageBitmaps
    |> Map.isEmpty
    |> Expect.isTrue "should start empty"
  }
]

// --- Coverage-Based Test Selection Tests ---

[<Tests>]
let coverageSelectionTests = testList "Coverage-based test selection" [
  test "buildFileMask sets bits for probes in target file only" {
    let maps = [| { Slots = [|
      { File = "A.fs"; Line = 1; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
      { File = "A.fs"; Line = 2; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 1 }
      { File = "B.fs"; Line = 1; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 2 }
    |]; TotalProbes = 3; TrackerTypeName = "t"; HitsFieldName = "h" } |]
    let mask = CoverageBitmap.buildFileMask "A.fs" maps
    CoverageBitmap.popCount mask |> Expect.equal "2 probes in A.fs" 2
    CoverageBitmap.isSet 0 mask |> Expect.isTrue "probe 0 (A.fs:1)"
    CoverageBitmap.isSet 1 mask |> Expect.isTrue "probe 1 (A.fs:2)"
    CoverageBitmap.isSet 2 mask |> Expect.isFalse "probe 2 (B.fs:1)"
  }

  test "buildFileMask with no matching file returns zero popcount" {
    let maps = [| { Slots = [|
      { File = "B.fs"; Line = 1; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
    |]; TotalProbes = 1; TrackerTypeName = "t"; HitsFieldName = "h" } |]
    let mask = CoverageBitmap.buildFileMask "A.fs" maps
    CoverageBitmap.popCount mask |> Expect.equal "no probes" 0
  }

  test "findCoverageAffected returns tests covering changed file" {
    let maps = [| { Slots = [|
      { File = "A.fs"; Line = 1; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
      { File = "A.fs"; Line = 2; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 1 }
      { File = "B.fs"; Line = 1; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 2 }
    |]; TotalProbes = 3; TrackerTypeName = "t"; HitsFieldName = "h" } |]
    let t1 = mkTestId "ns" (TestFramework.Unknown "test_a")
    let t2 = mkTestId "ns" (TestFramework.Unknown "test_b")
    let bm1 = CoverageBitmap.ofBoolArray [| true; false; false |]
    let bm2 = CoverageBitmap.ofBoolArray [| false; false; true |]
    let bitmaps = Map.ofList [ t1, bm1; t2, bm2 ]
    let affected = CoverageBitmap.findCoverageAffected "A.fs" maps bitmaps
    affected |> Array.length |> Expect.equal "only t1 affected" 1
    affected |> Array.contains t1 |> Expect.isTrue "t1 covers A.fs"
    affected |> Array.contains t2 |> Expect.isFalse "t2 doesn't cover A.fs"
  }

  test "findCoverageAffected returns empty when no bitmaps exist" {
    let maps = [| { Slots = [|
      { File = "A.fs"; Line = 1; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
    |]; TotalProbes = 1; TrackerTypeName = "t"; HitsFieldName = "h" } |]
    let affected = CoverageBitmap.findCoverageAffected "A.fs" maps Map.empty
    affected |> Array.isEmpty |> Expect.isTrue "no bitmaps = no coverage-based selection"
  }

  test "findCoverageAffected skips bitmaps with mismatched size" {
    let maps = [| { Slots = [|
      { File = "A.fs"; Line = 1; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
      { File = "A.fs"; Line = 2; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 1 }
    |]; TotalProbes = 2; TrackerTypeName = "t"; HitsFieldName = "h" } |]
    let t1 = mkTestId "ns" (TestFramework.Unknown "t1")
    let bm_wrong_size = CoverageBitmap.ofBoolArray [| true; false; true |]
    let bitmaps = Map.ofList [ t1, bm_wrong_size ]
    let affected = CoverageBitmap.findCoverageAffected "A.fs" maps bitmaps
    affected |> Array.isEmpty |> Expect.isTrue "mismatched size skipped"
  }

  test "afterTypeCheck unions symbol and coverage affected tests" {
    let tid1 = mkTestId "Tests.t1" TestFramework.Expecto
    let tid2 = mkTestId "Tests.t2" TestFramework.Expecto
    let tc1 = { Id = tid1; FullName = "Tests.t1"; DisplayName = "t1"
                Origin = TestOrigin.ReflectionOnly
                Labels = []; Framework = TestFramework.Expecto
                Category = TestCategory.Unit }
    let tc2 = { Id = tid2; FullName = "Tests.t2"; DisplayName = "t2"
                Origin = TestOrigin.ReflectionOnly
                Labels = []; Framework = TestFramework.Expecto
                Category = TestCategory.Unit }
    let graph =
      { SymbolToTests = Map.empty
        TransitiveCoverage = Map.ofList [ "Module.add", [| tid1 |] ]
        PerFileIndex = Map.empty
        SourceVersion = 0 }
    let maps = [| { Slots = [|
      { File = "Module.fs"; Line = 1; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
    |]; TotalProbes = 1; TrackerTypeName = "t"; HitsFieldName = "h" } |]
    let bm = CoverageBitmap.ofBoolArray [| true |]
    let state =
      { LiveTestState.empty with
          Activation = LiveTestingActivation.Active
          DiscoveredTests = [| tc1; tc2 |]
          TestCoverageBitmaps = Map.ofList [ tid2, bm ]
          TestSessionMap = Map.ofList [ tid1, "s"; tid2, "s" ] }
    let instrMaps = Map.ofList [ "s", maps ]
    match TestCycleEffects.afterTypeCheck ["Module.add"] "Module.fs" RunTrigger.Keystroke graph state None instrMaps with
    | [ TestCycleEffect.RequestRebuild (_, tests, _, _, _, _, _) ] ->
      let ids = tests |> Array.map (fun t -> t.Id) |> Set.ofArray
      ids |> Set.contains tid1 |> Expect.isTrue "t1 from symbol heuristic"
      ids |> Set.contains tid2 |> Expect.isTrue "t2 from coverage bitmap"
    | other -> failtestf "expected single RequestRebuild for .fs file, got %A" other
  }
]

// --- SequencePoint Range Tests ---

[<Tests>]
let sequencePointRangeTests = testList "SequencePoint.hasRange" [
  testCase "valid range returns true" <| fun () ->
    { File = "a.fs"; Line = 10; Column = 4; EndLine = 10; EndColumn = 20; BranchId = 0 }
    |> SequencePoint.hasRange
    |> Expect.isTrue "single-line range should be valid"

  testCase "multi-line range returns true" <| fun () ->
    { File = "a.fs"; Line = 10; Column = 0; EndLine = 12; EndColumn = 5; BranchId = 0 }
    |> SequencePoint.hasRange
    |> Expect.isTrue "multi-line range should be valid"

  testCase "zero EndLine returns false (degenerate)" <| fun () ->
    { File = "a.fs"; Line = 10; Column = 0; EndLine = 0; EndColumn = 0; BranchId = 0 }
    |> SequencePoint.hasRange
    |> Expect.isFalse "zero EndLine should be degenerate"

  testCase "same line same column returns false (zero-width)" <| fun () ->
    { File = "a.fs"; Line = 10; Column = 5; EndLine = 10; EndColumn = 5; BranchId = 0 }
    |> SequencePoint.hasRange
    |> Expect.isFalse "zero-width range should be degenerate"

  testCase "EndLine before Line returns false (inverted)" <| fun () ->
    { File = "a.fs"; Line = 10; Column = 0; EndLine = 9; EndColumn = 20; BranchId = 0 }
    |> SequencePoint.hasRange
    |> Expect.isFalse "inverted range should be degenerate"
]

// --- projectWithCoverage Range Enrichment Tests ---

let private mkTestSp file line col endLine endCol : SequencePoint =
  { File = file; Line = line; Column = col; EndLine = endLine; EndColumn = endCol; BranchId = 0 }

let private mkTestMap (slots: SequencePoint array) : InstrumentationMap =
  { Slots = slots; TotalProbes = slots.Length; TrackerTypeName = "T"; HitsFieldName = "H" }

[<Tests>]
let rangeLookupTests = testList "FileAnnotations.projectWithCoverage range enrichment" [
  test "enriches CoverageLineAnnotation with EndLine/EndColumn from matching SequencePoint" {
    let filePath = @"C:\src\MyModule.fs"
    let sp = mkTestSp filePath 10 4 15 20
    let maps = [| mkTestMap [| sp |] |]
    let ca : CoverageAnnotation =
      { Symbol = "MyModule.foo"; FilePath = filePath; DefinitionLine = 10
        Status = CoverageStatus.Covered(1, CoverageHealth.AllPassing)
        BranchCoverage = BranchCoverage.Unknown }
    let state = { LiveTestState.empty with CoverageAnnotations = [| ca |] }
    let cycle =
      { LiveTestCycleState.empty with
          TestState = state
          InstrumentationMaps = Map.ofList [ "sess1", maps ] }
    let result = FileAnnotations.projectWithCoverage filePath cycle
    result.CoverageAnnotations |> Expect.hasLength "should have one annotation" 1
    result.CoverageAnnotations.[0].EndLine |> Expect.equal "EndLine should come from SP" 15
    result.CoverageAnnotations.[0].EndColumn |> Expect.equal "EndColumn should come from SP" 20
  }

  test "EndLine/EndColumn remain 0 when no matching SequencePoint exists" {
    let filePath = @"C:\src\MyModule.fs"
    let sp = mkTestSp @"C:\src\Other.fs" 10 4 15 20
    let maps = [| mkTestMap [| sp |] |]
    let ca : CoverageAnnotation =
      { Symbol = "MyModule.foo"; FilePath = filePath; DefinitionLine = 10
        Status = CoverageStatus.Covered(1, CoverageHealth.AllPassing)
        BranchCoverage = BranchCoverage.Unknown }
    let state = { LiveTestState.empty with CoverageAnnotations = [| ca |] }
    let cycle =
      { LiveTestCycleState.empty with
          TestState = state
          InstrumentationMaps = Map.ofList [ "sess1", maps ] }
    let result = FileAnnotations.projectWithCoverage filePath cycle
    result.CoverageAnnotations |> Expect.hasLength "should have one annotation" 1
    result.CoverageAnnotations.[0].EndLine |> Expect.equal "EndLine should be 0" 0
    result.CoverageAnnotations.[0].EndColumn |> Expect.equal "EndColumn should be 0" 0
  }

  test "picks widest range when multiple SPs on same line" {
    let filePath = @"C:\src\MyModule.fs"
    let sp1 = mkTestSp filePath 10 4 10 20
    let sp2 = mkTestSp filePath 10 4 12 30
    let maps = [| mkTestMap [| sp1; sp2 |] |]
    let ca : CoverageAnnotation =
      { Symbol = "MyModule.foo"; FilePath = filePath; DefinitionLine = 10
        Status = CoverageStatus.Covered(1, CoverageHealth.AllPassing)
        BranchCoverage = BranchCoverage.Unknown }
    let state = { LiveTestState.empty with CoverageAnnotations = [| ca |] }
    let cycle =
      { LiveTestCycleState.empty with
          TestState = state
          InstrumentationMaps = Map.ofList [ "sess1", maps ] }
    let result = FileAnnotations.projectWithCoverage filePath cycle
    result.CoverageAnnotations.[0].EndLine |> Expect.equal "should pick widest EndLine" 12
    result.CoverageAnnotations.[0].EndColumn |> Expect.equal "should pick widest EndColumn" 30
  }

  test "filters out degenerate SPs (EndLine=0)" {
    let filePath = @"C:\src\MyModule.fs"
    let spDegen : SequencePoint =
      { File = filePath; Line = 10; Column = 4; EndLine = 0; EndColumn = 0; BranchId = 0 }
    let spGood = mkTestSp filePath 10 4 15 20
    let maps = [| mkTestMap [| spDegen; spGood |] |]
    let ca : CoverageAnnotation =
      { Symbol = "MyModule.foo"; FilePath = filePath; DefinitionLine = 10
        Status = CoverageStatus.Covered(1, CoverageHealth.AllPassing)
        BranchCoverage = BranchCoverage.Unknown }
    let state = { LiveTestState.empty with CoverageAnnotations = [| ca |] }
    let cycle =
      { LiveTestCycleState.empty with
          TestState = state
          InstrumentationMaps = Map.ofList [ "sess1", maps ] }
    let result = FileAnnotations.projectWithCoverage filePath cycle
    result.CoverageAnnotations.[0].EndLine |> Expect.equal "should use good SP" 15
    result.CoverageAnnotations.[0].EndColumn |> Expect.equal "should use good SP EndColumn" 20
  }

  test "enriches synthesized coverage annotations with range data" {
    let filePath = @"C:\src\MyModule.fs"
    let sp = mkTestSp filePath 10 4 15 20
    let maps = [| mkTestMap [| sp |] |]
    let tid = TestId.TestId "test1"
    let passed = TestResult.Passed(System.TimeSpan.FromMilliseconds 100.0)
    let depGraph =
      { TestDependencyGraph.empty with
          SymbolToTests = Map.ofList [ "MyModule.foo", [| tid |] ] }
    let lastResults =
      Map.ofList
        [ tid,
          { TestId = tid; TestName = "test1"; Result = passed
            Timestamp = System.DateTimeOffset.UtcNow; Output = None } ]
    let symRef : SymbolReference =
      { SymbolFullName = "MyModule.foo"; UseKind = SymbolUseKind.Definition
        UsedInTestId = None; FilePath = filePath; Line = 10 }
    let analysisCache =
      { FileAnalysisCache.empty with
          FileSymbols = Map.ofList [ filePath, [ symRef ] ] }
    let state =
      { LiveTestState.empty with
          LastResults = lastResults; CoverageAnnotations = [||] }
    let cycle =
      { LiveTestCycleState.empty with
          TestState = state; DepGraph = depGraph
          AnalysisCache = analysisCache
          InstrumentationMaps = Map.ofList [ "sess1", maps ] }
    let result = FileAnnotations.projectWithCoverage filePath cycle
    result.CoverageAnnotations |> Expect.hasLength "should synthesize one annotation" 1
    result.CoverageAnnotations.[0].EndLine |> Expect.equal "synthesized EndLine from SP" 15
    result.CoverageAnnotations.[0].EndColumn |> Expect.equal "synthesized EndColumn from SP" 20
  }
]

// --- CoverageBitmap Tests ---

[<Tests>]
let coverageBitmapTests = testList "CoverageBitmap" [
  testList "ofBoolArray/toBoolArray round-trip" [
    test "empty array round-trips" {
      let bm = CoverageBitmap.ofBoolArray [||]
      bm |> CoverageBitmap.toBoolArray |> Expect.equal "should round-trip empty" [||]
    }

    test "single true round-trips" {
      let hits = [| true |]
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      |> Expect.equal "should round-trip [true]" hits
    }

    test "single false round-trips" {
      let hits = [| false |]
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      |> Expect.equal "should round-trip [false]" hits
    }

    test "64 elements round-trips (exact word boundary)" {
      let hits = Array.init 64 (fun i -> i % 3 = 0)
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      |> Expect.equal "should round-trip 64 elements" hits
    }

    test "65 elements round-trips (crosses word boundary)" {
      let hits = Array.init 65 (fun i -> i % 2 = 0)
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      |> Expect.equal "should round-trip 65 elements" hits
    }

    test "256 elements round-trips (4 words)" {
      let hits = Array.init 256 (fun i -> i % 5 = 0 || i % 7 = 0)
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray
      |> Expect.equal "should round-trip 256 elements" hits
    }

    testProperty "round-trip preserves any bool array" (fun (hits: bool array) ->
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.toBoolArray = hits)
  ]

  testList "equivalent" [
    test "identical bitmaps are equivalent" {
      let bm = CoverageBitmap.ofBoolArray [| true; false; true; true |]
      CoverageBitmap.equivalent bm bm |> Expect.isTrue "same bitmap should be equivalent"
    }

    test "different bitmaps are not equivalent" {
      let a = CoverageBitmap.ofBoolArray [| true; false |]
      let b = CoverageBitmap.ofBoolArray [| false; true |]
      CoverageBitmap.equivalent a b |> Expect.isFalse "different bitmaps should not be equivalent"
    }

    test "different sizes are not equivalent" {
      let a = CoverageBitmap.ofBoolArray [| true |]
      let b = CoverageBitmap.ofBoolArray [| true; true |]
      CoverageBitmap.equivalent a b |> Expect.isFalse "different sizes should not be equivalent"
    }

    test "empty bitmaps are equivalent" {
      CoverageBitmap.equivalent CoverageBitmap.empty CoverageBitmap.empty
      |> Expect.isTrue "empty bitmaps should be equivalent"
    }
  ]

  testList "popCount" [
    test "empty bitmap has 0 population" {
      CoverageBitmap.empty |> CoverageBitmap.popCount |> Expect.equal "should be 0" 0
    }

    test "all-true bitmap has count = length" {
      let bm = CoverageBitmap.ofBoolArray (Array.create 100 true)
      bm |> CoverageBitmap.popCount |> Expect.equal "should be 100" 100
    }

    test "all-false bitmap has count = 0" {
      let bm = CoverageBitmap.ofBoolArray (Array.create 100 false)
      bm |> CoverageBitmap.popCount |> Expect.equal "should be 0" 0
    }

    testProperty "popCount equals count of true values" (fun (hits: bool array) ->
      let expected = hits |> Array.filter id |> Array.length
      hits |> CoverageBitmap.ofBoolArray |> CoverageBitmap.popCount = expected)
  ]

  testList "intersect" [
    test "AND of identical bitmaps is same bitmap" {
      let bm = CoverageBitmap.ofBoolArray [| true; false; true |]
      let result = CoverageBitmap.intersect bm bm
      CoverageBitmap.equivalent result bm |> Expect.isTrue "AND with self should be self"
    }

    test "AND with all-false yields all-false" {
      let a = CoverageBitmap.ofBoolArray [| true; true; true |]
      let b = CoverageBitmap.ofBoolArray [| false; false; false |]
      let result = CoverageBitmap.intersect a b
      result |> CoverageBitmap.popCount |> Expect.equal "AND with zeros should be 0" 0
    }
  ]

  testList "xorDiff" [
    test "XOR of identical bitmaps is all zeros" {
      let bm = CoverageBitmap.ofBoolArray [| true; false; true |]
      let result = CoverageBitmap.xorDiff bm bm
      result |> CoverageBitmap.popCount |> Expect.equal "XOR with self should be 0" 0
    }

    test "XOR finds differences" {
      let a = CoverageBitmap.ofBoolArray [| true;  false; true;  false |]
      let b = CoverageBitmap.ofBoolArray [| true;  true;  false; false |]
      let result = CoverageBitmap.xorDiff a b
      result |> CoverageBitmap.popCount |> Expect.equal "should have 2 differences" 2
    }
  ]

  testList "isSet" [
    test "returns true for set bit" {
      let bm = CoverageBitmap.ofBoolArray [| false; true; false |]
      bm |> CoverageBitmap.isSet 1 |> Expect.isTrue "bit 1 should be set"
    }

    test "returns false for unset bit" {
      let bm = CoverageBitmap.ofBoolArray [| false; true; false |]
      bm |> CoverageBitmap.isSet 0 |> Expect.isFalse "bit 0 should not be set"
    }

    test "out of range returns false" {
      let bm = CoverageBitmap.ofBoolArray [| true |]
      bm |> CoverageBitmap.isSet 5 |> Expect.isFalse "out of range should be false"
    }
  ]

  testList "memory efficiency" [
    test "bitmap uses 8x less memory than bool array" {
      let hits = Array.create 256 true
      let bm = CoverageBitmap.ofBoolArray hits
      bm.Bits.Length |> Expect.equal "should use 4 uint64 words" 4
    }

    test "2608 probes fits in 41 uint64 words" {
      let hits = Array.create 2608 false
      let bm = CoverageBitmap.ofBoolArray hits
      bm.Bits.Length |> Expect.equal "should use 41 words for 2608 probes" 41
    }
  ]
]

// --- Coverage cycle Verification Tests ---

[<Tests>]
let coverageCycleVerificationTests = testList "Coverage cycle Verification" [
  test "InstrumentationMapsReady populates model maps" {
    let maps = [|
      { InstrumentationMap.Slots = [| { SequencePoint.File = "test.fs"; Line = 10; Column = 1; EndLine = 0; EndColumn = 0; BranchId = 0 } |]
        TotalProbes = 1; TrackerTypeName = "__SageFsCoverage"; HitsFieldName = "Hits" }
    |]
    let model0 = (SageFsModel.initial())
    let model1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.InstrumentationMapsReady ("s1", maps))) model0
    model1.LiveTesting.InstrumentationMaps
    |> Map.containsKey "s1"
    |> Expect.isTrue "should have maps for session s1"
    model1.LiveTesting.InstrumentationMaps
    |> Map.find "s1"
    |> Array.length
    |> Expect.equal "should have 1 map" 1
  }

  test "CoverageBitmapCollected populates TestCoverageBitmaps" {
    let tid = TestId.TestId "ns.test1"
    let bitmap = CoverageBitmap.ofBoolArray [| true; false; true |]
    let model0 = (SageFsModel.initial())
    let model1, _ = SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.CoverageBitmapCollected ([| tid |], bitmap))) model0
    model1.LiveTesting.TestState.TestCoverageBitmaps
    |> Map.containsKey tid
    |> Expect.isTrue "should have bitmap for test"
  }

  test "afterTypeCheck uses coverage bitmaps when both maps and bitmaps exist" {
    let tid = TestId.TestId "ns.test1"
    let sp = { SequencePoint.File = "src/MyFile.fs"; Line = 10; Column = 1; EndLine = 0; EndColumn = 0; BranchId = 0 }
    let imap = {
      InstrumentationMap.Slots = [| sp |]; TotalProbes = 1
      TrackerTypeName = "__SageFsCoverage"; HitsFieldName = "Hits"
    }
    let bitmap = CoverageBitmap.ofBoolArray [| true |]
    let state = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        DiscoveredTests = [|
          { TestCase.Id = tid; FullName = "ns.test1"; DisplayName = "test1"
            Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto
            Category = TestCategory.Unit }
        |]
        TestCoverageBitmaps = Map.ofList [ tid, bitmap ]
        TestSessionMap = Map.ofList [ tid, "s1" ]
        RunPhases = Map.ofList [ "s1", TestRunPhase.Idle ]
    }
    let instrumentationMaps = Map.ofList [ "s1", [| imap |] ]
    let effects =
      TestCycleEffects.afterTypeCheck
        [] "src/MyFile.fs" RunTrigger.FileSave
        TestDependencyGraph.empty state None instrumentationMaps
    effects
    |> List.isEmpty
    |> Expect.isFalse "should produce RunAffectedTests effect when coverage matches"
  }

  test "afterTypeCheck with empty bitmaps skips coverage path" {
    // WHY: The coverage path requires both instrumentation maps AND collected bitmaps.
    // When bitmaps are absent (first run, not yet collected), the coverage path must be
    // bypassed entirely — we cannot use zero-data bitmaps to select which tests to run.
    // This test uses Keystroke trigger (not FileSave) to isolate the coverage path behavior;
    // FileSave on a compiled file with empty dep graph also triggers the compiled-file fallback
    // (which is separately tested in LiveTestRebuildCycleTests), which would obscure this test's intent.
    let tid = TestId.TestId "ns.test2"
    let sp = { SequencePoint.File = "src/Other.fs"; Line = 5; Column = 1; EndLine = 0; EndColumn = 0; BranchId = 0 }
    let imap = {
      InstrumentationMap.Slots = [| sp |]; TotalProbes = 1
      TrackerTypeName = "__SageFsCoverage"; HitsFieldName = "Hits"
    }
    let state = {
      LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        DiscoveredTests = [|
          { TestCase.Id = tid; FullName = "ns.test2"; DisplayName = "test2"
            Origin = TestOrigin.ReflectionOnly; Labels = []; Framework = TestFramework.Expecto
            Category = TestCategory.Unit }
        |]
        TestCoverageBitmaps = Map.empty
        TestSessionMap = Map.ofList [ tid, "s1" ]
        RunPhases = Map.ofList [ "s1", TestRunPhase.Idle ]
    }
    let instrumentationMaps = Map.ofList [ "s1", [| imap |] ]
    let effects =
      TestCycleEffects.afterTypeCheck
        [] "src/Other.fs" RunTrigger.Keystroke
        TestDependencyGraph.empty state None instrumentationMaps
    effects
    |> List.isEmpty
    |> Expect.isTrue "should produce no effects without bitmaps"
  }
]
