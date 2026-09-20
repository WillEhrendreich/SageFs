/// Island A (sagefs-ux-roast.md §11): render the health verdict.
///
/// `SessionHealth.classify` (SageFs.Core/SessionHealth.fs) was, until today,
/// consumed by exactly two machine-facing surfaces (`/api/sessions`,
/// `get_fsi_status`) and rendered by zero human surfaces — a dashboard user
/// watching a Degraded session saw the same green "Ready" as a healthy one
/// (sagefs-ux-roast.md §1). These tests pin the dashboard's new rendering of
/// that verdict, plus the smaller items from the same island (§2.1, §3.4,
/// §3.5, §4.1, §4.3, §7.2), against the RENDERED HTML rather than internal
/// plumbing, so a regression that silently re-hides the verdict fails here.
module SageFs.Tests.DashboardHealthVerdictRenderingTests

open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

module private Fixtures =
  let now = System.DateTime(2026, 9, 20, 12, 0, 0, System.DateTimeKind.Utc)

  let project (name: string) : SageFs.ProjectLoading.ClassifiedProject =
    { Path = sprintf "/repo/%s/%s.fsproj" name name
      Role = SageFs.ProjectLoading.ProjectRole.Executable
      PackageRefs = [] }

  let info (id: string) (status: WorkerProtocol.SessionStatus) (projects: SageFs.ProjectLoading.ClassifiedProject list) : WorkerProtocol.SessionInfo =
    { Id = WorkerProtocol.SessionId.validate id |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
      Name = None
      Projects = projects |> List.map (fun p -> p.Path)
      WorkingDirectory = "/w"
      SolutionRoot = None
      CreatedAt = now.AddMinutes -5.0
      LastActivity = now.AddMinutes -3.0
      Status = WorkerProtocol.SessionLifecycleStatus.ofWorkerReport (WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1; Port = None }) status
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      ActiveProject = None
      ProjectRoles = projects
      App = SageFs.AppRun.AppRunState.NotRunning }

  let nothingLoadedWarmup : SageFs.WarmupContext =
    { SourceFilesScanned = 1
      AssembliesLoaded = []
      NamespacesOpened = []
      FailedOpens = [ { Name = "MyApp"; Kind = SageFs.WarmUp.OpenableKind.Namespace; ErrorMessage = "no namespaces/modules were found to open"; Diagnostics = []; RetryCount = 1; DurationMs = 0.0 } ]
      PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = 5L }
      StartedAt = System.DateTimeOffset.UtcNow }

  let card (health: SessionHealth) (session: WorkerProtocol.SessionInfo) : ParsedSession =
    sessionCardOf now None 0 health session

[<Tests>]
let liveSessionCardsClassificationTests =
  let info = Fixtures.info
  testList "liveSessionCards computes the real SessionHealth verdict per card" [

    testCase "WHY — a Ready session with resolved projects but no fetched warmup context classifies Healthy, not a lie — SessionHealth.classify itself treats absent data as nothing to be suspicious about" (fun () ->
      let cards =
        liveSessionCards Fixtures.now (fun _ -> None) Map.empty (fun _ -> None)
          [ info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready [ Fixtures.project "MyApp" ] ]
      (cards |> List.map (fun c -> c.Health)) |> Expect.equal "quiet default, not an invented alarm" [ SessionHealth.Healthy ])

    testCase "WHY — the VIEWED session's real warmup context flows through and produces Degraded when nothing loaded" (fun () ->
      let target = info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready [ Fixtures.project "MyApp" ]
      let cards =
        liveSessionCards Fixtures.now (fun _ -> None) Map.empty
          (fun sid -> if sid = target.Id then Some Fixtures.nothingLoadedWarmup else None)
          [ target ]
      match cards with
      | [ c ] ->
        match c.Health with
        | SessionHealth.Degraded reason -> reason |> Expect.stringContains "the reason names the actual warmup failure" "MyApp"
        | other -> failtestf "expected Degraded, got %A" other
      | _ -> failtest "expected exactly one card")

    testCase "a Faulted session is Failed regardless of warmup" (fun () ->
      let faulted = { info "0a2b3c4d" WorkerProtocol.SessionStatus.Faulted [] with Status = WorkerProtocol.SessionLifecycleStatus.Faulted (Some "warmup timed out") }
      let cards = liveSessionCards Fixtures.now (fun _ -> None) Map.empty (fun _ -> None) [ faulted ]
      match cards |> List.map (fun c -> c.Health) with
      | [ SessionHealth.Failed reason ] -> reason |> Expect.equal "the fault reason survives into Health" "warmup timed out"
      | other -> failtestf "expected [Failed _], got %A" other)
  ]

