module SageFs.Tests.DashboardParsingTests

open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Features.BindingExplorer
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open System.Text.RegularExpressions

/// Dashboard output/diagnostics parsers — mirrors Dashboard.fs logic.
/// Tests validate the regex-based parsing produces correct structured data.
module DashboardParsing =
  let parseOutputLines (content: string) =
    let tsKindRegex = Regex(@"^\[(\d{2}:\d{2}:\d{2})\]\s*\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
    let kindOnlyRegex = Regex(@"^\[(\w+)\]\s*(.*)", RegexOptions.Singleline)
    content.Split('\n')
    |> Array.filter (fun (l: string) -> l.Length > 0)
    |> Array.map (fun (l: string) ->
      let m = tsKindRegex.Match(l)
      if m.Success then
        let kind =
          match m.Groups.[2].Value.ToLowerInvariant() with
          | "result" -> "Result"
          | "error" -> "Error"
          | "info" -> "Info"
          | _ -> "System"
        Some m.Groups.[1].Value, kind, m.Groups.[3].Value
      else
        let m2 = kindOnlyRegex.Match(l)
        if m2.Success then
          let kind =
            match m2.Groups.[1].Value.ToLowerInvariant() with
            | "result" -> "Result"
            | "error" -> "Error"
            | "info" -> "Info"
            | _ -> "System"
          None, kind, m2.Groups.[2].Value
        else
          None, "Result", l)
    |> Array.toList

  let parseDiagLines (content: string) =
    let diagRegex = Regex(@"^\[(\w+)\]\s*\((\d+),(\d+)\)\s*(.*)")
    content.Split('\n')
    |> Array.filter (fun (l: string) -> l.Length > 0)
    |> Array.map (fun (l: string) ->
      let m = diagRegex.Match(l)
      if m.Success then
        let severity = if m.Groups.[1].Value = "error" then "Error" else "Warning"
        let line = int m.Groups.[2].Value
        let col = int m.Groups.[3].Value
        let message = m.Groups.[4].Value
        severity, message, line, col
      else
        let severity = if l.Contains("[error]") then "Error" else "Warning"
        severity, l, 0, 0)
    |> Array.toList

[<Tests>]
let tests = testList "Dashboard parsing" [
  testCase "output: parses timestamped result line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[14:30:05] [result] val x: int = 42"
    result |> Expect.equal "extract timestamp, kind, text" [(Some "14:30:05", "Result", "val x: int = 42")])

  testCase "output: parses result line without timestamp" (fun () ->
    let result = DashboardParsing.parseOutputLines "[result] val x: int = 42"
    result |> Expect.equal "fallback without timestamp" [(None, "Result", "val x: int = 42")])

  testCase "output: parses timestamped error line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[09:15:00] [error] Something went wrong"
    result |> Expect.equal "extract error kind with timestamp" [(Some "09:15:00", "Error", "Something went wrong")])

  testCase "output: parses info line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[12:00:00] [info] Loading..."
    result |> Expect.equal "extract info kind" [(Some "12:00:00", "Info", "Loading...")])

  testCase "output: parses system line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[08:00:00] [system] let x = 1"
    result |> Expect.equal "extract system kind" [(Some "08:00:00", "System", "let x = 1")])

  testCase "output: non-prefixed line defaults to Result" (fun () ->
    let result = DashboardParsing.parseOutputLines "plain text"
    result |> Expect.equal "fallback to Result" [(None, "Result", "plain text")])

  testCase "output: skips empty lines" (fun () ->
    let lines = DashboardParsing.parseOutputLines "[14:30:05] [result] a\n\n[14:30:06] [error] b"
    lines.Length |> Expect.equal "should skip empty lines" 2)

  testCase "output: multiple timestamped lines" (fun () ->
    let result = DashboardParsing.parseOutputLines "[14:30:05] [result] a\n[14:30:06] [error] b\n[14:30:07] [info] c"
    result.Length |> Expect.equal "should have 3 lines" 3
    let (ts1, k1, _) = result.[0]
    (ts1, k1) |> Expect.equal "first line" (Some "14:30:05", "Result")
    let (ts2, k2, _) = result.[1]
    (ts2, k2) |> Expect.equal "second line" (Some "14:30:06", "Error")
    let (ts3, k3, _) = result.[2]
    (ts3, k3) |> Expect.equal "third line" (Some "14:30:07", "Info"))

  testCase "diag: extracts line and col from error" (fun () ->
    let result = DashboardParsing.parseDiagLines "[error] (5,12) Type not defined"
    result |> Expect.equal "extract severity, msg, line, col" [("Error", "Type not defined", 5, 12)])

  testCase "diag: extracts line and col from warning" (fun () ->
    let result = DashboardParsing.parseDiagLines "[warning] (1,0) Value unused"
    result |> Expect.equal "parse warning" [("Warning", "Value unused", 1, 0)])

  testCase "diag: multiple diagnostics" (fun () ->
    let result = DashboardParsing.parseDiagLines "[error] (5,12) Bad\n[warning] (10,3) Suspicious"
    result.Length |> Expect.equal "should have 2 diagnostics" 2
    let (s1, _, l1, c1) = result.[0]
    (s1, l1, c1) |> Expect.equal "first diagnostic" ("Error", 5, 12)
    let (s2, _, l2, c2) = result.[1]
    (s2, l2, c2) |> Expect.equal "second diagnostic" ("Warning", 10, 3))

  testCase "diag: fallback for non-standard format" (fun () ->
    let result = DashboardParsing.parseDiagLines "some random diagnostic"
    result |> Expect.equal "fallback to Warning 0,0" [("Warning", "some random diagnostic", 0, 0)])

]

