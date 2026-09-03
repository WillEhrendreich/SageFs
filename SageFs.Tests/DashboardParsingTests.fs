module SageFs.Tests.DashboardParsingTests

open Expecto
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

  type ParsedSession = {
    Id: string; Status: string; IsActive: bool; IsSelected: bool
    ProjectsText: string; EvalCount: int
    Uptime: string; WorkingDir: string; LastActivity: string
  }

  let parseSessionLines (content: string) =
    let sessionRegex = Regex(@"^([> ])\s+(\S+)\s*\[([^\]]+)\](\s*\*)?(\s*\([^)]*\))?(\s*evals:\d+)?(\s*up:(?:just now|\S+))?(\s*dir:\S.*?)?(\s*last:.+)?$")
    let extractTag (prefix: string) (value: string) =
      let v = value.Trim()
      if v.StartsWith(prefix) then v.Substring(prefix.Length).Trim()
      else ""
    content.Split('\n')
    |> Array.filter (fun (l: string) -> l.Length > 0)
    |> Array.map (fun (l: string) ->
      let m = sessionRegex.Match(l)
      if m.Success then
        let evalsMatch = Regex.Match(m.Groups.[6].Value, @"evals:(\d+)")
        { Id = m.Groups.[2].Value
          Status = m.Groups.[3].Value
          IsActive = m.Groups.[4].Value.Contains("*")
          IsSelected = m.Groups.[1].Value = ">"
          ProjectsText = m.Groups.[5].Value.Trim()
          EvalCount = if evalsMatch.Success then int evalsMatch.Groups.[1].Value else 0
          Uptime = extractTag "up:" m.Groups.[7].Value
          WorkingDir = extractTag "dir:" m.Groups.[8].Value
          LastActivity = extractTag "last:" m.Groups.[9].Value }
      else
        { Id = l.Trim(); Status = "unknown"; IsActive = false; IsSelected = false
          ProjectsText = ""; EvalCount = 0
          Uptime = ""; WorkingDir = ""; LastActivity = "" })
    |> Array.toList

