#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/FileAnnotationCoverage.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.FileAnnotationCoverage

let annotation (tests: string list) =
  { CoverageAnnotation.Line = 26
    Health = CoverageHealth.AllPassing
    BranchCoverage = BranchCoverage.Unknown
    CoveringTests = tests }

let tests =
  testList "VS Code covering-tests hover contract" [
    testCase "a line no test covers says nothing extra" <| fun _ ->
      annotation []
      |> CoverageAnnotation.hoverMessage
      |> Expect.equal "the hover is the status alone" "Coverage: all tests passing"

    testCase "one covering test is named" <| fun _ ->
      annotation [ "circle area" ]
      |> CoverageAnnotation.hoverMessage
      |> Expect.equal "the status, then the one test" "Coverage: all tests passing\n\n1 test covers this: circle area"

    testCase "several covering tests are counted and named, in the order the daemon sent them" <| fun _ ->
      annotation [ "circle area"; "rectangle area"; "triangle area" ]
      |> CoverageAnnotation.hoverMessage
      |> Expect.equal "the count and the names" "Coverage: all tests passing\n\n3 tests cover this: circle area, rectangle area, triangle area"

    testCase "the covering-tests text stands alone for any caller that wants only that" <| fun _ ->
      CoverageAnnotation.coveringTestsText [ "a"; "b" ] |> Expect.equal "counted" "2 tests cover this: a, b"
      CoverageAnnotation.coveringTestsText [] |> Expect.equal "empty for none" ""
  ]

Expecto.Tests.runTestsWithCLIArgs [] [||] tests