/// Sidebar cards are built from typed session state. They used to be
/// regex-parsed back out of the TUI's text and then status-overridden.
module SidebarCards =
  let now = System.DateTime(2026, 9, 11, 12, 0, 0, System.DateTimeKind.Utc)

  let info (id: string) (status: WorkerProtocol.SessionStatus) (projects: string list) : WorkerProtocol.SessionInfo =
    { Id = WorkerProtocol.SessionId.validate id |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
      Name = None; Projects = projects; WorkingDirectory = "/w"; SolutionRoot = None
      CreatedAt = now.AddMinutes -5.0; LastActivity = now.AddMinutes -3.0
      Status = WorkerProtocol.SessionLifecycleStatus.ofWorkerReport (WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1; Port = None }) status
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      ActiveProject = None; ProjectRoles = []; App = SageFs.AppRun.AppRunState.NotRunning }

  let card (session: WorkerProtocol.SessionInfo) = sessionCardOf now None 0 session

[<Tests>]
let sidebarCardTests =
  let info = SidebarCards.info
  testList "Sidebar cards from typed state" [
    testCase "WHY — sessionCardOf — a project list containing ')' keeps its card, because the TUI regex silently dropped such sessions from the sidebar" (fun () ->
      let c = SidebarCards.card (info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready [ "/w/Weird (v2).fsproj" ])
      c.ProjectsText |> Expect.equal "the project name survives intact" "(Weird (v2))")

    testCase "Ready and Evaluating sessions are running" (fun () ->
      (SidebarCards.card (info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready [])).Status |> Expect.equal "Ready = running" SessionDisplayStatus.Running
      (SidebarCards.card (info "0a2b3c4d" WorkerProtocol.SessionStatus.Evaluating [])).Status |> Expect.equal "Evaluating = running" SessionDisplayStatus.Running)

    testCase "WHY — sessionCardOf — a faulted session shows as faulted with its reason, because the regex fallback showed errored sessions as running" (fun () ->
      let faulted = { info "0a2b3c4d" WorkerProtocol.SessionStatus.Faulted [] with Status = WorkerProtocol.SessionLifecycleStatus.Faulted (Some "warmup timed out") }
      let c = SidebarCards.card faulted
      c.Status |> Expect.equal "Faulted = faulted" (SessionDisplayStatus.Faulted "warmup timed out")
      c.StatusMessage |> Expect.equal "the reason is shown" (Some "warmup timed out"))

    testCase "WHY — sessionCardOf — a running session can never show a stale fault reason, because Ready structurally carries no fault reason at all" (fun () ->
      let recovered = info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready []
      (SidebarCards.card recovered).StatusMessage |> Expect.isNone "no message on a running card")

    testCase "a starting session shows its warmup progress" (fun () ->
      let c = sessionCardOf SidebarCards.now (Some "[3/10] open System") 0 (info "0a2b3c4d" WorkerProtocol.SessionStatus.Starting [])
      c.Status |> Expect.equal "Starting = starting" SessionDisplayStatus.Starting
      c.StatusMessage |> Expect.equal "warmup progress is the message" (Some "[3/10] open System"))

    testCase "uptime and last activity use the sidebar's words" (fun () ->
      let c = SidebarCards.card (info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready [])
      c.Uptime |> Expect.equal "up five minutes" "5m"
      c.LastActivity |> Expect.equal "last active three minutes ago" "3m ago")
  ]