[<Tests>]
let tests = testList "Dashboard parsing" [
  testCase "output: parses timestamped result line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[14:30:05] [result] val x: int = 42"
    Expect.equal result [(Some "14:30:05", "Result", "val x: int = 42")] "extract timestamp, kind, text")

  testCase "output: parses result line without timestamp" (fun () ->
    let result = DashboardParsing.parseOutputLines "[result] val x: int = 42"
    Expect.equal result [(None, "Result", "val x: int = 42")] "fallback without timestamp")

  testCase "output: parses timestamped error line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[09:15:00] [error] Something went wrong"
    Expect.equal result [(Some "09:15:00", "Error", "Something went wrong")] "extract error kind with timestamp")

  testCase "output: parses info line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[12:00:00] [info] Loading..."
    Expect.equal result [(Some "12:00:00", "Info", "Loading...")] "extract info kind")

  testCase "output: parses system line" (fun () ->
    let result = DashboardParsing.parseOutputLines "[08:00:00] [system] let x = 1"
    Expect.equal result [(Some "08:00:00", "System", "let x = 1")] "extract system kind")

  testCase "output: non-prefixed line defaults to Result" (fun () ->
    let result = DashboardParsing.parseOutputLines "plain text"
    Expect.equal result [(None, "Result", "plain text")] "fallback to Result")

  testCase "output: skips empty lines" (fun () ->
    let lines = DashboardParsing.parseOutputLines "[14:30:05] [result] a\n\n[14:30:06] [error] b"
    Expect.equal lines.Length 2 "should skip empty lines")

  testCase "output: multiple timestamped lines" (fun () ->
    let result = DashboardParsing.parseOutputLines "[14:30:05] [result] a\n[14:30:06] [error] b\n[14:30:07] [info] c"
    Expect.equal result.Length 3 "should have 3 lines"
    let (ts1, k1, _) = result.[0]
    Expect.equal (ts1, k1) (Some "14:30:05", "Result") "first line"
    let (ts2, k2, _) = result.[1]
    Expect.equal (ts2, k2) (Some "14:30:06", "Error") "second line"
    let (ts3, k3, _) = result.[2]
    Expect.equal (ts3, k3) (Some "14:30:07", "Info") "third line")

  testCase "diag: extracts line and col from error" (fun () ->
    let result = DashboardParsing.parseDiagLines "[error] (5,12) Type not defined"
    Expect.equal result [("Error", "Type not defined", 5, 12)] "extract severity, msg, line, col")

  testCase "diag: extracts line and col from warning" (fun () ->
    let result = DashboardParsing.parseDiagLines "[warning] (1,0) Value unused"
    Expect.equal result [("Warning", "Value unused", 1, 0)] "parse warning")

  testCase "diag: multiple diagnostics" (fun () ->
    let result = DashboardParsing.parseDiagLines "[error] (5,12) Bad\n[warning] (10,3) Suspicious"
    Expect.equal result.Length 2 "should have 2 diagnostics"
    let (s1, _, l1, c1) = result.[0]
    Expect.equal (s1, l1, c1) ("Error", 5, 12) "first diagnostic"
    let (s2, _, l2, c2) = result.[1]
    Expect.equal (s2, l2, c2) ("Warning", 10, 3) "second diagnostic")

  testCase "diag: fallback for non-standard format" (fun () ->
    let result = DashboardParsing.parseDiagLines "some random diagnostic"
    Expect.equal result [("Warning", "some random diagnostic", 0, 0)] "fallback to Warning 0,0")

  testCase "session: parses full session line with all fields" (fun () ->
    let line = "> session-abc [running] * (MyProj, Other) evals:5 up:2h15m dir:C:\\Code\\Test last:3m ago"
    let result = DashboardParsing.parseSessionLines line
    Expect.equal result.Length 1 "should parse one session"
    let s = result.[0]
    Expect.equal s.Id "session-abc" "id"
    Expect.equal s.Status "running" "status"
    Expect.isTrue s.IsActive "active"
    Expect.isTrue s.IsSelected "selected"
    Expect.equal s.EvalCount 5 "evals"
    Expect.equal s.Uptime "2h15m" "uptime"
    Expect.stringContains s.WorkingDir "Code" "working dir"
    Expect.equal s.LastActivity "3m ago" "last activity")

  testCase "session: parses minimal session line" (fun () ->
    let result = DashboardParsing.parseSessionLines "  session-1 [starting]"
    Expect.equal result.Length 1 "should parse"
    let s = result.[0]
    Expect.equal s.Id "session-1" "id"
    Expect.equal s.Status "starting" "status"
    Expect.isFalse s.IsActive "not active"
    Expect.isFalse s.IsSelected "not selected"
    Expect.equal s.Uptime "" "no uptime"
    Expect.equal s.WorkingDir "" "no dir"
    Expect.equal s.LastActivity "" "no last")

  testCase "session: parses 'just now' uptime" (fun () ->
    let result = DashboardParsing.parseSessionLines "  session-1 [running] up:just now last:just now"
    let s = result.[0]
    Expect.stringContains s.Uptime "just now" "just now uptime"
    Expect.stringContains s.LastActivity "just now" "just now last")

  testCase "session: multiple sessions" (fun () ->
    let lines = "> session-1 [running] * up:1h\n  session-2 [starting] up:5m"
    let result = DashboardParsing.parseSessionLines lines
    Expect.equal result.Length 2 "two sessions"
    Expect.equal result.[0].Id "session-1" "first id"
    Expect.isTrue result.[0].IsSelected "first selected"
    Expect.equal result.[1].Id "session-2" "second id"
    Expect.isFalse result.[1].IsSelected "second not selected")

  testCase "session: error status with reason" (fun () ->
    let result = DashboardParsing.parseSessionLines "  session-x [error: crashed]"
    let s = result.[0]
    Expect.equal s.Status "error: crashed" "error with reason")

  testCase "session: selected vs unselected parsing" (fun () ->
    let line = "  session-abc [running] *"
    let result = DashboardParsing.parseSessionLines line
    let s = result.[0]
    Expect.isFalse s.IsSelected "unselected session"
    Expect.isTrue s.IsActive "but still active")
]

