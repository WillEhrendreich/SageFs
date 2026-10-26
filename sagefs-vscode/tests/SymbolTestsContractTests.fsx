// WHY — "SageFs: Show Test Call Graph" answered "No dependency graph available yet" for every empty
// map, and asked the daemon about its primary session whichever session the window showed. The route now
// states why a map is empty; these pin what the command says for each reason.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring TypeExplorerContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/SymbolTestsPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.SymbolTestsPure

let tests =
  testList "VS Code tests covering a symbol - an empty map says why" [

    testCase "WHY — the daemon's five state words parse, and anything else is not stated" <| fun () ->
      parseState "nothing_analysed" |> Expect.equal "nothing analysed" NothingAnalysed
      parseState "no_coverage_recorded" |> Expect.equal "no coverage" NoCoverageRecorded
      parseState "coverage_of_another_build" |> Expect.equal "another build" CoverageOfAnotherBuild
      parseState "nothing_reached" |> Expect.equal "nothing reached" NothingReached
      parseState "mapped" |> Expect.equal "mapped" Mapped
      parseState "" |> Expect.equal "older daemon" StateNotStated

    testCase "WHY — each reason gets a different sentence, so a person knows what to do next" <| fun () ->
      [ NothingAnalysed; NoCoverageRecorded; CoverageOfAnotherBuild; NothingReached; StateNotStated ]
      |> List.map emptyMessage
      |> List.distinct
      |> List.length
      |> Expect.equal "five distinct sentences" 5

    testCase "WHY — not analysed tells the person to edit or save, and no coverage tells them to run the tests" <| fun () ->
      emptyMessage NothingAnalysed |> Expect.stringContains "names the next step" "Edit or save"
      emptyMessage NoCoverageRecorded |> Expect.stringContains "names the next step" "Run the tests"
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
