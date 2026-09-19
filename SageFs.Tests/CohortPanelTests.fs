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
/// into a `CohortFrame` exactly as `CohortOwner.frameOf` does. `snapshots`
/// is `project`'s other input — the per-session Pass/Fail/Stale test
/// outcomes that become the frame's matrix bitplanes (§5.7); `frameAfter`
/// passes none, for tests that don't care about the matrix.
let private frameAfterWith (commands: CohortCommand<MemberId> list) (snapshots: SessionSnapshot<MemberId>[]) : CohortFrame<MemberId> =
  let finalState, seq =
    commands
    |> List.fold
      (fun (state, seq) cmd ->
        match decide epoch [| byte seq |] state cmd with
        | Ok(newState, _, _) -> newState, seq + 1L<ledgerSeq>
        | Error err -> failwithf "unexpected refusal building test frame: %A" err)
      (CohortState.empty (), 0L<ledgerSeq>)
  project { Seq = (if seq = 0L<ledgerSeq> then 0L<ledgerSeq> else seq - 1L<ledgerSeq>); State = finalState } snapshots

let private frameAfter (commands: CohortCommand<MemberId> list) : CohortFrame<MemberId> =
  frameAfterWith commands [||]

let private emptyFrame : CohortFrame<MemberId> =
  project (replayHead []) [||]

[<Tests>]
let cohortPanelTests =
  testList "Cohort dashboard panel" [

    testCase "WHY — an empty cohort renders a tasteful empty state, never a blank or broken panel" <| fun _ ->
      let html = renderCohortPanel emptyFrame |> render
      html |> Expect.stringContains "shows the panel heading with zero members" "Cohort — 0 members"
      html |> Expect.stringContains "explains how to join, instead of rendering nothing" "join_cohort"

    testCase "WHY — a burst of orphaned claims renders a BOUNDED list with an honest overflow line, never 43 rows" <| fun _ ->
      // The live symptom: 43 orphaned claims, hours old, all listed. Each of
      // 43 members holds one claim, then departs (orphaning it).
      let members = [ for i in 1 .. 43 -> MemberId.Minted (sprintf "agent-%d" i) ]
      let frame =
        frameAfter
          [ for m in members -> CohortCommand.Join(m, JoinableRole.Implementer, None)
            for i, m in List.indexed members -> CohortCommand.AcquireClaim(m, ClaimScope.File (sprintf "src/F%d.fs" i), "editing")
            for m in members -> CohortCommand.Depart m ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "the heading still reports the TRUE total" "Claims (43)"
      let cap = Features.CohortBoundedView.rowCap
      (html.Split("fence ").Length - 1)
      |> Expect.equal "only `rowCap` claim rows are drawn" cap
      html |> Expect.stringContains "the overflow line says how many were hidden and why" (sprintf "+%d more claims (%d orphaned)" (43 - cap) (43 - cap))
      html |> Expect.stringContains "members are bounded the same way" (sprintf "+%d more members (%d departed)" (43 - cap) (43 - cap))

    testCase "WHY — get_cohort_status text is bounded the same way, with the true totals and a '+N more' line" <| fun _ ->
      let members = [ for i in 1 .. 43 -> MemberId.Minted (sprintf "agent-%d" i) ]
      let frame =
        frameAfter
          [ for m in members -> CohortCommand.Join(m, JoinableRole.Implementer, None)
            for i, m in List.indexed members -> CohortCommand.AcquireClaim(m, ClaimScope.File (sprintf "src/F%d.fs" i), "editing")
            for m in members -> CohortCommand.Depart m ]
      let text = Features.CohortStatusText.render frame
      let cap = Features.CohortBoundedView.rowCap
      text |> Expect.stringContains "the header keeps the true claim total" "Claims (43):"
      (text.Split("held-by=").Length - 1) |> Expect.equal "only `rowCap` claim rows are listed" cap
      text |> Expect.stringContains "the hidden claims are counted, not dropped" (sprintf "+%d more claims (%d orphaned)" (43 - cap) (43 - cap))

    testCase "WHY — a joined member renders their id and role" <| fun _ ->
      let frame = frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer, None) ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "shows the singular member count in the heading" "Cohort — 1 member"
      html |> Expect.stringContains "shows the member's display id" (MemberId.display alice)
      html |> Expect.stringContains "shows the member's role" "Implementer"

    testCase "WHY — two members pluralize the heading count" <| fun _ ->
      let frame =
        frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer, None); CohortCommand.Join(bob, JoinableRole.Verifier, None) ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "pluralizes members in the heading" "Cohort — 2 members"
      html |> Expect.stringContains "shows the second member's role" "Verifier"

    testCase "WHY — a held claim renders its scope and the holder's display id, not the raw MemberId case" <| fun _ ->
      let frame =
        frameAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
            CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing") ]
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "shows the claimed file path" "src/Foo.fs"
      html |> Expect.stringContains "shows who holds the claim" (sprintf "held by %s" (MemberId.display alice))

    testCase "WHY — the ledger version in the panel is the frame's Version (the ledger Seq), not a fabricated counter" <| fun _ ->
      let frame =
        frameAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
            CohortCommand.AcquireClaim(alice, ClaimScope.Project "src/Foo.fsproj", "working") ]
      frame.Version |> Expect.equal "two applied commands land ledger seq 1 (0-indexed)" 1L<ledgerSeq>
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "renders the ledger version" (sprintf "Ledger v%d" (int64 frame.Version))

    testCase "WHY — renderMainContent always includes the cohort panel, so it can never be a blank/missing sidebar section" <| fun _ ->
      let frame = frameAfter [ CohortCommand.Join(alice, JoinableRole.Observer, None) ]
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

    // §6.5 "Matrix view — a picture, with a text fallback" (Phase 2 item 16).
    testCase "WHY — a frame with projected test outcomes renders the matrix picture and its text fallback" <| fun _ ->
      let snapshots =
        [| { Member = None; SessionId = "integration"; Generation = 1L
             PassingTests = [ Cohort.TestId "t1" ]; FailingTests = [ Cohort.TestId "t2" ]; StaleTests = [] }
           { Member = Some alice; SessionId = "sess-alice"; Generation = 1L
             PassingTests = [ Cohort.TestId "t1" ]; FailingTests = []; StaleTests = [ Cohort.TestId "t2" ] } |]
      let frame = frameAfterWith [ CohortCommand.Join(alice, JoinableRole.Implementer, None) ] snapshots
      frame.TestIds.Length |> Expect.equal "two distinct tests were projected" 2
      let html = renderCohortPanel frame |> render
      html |> Expect.stringContains "carries the cohort-matrix dom id" "cohort-matrix"
      html |> Expect.stringContains "embeds the matrix as an inline PNG data URI, no served route" "data:image/png;base64,"
      html |> Expect.stringContains "labels the text fallback" "Text fallback"
      html
      |> Expect.stringContains
        "the visible text fallback is exactly CohortMatrixRender.toCharGrid's own output"
        (Features.CohortMatrixRender.toCharGrid frame.Pass frame.Fail frame.Stale)

    testCase "WHY — a frame with zero projected test outcomes renders no matrix section at all" <| fun _ ->
      let frame = frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer, None) ]
      frame.TestIds.Length |> Expect.equal "no snapshots were projected" 0
      let html = renderCohortPanel frame |> render
      (html.Contains "cohort-matrix") |> Expect.isFalse "no matrix dom id when there are no test outcomes to show"
      (html.Contains "data:image/png") |> Expect.isFalse "no PNG data URI when there are no test outcomes to show"
  ]