/// Tests for error formatting in eval handler (Bug #6)
module ErrorFormatting =
  let formatEvalResult (result: string) =
    let isError =
      result.StartsWith("Error:") || result.Contains("Evaluation failed")
    let displayResult =
      if isError then
        result
          .Replace("FSharp.Compiler.Interactive.Shell+FsiCompilationException: ", "")
          .Replace("Evaluation failed: ", "⚠ ")
      else result
    let cssClass =
      if isError then "output-line output-error"
      else "output-line output-result"
    displayResult, cssClass

[<Tests>]
let errorFormattingTests =
  testList "Error formatting (Bug #6)" [
    testCase "strips FsiCompilationException name" (fun () ->
      let input = "Error: Evaluation failed: FSharp.Compiler.Interactive.Shell+FsiCompilationException: The value 'x' is not defined"
      let display, css = ErrorFormatting.formatEvalResult input
      (display.Contains("FsiCompilationException")) |> Expect.isFalse "should strip exception name"
      display |> Expect.stringContains "should have warning prefix" "⚠"
      css |> Expect.equal "error CSS class" "output-line output-error")

    testCase "clean error gets warning prefix" (fun () ->
      let input = "Evaluation failed: syntax error"
      let display, _ = ErrorFormatting.formatEvalResult input
      display |> Expect.equal "replaces prefix with warning emoji" "⚠ syntax error")

    testCase "success result unchanged" (fun () ->
      let input = "val it: int = 42"
      let display, css = ErrorFormatting.formatEvalResult input
      display |> Expect.equal "result text unchanged" input
      css |> Expect.equal "success CSS class" "output-line output-result")

    testCase "Error: prefix detected" (fun () ->
      let input = "Error: something went wrong"
      let _, css = ErrorFormatting.formatEvalResult input
      css |> Expect.equal "Error: triggers error styling" "output-line output-error")
  ]

/// Tests for output content-hash dedup (Bug #5)
module OutputDedup =
  type Region = { Id: string; Content: string }

  let filterDuplicateOutput (lastHash: int) (regions: Region list) =
    let outputRegion = regions |> List.tryFind (fun r -> r.Id = "output")
    let outputHash =
      outputRegion
      |> Option.map (fun r -> r.Content.GetHashCode())
      |> Option.defaultValue 0
    let filtered =
      if outputHash = lastHash && outputHash <> 0
      then regions |> List.filter (fun r -> r.Id <> "output")
      else regions
    filtered, outputHash

[<Tests>]
let outputDedupTests =
  let filter = OutputDedup.filterDuplicateOutput
  let mkRegion id content : OutputDedup.Region = { Id = id; Content = content }
  testList "Output dedup (Bug #5)" [
    testCase "first push includes output (lastHash=0)" (fun () ->
      let regions = [mkRegion "output" "hello"; mkRegion "sessions" "s1"]
      let filtered, hash = filter 0 regions
      filtered.Length |> Expect.equal "all regions included on first push" 2
      hash |> Expect.notEqual "hash is non-zero" 0)

    testCase "identical content filtered out" (fun () ->
      let regions = [mkRegion "output" "hello"; mkRegion "sessions" "s1"]
      let _, hash1 = filter 0 regions
      let filtered, _ = filter hash1 regions
      filtered.Length |> Expect.equal "output region filtered" 1
      (List.head filtered).Id |> Expect.equal "only non-output remains" "sessions")

    testCase "changed content included" (fun () ->
      let regions1 = [mkRegion "output" "hello"]
      let _, hash1 = filter 0 regions1
      let regions2 = [mkRegion "output" "world"]
      let filtered, _ = filter hash1 regions2
      filtered.Length |> Expect.equal "new output included" 1)

    testCase "no output region passes through" (fun () ->
      let regions = [mkRegion "sessions" "s1"]
      let filtered, hash = filter 0 regions
      filtered.Length |> Expect.equal "all regions pass" 1
      hash |> Expect.equal "hash stays 0" 0)
  ]