/// Tests for the live session state override (Bug #3)
module SessionStateOverride =
  open SageFs
  open SageFs.Server.Dashboard
  open SageFs.Server.DashboardTypes

  let mkSession (id: WorkerProtocol.SessionId) (status: SessionDisplayStatus) =
    { ParsedSession.Id = id
      Status = status
      StatusMessage = None
      IsActive = false
      IsSelected = false
      ProjectsText = ""
      EvalCount = 0
      Uptime = ""
      WorkingDir = ""
      LastActivity = ""
      TestSummary = None
      CoverageSummary = None
      TestTreemapEntries = [||]
      BindingEntries = [||]
      AgentBadges = []
      GuidanceCssClass = "" }
[<Tests>]
let stateOverrideTests =
  let open' getState = SageFs.Server.DashboardTypes.overrideSessionStatuses getState (fun _ -> None)
  let mk = SessionStateOverride.mkSession
  let s1 = WorkerProtocol.SessionId.validate "s1" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
  let s2 = WorkerProtocol.SessionId.validate "s2" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
  testList "Session state override" [
    testCase "Ready maps to running" (fun () ->
      let r = open' (fun _ -> SessionState.Ready) [mk s1 SessionDisplayStatus.Starting]
      Expect.equal (List.head r).Status SessionDisplayStatus.Running "Ready = running")

    testCase "Evaluating maps to running" (fun () ->
      let r = open' (fun _ -> SessionState.Evaluating) [mk s1 SessionDisplayStatus.Starting]
      Expect.equal (List.head r).Status SessionDisplayStatus.Running "Evaluating = running")

    testCase "WarmingUp maps to starting" (fun () ->
      let r = open' (fun _ -> SessionState.WarmingUp) [mk s1 SessionDisplayStatus.Running]
      Expect.equal (List.head r).Status SessionDisplayStatus.Starting "WarmingUp = starting")

    testCase "Faulted maps to faulted" (fun () ->
      let r = open' (fun _ -> SessionState.Faulted) [mk s1 SessionDisplayStatus.Running]
      Expect.equal (List.head r).Status SessionDisplayStatus.Faulted "Faulted = faulted")

    testCase "Uninitialized maps to lost" (fun () ->
      let r = open' (fun _ -> SessionState.Uninitialized) [mk s1 SessionDisplayStatus.Running]
      Expect.equal (List.head r).Status SessionDisplayStatus.Lost "Uninitialized = lost (worker gone, needs restart)")

    testCase "overrides each session independently" (fun () ->
      let sessions = [ mk s1 SessionDisplayStatus.Starting; mk s2 SessionDisplayStatus.Running ]
      let getState (sid: WorkerProtocol.SessionId) =
        if WorkerProtocol.SessionId.value sid = (WorkerProtocol.SessionId.value s1) then SessionState.Ready else SessionState.Faulted
      let r = open' getState sessions
      Expect.equal (List.head r).Status SessionDisplayStatus.Running "s1 becomes running"
      Expect.equal (r |> List.item 1).Status SessionDisplayStatus.Faulted "s2 becomes faulted")
  ]

