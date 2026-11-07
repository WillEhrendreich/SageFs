/// Contextual panel visibility (dashboard-ux-redesign.md, suggested order
/// item 2): hot reload only in the Hot Reload workflow, live testing only
/// when it's on, cohort and lanes only while members are present, friction
/// only when a tab asks for it. `PanelContext.decide` is the rule; the
/// render tests push real panels through `apply` and `renderMainContent`.
module SageFs.Tests.DashboardPanelVisibilityTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open Falco.Markup
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Server
open SageFs.Server.DashboardPanelContext
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private alice = MemberId.Minted "alice"

/// The ledger a list of commands produces, one minute apart.
let private ledgerAfter (commands: CohortCommand<MemberId> list) : LedgerEntry<MemberId> list =
  commands
  |> List.fold
    (fun (state, entries: LedgerEntry<MemberId> list) cmd ->
      let seq = int64 entries.Length
      let clock = epoch.AddMinutes(float seq)
      match decide clock [| byte seq |] state cmd with
      | Ok(newState, events, _) ->
        newState, entries @ [ { Seq = LanguagePrimitives.Int64WithMeasure seq; Clock = clock; Entropy = [| byte seq |]; Command = cmd; Events = events } ]
      | Error err -> failwithf "unexpected refusal building test ledger: %A" err)
    (CohortState.empty (), [])
  |> snd

let private frameOf (ledger: LedgerEntry<MemberId> list) : CohortFrame<MemberId> =
  project (replayHead ledger) [||]

let private frameAfter commands = ledgerAfter commands |> frameOf