/// Tests for connection count display formatting (Bug #11)
module ConnectionCountDisplay =
  let formatConnectionCounts (total: int) (allCounts: SageFs.ConnectionCounts) =
    let parts =
      [ if allCounts.Browsers > 0 then sprintf "🌐 %d" allCounts.Browsers
        if allCounts.McpAgents > 0 then sprintf "🤖 %d" allCounts.McpAgents
        if allCounts.Terminals > 0 then sprintf "💻 %d" allCounts.Terminals ]
    if parts.IsEmpty then sprintf "%d connected" total
    else System.String.Join(" ", parts)

[<Tests>]
let connectionCountTests =
  let fmt = ConnectionCountDisplay.formatConnectionCounts
  let mk b m t : SageFs.ConnectionCounts = { Browsers = b; McpAgents = m; Terminals = t }
  testList "Connection count display (Bug #11)" [
    testCase "shows icon breakdown when counts available" (fun () ->
      let label = fmt 3 (mk 1 1 1)
      label |> Expect.stringContains "shows browser icon" "🌐 1"
      label |> Expect.stringContains "shows MCP icon" "🤖 1"
      label |> Expect.stringContains "shows terminal icon" "💻 1")

    testCase "hides zero-count kinds" (fun () ->
      let label = fmt 2 (mk 2 0 0)
      label |> Expect.stringContains "shows browsers" "🌐 2"
      (label.Contains("🤖")) |> Expect.isFalse "no MCP icon"
      (label.Contains("💻")) |> Expect.isFalse "no terminal icon")

    testCase "shows total when all counts zero" (fun () ->
      let label = fmt 0 (mk 0 0 0)
      label |> Expect.equal "fallback to total" "0 connected")

    testCase "consistent format regardless of input" (fun () ->
      let counts = mk 1 1 0
      let label1 = fmt 2 counts
      let label2 = fmt 2 counts
      label1 |> Expect.equal "deterministic output" label2)
  ]

// ─── Stopped session filtering ───────────────────────────────────────────────

[<Tests>]
let stoppedSessionFilterTests =
  let info = SidebarCards.info
  let ids (cards: ParsedSession list) = cards |> List.map (fun c -> WorkerProtocol.SessionId.value c.Id)
  testList "Stopped session filtering" [
    testCase "stopped sessions are left out; the rest keep registry order" (fun () ->
      let cards =
        liveSessionCards SidebarCards.now (fun _ -> None) Map.empty
          [ info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready []
            info "0a2b3c4e" WorkerProtocol.SessionStatus.Stopped []
            info "0a2b3c4f" WorkerProtocol.SessionStatus.Starting [] ]
      (ids cards) |> Expect.equal "stopped hidden, order kept" [ "0a2b3c4d"; "0a2b3c4f" ])

    testCase "faulted sessions stay visible so the user can restart them" (fun () ->
      let cards =
        liveSessionCards SidebarCards.now (fun _ -> None) Map.empty
          [ info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready []
            info "0a2b3c4e" WorkerProtocol.SessionStatus.Faulted [] ]
      (ids cards) |> Expect.equal "faulted session stays visible" [ "0a2b3c4d"; "0a2b3c4e" ])

    testCase "each card carries its session's eval count" (fun () ->
      let a = info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready []
      let cards = liveSessionCards SidebarCards.now (fun _ -> None) (Map.ofList [ a.Id, 7 ]) [ a ]
      (cards |> List.map _.EvalCount) |> Expect.equal "eval count from the typed registry" [ 7 ])
  ]

