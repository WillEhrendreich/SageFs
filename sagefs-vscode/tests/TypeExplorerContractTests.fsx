// WHY — the Type Explorer listed 50 of System.IO's 212 members and looked complete, because the daemon cut
// every completion reply at 50 and said nothing. The reply now states `total` and `totalKind`; these pin what
// the tree does with them: a partial listing says so, an exact full listing says nothing, and a floor always
// says there may be more.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring WorkflowPickContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/TypeExplorerPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.TypeExplorerPure

let tests =
  testList "VS Code Type Explorer - partial listings say so" [

    testCase "WHY — an exact size larger than the rows shown is a partial listing" <| fun () ->
      tailOf 50 (ExactSize 212) |> Expect.equal "partial" (PartialListing(50, ExactSize 212))

    testCase "WHY — an exact size equal to the rows shown is complete, so the tree adds no row" <| fun () ->
      tailOf 27 (ExactSize 27) |> Expect.equal "complete" NothingMore
      tailRow NothingMore |> Expect.isEmpty "no row"

    testCase "WHY — a floor is always partial, because the daemon stopped listing and the real size is not known" <| fun () ->
      tailOf 2000 (AtLeastSize 2000) |> Expect.equal "partial" (PartialListing(2000, AtLeastSize 2000))

    testCase "WHY — a reply without a size adds no row, so an older daemon looks as it always did" <| fun () ->
      tailOf 50 SizeNotStated |> Expect.equal "unstated" NothingMore

    testCase "WHY — the row names both numbers for an exact size and says 'at least' for a floor" <| fun () ->
      tailRow (PartialListing(50, ExactSize 212))
      |> List.map fst
      |> Expect.equal "exact row" [ "Showing 50 of 212" ]
      tailRow (PartialListing(200, AtLeastSize 2000))
      |> List.map fst
      |> Expect.equal "floor row" [ "Showing 200 of at least 2000" ]

    testCase "WHY — the daemon's two wire words parse to the two sizes, and anything else is unstated" <| fun () ->
      parseSetSize (Some 5) "exact" |> Expect.equal "exact" (ExactSize 5)
      parseSetSize (Some 5) "atLeast" |> Expect.equal "floor" (AtLeastSize 5)
      parseSetSize (Some 5) "" |> Expect.equal "no kind" SizeNotStated
      parseSetSize None "exact" |> Expect.equal "no total" SizeNotStated
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