[<Tests>]
let sessionCardHealthRenderingTests =
  testList "the sidebar card renders Health WITHOUT expanding anything (roast §1)" [

    testCase "a Healthy card renders no health badge and no reason line — the common case must stay quiet" (fun () ->
      let s = Fixtures.card SessionHealth.Healthy (Fixtures.info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready [])
      let html = renderSessionsForSession "0a2b3c4d" [ s ] false |> renderNode
      html.Contains("session-health-badge") |> Expect.isFalse "no badge for Healthy"
      html.Contains("session-health-reason") |> Expect.isFalse "no reason line for Healthy"
      html.Contains("<details") |> Expect.isFalse "the card itself never needs a disclosure for health")

    testCase "a Degraded card's reason is plain, unconditional HTML — never behind a <details> or hover-only title" (fun () ->
      let s = Fixtures.card (SessionHealth.Degraded "Session is Ready but nothing was loaded: 0 assemblies, 0 namespaces opened.") (Fixtures.info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready [])
      let html = renderSessionsForSession "0a2b3c4d" [ s ] false |> renderNode
      html.Contains("session-health-badge") |> Expect.isTrue "the badge is present"
      html |> Expect.stringContains "the short verdict word appears on the card" "Degraded"
      html |> Expect.stringContains "the FULL reason is inline, not truncated" "0 assemblies, 0 namespaces opened"
      // Reason text must not be reachable only via a wrapping <details> —
      // it must sit in the always-rendered card body.
      let reasonIdx = html.IndexOf("0 assemblies, 0 namespaces opened")
      let detailsIdx = html.LastIndexOf("<details", reasonIdx)
      let detailsCloseIdx = html.IndexOf("</details>", (max detailsIdx 0))
      (detailsIdx < 0 || detailsCloseIdx < reasonIdx) |> Expect.isTrue "the reason is not nested inside a <details> disclosure")

    testCase "a Failed card's reason renders too, distinctly colored from Degraded" (fun () ->
      let s = Fixtures.card (SessionHealth.Failed "Session is stopped.") (Fixtures.info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready [])
      let html = renderSessionsForSession "0a2b3c4d" [ s ] false |> renderNode
      html |> Expect.stringContains "Failed's word appears" "Failed"
      html |> Expect.stringContains "Failed's reason appears" "Session is stopped."
      html |> Expect.stringContains "Failed uses the red token, not the yellow Degraded one" "var(--fg-red)")
  ]

[<Tests>]
let sessionHealthLineTests =
  testList "renderSessionHealthLine — the viewed session's verdict under the daemon health bar (roast §1.4 'statusline')" [

    testCase "Healthy renders an empty line — quiet by design" (fun () ->
      let html = renderSessionHealthLine SessionHealth.Healthy |> renderNode
      html.Contains(DomIds.SessionHealthLine) |> Expect.isTrue "the id is always present so the SSE morph target is stable"
      html.Contains("Health:") |> Expect.isFalse "no visible text for the quiet case")

    testCase "Starting renders an empty line — cannot judge yet, so it must not guess" (fun () ->
      let html = renderSessionHealthLine SessionHealth.Starting |> renderNode
      html.Contains("Health:") |> Expect.isFalse "no visible text while starting")

    testCase "Degraded reuses SessionHealth.describeForAgent verbatim — the dashboard and get_fsi_status can never disagree" (fun () ->
      let html = renderSessionHealthLine (SessionHealth.Degraded "run dotnet build") |> renderNode
      let expected = SessionHealth.describeForAgent (SessionHealth.Degraded "run dotnet build") |> Option.get
      html |> Expect.stringContains "the exact agent-facing wording appears verbatim" expected
      html.Contains("role=\"status\"") |> Expect.isTrue "announced to assistive tech as a live status region")
  ]

[<Tests>]
let evalStatsDuplicateIdRegressionTests =
  testList "the tabline no longer emits a duplicate #eval-stats id (roast §3.4)" [

    testCase "renderMainContent emits exactly one element carrying the eval-stats id" (fun () ->
      let snap =
        { DashboardSnapshot.Version = "0.0.0"
          SessionState = "ready"; SessionId = "t"; WorkingDir = "/w"
          WarmupProgress = ""; WorkflowLabel = "Interactive"
          EvalStats = { Count = 3; AvgMs = 1.0; MinMs = 1.0; MaxMs = 1.0; Sparkline = ""; P50Ms = None; P95Ms = None }
          ThemeName = "default"; ConnectionLabel = None; ConnectionState = DashboardConnectionState.Connected
          HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
          OutputPanel = Elem.div [] []
          SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
          ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
          BindingsPanel = Elem.div [] []; DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []
          DiagnosticsPanel = Elem.div [] []; FilmstripPanel = Elem.div [] []; AlarmPanel = Elem.div [] []
          LiveTestingPanel = Elem.div [] []; FrictionPanel = Elem.div [] []; CohortPanel = Elem.div [] []
          ActiveProject = None; ProjectRoles = []
          App = SageFs.AppRun.AppRunState.NotRunning
          EvalToPixelP50Ms = None; EvalToPixelP99Ms = None }
      let html = renderMainContent snap |> renderNode
      let needle = sprintf "id=\"%s\"" DomIds.EvalStats
      let count =
        let mutable c = 0
        let mutable idx = html.IndexOf(needle)
        while idx >= 0 do
          c <- c + 1
          idx <- html.IndexOf(needle, idx + needle.Length)
        c
      count |> Expect.equal "exactly one #eval-stats — the previous duplicate was invalid HTML and a morph-target hazard" 1)
  ]

