module SageFs.Tests.DashboardParsingTests

open Expecto
open SageFs
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

  let mkSession id status =
    { ParsedSession.Id = id
      Status = status
      IsActive = false
      IsSelected = false
      ProjectsText = ""
      EvalCount = 0
      Uptime = ""
      WorkingDir = ""
      LastActivity = "" }

[<Tests>]
let stateOverrideTests =
  let open' = SageFs.Server.Dashboard.overrideSessionStatuses
  let mk = SessionStateOverride.mkSession
  testList "Session state override" [
    testCase "Ready maps to running" (fun () ->
      let r = open' (fun _ -> SessionState.Ready) [mk "s1" "starting"]
      Expect.equal (List.head r).Status "running" "Ready = running")

    testCase "Evaluating maps to running" (fun () ->
      let r = open' (fun _ -> SessionState.Evaluating) [mk "s1" "starting"]
      Expect.equal (List.head r).Status "running" "Evaluating = running")

    testCase "WarmingUp maps to starting" (fun () ->
      let r = open' (fun _ -> SessionState.WarmingUp) [mk "s1" "running"]
      Expect.equal (List.head r).Status "starting" "WarmingUp = starting")

    testCase "Faulted maps to faulted" (fun () ->
      let r = open' (fun _ -> SessionState.Faulted) [mk "s1" "running"]
      Expect.equal (List.head r).Status "faulted" "Faulted = faulted")

    testCase "Uninitialized maps to stopped" (fun () ->
      let r = open' (fun _ -> SessionState.Uninitialized) [mk "s1" "running"]
      Expect.equal (List.head r).Status "stopped" "Uninitialized = stopped")

    testCase "overrides each session independently" (fun () ->
      let sessions = [ mk "live" "starting"; mk "dead" "running" ]
      let getState sid =
        if sid = "live" then SessionState.Ready else SessionState.Faulted
      let r = open' getState sessions
      Expect.equal (List.head r).Status "running" "live becomes running"
      Expect.equal (r |> List.item 1).Status "faulted" "dead becomes faulted")
  ]

/// Tests for TUI chrome filtering in session parsing (Bug #2)
[<Tests>]
let ghostSessionTests =
  testList "Ghost session filtering (Bug #2)" [
    testCase "filters TUI keyboard shortcut lines" (fun () ->
      let input = "  session-abc [running] *\n↑↓ nav · Enter switch"
      let sessions = SageFs.Server.Dashboard.parseSessionLines input
      Expect.equal sessions.Length 1 "only real session, no ghost from shortcuts")

    testCase "filters box-drawing border lines" (fun () ->
      let input = "  session-abc [running] *\n──────────"
      let sessions = SageFs.Server.Dashboard.parseSessionLines input
      Expect.equal sessions.Length 1 "border lines filtered")

    testCase "filters spinner lines" (fun () ->
      let input = "  session-abc [running] *\n⏳ Loading..."
      let sessions = SageFs.Server.Dashboard.parseSessionLines input
      Expect.equal sessions.Length 1 "spinner lines filtered")

    testCase "filters Ctrl+Tab cycle lines" (fun () ->
      let input = "  session-abc [running] *\nCtrl+Tab cycle sessions"
      let sessions = SageFs.Server.Dashboard.parseSessionLines input
      Expect.equal sessions.Length 1 "Ctrl+Tab line filtered")

    testCase "preserves all valid sessions" (fun () ->
      let input = "  session-abc [running] *\n  session-def [starting]"
      let sessions = SageFs.Server.Dashboard.parseSessionLines input
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
  type ConnCounts = { Browsers: int; McpAgents: int; Terminals: int }

  let formatConnectionCounts (total: int) (allCounts: ConnCounts) =
    let parts =
      [ if allCounts.Browsers > 0 then sprintf "🌐 %d" allCounts.Browsers
        if allCounts.McpAgents > 0 then sprintf "🤖 %d" allCounts.McpAgents
        if allCounts.Terminals > 0 then sprintf "💻 %d" allCounts.Terminals ]
    if parts.IsEmpty then sprintf "%d connected" total
    else System.String.Join(" ", parts)

[<Tests>]
let connectionCountTests =
  let fmt = ConnectionCountDisplay.formatConnectionCounts
  let mk b m t : ConnectionCountDisplay.ConnCounts = { Browsers = b; McpAgents = m; Terminals = t }
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
  let parse = SageFs.Server.Dashboard.parseSessionLines
  let override' = SageFs.Server.Dashboard.overrideSessionStatuses
  testList "Stopped session filtering" [
    testCase "stopped sessions are filtered from rendered list" (fun () ->
      // Simulate: two sessions parsed from TUI, one has live state Uninitialized (= stopped)
      let input = "  sess-a [running] *\n  sess-b [running]"
      let parsed = parse input
      let getState id =
        if id = "sess-b" then SageFs.SessionState.Uninitialized
        else SageFs.SessionState.Ready
      let corrected = override' getState parsed
      let visible = corrected |> List.filter (fun s -> s.Status <> "stopped")
      Expect.equal visible.Length 1 "stopped session filtered out"
      Expect.equal visible.[0].Id "sess-a" "only running session remains")

    testCase "faulted sessions are kept visible" (fun () ->
      let input = "  sess-a [running] *\n  sess-b [running]"
      let parsed = parse input
      let getState id =
        if id = "sess-b" then SageFs.SessionState.Faulted
        else SageFs.SessionState.Ready
      let corrected = override' getState parsed
      let visible = corrected |> List.filter (fun s -> s.Status <> "stopped")
      Expect.equal visible.Length 2 "faulted session stays visible")

    testCase "all stopped results in empty list" (fun () ->
      let input = "  sess-a [running]\n  sess-b [starting]"
      let parsed = parse input
      let getState _ = SageFs.SessionState.Uninitialized
      let corrected = override' getState parsed
      let visible = corrected |> List.filter (fun s -> s.Status <> "stopped")
      Expect.isEmpty visible "all stopped = empty list")
  ]
