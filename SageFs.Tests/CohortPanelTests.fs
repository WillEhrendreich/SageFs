/// Cohort dashboard panel (cohort-integration-plan.md Slice 4): pure render
/// tests for `renderCohortPanel`. These drive `Cohort.decide`/`project`
/// directly to build sample frames — no owner, no daemon — the same "pure
/// core, no IO" discipline `CohortOwnerTests.fs` uses, applied to the render
/// side instead of the actor side.
module SageFs.Tests.CohortPanelTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Server
open SageFs.Server.DashboardFragments

let private render (node: XmlNode) = renderNode node

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"

/// Folds `decide` over a fixed command list starting from an empty state —
/// no owner, no ledger, no IO — then projects the resulting `LedgerHead`
/// into a `CohortFrame` exactly as `CohortOwner.frameOf` does.
let private frameAfter (commands: CohortCommand<MemberId> list) : CohortFrame<MemberId> =
  let finalState, seq =
    commands
    |> List.fold
      (fun (state, seq) cmd ->
        match decide epoch [| byte seq |] state cmd with
        | Ok(newState, _, _) -> newState, seq + 1L<ledgerSeq>
        | Error err -> failwithf "unexpected refusal building test frame: %A" err)
      (CohortState.empty (), 0L<ledgerSeq>)
  project { Seq = (if seq = 0L<ledgerSeq> then 0L<ledgerSeq> else seq - 1L<ledgerSeq>); State = finalState } [||]

let private emptyFrame : CohortFrame<MemberId> =
  project (replayHead []) [||]

[<Tests>]
let cohortPanelTests =
  testList "Cohort dashboard panel" [

    testCase "WHY — an empty cohort renders a tasteful empty state, never a blank or broken panel" <| fun _ ->
      let html = renderCohortPanel emptyFrame |> render
      html |> Expect.stringContains "shows the panel heading with zero members" "Cohort — 0 members"
      html |> Expect.stringContains "explains how to join, instead of rendering nothing" "join_cohort"

    testCase "WHY — a joined member renders their id and role" <| fun _ ->
      let frame = frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer) ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "shows the singular member count in the heading" "Cohort — 1 member"
      html |> Expect.stringContains "shows the member's display id" (MemberId.display alice)
      html |> Expect.stringContains "shows the member's role" "Implementer"

    testCase "WHY — two members pluralize the heading count" <| fun _ ->
      let frame =
        frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer); CohortCommand.Join(bob, JoinableRole.Verifier) ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "pluralizes members in the heading" "Cohort — 2 members"
      html |> Expect.stringContains "shows the second member's role" "Verifier"

    testCase "WHY — a held claim renders its scope and the holder's display id, not the raw MemberId case" <| fun _ ->
      let frame =
        frameAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer)
            CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing") ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "shows the claimed file path" "src/Foo.fs"
      html |> Expect.stringContains "shows who holds the claim" (sprintf "held by %s" (MemberId.display alice))

    testCase "WHY — the ledger version in the panel is the frame's Version (the ledger Seq), not a fabricated counter" <| fun _ ->
      let frame =
        frameAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer)
            CohortCommand.AcquireClaim(alice, ClaimScope.Project "src/Foo.fsproj", "working") ]
      frame.Version |> Expect.equal "two applied commands land ledger seq 1 (0-indexed)" 1L<ledgerSeq>
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "renders the ledger version" (sprintf "Ledger v%d" (int64 frame.Version))

    testCase "WHY — renderMainContent always includes the cohort panel, so it can never be a blank/missing sidebar section" <| fun _ ->
      let frame = frameAfter [ CohortCommand.Join(alice, JoinableRole.Observer) ]
      let cohortPanel = renderCohortPanel frame
      let snap : DashboardTypes.DashboardSnapshot =
        { DashboardTypes.DashboardSnapshot.Version = "0.0.0"
          ConnectionState = DashboardTypes.DashboardConnectionState.Connected
          SessionState = "No session"; SessionId = ""; WorkingDir = ""
          WarmupProgress = ""; WorkflowLabel = "Interactive"
          EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
          AlarmPanel = Elem.div [] []; DaemonHealth = Elem.div [] []
          FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []
          FilmstripPanel = Elem.div [] []; ThemeName = "default"; ConnectionLabel = None
          HotReloadPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []
          SessionContextPanel = Elem.div [] []; OutputPanel = Elem.div [] []
          SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
          ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
          BindingsPanel = Elem.div [] []; FrictionPanel = Elem.div [] []
          CohortPanel = cohortPanel
          ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning
          EvalToPixelP50Ms = None; EvalToPixelP99Ms = None }
      let html = renderMainContent snap |> render
      html |> Expect.stringContains "renderMainContent carries the cohort-panel dom id" "cohort-panel"
      html |> Expect.stringContains "renderMainContent carries the joined member's id" (MemberId.display alice)
  ]
