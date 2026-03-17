#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/FileAnnotationCoverage.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.FileAnnotationCoverage

let annotation health branchCoverage =
  { CoverageAnnotation.Line = 42
    Health = health
    BranchCoverage = branchCoverage }

let tests =
  testList "VS Code branch coverage contract" [
    testCase "branch coverage overrides passing line coverage" <| fun _ ->
      annotation CoverageHealth.AllPassing BranchCoverage.FullyCovered
      |> CoverageAnnotation.decorationKind
      |> Expect.equal "branch-full should win over line coverage" CoverageDecorationKind.BranchFull

    testCase "branch partial overrides failing line coverage" <| fun _ ->
      annotation CoverageHealth.SomeFailing (BranchCoverage.PartiallyCovered (2, 3))
      |> CoverageAnnotation.decorationKind
      |> Expect.equal "branch-partial should win over line coverage" (CoverageDecorationKind.BranchPartial (2, 3))

    testCase "branch not covered renders branch-none decoration" <| fun _ ->
      annotation CoverageHealth.NoCoverage BranchCoverage.NotCovered
      |> CoverageAnnotation.decorationKind
      |> Expect.equal "branch-none should be explicit" CoverageDecorationKind.BranchNone

    testCase "unknown branch coverage falls back to line health" <| fun _ ->
      annotation CoverageHealth.SomeFailing BranchCoverage.Unknown
      |> CoverageAnnotation.decorationKind
      |> Expect.equal "unknown branch coverage should keep line semantics" CoverageDecorationKind.LineFailing

    testCase "partial branch hover includes covered and total counts" <| fun _ ->
      annotation CoverageHealth.AllPassing (BranchCoverage.PartiallyCovered (2, 3))
      |> CoverageAnnotation.hoverMessage
      |> Expect.equal "partial branch hover should explain the missing path" "Branch coverage: 2/3 branches covered"
  ]

Expecto.Tests.runTestsWithCLIArgs [] [||] tests
