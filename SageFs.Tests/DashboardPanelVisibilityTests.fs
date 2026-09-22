/// Contextual panel visibility (dashboard-ux-redesign.md, suggested order
/// item 2): hot reload only in the Hot Reload workflow, live testing only
/// when it's on, cohort and lanes only while members are present, friction
/// only when a tab asks for it. `PanelVisibility.decide` is the rule; the
/// render tests push real panels through `apply` and `renderMainContent`.
module SageFs.Tests.DashboardPanelVisibilityTests

open System
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Server
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

let private hotReload = WorkflowTypes.SessionWorkflow.HotReload WorkflowTypes.BrowserRefreshConfig.defaults
let private off = Features.LiveTestActivity.LiveTestActivity.Off
let private running = Features.LiveTestActivity.LiveTestActivity.Running Features.LiveTestActivity.TestTally.empty

let private facts viewed cohort friction : PanelFacts =
  { Viewed = viewed; Cohort = cohort; Friction = friction }

let private replFacts =
  facts (ViewedSession.Viewing(WorkflowTypes.SessionWorkflow.Interactive, off)) CohortPresence.NoActiveMembers FrictionPanelOptIn.NotOptedIn

let private isShown facts panel =
  match PanelVisibility.decide facts panel with
  | PanelVisibility.Shown -> true
  | PanelVisibility.Hidden _ -> false

/// A snapshot carrying the REAL optional panels, so the render tests see the
/// same markup the dashboard sends.
let private snapshotWith (activity: Features.LiveTestActivity.LiveTestActivity) (ledger: LedgerEntry<MemberId> list) : DashboardSnapshot =
  { Version = "0.0.0"
    ConnectionState = DashboardConnectionState.Connected
    SessionState = "Ready"; SessionId = "abcd1234"; WorkingDir = ""
    WarmupProgress = ""; WorkflowLabel = "Interactive"
    EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
    AlarmPanel = Elem.div [] []; DaemonHealth = Elem.div [] []
    FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []
    FilmstripPanel = Elem.div [] []; ThemeName = "default"; ConnectionLabel = None
    HotReloadPanel = renderHotReloadPanel "abcd1234" [] 0
    LiveTestingPanel = renderLiveTestingPanel activity
    SessionContextPanel = Elem.div [] []; OutputPanel = Elem.div [] []
    SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
    ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
    BindingsPanel = Elem.div [] []
    FrictionPanel = Elem.div [ Attr.id DomIds.FrictionPanel ] [ Text.raw "Friction" ]
    CohortPanel = Elem.div [] [ renderCohortPanel (frameOf ledger); renderCohortLanesPanel ledger ]
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
        |> List.map (fun wf -> isShown { replFacts with Viewed = ViewedSession.Viewing(wf, off) } OptionalPanel.HotReload)
        |> Expect.equal "REPL no, Live Testing no, Hot Reload yes" [ false; false; true ]

      testCase "live testing shows only when it's on for the viewed session" <| fun _ ->
        [ off; running; Features.LiveTestActivity.LiveTestActivity.Discovering ]
        |> List.map (fun activity -> isShown { replFacts with Viewed = ViewedSession.Viewing(WorkflowTypes.SessionWorkflow.Interactive, activity) } OptionalPanel.LiveTesting)
        |> Expect.equal "off no, running yes, discovering yes" [ false; true; true ]

      testCase "with no session open, neither session panel shows" <| fun _ ->
        let noSession = { replFacts with Viewed = ViewedSession.NoSession }
        [ isShown noSession OptionalPanel.HotReload; isShown noSession OptionalPanel.LiveTesting ]
        |> Expect.equal "both hidden" [ false; false ]

      testCase "cohort shows only while members are present" <| fun _ ->
        [ isShown replFacts OptionalPanel.Cohort
          isShown { replFacts with Cohort = CohortPresence.ActiveMembers 2 } OptionalPanel.Cohort ]
        |> Expect.equal "no members no, members yes" [ false; true ]

      testCase "friction is off by default and on when the tab asks for it" <| fun _ ->
        [ isShown replFacts OptionalPanel.Friction
          isShown { replFacts with Friction = FrictionPanelOptIn.OptedIn } OptionalPanel.Friction ]
        |> Expect.equal "default no, opted in yes" [ false; true ]

      testCase "every hidden panel says why" <| fun _ ->
        [ OptionalPanel.HotReload; OptionalPanel.LiveTesting; OptionalPanel.Cohort; OptionalPanel.Friction ]
        |> List.forall (fun p ->
          match PanelVisibility.decide replFacts p with
          | PanelVisibility.Hidden reason -> not (String.IsNullOrWhiteSpace reason)
          | PanelVisibility.Shown -> false)
        |> Expect.isTrue "a REPL session hides all four, each with a reason"

      testCase "?panels=friction opts in, anything else doesn't" <| fun _ ->
        [ "friction"; "FRICTION"; "cohort,friction"; ""; "cohort"; null ]
        |> List.map PanelFacts.frictionOptInOfQuery
        |> Expect.equal "only a request naming friction opts in"
          [ FrictionPanelOptIn.OptedIn; FrictionPanelOptIn.OptedIn; FrictionPanelOptIn.OptedIn
            FrictionPanelOptIn.NotOptedIn; FrictionPanelOptIn.NotOptedIn; FrictionPanelOptIn.NotOptedIn ]

      testCase "a cohort whose members all departed has no active members, whatever the ledger holds" <| fun _ ->
        frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer, None); CohortCommand.Depart alice ]
        |> PanelFacts.cohortPresence
        |> Expect.equal "stale rows are not a running cohort" CohortPresence.NoActiveMembers

      testCase "a cohort with a present member is running" <| fun _ ->
        frameAfter [ CohortCommand.Join(alice, JoinableRole.Implementer, None) ]
        |> PanelFacts.cohortPresence
        |> Expect.equal "one member present" (CohortPresence.ActiveMembers 1)
    ]

    testList "what the dashboard renders" [
      // A member holding a claim, so the lanes panel has a span to draw.
      let joined =
        ledgerAfter
          [ CohortCommand.Join(alice, JoinableRole.Implementer, None)
            CohortCommand.AcquireClaim(alice, ClaimScope.File "src/Foo.fs", "editing") ]

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
    ]
  ]
