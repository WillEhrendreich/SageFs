// WHY — `sagefs.projectPath` was persisted with ConfigurationTarget.Global and
// then trusted unconditionally, so a project picked in one workspace was reused
// in every other F# workspace on the machine ("it attempted to scan some
// irrelevant projects in other dirs not sure why"). These pin the rules that
// make a stale pin detectable and the pick list predictable.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring AppRunContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/ProjectPickPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.ProjectPickPure

let private folders = [| "/home/me/repo" |]

let tests =
  testList "VS Code project pick - pure logic" [

    testCase "WHY - nothing persisted means discover normally" <| fun _ ->
      chooseConfigured folders [||] "" |> Expect.equal "blank" NotConfigured
      chooseConfigured folders [||] "   " |> Expect.equal "whitespace" NotConfigured

    testCase "WHY - an absolute path inside this workspace is honoured" <| fun _ ->
      chooseConfigured folders [||] "/home/me/repo/src/A.fsproj"
      |> Expect.equal "inside" (UseConfigured "/home/me/repo/src/A.fsproj")

    testCase "WHY - an absolute path from ANOTHER workspace is ignored, not silently used" <| fun _ ->
      match chooseConfigured folders [||] "/home/me/other/B.fsproj" with
      | IgnoreStale(path, reason) ->
        path |> Expect.equal "names the offender" "/home/me/other/B.fsproj"
        reason |> Expect.stringContains "explains it was a global setting" "globally"
      | other -> failtestf "expected IgnoreStale, got %A" other

    testCase "WHY - a relative path is only honoured when this workspace really has it" <| fun _ ->
      chooseConfigured folders [| "src/A.fsproj" |] "src/A.fsproj"
      |> Expect.equal "present" (UseConfigured "src/A.fsproj")
      match chooseConfigured folders [| "src/A.fsproj" |] "other/B.fsproj" with
      | IgnoreStale _ -> ()
      | other -> failtestf "expected IgnoreStale, got %A" other

    testCase "WHY - a relative pin matches regardless of slash direction, because Windows saved backslashes" <| fun _ ->
      chooseConfigured folders [| "src/A.fsproj" |] "src\\A.fsproj"
      |> Expect.equal "normalised" (UseConfigured "src\\A.fsproj")

    testCase "WHY - a path that merely shares a prefix with the workspace is NOT inside it" <| fun _ ->
      isWithinWorkspace [| "/home/me/repo" |] "/home/me/repo-other/A.fsproj"
      |> Expect.isFalse "prefix is not containment"

    testCase "WHY - the workspace root itself counts as inside" <| fun _ ->
      isWithinWorkspace [| "/home/me/repo" |] "/home/me/repo" |> Expect.isTrue "root"

    testCase "WHY - solutions sort first, because picking one loads everything" <| fun _ ->
      sortCandidates [| "src/B.fsproj"; "All.slnx"; "src/A.fsproj" |]
      |> Array.head
      |> Expect.equal "solution first" "All.slnx"

    testCase "WHY - shallower projects sort before deeper ones, because the root project is the likely target" <| fun _ ->
      sortCandidates [| "a/b/c/Deep.fsproj"; "Top.fsproj" |]
      |> Expect.equal "shallow first" [| "Top.fsproj"; "a/b/c/Deep.fsproj" |]

    testCase "WHY - equal depth sorts alphabetically, so the list is stable between runs" <| fun _ ->
      sortCandidates [| "z/Z.fsproj"; "a/A.fsproj" |]
      |> Expect.equal "alphabetical" [| "a/A.fsproj"; "z/Z.fsproj" |]

    testCase "WHY - the pick always offers Browse, so a project the scan capped out is still reachable" <| fun _ ->
      let rows = pickRows [| "A.fsproj" |]
      rows |> Array.last |> Expect.equal "browse last" BrowseRow
      rows.Length |> Expect.equal "candidates plus browse" 2

    testCase "WHY - hitting the scan cap is detectable, because silent truncation hid 20 of 30 projects" <| fun _ ->
      isTruncated 10 10 |> Expect.isTrue "at the cap means possibly more"
      isTruncated 10 9 |> Expect.isFalse "under the cap is complete"
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