[<Tests>]
let perSessionTestSummaryTests =
  let newCard () = SidebarCards.card (SidebarCards.info "0a2b3c4d" WorkerProtocol.SessionStatus.Ready [])
  testList "Per-session test summary" [
    testCase "cards have TestSummary = None until enriched" (fun () ->
      (newCard ()).TestSummary |> Expect.isNone "cards start with no test summary")

    testCase "TestSummary can be injected via record update" (fun () ->
      let summary =
        { SageFs.Features.LiveTesting.TestSummary.empty with
            Total = 42; Passed = 40; Failed = 2 }
      let enriched = { newCard () with TestSummary = Some summary }
      enriched.TestSummary |> Expect.isSome "should have summary"
      enriched.TestSummary.Value.Total |> Expect.equal "total 42" 42
      enriched.TestSummary.Value.Failed |> Expect.equal "failed 2" 2)

    testCase "inline badge renders in session HTML when TestSummary present" (fun () ->
      let summary =
        { SageFs.Features.LiveTesting.TestSummary.empty with
            Total = 10; Passed = 8; Failed = 2 }
      let session : ParsedSession =
        { Id = WorkerProtocol.SessionId.validate "sess-a" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
          Status = SessionDisplayStatus.Running
          StatusMessage = None
          ProjectsText = "(MyProj)"
          EvalCount = 5
          Uptime = "2m"
          WorkingDir = "/tmp"
          LastActivity = "just now"
          TestSummary = Some summary
          CoverageSummary = None
          TestTreemapEntries = [||]; CoverageTreemap = None
          BindingEntries = [||]
          AgentBadges = []
          GuidanceCssClass = ""
          ActiveProject = None
          ProjectRoles = []
          App = SageFs.AppRun.AppRunState.NotRunning }
      let html =
        renderSessionsForSession "" [session] false
        |> renderNode
      (html.Contains("✓8")) |> Expect.isTrue "should contain passed badge"
      (html.Contains("✗2")) |> Expect.isTrue "should contain failed badge")

    testCase "no badge when TestSummary is None" (fun () ->
      let session : ParsedSession =
        { Id = WorkerProtocol.SessionId.validate "sess-a" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
          Status = SessionDisplayStatus.Running
          StatusMessage = None
          ProjectsText = "(MyProj)"
          EvalCount = 5
          Uptime = "2m"
          WorkingDir = "/tmp"
          LastActivity = "just now"
          TestSummary = None
          CoverageSummary = None
          TestTreemapEntries = [||]; CoverageTreemap = None
          BindingEntries = [||]
          AgentBadges = []
          GuidanceCssClass = ""
          ActiveProject = None
          ProjectRoles = []
          App = SageFs.AppRun.AppRunState.NotRunning }
      let html =
        renderSessionsForSession "" [session] false
        |> renderNode
      (html.Contains("✓")) |> Expect.isFalse "no pass badge when no tests"
      (html.Contains("✗")) |> Expect.isFalse "no fail badge when no tests")
  ]

[<Tests>]
let perSessionCoverageTests =
  testList "Per-session coverage strip" [
    testCase "coverage strip renders gradient when CoverageSummary present" (fun () ->
      let summary =
        { SageFs.Features.LiveTesting.CoverageSummary.empty with
            TotalProbes = 64; CoveredProbes = 48; CoveragePercent = 75.0
            DensityStrip = [| 1.0; 0.8; 0.5; 0.0 |] }
      let session : ParsedSession =
        { Id = WorkerProtocol.SessionId.validate "sess-cov" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
          Status = SessionDisplayStatus.Running
          StatusMessage = None
          ProjectsText = "(MyProj)"
          EvalCount = 5
          Uptime = "2m"
          WorkingDir = "/tmp"
          LastActivity = "just now"
          TestSummary = None
          CoverageSummary = Some summary
          TestTreemapEntries = [||]; CoverageTreemap = None
          BindingEntries = [||]
          AgentBadges = []
          GuidanceCssClass = ""
          ActiveProject = None
          ProjectRoles = []
          App = SageFs.AppRun.AppRunState.NotRunning }
      let html =
        renderSessionsForSession "" [session] false
        |> renderNode
      (html.Contains("linear-gradient")) |> Expect.isTrue "should render gradient"
      (html.Contains("75%")) |> Expect.isTrue "should show percentage")

    testCase "no coverage strip when CoverageSummary is None" (fun () ->
      let session : ParsedSession =
        { Id = WorkerProtocol.SessionId.validate "sess-nocov" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
          Status = SessionDisplayStatus.Running
          StatusMessage = None
          ProjectsText = "(MyProj)"
          EvalCount = 5
          Uptime = "2m"
          WorkingDir = "/tmp"
          LastActivity = "just now"
          TestSummary = None
          CoverageSummary = None
          TestTreemapEntries = [||]; CoverageTreemap = None
          BindingEntries = [||]
          AgentBadges = []
          GuidanceCssClass = ""
          ActiveProject = None
          ProjectRoles = []
          App = SageFs.AppRun.AppRunState.NotRunning }
      let html =
        renderSessionsForSession "" [session] false
        |> renderNode
      (html.Contains("linear-gradient")) |> Expect.isFalse "no gradient without coverage")
  ]

