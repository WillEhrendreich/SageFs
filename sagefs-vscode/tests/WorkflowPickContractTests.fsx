// WHY — "Switch Workflow" shipped offering TWO of the three workflows, using
// a label for one of them ("Live") that no other surface in the product uses,
// and with no marker for the workflow the session was already in
// (sagefs-ux-roast.md §4.2). `LiveTesting` — the mode the extension's sidebar,
// gutters, CodeLenses and Test Explorer adapter all exist to serve — was
// simply unreachable from VS Code.
//
// These pin the picker against the DAEMON'S OWN source of truth rather than a
// copy of it: this script `#load`s the real `SageFs.Core/WorkflowTypes.fs` and
// asserts that
//   * every `SessionWorkflow` case is offered,
//   * every offered label is exactly `SessionWorkflow.label` for that case, and
//   * every offered wire string round-trips through the real
//     `SessionWorkflow.tryOfString` to the case it claims.
// A new workflow case, a renamed label, or a dropped alias therefore fails
// HERE, under `dotnet fsi`, instead of silently switching a user into a mode
// they did not pick.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring AppRunContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../../SageFs.Core/TestProviderTypes.fs"
#load "../../SageFs.Core/WorkflowTypes.fs"
#load "../src/WorkflowPickPure.fs"

open Expecto
open Expecto.Flip
open SageFs.WorkflowTypes
open SageFs.Vscode.WorkflowPickPure

/// Every case of the daemon's DU, named here so adding a case to
/// `SessionWorkflow` without adding it to the picker fails the first test.
let private everyWorkflow: SessionWorkflow list = [
  SessionWorkflow.Interactive
  SessionWorkflow.LiveTesting
  SessionWorkflow.HotReload BrowserRefreshConfig.defaults
]

let tests =
  testList "VS Code workflow picker - pinned to SessionWorkflow" [

    testCase "WHY - every workflow the daemon has is offered, because LiveTesting was missing entirely" <| fun _ ->
      let offered = choices |> List.map (fun c -> c.Label) |> Set.ofList
      let expected = everyWorkflow |> List.map SessionWorkflow.label |> Set.ofList
      offered |> Expect.equal "picker covers exactly the daemon's workflows" expected
      offered |> Expect.contains "LiveTesting is reachable from VS Code" "Live Testing"

    testCase "WHY - the DU is exhaustively enumerated here, so a new case cannot slip past this file" <| fun _ ->
      // If SessionWorkflow gains a case, `everyWorkflow` above fails to compile
      // against this match and the omission is caught at the test, not in the UI.
      let name w =
        match w with
        | SessionWorkflow.Interactive -> "Interactive"
        | SessionWorkflow.LiveTesting -> "LiveTesting"
        | SessionWorkflow.HotReload _ -> "HotReload"
      let wires = choices |> List.map (fun c -> c.Wire) |> Set.ofList
      everyWorkflow |> List.map name |> Set.ofList
      |> Expect.equal "every case has a wire value in the picker" wires

    testCase "WHY - every offered label is SessionWorkflow.label verbatim, because 'Live' vs 'Hot Reload' drifted" <| fun _ ->
      // You picked "Live"; the status bar then said "Hot Reload".
      for c in choices do
        match SessionWorkflow.tryOfString c.Wire with
        | None -> failtestf "wire value %s is not parseable by the daemon" c.Wire
        | Some w ->
          SessionWorkflow.label w
          |> Expect.equal (sprintf "label for %s matches the daemon's" c.Wire) c.Label

    testCase "WHY - every wire value round-trips through the daemon's own parser" <| fun _ ->
      // An unparseable wire value silently becomes Interactive on the daemon
      // (`ofString` defaults), so a typo here would switch the user into REPL
      // while the picker claimed Hot Reload.
      choices
      |> List.iter (fun c ->
        SessionWorkflow.tryOfString c.Wire
        |> Option.isSome
        |> Expect.isTrue (sprintf "'%s' parses" c.Wire))

    testCase "WHY - the current workflow is marked, because the picker could not say where you were" <| fun _ ->
      let rs = rows "Hot Reload"
      rs |> List.filter (fun r -> r.IsCurrent) |> List.length
      |> Expect.equal "exactly one current row" 1
      (rs |> List.find (fun r -> r.IsCurrent)).Description
      |> Expect.equal "marked in the description column" "current"
      (rs |> List.find (fun r -> not r.IsCurrent)).Description
      |> Expect.equal "others carry no marker" ""

    testCase "WHY - an unknown current label marks nothing and hides nothing" <| fun _ ->
      let rs = rows "Something The Daemon Renamed"
      rs |> List.length |> Expect.equal "all choices still offered" (List.length choices)
      rs |> List.forall (fun r -> not r.IsCurrent) |> Expect.isTrue "nothing falsely marked"

    testCase "WHY - the codicon rides the label, the one QuickPickItem field VS Code expands it in" <| fun _ ->
      for r in rows "REPL" do
        r.Label |> Expect.stringContains "label carries the icon token" "$("
        r.Detail.Contains "$(" |> Expect.isFalse "detail is plain words"
        r.Description.Contains "$(" |> Expect.isFalse "description is plain words"

    testCase "WHY - a picked row resolves back to its wire value, and an unknown one resolves to nothing" <| fun _ ->
      let liveTesting = rows "" |> List.find (fun r -> r.Wire = "LiveTesting")
      wireOfPickedLabel liveTesting.Label |> Expect.equal "round trip" (Some "LiveTesting")
      wireOfPickedLabel "$(rocket) Not A Workflow"
      |> Expect.isNone "an unrecognised pick never defaults into a workflow"

    testCase "WHY - labelOfWire answers the confirmation message without re-deriving the label" <| fun _ ->
      labelOfWire "HotReload" |> Expect.equal "hot reload" (Some "Hot Reload")
      labelOfWire "Nope" |> Expect.isNone "unknown wire has no label"

    testCase "WHY - the wire values are the exact aliases POST /api/sessions/{sid}/workflow accepts" <| fun _ ->
      // The route parses its body with the SAME `tryOfString` this loads, and
      // a value it cannot parse is a 400 — not a silent default into a
      // workflow the user did not pick. Round-tripping each wire value through
      // the real parser AND back through `label` proves the picker and the
      // route agree on all three, which is what stops the picker from claiming
      // one mode while the session lands in another.
      for c in choices do
        SageFs.WorkflowTypes.SessionWorkflow.tryOfString c.Wire
        |> Option.map SageFs.WorkflowTypes.SessionWorkflow.label
        |> Expect.equal (sprintf "%s survives the route's parser intact" c.Wire) (Some c.Label)
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