/// Tests for TUI chrome filtering in session parsing (Bug #2)
[<Tests>]
let ghostSessionTests =
  testList "Ghost session filtering (Bug #2)" [
    testCase "filters TUI keyboard shortcut lines" (fun () ->
      let input = "  0a2b3c4d [running] *\n↑↓ nav · Enter switch"
      let sessions = SageFs.Server.DashboardTypes.parseSessionLines input
      Expect.equal sessions.Length 1 "only real session, no ghost from shortcuts")

    testCase "filters box-drawing border lines" (fun () ->
      let input = "  0a2b3c4d [running] *\n──────────"
      let sessions = SageFs.Server.DashboardTypes.parseSessionLines input
      Expect.equal sessions.Length 1 "border lines filtered")

    testCase "filters spinner lines" (fun () ->
      let input = "  0a2b3c4d [running] *\n⏳ Loading..."
      let sessions = SageFs.Server.DashboardTypes.parseSessionLines input
      Expect.equal sessions.Length 1 "spinner lines filtered")

    testCase "filters Ctrl+Tab cycle lines" (fun () ->
      let input = "  0a2b3c4d [running] *\nCtrl+Tab cycle sessions"
      let sessions = SageFs.Server.DashboardTypes.parseSessionLines input
      Expect.equal sessions.Length 1 "Ctrl+Tab line filtered")

    testCase "preserves all valid sessions" (fun () ->
      let input = "  0a2b3c4d [running] *\n  0a2b3c4e [starting]"
      let sessions = SageFs.Server.DashboardTypes.parseSessionLines input
      Expect.equal sessions.Length 2 "both valid sessions kept")
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
      Expect.isFalse (display.Contains("FsiCompilationException")) "should strip exception name"
      Expect.stringContains display "⚠" "should have warning prefix"
      Expect.equal css "output-line output-error" "error CSS class")

    testCase "clean error gets warning prefix" (fun () ->
      let input = "Evaluation failed: syntax error"
      let display, _ = ErrorFormatting.formatEvalResult input
      Expect.equal display "⚠ syntax error" "replaces prefix with warning emoji")

    testCase "success result unchanged" (fun () ->
      let input = "val it: int = 42"
      let display, css = ErrorFormatting.formatEvalResult input
      Expect.equal display input "result text unchanged"
      Expect.equal css "output-line output-result" "success CSS class")

    testCase "Error: prefix detected" (fun () ->
      let input = "Error: something went wrong"
      let _, css = ErrorFormatting.formatEvalResult input
      Expect.equal css "output-line output-error" "Error: triggers error styling")
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
      Expect.equal filtered.Length 2 "all regions included on first push"
      Expect.notEqual hash 0 "hash is non-zero")

    testCase "identical content filtered out" (fun () ->
      let regions = [mkRegion "output" "hello"; mkRegion "sessions" "s1"]
      let _, hash1 = filter 0 regions
      let filtered, _ = filter hash1 regions
      Expect.equal filtered.Length 1 "output region filtered"
      Expect.equal (List.head filtered).Id "sessions" "only non-output remains")

    testCase "changed content included" (fun () ->
      let regions1 = [mkRegion "output" "hello"]
      let _, hash1 = filter 0 regions1
      let regions2 = [mkRegion "output" "world"]
      let filtered, _ = filter hash1 regions2
      Expect.equal filtered.Length 1 "new output included")

    testCase "no output region passes through" (fun () ->
      let regions = [mkRegion "sessions" "s1"]
      let filtered, hash = filter 0 regions
      Expect.equal filtered.Length 1 "all regions pass"
      Expect.equal hash 0 "hash stays 0")
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
      Expect.stringContains label "🌐 1" "shows browser icon"
      Expect.stringContains label "🤖 1" "shows MCP icon"
      Expect.stringContains label "💻 1" "shows terminal icon")

    testCase "hides zero-count kinds" (fun () ->
      let label = fmt 2 (mk 2 0 0)
      Expect.stringContains label "🌐 2" "shows browsers"
      Expect.isFalse (label.Contains("🤖")) "no MCP icon"
      Expect.isFalse (label.Contains("💻")) "no terminal icon")

    testCase "shows total when all counts zero" (fun () ->
      let label = fmt 0 (mk 0 0 0)
      Expect.equal label "0 connected" "fallback to total")

    testCase "consistent format regardless of input" (fun () ->
      let counts = mk 1 1 0
      let label1 = fmt 2 counts
      let label2 = fmt 2 counts
      Expect.equal label1 label2 "deterministic output")
  ]