[<Tests>]
let bindingsPanelTests =
  testList "Bindings panel rendering" [
    testCase "empty panel when no snapshot" (fun () ->
      let html = renderBindingsPanel None |> renderNode
      (html.Contains("bindings-panel")) |> Expect.isTrue "has panel id"
      (html.Contains("Bindings (0)")) |> Expect.isTrue "shows zero count"
      (html.Contains("No bindings yet")) |> Expect.isTrue "shows placeholder")

    testCase "active bindings render name and type" (fun () ->
      let scope : SageFs.Features.BindingExplorer.BindingScopeSnapshot = {
        Bindings = [
          { Name = "x"; TypeSig = "int"; CellIndex = 0; ShadowedBy = []; ReferencedIn = []; Value = None }
          { Name = "greet"; TypeSig = "string -> string"; CellIndex = 1; ShadowedBy = []; ReferencedIn = []; Value = None }
        ]
        ActiveBindings =
          [ ("x", { Name = "x"; TypeSig = "int"; CellIndex = 0; ShadowedBy = []; ReferencedIn = []; Value = None })
            ("greet", { Name = "greet"; TypeSig = "string -> string"; CellIndex = 1; ShadowedBy = []; ReferencedIn = []; Value = None }) ]
          |> Map.ofList
        ShadowedBindings = []
      }
      let html = renderBindingsPanel (Some scope) |> renderNode
      (html.Contains("Bindings (2)")) |> Expect.isTrue "shows count of 2"
      (html.Contains("x")) |> Expect.isTrue "has binding name x"
      (html.Contains("int")) |> Expect.isTrue "has type sig int"
      (html.Contains("greet")) |> Expect.isTrue "has binding name greet"
      (html.Contains("string -&gt; string") || html.Contains("string -> string")) |> Expect.isTrue "has function type sig")

    testCase "shadowed bindings render in collapsed section" (fun () ->
      let scope : SageFs.Features.BindingExplorer.BindingScopeSnapshot = {
        Bindings = [
          { Name = "x"; TypeSig = "int"; CellIndex = 0; ShadowedBy = [2]; ReferencedIn = []; Value = None }
          { Name = "x"; TypeSig = "string"; CellIndex = 2; ShadowedBy = []; ReferencedIn = []; Value = None }
        ]
        ActiveBindings =
          [ ("x", { Name = "x"; TypeSig = "string"; CellIndex = 2; ShadowedBy = []; ReferencedIn = []; Value = None }) ]
          |> Map.ofList
        ShadowedBindings =
          [ { Name = "x"; TypeSig = "int"; CellIndex = 0; ShadowedBy = [2]; ReferencedIn = []; Value = None } ]
      }
      let html = renderBindingsPanel (Some scope) |> renderNode
      (html.Contains("Bindings (1)")) |> Expect.isTrue "shows active count 1"
      (html.Contains("1 shadowed")) |> Expect.isTrue "mentions shadowed count")

    testCase "reference count shown when binding is referenced" (fun () ->
      let scope : SageFs.Features.BindingExplorer.BindingScopeSnapshot = {
        Bindings = [
          { Name = "helper"; TypeSig = "int -> int"; CellIndex = 0; ShadowedBy = []; ReferencedIn = [1; 2]; Value = None }
        ]
        ActiveBindings =
          [ ("helper", { Name = "helper"; TypeSig = "int -> int"; CellIndex = 0; ShadowedBy = []; ReferencedIn = [1; 2]; Value = None }) ]
          |> Map.ofList
        ShadowedBindings = []
      }
      let html = renderBindingsPanel (Some scope) |> renderNode
      (html.Contains("→2")) |> Expect.isTrue "shows reference count arrow")
  ]

