module SageFs.Tests.FileCoverageTests

open Expecto
open Expecto.Flip
open System.Text.Json
open SageFs.Features.LiveTesting
open SageFs.McpTools

let private mkCovAnn line endLine endCol status testIds branchCov : CoverageLineAnnotation =
  { Line = line; EndLine = endLine; EndColumn = endCol
    Detail = status; CoveringTestIds = testIds; BranchCoverage = branchCov }

let private mkFileAnns path covAnns : FileAnnotations =
  { FilePath = path; TestAnnotations = [||]; CoverageAnnotations = covAnns
    InlineFailures = [||]; CodeLenses = [||]; PerformanceAnnotations = [||] }

[<Tests>]
let tests =
  testList "getFileCoverage formatting" [

    test "empty annotations produce zero-line JSON" {
      let json = formatFileCoverageResponse (mkFileAnns "test.fs" [||]) LiveTestState.empty
      let doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      root.GetProperty("FilePath").GetString()
        |> Expect.equal "filePath" "test.fs"
      root.GetProperty("Lines").GetArrayLength()
        |> Expect.equal "no lines" 0
      root.GetProperty("Summary").GetProperty("TotalLines").GetInt32()
        |> Expect.equal "total 0" 0
      root.GetProperty("Summary").GetProperty("CoveragePercent").GetDouble()
        |> Expect.equal "pct 0" 0.0
    }

    test "covered line has correct structure" {
      let tid = TestId.create "MyTest" TestFramework.Expecto
      let cov =
        mkCovAnn 10 10 30
          (CoverageStatus.Covered (1, CoverageHealth.AllPassing))
          [| tid |]
          (Some LineCoverage.FullyCovered)
      let json = formatFileCoverageResponse (mkFileAnns "Foo.fs" [| cov |]) LiveTestState.empty
      let doc = JsonDocument.Parse(json)
      let line0 = doc.RootElement.GetProperty("Lines").EnumerateArray() |> Seq.head
      line0.GetProperty("Line").GetInt32()
        |> Expect.equal "line 10" 10
      line0.GetProperty("Covered").GetBoolean()
        |> Expect.isTrue "is covered"
      line0.GetProperty("TestCount").GetInt32()
        |> Expect.equal "1 test" 1
      line0.GetProperty("Health").GetString()
        |> Expect.equal "all passing" "AllPassing"
      line0.GetProperty("BranchCoverage").GetString()
        |> Expect.equal "fully covered" "FullyCovered"
    }

    test "not-covered line has correct structure" {
      let cov = mkCovAnn 5 5 20 CoverageStatus.NotCovered [||] None
      let json = formatFileCoverageResponse (mkFileAnns "Bar.fs" [| cov |]) LiveTestState.empty
      let doc = JsonDocument.Parse(json)
      let line0 = doc.RootElement.GetProperty("Lines").EnumerateArray() |> Seq.head
      line0.GetProperty("Covered").GetBoolean()
        |> Expect.isFalse "not covered"
      line0.GetProperty("TestCount").GetInt32()
        |> Expect.equal "0 tests" 0
      line0.GetProperty("Health").GetString()
        |> Expect.equal "not covered" "NotCovered"
      line0.GetProperty("BranchCoverage").GetString()
        |> Expect.equal "unknown branch" "Unknown"
    }

    test "summary computes correct coverage percent" {
      let covered =
        mkCovAnn 1 1 10
          (CoverageStatus.Covered (1, CoverageHealth.AllPassing)) [||] None
      let notCov = mkCovAnn 2 2 10 CoverageStatus.NotCovered [||] None
      let notCov2 = mkCovAnn 3 3 10 CoverageStatus.NotCovered [||] None
      let json =
        formatFileCoverageResponse
          (mkFileAnns "Mix.fs" [| covered; notCov; notCov2 |])
          LiveTestState.empty
      let doc = JsonDocument.Parse(json)
      let summary = doc.RootElement.GetProperty("Summary")
      summary.GetProperty("CoveredLines").GetInt32()
        |> Expect.equal "1 covered" 1
      summary.GetProperty("TotalLines").GetInt32()
        |> Expect.equal "3 total" 3
      summary.GetProperty("CoveragePercent").GetDouble()
        |> Expect.equal "33.3%" 33.3
    }

    test "partial branch coverage formatted correctly" {
      let cov =
        mkCovAnn 7 7 25
          (CoverageStatus.Covered (2, CoverageHealth.SomeFailing))
          [||]
          (Some (LineCoverage.PartiallyCovered (3, 5)))
      let json = formatFileCoverageResponse (mkFileAnns "Branch.fs" [| cov |]) LiveTestState.empty
      let doc = JsonDocument.Parse(json)
      let line0 = doc.RootElement.GetProperty("Lines").EnumerateArray() |> Seq.head
      line0.GetProperty("Health").GetString()
        |> Expect.equal "some failing" "SomeFailing"
      line0.GetProperty("BranchCoverage").GetString()
        |> Expect.equal "partial 3/5" "Partial(3/5)"
    }

    test "covering test names resolved from discovered tests" {
      let tid = TestId.create "MyModule.myTest" TestFramework.Expecto
      let tc : TestCase = {
        Id = tid
        FullName = "MyModule.myTest"
        DisplayName = "my test"
        Origin = TestOrigin.ReflectionOnly
        Labels = []
        Framework = TestFramework.Expecto
        Category = TestCategory.Unit
      }
      let testState = { LiveTestState.empty with DiscoveredTests = [| tc |] }
      let cov =
        mkCovAnn 1 1 10
          (CoverageStatus.Covered (1, CoverageHealth.AllPassing))
          [| tid |] None
      let json = formatFileCoverageResponse (mkFileAnns "Named.fs" [| cov |]) testState
      let doc = JsonDocument.Parse(json)
      let tests =
        doc.RootElement.GetProperty("Lines").EnumerateArray()
        |> Seq.head
        |> fun l -> l.GetProperty("CoveringTests")
      tests.EnumerateArray()
        |> Seq.head
        |> fun t -> t.GetString()
        |> Expect.equal "resolved name" "my test"
    }

    test "covering test falls back to TestId when not discovered" {
      let tid = TestId.create "Unknown.test" TestFramework.Expecto
      let cov =
        mkCovAnn 1 1 10
          (CoverageStatus.Covered (1, CoverageHealth.AllPassing))
          [| tid |] None
      let json = formatFileCoverageResponse (mkFileAnns "Fallback.fs" [| cov |]) LiveTestState.empty
      let doc = JsonDocument.Parse(json)
      let tests =
        doc.RootElement.GetProperty("Lines").EnumerateArray()
        |> Seq.head
        |> fun l -> l.GetProperty("CoveringTests")
      let testName = tests.EnumerateArray() |> Seq.head |> fun t -> t.GetString()
      // Falls back to the raw TestId value (hex hash)
      testName |> Expect.isNonEmpty "has fallback name"
    }
  ]