// ─── Stopped session filtering ───────────────────────────────────────────────

[<Tests>]
let stoppedSessionFilterTests =
  let parse = SageFs.Server.DashboardTypes.parseSessionLines
  let override' getState = SageFs.Server.DashboardTypes.overrideSessionStatuses getState (fun _ -> None)
  let sessB = WorkerProtocol.SessionId.validate "0a2b3c4e" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
  testList "Stopped session filtering" [
    testCase "lost sessions are KEPT visible (worker gone, user can restart)" (fun () ->
      // Uninitialized now maps to "lost" (not "stopped"). The sidebar keeps
      // lost sessions visible with a yellow border so the user can see
      // which ones need a restart, rather than silently hiding them.
      let input = "  0a2b3c4d [running] *\n  0a2b3c4e [running]"
      let parsed = parse input
      let getState (sid: WorkerProtocol.SessionId) =
        if WorkerProtocol.SessionId.value sid = (WorkerProtocol.SessionId.value sessB) then SageFs.SessionState.Uninitialized
        else SageFs.SessionState.Ready
      let corrected = override' getState parsed
      let visible = corrected |> List.filter (fun s -> s.Status <> SessionDisplayStatus.Stopped)
      Expect.equal visible.Length 2 "lost session is NOT filtered out"
      let visibleLost = corrected |> List.filter (fun s -> s.Status = SessionDisplayStatus.Lost)
      Expect.equal visibleLost.Length 1 "0a2b3c4e has lost status"
      Expect.equal (WorkerProtocol.SessionId.value visibleLost.[0].Id) "0a2b3c4e" "lost session identified")

    testCase "faulted sessions are kept visible" (fun () ->
      let input = "  0a2b3c4d [running] *\n  0a2b3c4e [running]"
      let parsed = parse input
      let getState (sid: WorkerProtocol.SessionId) =
        if WorkerProtocol.SessionId.value sid = (WorkerProtocol.SessionId.value sessB) then SageFs.SessionState.Faulted
        else SageFs.SessionState.Ready
      let corrected = override' getState parsed
      let visible = corrected |> List.filter (fun s -> s.Status <> SessionDisplayStatus.Stopped)
      Expect.equal visible.Length 2 "faulted session stays visible")

    testCase "all-lost sessions remain visible (no longer hidden)" (fun () ->
      // Previously Uninitialized mapped to "stopped" and was hidden.
      // Now it maps to "lost" and stays visible.
      let input = "  0a2b3c4d [running]\n  0a2b3c4e [starting]"
      let parsed = parse input
      let getState _ = SageFs.SessionState.Uninitialized
      let corrected = override' getState parsed
      let visible = corrected |> List.filter (fun s -> s.Status <> SessionDisplayStatus.Stopped)
      Expect.equal visible.Length 2 "lost sessions are NOT filtered out"
      Expect.equal (List.length visible) (List.length corrected) "all sessions remain visible"
      let lostCount = visible |> List.filter (fun s -> s.Status = SessionDisplayStatus.Lost) |> List.length
      Expect.equal lostCount 2 "both sessions show as lost")
  ]