[<Tests>]
let bindingsValueDisplayTests =
  let mkBinding name typeSig cellIdx value =
    { Name = name; TypeSig = typeSig; CellIndex = cellIdx; ShadowedBy = []; ReferencedIn = []; Value = value }
  let mkScope bindings =
    let active = bindings |> List.map (fun (b: SageFs.Features.BindingExplorer.BindingInfo) -> (b.Name, b)) |> Map.ofList
    { Bindings = bindings; ActiveBindings = active; ShadowedBindings = [] }
  testList "Value display in bindings panel" [
    testCase "shows value when present" (fun () ->
      let b = mkBinding "x" "int" 0 (Some "42")
      let scope = mkScope [b]
      let html = renderBindingsPanel (Some scope) |> renderNode
      (html.Contains("value-display")) |> Expect.isTrue "has value-display class"
      (html.Contains("= 42")) |> Expect.isTrue "shows the value")

    testCase "no value element when None" (fun () ->
      let b = mkBinding "y" "string" 0 None
      let scope = mkScope [b]
      let html = renderBindingsPanel (Some scope) |> renderNode
      (html.Contains("value-display")) |> Expect.isFalse "no value-display class when value is None")

    testCase "string value shown with quotes" (fun () ->
      let b = mkBinding "name" "string" 0 (Some "\"hello\"")
      let scope = mkScope [b]
      let html = renderBindingsPanel (Some scope) |> renderNode
      (html.Contains("value-display")) |> Expect.isTrue "has value-display"
      (html.Contains("= &quot;hello&quot;") || html.Contains("= \"hello\"")) |> Expect.isTrue "shows quoted string value")

    testCase "long value is truncated via CSS" (fun () ->
      let longVal = String.replicate 50 "abc"
      let b = mkBinding "data" "string" 0 (Some longVal)
      let scope = mkScope [b]
      let html = renderBindingsPanel (Some scope) |> renderNode
      (html.Contains("text-overflow: ellipsis")) |> Expect.isTrue "has CSS truncation"
      (html.Contains("max-width: 20em")) |> Expect.isTrue "has max-width constraint")
  ]

[<Tests>]
let dashboardActualParsingTests = testList "Dashboard actual parsing" [
  testList "parseOutputLines" [
    testCase "timestamp + kind line" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseOutputLines "[12:34:56] [result] val x = 42"
      result.Length |> Expect.equal "one line" 1
      result.[0].Timestamp |> Expect.equal "timestamp" (Some "12:34:56")
      result.[0].Kind |> Expect.equal "kind" ResultLine
      result.[0].Text |> Expect.equal "text" "val x = 42")
    testCase "kind-only line" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseOutputLines "[error] Something went wrong"
      result.[0].Timestamp |> Expect.equal "no timestamp" None
      result.[0].Kind |> Expect.equal "kind" ErrorLine
      result.[0].Text |> Expect.equal "text" "Something went wrong")
    testCase "plain text falls back to ResultLine" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseOutputLines "just some output"
      result.[0].Kind |> Expect.equal "fallback kind" ResultLine)
    testCase "empty input returns empty list" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseOutputLines ""
      result |> Expect.isEmpty "empty input")
    testCase "multiple lines parsed" (fun () ->
      let input = "[12:00:00] [result] line1\n[error] line2\nplain line3"
      let result = SageFs.Server.DashboardTypes.parseOutputLines input
      result.Length |> Expect.equal "three lines" 3
      result.[0].Kind |> Expect.equal "first" ResultLine
      result.[1].Kind |> Expect.equal "second" ErrorLine
      result.[2].Kind |> Expect.equal "third" ResultLine)
    testCase "info line kind" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseOutputLines "[info] Loading..."
      result.[0].Kind |> Expect.equal "info" InfoLine)
    testCase "system line kind" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseOutputLines "[system] Startup"
      result.[0].Kind |> Expect.equal "system" SystemLine)
  ]

  testList "parseDiagLines" [
    testCase "standard diagnostic format" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseDiagLines "[error] (10,5) Something is wrong"
      result.Length |> Expect.equal "one diag" 1
      result.[0].Severity |> Expect.equal "error" DiagError
      result.[0].Line |> Expect.equal "line" 10
      result.[0].Col |> Expect.equal "col" 5
      result.[0].Message |> Expect.equal "msg" "Something is wrong")
    testCase "warning diagnostic" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseDiagLines "[warning] (3,1) Unused variable"
      result.[0].Severity |> Expect.equal "warning" DiagWarning)
    testCase "unstructured line with [error] falls back to DiagError" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseDiagLines "Some text with [error] in it"
      result.[0].Severity |> Expect.equal "error fallback" DiagError
      result.[0].Line |> Expect.equal "line 0" 0)
    testCase "unstructured line without error falls back to DiagWarning" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseDiagLines "Some random diagnostic text"
      result.[0].Severity |> Expect.equal "warning fallback" DiagWarning)
    testCase "empty input returns empty list" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseDiagLines ""
      result |> Expect.isEmpty "empty")
    testCase "multiple diagnostics" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseDiagLines "[error] (1,1) first\n[warning] (2,2) second"
      result.Length |> Expect.equal "two diags" 2
      result.[0].Severity |> Expect.equal "first error" DiagError
      result.[1].Severity |> Expect.equal "second warning" DiagWarning)
  ]
]