[<Tests>]
let workflowBadgeTests =
  testList "the tabline renders the real workflow, read-only (roast §4.1)" [

    testCase "WorkflowLabel appears in the rendered tabline when a session is in play" (fun () ->
      let snap =
        { DashboardSnapshot.Version = "0.0.0"
          SessionState = "ready"; SessionId = "t"; WorkingDir = "/w"
          WarmupProgress = ""; WorkflowLabel = "LiveTesting"
          EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
          ThemeName = "default"; ConnectionLabel = None; ConnectionState = DashboardConnectionState.Connected
          HotReloadPanel = Elem.div [] []; SessionContextPanel = Elem.div [] []
          OutputPanel = Elem.div [] []
          SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
          ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
          BindingsPanel = Elem.div [] []; DaemonHealth = Elem.div [] []; FailureNarrativesPanel = Elem.div [] []
          DiagnosticsPanel = Elem.div [] []; FilmstripPanel = Elem.div [] []; AlarmPanel = Elem.div [] []
          LiveTestingPanel = Elem.div [] []; FrictionPanel = Elem.div [] []; CohortPanel = Elem.div [] []
          ActiveProject = None; ProjectRoles = []
          App = SageFs.AppRun.AppRunState.NotRunning
          EvalToPixelP50Ms = None; EvalToPixelP99Ms = None }
      let html = renderMainContent snap |> renderNode
      html |> Expect.stringContains "the word 'workflow' problem: the ACTUAL workflow now renders" "LiveTesting")
  ]

[<Tests>]
let quickStartAccessibilityTests =
  testList "Quick Start is a real, keyboard-reachable button (roast §2.1)" [

    testCase "the picker card carries role=button, tabindex=0, and a keydown handler" (fun () ->
      let html = renderSessionPickerSorted PreviousSessionSort.Recent [] |> renderNode
      html.Contains("role=\"button\"") |> Expect.isTrue "screen readers must announce this as actionable"
      html.Contains("tabindex=\"0\"") |> Expect.isTrue "must be reachable by Tab"
      html.Contains("data-on:keydown") |> Expect.isTrue "Enter/Space must activate it like a native button")
  ]

[<Tests>]
let hotReloadZeroFilesTests =
  testList "Watch All / Unwatch All with zero discovered files say so instead of doing nothing (roast §4.3)" [

    testCase "zero files: no Watch All / Unwatch All buttons, an explanation instead" (fun () ->
      let html = renderHotReloadPanel "abcd1234" [] 0 |> renderNode
      html.Contains("Watch All") |> Expect.isFalse "must not offer a control that silently does nothing"
      html.Contains("Unwatch All") |> Expect.isFalse "must not offer a control that silently does nothing"
      html |> Expect.stringContains "explains why there is nothing to watch" "No source files were discovered")

    testCase "non-zero files: the buttons are back" (fun () ->
      let html = renderHotReloadPanel "abcd1234" [ {| path = "src/A.fs"; watched = false |} ] 0 |> renderNode
      html |> Expect.stringContains "Watch All present when there is something to watch" "Watch All")
  ]

[<Tests>]
let purgeConfirmationTests =
  testList "Purge — the irreversible teardown level — is gated by a confirmation (roast Island G)" [

    testCase "the purge control's click handler asks for confirmation before posting" (fun () ->
      let s = Fixtures.card SessionHealth.Healthy (Fixtures.info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready [])
      let html = renderSessionsForSession "0a2b3c4d" [ s ] false |> renderNode
      html |> Expect.stringContains "a native confirm() gate sits before the destructive request" "confirm("
      html |> Expect.stringContains "the confirmation names the consequence" "cannot be undone")
  ]

[<Tests>]
let directoryNotFoundRemedyTests =
  testList "\"Directory not found\" carries a remedy, not just a fact (roast §6.6)" [

    testCase "the message names what to do about it" (fun () ->
      // Exercised indirectly: DirectoryNotFound copy lives in Dashboard.fs's
      // route handlers, which need a live HttpContext to call directly. This
      // pins the literal string the roast asked to change, so a regression
      // back to the bare fact (no remedy) is visible in a text diff even
      // though the route itself needs an integration harness to execute.
      let path = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs", "Dashboard.fs")
      let source = System.IO.File.ReadAllText(path)
      source |> Expect.stringContains "Directory not found now tells the user what to do about it" "Directory not found: %s — check the path for typos, or create the directory first.")
  ]