[<Tests>]
let perSessionTestSummaryTests =
  let parse = SageFs.Server.DashboardTypes.parseSessionLines
  testList "Per-session test summary" [
    testCase "parsed sessions have TestSummary = None by default" (fun () ->
      let input = "  0a2b3c4d [running] *"
      let sessions = parse input
      Expect.isNone sessions.[0].TestSummary "parsed sessions start with no test summary")

    testCase "TestSummary can be injected via record update" (fun () ->
      let input = "  0a2b3c4d [running] *"
      let sessions = parse input
      let summary =
        { SageFs.Features.LiveTesting.TestSummary.empty with
            Total = 42; Passed = 40; Failed = 2 }
      let enriched = { sessions.[0] with TestSummary = Some summary }
      Expect.isSome enriched.TestSummary "should have summary"
      Expect.equal enriched.TestSummary.Value.Total 42 "total 42"
      Expect.equal enriched.TestSummary.Value.Failed 2 "failed 2")

    testCase "inline badge renders in session HTML when TestSummary present" (fun () ->
      let summary =
        { SageFs.Features.LiveTesting.TestSummary.empty with
            Total = 10; Passed = 8; Failed = 2 }
      let session : ParsedSession =
        { Id = WorkerProtocol.SessionId.validate "sess-a" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
          Status = SessionDisplayStatus.Running
          StatusMessage = None
          IsActive = true
          IsSelected = false
          ProjectsText = "(MyProj)"
          EvalCount = 5
          Uptime = "2m"
          WorkingDir = "/tmp"
          LastActivity = "just now"
          TestSummary = Some summary
          CoverageSummary = None
          TestTreemapEntries = [||]
          BindingEntries = [||]
          AgentBadges = []
          GuidanceCssClass = "" }
      let html =
        renderSessions [session] false
        |> renderNode
      Expect.isTrue (html.Contains("✓8")) "should contain passed badge"
      Expect.isTrue (html.Contains("✗2")) "should contain failed badge")

    testCase "no badge when TestSummary is None" (fun () ->
      let session : ParsedSession =
        { Id = WorkerProtocol.SessionId.validate "sess-a" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
          Status = SessionDisplayStatus.Running
          StatusMessage = None
          IsActive = true
          IsSelected = false
          ProjectsText = "(MyProj)"
          EvalCount = 5
          Uptime = "2m"
          WorkingDir = "/tmp"
          LastActivity = "just now"
          TestSummary = None
          CoverageSummary = None
          TestTreemapEntries = [||]
          BindingEntries = [||]
          AgentBadges = []
          GuidanceCssClass = "" }
      let html =
        renderSessions [session] false
        |> renderNode
      Expect.isFalse (html.Contains("✓")) "no pass badge when no tests"
      Expect.isFalse (html.Contains("✗")) "no fail badge when no tests")
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
          IsActive = true
          IsSelected = false
          ProjectsText = "(MyProj)"
          EvalCount = 5
          Uptime = "2m"
          WorkingDir = "/tmp"
          LastActivity = "just now"
          TestSummary = None
          CoverageSummary = Some summary
          TestTreemapEntries = [||]
          BindingEntries = [||]
          AgentBadges = []
          GuidanceCssClass = "" }
      let html =
        renderSessions [session] false
        |> renderNode
      Expect.isTrue (html.Contains("linear-gradient")) "should render gradient"
      Expect.isTrue (html.Contains("75%")) "should show percentage")

    testCase "no coverage strip when CoverageSummary is None" (fun () ->
      let session : ParsedSession =
        { Id = WorkerProtocol.SessionId.validate "sess-nocov" |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
          Status = SessionDisplayStatus.Running
          StatusMessage = None
          IsActive = true
          IsSelected = false
          ProjectsText = "(MyProj)"
          EvalCount = 5
          Uptime = "2m"
          WorkingDir = "/tmp"
          LastActivity = "just now"
          TestSummary = None
          CoverageSummary = None
          TestTreemapEntries = [||]
          BindingEntries = [||]
          AgentBadges = []
          GuidanceCssClass = "" }
      let html =
        renderSessions [session] false
        |> renderNode
      Expect.isFalse (html.Contains("linear-gradient")) "no gradient without coverage")
  ]