/// A present member holding a claim, so the lanes panel has a span to draw.
let private memberHoldingAClaim =
  ledgerAfter
    [ CohortCommand.Join(alice, JoinableRole.Implementer, None, CohortScope.Machine)
      CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing", CohortScope.Machine) ]

let private hotReload = WorkflowTypes.SessionWorkflow.HotReload WorkflowTypes.BrowserRefreshConfig.defaults
let private off = Features.LiveTestActivity.LiveTestActivity.Off
let private running = Features.LiveTestActivity.LiveTestActivity.Running Features.LiveTestActivity.TestTally.empty

/// Facts for a session whose code was checked and found clean, with no alarm and no failing test.
let private facts viewed cohort friction : PanelFacts =
  { Viewed = viewed; Cohort = cohort; Friction = friction
    Diagnostics = DiagnosticsCheck.Checked 0; Alarms = 0; FailingTests = 0 }

let private replFacts =
  facts (ViewedSession.Viewing(WorkflowTypes.SessionWorkflow.Interactive, off)) CohortPresence.NoActiveMembers FrictionPanelOptIn.NotOptedIn

let private isShown facts panel =
  match PanelContext.decide facts panel with
  | PanelPresence.Shown -> true
  | PanelPresence.Hidden _
  | PanelPresence.Quiet _ -> false

/// A snapshot carrying the REAL optional panels, so the render tests see the
/// same markup the dashboard sends.
let private snapshotWith (activity: Features.LiveTestActivity.LiveTestActivity) (ledger: LedgerEntry<MemberId> list) : DashboardSnapshot =
  { Version = "0.0.0"
    InView = FixtureViews.running
    SessionId = "abcd1234"; WorkingDir = ""
    WarmupProgress = ""; WorkflowLabel = "Interactive"
    EvalStats = FixtureStats.noEvals
    AlarmPanel = renderAlarmBanner []; DaemonHealth = Elem.div [] []
    FailureNarrativesPanel = renderFailureNarratives (FailureNarrativesPanelView.fromNarratives [])
    DiagnosticsPanel = renderCurrentDiagnostics []
    FilmstripPanel = Elem.div [] []; ThemeName = "default"; ConnectionLabel = None
    HotReloadPanel = renderHotReloadPanel "abcd1234" [] 0
    LiveTestingPanel = renderLiveTestingPanel activity
    SessionContextPanel = Elem.div [] []; OutputPanel = Elem.div [] []
    SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
    ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
    BindingsPanel = Elem.div [] []
    FrictionPanel = Elem.div [ Attr.id DomIds.FrictionPanel ] [ Text.raw "Friction" ]
    CohortPanel = Elem.div [] [ renderCohortPanel (frameOf ledger); renderCohortLanesPanel ledger ]
    HygienePanel = Elem.div [] []
    ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning
    EvalToPixelP50Ms = None; EvalToPixelP99Ms = None }

let private idAttr (id: string) = sprintf "id=\"%s\"" id

let private renderedIds (facts: PanelFacts) (snap: DashboardSnapshot) =
  let html = PanelVisibility.apply facts snap |> renderMainContent |> renderNode
  [ DomIds.HotReloadPanel; DomIds.LiveTestingPanel; DomIds.CohortPanel; DomIds.CohortLanes; DomIds.FrictionPanel ]
  |> List.filter (fun id -> html.Contains(idAttr id))

[<Tests>]
let dashboardPanelVisibilityTests =
  testList "Dashboard panel visibility" [

    testList "the rules" [
      testCase "hot reload shows only in the Hot Reload workflow" <| fun _ ->
        [ WorkflowTypes.SessionWorkflow.Interactive; WorkflowTypes.SessionWorkflow.LiveTesting; hotReload ]
        |> List.map (fun wf -> isShown { replFacts with Viewed = ViewedSession.Viewing(wf, off) } ContextPanel.HotReload)
        |> Expect.equal "REPL no, Live Testing no, Hot Reload yes" [ false; false; true ]

      testCase "live testing shows only when it's on for the viewed session" <| fun _ ->
        [ off; running; Features.LiveTestActivity.LiveTestActivity.Discovering ]
        |> List.map (fun activity -> isShown { replFacts with Viewed = ViewedSession.Viewing(WorkflowTypes.SessionWorkflow.Interactive, activity) } ContextPanel.LiveTesting)
        |> Expect.equal "off no, running yes, discovering yes" [ false; true; true ]

      testCase "with no session open, neither session panel shows" <| fun _ ->
        let noSession = { replFacts with Viewed = ViewedSession.NoSession }
        [ isShown noSession ContextPanel.HotReload; isShown noSession ContextPanel.LiveTesting ]
        |> Expect.equal "both hidden" [ false; false ]

      testCase "cohort shows only while members are present" <| fun _ ->
        [ isShown replFacts ContextPanel.Cohort
          isShown { replFacts with Cohort = CohortPresence.ActiveMembers 2 } ContextPanel.Cohort ]
        |> Expect.equal "no members no, members yes" [ false; true ]

      testCase "friction is off by default and on when the tab asks for it" <| fun _ ->
        [ isShown replFacts ContextPanel.Friction
          isShown { replFacts with Friction = FrictionPanelOptIn.OptedIn } ContextPanel.Friction ]
        |> Expect.equal "default no, opted in yes" [ false; true ]

      testCase "WHY — a panel hidden because a workflow is off names the Mode choice that turns it on, because a reason with no next step left reviewers asking how" <| fun _ ->
        for panel, mode in [ ContextPanel.HotReload, "Mode: Hot Reload"; ContextPanel.LiveTesting, "Mode: Live Testing" ] do
          match PanelContext.decide replFacts panel with
          | PanelPresence.Hidden(because, _) -> HiddenBecause.text because |> Expect.stringContains (sprintf "the %A sentence names %s" panel mode) mode
          | PanelPresence.Shown
          | PanelPresence.Quiet _ -> failtestf "a REPL session should hide %A" panel

      testCase "every hidden panel says why" <| fun _ ->
        [ ContextPanel.HotReload; ContextPanel.LiveTesting; ContextPanel.Cohort ]
        |> List.forall (fun p ->
          match PanelContext.decide replFacts p with
          | PanelPresence.Hidden(because, _) -> not (String.IsNullOrWhiteSpace (HiddenBecause.text because))
          | PanelPresence.Shown
          | PanelPresence.Quiet _ -> false)
        |> Expect.isTrue "a REPL session hides the three, each with a reason"

      testCase "WHY — a friction panel nobody asked for is quiet, so no line about how to ask for it is written for users to read" <| fun _ ->
        PanelContext.decide replFacts ContextPanel.Friction
        |> Expect.equal "quiet, rather than hidden with a note for developers" (PanelPresence.Quiet QuietBecause.FrictionNotRequested)

      testCase "a panel hidden because no session is selected says so, because a session can be open and not selected" <| fun _ ->
        let noSession = { replFacts with Viewed = ViewedSession.NoSession }
        match PanelContext.decide noSession ContextPanel.HotReload with
        | PanelPresence.Hidden(because, _) ->
          HiddenBecause.text because |> Expect.stringContains "the reason says the session is not selected" "no session is selected"
        | PanelPresence.Shown
        | PanelPresence.Quiet _ -> failtest "a panel with no session selected is hidden with a reason"

      testCase "?panels=friction opts in, anything else doesn't" <| fun _ ->
        [ "friction"; "FRICTION"; "cohort,friction"; ""; "cohort"; null ]
        |> List.map PanelFacts.frictionOptInOfQuery
        |> Expect.equal "only a request naming friction opts in"
          [ FrictionPanelOptIn.OptedIn; FrictionPanelOptIn.OptedIn; FrictionPanelOptIn.OptedIn
            FrictionPanelOptIn.NotOptedIn; FrictionPanelOptIn.NotOptedIn; FrictionPanelOptIn.NotOptedIn ]

      testCase "a cohort whose members all departed has no active members, whatever the ledger holds" <| fun _ ->
        frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer, None, CohortScope.Machine); CohortCommand.Depart(alice, CohortScope.Machine) ]
        |> PanelFacts.cohortPresence
        |> Expect.equal "stale rows are not a running cohort" CohortPresence.NoActiveMembers

      testCase "a cohort with a present member is running" <| fun _ ->
        frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer, None, CohortScope.Machine) ]
        |> PanelFacts.cohortPresence
        |> Expect.equal "one member present" (CohortPresence.ActiveMembers 1)
    ]

    testList "what the dashboard renders" [
      // A member holding a claim, so the lanes panel has a span to draw.
      let joined =
        ledgerAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer, None, CohortScope.Machine)
            CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing", CohortScope.Machine) ]

      testCase "a REPL session renders none of the optional panels" <| fun _ ->
        renderedIds replFacts (snapshotWith off joined)
        |> Expect.isEmpty "no hot reload, live testing, cohort, lanes or friction panel"

      testCase "switching to Hot Reload renders the hot reload panel and nothing else optional" <| fun _ ->
        renderedIds { replFacts with Viewed = ViewedSession.Viewing(hotReload, off) } (snapshotWith off joined)
        |> Expect.equal "just hot reload" [ DomIds.HotReloadPanel ]

      testCase "live testing on renders its panel" <| fun _ ->
        renderedIds { replFacts with Viewed = ViewedSession.Viewing(WorkflowTypes.SessionWorkflow.LiveTesting, running) } (snapshotWith running joined)
        |> Expect.equal "just live testing" [ DomIds.LiveTestingPanel ]

      testCase "a running cohort renders the cohort panel and its lanes" <| fun _ ->
        renderedIds { replFacts with Cohort = PanelFacts.cohortPresence (frameOf joined) } (snapshotWith off joined)
        |> Expect.equal "cohort and lanes" [ DomIds.CohortPanel; DomIds.CohortLanes ]

      testCase "an opted-in tab renders the friction panel" <| fun _ ->
        renderedIds { replFacts with Friction = FrictionPanelOptIn.OptedIn } (snapshotWith off joined)
        |> Expect.equal "just friction" [ DomIds.FrictionPanel ]

      testCase "a panel that is not relevant says why, where the panel would have been" <| fun _ ->
        let html = PanelVisibility.apply replFacts (snapshotWith off joined) |> renderMainContent |> renderNode
        html |> Expect.stringContains "the hot reload panel says why it is not there" "Hot reload: off"
        html |> Expect.stringContains "the live testing panel says why it is not there" "Live testing: off"
        html |> Expect.stringContains "the cohort panel says why it is not there" "Agent team: none running"
        html |> Expect.stringContains "and the sentence that says why is in the line, a click away" "this is a REPL session"
        html.Contains "reporting has a real endpoint" |> Expect.isFalse "the friction panel nobody asked for says nothing, and certainly no note about a developer URL"
        html.Contains "panels=friction" |> Expect.isFalse "no query string for developers is printed on the page"

      testCase "the reason a panel is hidden names that panel, so the line says which one is missing" <| fun _ ->
        let html = PanelVisibility.apply replFacts (snapshotWith off joined) |> renderMainContent |> renderNode
        html |> Expect.stringContains "the hot reload line" (idAttr DomIds.HiddenReasonHotReload)
        html |> Expect.stringContains "the live testing line" (idAttr DomIds.HiddenReasonLiveTesting)
        html |> Expect.stringContains "the cohort line" (idAttr DomIds.HiddenReasonCohort)
        html.Contains(idAttr DomIds.HiddenReasonFriction) |> Expect.isFalse "and the friction panel, which is not offered, leaves no line"

      testCase "a hidden live testing panel offers the way to turn it on" <| fun _ ->
        let html = PanelVisibility.apply replFacts (snapshotWith off joined) |> renderMainContent |> renderNode
        let start = html.IndexOf(idAttr DomIds.HiddenReasonLiveTesting)
        (start >= 0) |> Expect.isTrue "the live testing reason line is on the page"
        let line = html.Substring(start, html.IndexOf("</details>", start) - start)
        line |> Expect.stringContains "the line posts to the enable route, inside the line" "/dashboard/live-testing/enable"
        line |> Expect.stringContains "and the button says what it does" "Turn on"

      testCase "a panel that is shown leaves no reason line behind" <| fun _ ->
        let html =
          PanelVisibility.apply { replFacts with Viewed = ViewedSession.Viewing(hotReload, off) } (snapshotWith off joined)
          |> renderMainContent
          |> renderNode
        html |> Expect.stringContains "the hot reload panel is there" (idAttr DomIds.HotReloadPanel)
        html.Contains(idAttr DomIds.HiddenReasonHotReload)
        |> Expect.isFalse "and it does not say why it is missing"

      testCase "a hot reload panel hidden in a REPL session names the control that brings it back" <| fun _ ->
        let html = PanelVisibility.apply replFacts (snapshotWith off joined) |> renderMainContent |> renderNode
        let start = html.IndexOf(idAttr DomIds.HiddenReasonHotReload)
        let line = html.Substring(start, html.IndexOf("</details>", start) - start)
        line |> Expect.stringContains "the next step is the workflow switcher" "the workflow switcher in the header"
        line.Contains "<button" |> Expect.isFalse "a control that exists elsewhere on the page is named, not duplicated as a button"

      testCase "turning live testing on changes the rendered main content, so the push guard that skips an unchanged page cannot swallow it" <| fun _ ->
        let viewing activity = { replFacts with Viewed = ViewedSession.Viewing(WorkflowTypes.SessionWorkflow.Interactive, activity) }
        let pageFor activity =
          PanelVisibility.apply (viewing activity) (snapshotWith activity joined) |> renderMainContent |> renderNode
        pageFor running
        |> Expect.notEqual "off and running are different pages" (pageFor off)
    ]

    testList "one decision for every panel" [
      // The whole cross product, enumerated: 10 viewed states x 3 cohorts x 2 friction x 3 diagnostics x 3 alarm counts x 3 failure counts.
      let activities = [ off; Features.LiveTestActivity.LiveTestActivity.Discovering; running ]
      let workflows = [ WorkflowTypes.SessionWorkflow.Interactive; WorkflowTypes.SessionWorkflow.LiveTesting; hotReload ]
      let viewedStates =
        ViewedSession.NoSession :: [ for wf in workflows do for activity in activities -> ViewedSession.Viewing(wf, activity) ]
      let allFacts : PanelFacts list =
        [ for viewed in viewedStates do
            for cohort in [ CohortPresence.NoActiveMembers; CohortPresence.OnlyHistory(1, 0); CohortPresence.ActiveMembers 2 ] do
              for friction in [ FrictionPanelOptIn.NotOptedIn; FrictionPanelOptIn.OptedIn ] do
                for diagnostics in [ DiagnosticsCheck.NotChecked; DiagnosticsCheck.Checked 0; DiagnosticsCheck.Checked 3 ] do
                  for alarms in [ 0; 1; 5 ] do
                    for failing in [ 0; 1; 5 ] ->
                      { Viewed = viewed; Cohort = cohort; Friction = friction
                        Diagnostics = diagnostics; Alarms = alarms; FailingTests = failing } ]

      testCase "the enumeration is the whole cross product" <| fun _ ->
        allFacts |> Expect.hasLength "10 x 3 x 2 x 3 x 3 x 3" 1620

      testCase "every state of the facts gives each panel exactly one outcome, and a reason is always a sentence" <| fun _ ->
        allFacts
        |> List.iter (fun facts ->
          let decided = PanelContext.decisions facts
          decided |> List.map fst |> Expect.equal "one entry per panel, in order" ContextPanel.all
          decided
          |> List.iter (fun (panel, presence) ->
            match presence with
            | PanelPresence.Shown -> ()
            | PanelPresence.Hidden(because, _) ->
              match String.IsNullOrWhiteSpace (HiddenBecause.text because) with
              | true -> failtestf "%A hidden with a blank sentence for %A" panel facts
              | false -> ()
            | PanelPresence.Quiet because ->
              match String.IsNullOrWhiteSpace (QuietBecause.text because) with
              | true -> failtestf "%A quiet with a blank sentence for %A" panel facts
              | false -> ()))

      testCase "ContextPanel.all lists every case the type has" <| fun _ ->
        let cases = Microsoft.FSharp.Reflection.FSharpType.GetUnionCases typeof<ContextPanel>
        ContextPanel.all |> Expect.hasLength "one entry per union case" cases.Length

      testCase "what the page draws agrees with what was decided, over every state of the facts" <| fun _ ->
        // Sampled across the whole enumeration (every 7th state, which is coprime with each factor), because each one renders a full page.
        allFacts
        |> List.indexed
        |> List.filter (fun (i, _) -> i % 7 = 0)
        |> List.iter (fun (_, facts) ->
          let activity =
            match facts.Viewed with
            | ViewedSession.Viewing(_, activity) -> activity
            | ViewedSession.NoSession -> off
          let html = PanelVisibility.apply facts (snapshotWith activity memberHoldingAClaim) |> renderMainContent |> renderNode
          PanelContext.decisions facts
          |> List.iter (fun (panel, presence) ->
            let hasReason = html.Contains(idAttr (PanelVisibility.reasonId panel))
            match presence with
            | PanelPresence.Hidden _ -> hasReason |> Expect.isTrue (sprintf "%A is hidden, so its reason line is on the page" panel)
            | PanelPresence.Shown
            | PanelPresence.Quiet _ -> hasReason |> Expect.isFalse (sprintf "%A is not hidden, so no reason line stands in for it" panel)))

      testProperty "an alarm count above zero shows the banner and zero is quiet" <|
        Prop.forAll (Arb.fromGen (Gen.choose (0, 999))) (fun alarms ->
          match PanelContext.decide { replFacts with Alarms = alarms } ContextPanel.Alarms, alarms with
          | PanelPresence.Quiet QuietBecause.NoAlarms, 0 -> true
          | PanelPresence.Shown, n -> n > 0
          | _ -> false)
    ]

    testList "a panel that is on the page but empty says the true reason" [
      testCase "WHY — an open session whose worker did not answer is not described as having no active session" <| fun _ ->
        let html = renderHotReloadEmpty EmptyBecause.HotReloadStateUnanswered |> renderNode
        html |> Expect.stringContains "it says the worker did not report" "did not report its hot reload state"
        html.Contains "No session is selected" |> Expect.isFalse "and does not claim there is no session"

      testCase "no session in view says so, in both panels" <| fun _ ->
        renderHotReloadEmpty EmptyBecause.NoSessionInView |> renderNode
        |> Expect.stringContains "hot reload" "No session is selected"
        renderSessionContextEmpty EmptyBecause.NoSessionInView |> renderNode
        |> Expect.stringContains "session context" "No session is selected"

      testCase "a session whose warmup has not reported says that, rather than 'No session context'" <| fun _ ->
        let html = renderSessionContextEmpty EmptyBecause.WarmupNotReported |> renderNode
        html |> Expect.stringContains "the warmup reason" "has not reported its warmup"
        html.Contains "No session context" |> Expect.isFalse "the bare label is gone"

      testCase "each reason is a different sentence" <| fun _ ->
        [ EmptyBecause.NoSessionInView; EmptyBecause.HotReloadStateUnanswered; EmptyBecause.WarmupNotReported ]
        |> List.map EmptyBecause.text
        |> List.distinct
        |> Expect.hasLength "three reasons, three sentences" 3
    ]

    testList "decide first, then build" [
      let reads (optIn: FrictionPanelOptIn) : Threading.Tasks.Task<int> =
        task {
          let storeReads = ref 0
          let session =
            DashboardLandingLiveTestingTests.liveSession (DashboardLandingLiveTestingTests.sessionId "0a000001") "/w/first"
          let queries =
            { DashboardRenderGateTests.mkQueries (ref 0) [ session ] with
                GetFrictionStore = fun () ->
                  storeReads.Value <- storeReads.Value + 1
                  Threading.Tasks.Task.FromResult None }
          let! _ = Dashboard.buildDashboardSnapshotWithSessions queries (DashboardRenderGateTests.mkInfra ()) session.Id session.Id "" "default" None [ session ] optIn
          return storeReads.Value
        }

      testTask "WHY — a tab that did not ask for the friction panel never reads the friction store, because the panel was built and thrown away" {
        let! count = reads FrictionPanelOptIn.NotOptedIn
        count |> Expect.equal "no store read" 0
      }

      testTask "a tab that asked for it does read the store" {
        let! count = reads FrictionPanelOptIn.OptedIn
        count |> Expect.equal "one store read" 1
      }
    ]

    testList "a hidden panel says what to do next" [
      testCase "live testing off offers Turn on, posting to the enable route the dashboard serves" <| fun _ ->
        PanelContext.decide replFacts ContextPanel.LiveTesting
        |> Expect.equal "a button that posts to the enable route"
          (PanelPresence.Hidden(HiddenBecause.LiveTestingOff, NextStep.PostAction("Turn on", DashboardRoutes.LiveTestingEnable)))

      testCase "the panel's own toggle and the reason line's button post to routes that are literals in one module" <| fun _ ->
        let panelHtml = renderLiveTestingPanel off |> renderNode
        panelHtml |> Expect.stringContains "the panel's Enable posts to the same literal" DashboardRoutes.LiveTestingEnable
        let running = renderLiveTestingPanel running |> renderNode
        running |> Expect.stringContains "and its Disable posts to the disable literal" DashboardRoutes.LiveTestingDisable

      testCase "every route a next step posts to is a route the daemon registers" <| fun _ ->
        let registered =
          EndpointContractTests.RouteScan.daemonSources ()
          |> List.collect (IO.File.ReadAllText >> EndpointContractTests.RouteScan.routesIn)
          |> List.map (fun route -> route.Path)
          |> Set.ofList
        [ DashboardRoutes.LiveTestingEnable; DashboardRoutes.LiveTestingDisable ]
        |> List.filter (fun route -> not (registered.Contains route))
        |> Expect.isEmpty "each literal in DashboardRoutes is registered under that exact path"

      testCase "no session selected offers nothing to click, because the session picker is the next step and it is already on the page" <| fun _ ->
        let noSession = { replFacts with Viewed = ViewedSession.NoSession }
        [ ContextPanel.HotReload; ContextPanel.LiveTesting ]
        |> List.map (fun panel ->
          match PanelContext.decide noSession panel with
          | PanelPresence.Hidden(_, next) -> next
          | PanelPresence.Shown
          | PanelPresence.Quiet _ -> failtestf "%A should be hidden with no session selected" panel)
        |> Expect.equal "no next step for either" [ NextStep.NoNextStep; NextStep.NoNextStep ]
    ]

    testList "diagnostics that were never checked do not read as clean" [
      testCase "with no session viewed, diagnostics say they have not been checked" <| fun _ ->
        let notChecked = { replFacts with Viewed = ViewedSession.NoSession; Diagnostics = DiagnosticsCheck.NotChecked }
        match PanelContext.decide notChecked ContextPanel.Diagnostics with
        | PanelPresence.Hidden(HiddenBecause.NotCheckedYet _ as because, _) ->
          HiddenBecause.text because |> Expect.stringContains "the sentence says so" "not been checked"
        | other -> failtestf "expected NotCheckedYet, got %A" other

      testCase "a check that found nothing is quiet, which is a different decision from not having checked" <| fun _ ->
        PanelContext.decide { replFacts with Diagnostics = DiagnosticsCheck.Checked 0 } ContextPanel.Diagnostics
        |> Expect.equal "checked and clean" (PanelPresence.Quiet QuietBecause.NoDiagnostics)

      testCase "a check that found something shows the panel" <| fun _ ->
        PanelContext.decide { replFacts with Diagnostics = DiagnosticsCheck.Checked 2 } ContextPanel.Diagnostics
        |> Expect.equal "checked, with findings" PanelPresence.Shown

      testCase "the page says diagnostics were not checked, and says nothing when they were checked and clean" <| fun _ ->
        let page facts = PanelVisibility.apply facts (snapshotWith off []) |> renderMainContent |> renderNode
        page { replFacts with Diagnostics = DiagnosticsCheck.NotChecked }
        |> Expect.stringContains "a line for the unchecked state" (idAttr DomIds.HiddenReasonDiagnostics)
        (page { replFacts with Diagnostics = DiagnosticsCheck.Checked 0 }).Contains(idAttr DomIds.HiddenReasonDiagnostics)
        |> Expect.isFalse "no line for the clean state"
    ]

    testList "a cohort holding only history" [
      let orphaned =
        [ CohortCommand.Join(alice, JoinableRole.Implementer, None, CohortScope.Machine)
          CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing", CohortScope.Machine)
          CohortCommand.Depart(alice, CohortScope.Machine) ]

      testCase "a claim whose holder left is history another agent can trip on" <| fun _ ->
        frameAfter orphaned
        |> PanelFacts.cohortPresence
        |> Expect.equal "one orphaned claim, no landings" (CohortPresence.OnlyHistory(1, 0))

      testCase "the line says no cohort is running and that the claim remains" <| fun _ ->
        CohortPresence.historyLine 1 0
        |> Expect.equal "singular" "no cohort is running; 1 orphaned claim remains"
        CohortPresence.historyLine 2 1
        |> Expect.equal "plural, with a landing" "no cohort is running; 2 orphaned claims remain, 1 landing on record"

      testCase "the cohort panel stays on the page with the orphaned claim in it, headed by that line" <| fun _ ->
        let ledger = ledgerAfter orphaned
        let facts = { replFacts with Cohort = PanelFacts.cohortPresence (frameOf ledger) }
        let html = PanelVisibility.apply facts (snapshotWith off ledger) |> renderMainContent |> renderNode
        html |> Expect.stringContains "the panel is shown" (idAttr DomIds.CohortPanel)
        html |> Expect.stringContains "its header says no cohort is running and a claim remains" "no cohort is running; 1 orphaned claim remains"
        html |> Expect.stringContains "and the claim is listed" "src/Foo.fs"
        html.Contains(idAttr DomIds.HiddenReasonCohort) |> Expect.isFalse "so there is no line saying nothing is there"
    ]

    testList "the rules have teeth" [
      let decideBoth : (PanelFacts * ContextPanel) -> PanelPresence = fun (facts, panel) -> PanelContext.decide facts panel
      let mutant name description apply : MutationTestingFramework.Mutant<(PanelFacts * ContextPanel) -> PanelPresence> =
        { Name = name; Description = description; Apply = apply }
      let isHidden because next presence = presence = PanelPresence.Hidden(because, next)
      let turnOn = NextStep.PostAction("Turn on", DashboardRoutes.LiveTestingEnable)

      MutationTestingFramework.detectsOutputMutant
        (mutant "swapped reasons" "live testing off reports the hot reload reason" (fun real input ->
          match real input with
          | PanelPresence.Hidden(HiddenBecause.LiveTestingOff, next) -> PanelPresence.Hidden(HiddenBecause.SessionInReplMode, next)
          | other -> other))
        (replFacts, ContextPanel.LiveTesting)
        decideBoth
        (isHidden HiddenBecause.LiveTestingOff turnOn)

      MutationTestingFramework.detectsOutputMutant
        (mutant "dropped next step" "a hidden panel forgets its button" (fun real input ->
          match real input with
          | PanelPresence.Hidden(because, _) -> PanelPresence.Hidden(because, NextStep.NoNextStep)
          | other -> other))
        (replFacts, ContextPanel.LiveTesting)
        decideBoth
        (isHidden HiddenBecause.LiveTestingOff turnOn)

      MutationTestingFramework.detectsOutputMutant
        (mutant "history hidden" "a cohort with only an orphaned claim is hidden like an empty one" (fun real input ->
          match real input with
          | PanelPresence.Shown -> PanelPresence.Hidden(HiddenBecause.NoCohortRunning, NextStep.NoNextStep)
          | other -> other))
        ({ replFacts with Cohort = CohortPresence.OnlyHistory(1, 0) }, ContextPanel.Cohort)
        decideBoth
        (fun presence -> presence = PanelPresence.Shown)

      MutationTestingFramework.detectsOutputMutant
        (mutant "unchecked reads clean" "diagnostics that were never checked are quiet" (fun real input ->
          match real input with
          | PanelPresence.Hidden(HiddenBecause.NotCheckedYet _, _) -> PanelPresence.Quiet QuietBecause.NoDiagnostics
          | other -> other))
        ({ replFacts with Viewed = ViewedSession.NoSession; Diagnostics = DiagnosticsCheck.NotChecked }, ContextPanel.Diagnostics)
        decideBoth
        (fun presence -> match presence with PanelPresence.Hidden(HiddenBecause.NotCheckedYet _, _) -> true | _ -> false)

      MutationTestingFramework.detectsOutputMutant
        (mutant "quiet becomes shown" "a clean check shows an empty diagnostics panel" (fun real input ->
          match real input with
          | PanelPresence.Quiet _ -> PanelPresence.Shown
          | other -> other))
        (replFacts, ContextPanel.Diagnostics)
        decideBoth
        (fun presence -> presence = PanelPresence.Quiet QuietBecause.NoDiagnostics)
    ]
  ]