[<Tests>]
let captureToCssClassTests = testList "captureToCssClass" [
  testCase "keyword" (fun () ->
    (captureToCssClass "keyword") |> Expect.equal "keyword" "syn-keyword")
  testCase "keyword.control prefix" (fun () ->
    (captureToCssClass "keyword.control") |> Expect.equal "prefix" "syn-keyword")
  testCase "string" (fun () ->
    (captureToCssClass "string") |> Expect.equal "string" "syn-string")
  testCase "string.special prefix" (fun () ->
    (captureToCssClass "string.special") |> Expect.equal "prefix" "syn-string")
  testCase "comment" (fun () ->
    (captureToCssClass "comment") |> Expect.equal "comment" "syn-comment")
  testCase "number" (fun () ->
    (captureToCssClass "number") |> Expect.equal "number" "syn-number")
  testCase "operator" (fun () ->
    (captureToCssClass "operator") |> Expect.equal "operator" "syn-operator")
  testCase "type" (fun () ->
    (captureToCssClass "type") |> Expect.equal "type" "syn-type")
  testCase "type.builtin prefix" (fun () ->
    (captureToCssClass "type.builtin") |> Expect.equal "prefix" "syn-type")
  testCase "function" (fun () ->
    (captureToCssClass "function") |> Expect.equal "function" "syn-function")
  testCase "variable" (fun () ->
    (captureToCssClass "variable") |> Expect.equal "variable" "syn-variable")
  testCase "punctuation" (fun () ->
    (captureToCssClass "punctuation") |> Expect.equal "punctuation" "syn-punctuation")
  testCase "constant" (fun () ->
    (captureToCssClass "constant") |> Expect.equal "constant" "syn-constant")
  testCase "module" (fun () ->
    (captureToCssClass "module") |> Expect.equal "module" "syn-module")
  testCase "attribute" (fun () ->
    (captureToCssClass "attribute") |> Expect.equal "attribute" "syn-attribute")
  testCase "property" (fun () ->
    (captureToCssClass "property") |> Expect.equal "property" "syn-property")
  testCase "boolean maps to syn-constant" (fun () ->
    (captureToCssClass "boolean") |> Expect.equal "boolean→constant" "syn-constant")
  testCase "unknown returns empty" (fun () ->
    (captureToCssClass "whatever") |> Expect.equal "unknown" "")
  testCase "empty returns empty" (fun () ->
    (captureToCssClass "") |> Expect.equal "empty" "")
]

[<Tests>]
let routeValueTests =
  testList "Dashboard route values" [
    testCase "WHY — Falco route data — reads the session id 8e940641 as the number Infinity, which is why session routes read raw values" <| fun _ ->
      let ctx = Microsoft.AspNetCore.Http.DefaultHttpContext()
      ctx.Request.RouteValues.["id"] <- box "8e940641"
      (Falco.Request.getRoute ctx).GetString("id", "")
      |> Expecto.Flip.Expect.equal "Falco's typed parse (characterization)" "Infinity"

    testCase "WHY — Dashboard.routeValue — keeps the session id 8e940641 intact because every session button routes by it" <| fun _ ->
      let ctx = Microsoft.AspNetCore.Http.DefaultHttpContext()
      ctx.Request.RouteValues.["id"] <- box "8e940641"
      SageFs.Server.Dashboard.routeValue "id" ctx
      |> Expecto.Flip.Expect.equal "the raw id" "8e940641"

    testCase "WHY — Dashboard.routeValue — a missing route key reads as empty because session id validation then rejects it" <| fun _ ->
      SageFs.Server.Dashboard.routeValue "id" (Microsoft.AspNetCore.Http.DefaultHttpContext())
      |> Expecto.Flip.Expect.equal "empty" ""
  ]