[<Tests>]
let bindingsPanelTests =
  testList "Bindings panel rendering" [
    testCase "empty panel when no snapshot" (fun () ->
      let html = renderBindingsPanel None |> renderNode
      Expect.isTrue (html.Contains("bindings-panel")) "has panel id"
      Expect.isTrue (html.Contains("Bindings (0)")) "shows zero count"
      Expect.isTrue (html.Contains("No bindings yet")) "shows placeholder")

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
      Expect.isTrue (html.Contains("Bindings (2)")) "shows count of 2"
      Expect.isTrue (html.Contains("x")) "has binding name x"
      Expect.isTrue (html.Contains("int")) "has type sig int"
      Expect.isTrue (html.Contains("greet")) "has binding name greet"
      Expect.isTrue (html.Contains("string -&gt; string") || html.Contains("string -> string")) "has function type sig")

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
      Expect.isTrue (html.Contains("Bindings (1)")) "shows active count 1"
      Expect.isTrue (html.Contains("1 shadowed")) "mentions shadowed count")

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
      Expect.isTrue (html.Contains("→2")) "shows reference count arrow")
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
      Expect.isTrue (html.Contains("value-display")) "has value-display class"
      Expect.isTrue (html.Contains("= 42")) "shows the value")

    testCase "no value element when None" (fun () ->
      let b = mkBinding "y" "string" 0 None
      let scope = mkScope [b]
      let html = renderBindingsPanel (Some scope) |> renderNode
      Expect.isFalse (html.Contains("value-display")) "no value-display class when value is None")

    testCase "string value shown with quotes" (fun () ->
      let b = mkBinding "name" "string" 0 (Some "\"hello\"")
      let scope = mkScope [b]
      let html = renderBindingsPanel (Some scope) |> renderNode
      Expect.isTrue (html.Contains("value-display")) "has value-display"
      Expect.isTrue (html.Contains("= &quot;hello&quot;") || html.Contains("= \"hello\"")) "shows quoted string value")

    testCase "long value is truncated via CSS" (fun () ->
      let longVal = String.replicate 50 "abc"
      let b = mkBinding "data" "string" 0 (Some longVal)
      let scope = mkScope [b]
      let html = renderBindingsPanel (Some scope) |> renderNode
      Expect.isTrue (html.Contains("text-overflow: ellipsis")) "has CSS truncation"
      Expect.isTrue (html.Contains("max-width: 20em")) "has max-width constraint")
  ]

[<Tests>]
let dashboardActualParsingTests = testList "Dashboard actual parsing" [
  testList "parseOutputLines" [
    testCase "timestamp + kind line" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseOutputLines "[12:34:56] [result] val x = 42"
      Expect.equal result.Length 1 "one line"
      Expect.equal result.[0].Timestamp (Some "12:34:56") "timestamp"
      Expect.equal result.[0].Kind ResultLine "kind"
      Expect.equal result.[0].Text "val x = 42" "text")
    testCase "kind-only line" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseOutputLines "[error] Something went wrong"
      Expect.equal result.[0].Timestamp None "no timestamp"
      Expect.equal result.[0].Kind ErrorLine "kind"
      Expect.equal result.[0].Text "Something went wrong" "text")
    testCase "plain text falls back to ResultLine" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseOutputLines "just some output"
      Expect.equal result.[0].Kind ResultLine "fallback kind")
    testCase "empty input returns empty list" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseOutputLines ""
      Expect.isEmpty result "empty input")
    testCase "multiple lines parsed" (fun () ->
      let input = "[12:00:00] [result] line1\n[error] line2\nplain line3"
      let result = SageFs.Server.DashboardTypes.parseOutputLines input
      Expect.equal result.Length 3 "three lines"
      Expect.equal result.[0].Kind ResultLine "first"
      Expect.equal result.[1].Kind ErrorLine "second"
      Expect.equal result.[2].Kind ResultLine "third")
    testCase "info line kind" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseOutputLines "[info] Loading..."
      Expect.equal result.[0].Kind InfoLine "info")
    testCase "system line kind" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseOutputLines "[system] Startup"
      Expect.equal result.[0].Kind SystemLine "system")
  ]

  testList "parseDiagLines" [
    testCase "standard diagnostic format" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseDiagLines "[error] (10,5) Something is wrong"
      Expect.equal result.Length 1 "one diag"
      Expect.equal result.[0].Severity DiagError "error"
      Expect.equal result.[0].Line 10 "line"
      Expect.equal result.[0].Col 5 "col"
      Expect.equal result.[0].Message "Something is wrong" "msg")
    testCase "warning diagnostic" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseDiagLines "[warning] (3,1) Unused variable"
      Expect.equal result.[0].Severity DiagWarning "warning")
    testCase "unstructured line with [error] falls back to DiagError" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseDiagLines "Some text with [error] in it"
      Expect.equal result.[0].Severity DiagError "error fallback"
      Expect.equal result.[0].Line 0 "line 0")
    testCase "unstructured line without error falls back to DiagWarning" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseDiagLines "Some random diagnostic text"
      Expect.equal result.[0].Severity DiagWarning "warning fallback")
    testCase "empty input returns empty list" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseDiagLines ""
      Expect.isEmpty result "empty")
    testCase "multiple diagnostics" (fun () ->
      let result = SageFs.Server.DashboardTypes.parseDiagLines "[error] (1,1) first\n[warning] (2,2) second"
      Expect.equal result.Length 2 "two diags"
      Expect.equal result.[0].Severity DiagError "first error"
      Expect.equal result.[1].Severity DiagWarning "second warning")
  ]
]

[<Tests>]
let captureToCssClassTests = testList "captureToCssClass" [
  testCase "keyword" (fun () ->
    Expect.equal (captureToCssClass "keyword") "syn-keyword" "keyword")
  testCase "keyword.control prefix" (fun () ->
    Expect.equal (captureToCssClass "keyword.control") "syn-keyword" "prefix")
  testCase "string" (fun () ->
    Expect.equal (captureToCssClass "string") "syn-string" "string")
  testCase "string.special prefix" (fun () ->
    Expect.equal (captureToCssClass "string.special") "syn-string" "prefix")
  testCase "comment" (fun () ->
    Expect.equal (captureToCssClass "comment") "syn-comment" "comment")
  testCase "number" (fun () ->
    Expect.equal (captureToCssClass "number") "syn-number" "number")
  testCase "operator" (fun () ->
    Expect.equal (captureToCssClass "operator") "syn-operator" "operator")
  testCase "type" (fun () ->
    Expect.equal (captureToCssClass "type") "syn-type" "type")
  testCase "type.builtin prefix" (fun () ->
    Expect.equal (captureToCssClass "type.builtin") "syn-type" "prefix")
  testCase "function" (fun () ->
    Expect.equal (captureToCssClass "function") "syn-function" "function")
  testCase "variable" (fun () ->
    Expect.equal (captureToCssClass "variable") "syn-variable" "variable")
  testCase "punctuation" (fun () ->
    Expect.equal (captureToCssClass "punctuation") "syn-punctuation" "punctuation")
  testCase "constant" (fun () ->
    Expect.equal (captureToCssClass "constant") "syn-constant" "constant")
  testCase "module" (fun () ->
    Expect.equal (captureToCssClass "module") "syn-module" "module")
  testCase "attribute" (fun () ->
    Expect.equal (captureToCssClass "attribute") "syn-attribute" "attribute")
  testCase "property" (fun () ->
    Expect.equal (captureToCssClass "property") "syn-property" "property")
  testCase "boolean maps to syn-constant" (fun () ->
    Expect.equal (captureToCssClass "boolean") "syn-constant" "boolean→constant")
  testCase "unknown returns empty" (fun () ->
    Expect.equal (captureToCssClass "whatever") "" "unknown")
  testCase "empty returns empty" (fun () ->
    Expect.equal (captureToCssClass "") "" "empty")
]
