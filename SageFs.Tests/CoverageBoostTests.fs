module SageFs.Tests.CoverageBoostTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Features.AutoCompletion

/// Tests targeting low-coverage pure functions identified by code coverage analysis.
/// Covers: SessionStatus predicates, SessionLifecycle decisions, KeyCombo roundtrips,
/// UiAction reachability, SageFsError.describe completeness, scoreCandidate properties.

let sessionStatusPredicateTests = testList "SessionStatus predicates" [
  test "label and parse roundtrip for all cases" {
    let allStatuses = [
      SessionStatus.Starting; SessionStatus.Ready; SessionStatus.Evaluating
      SessionStatus.Faulted; SessionStatus.Restarting; SessionStatus.Stopped
    ]
    for s in allStatuses do
      SessionStatus.label s
      |> SessionStatus.parse
      |> Expect.equal (sprintf "roundtrip %A" s) (Ok s)
  }
  test "parse rejects unknown input" {
    SessionStatus.parse "Borked"
    |> Result.isError
    |> Expect.isTrue "should be Error"
  }
  test "isOperational only for Ready" {
    SessionStatus.isOperational SessionStatus.Ready
    |> Expect.isTrue "Ready is operational"
    SessionStatus.isOperational SessionStatus.Starting
    |> Expect.isFalse "Starting is not operational"
    SessionStatus.isOperational SessionStatus.Faulted
    |> Expect.isFalse "Faulted is not operational"
  }
  test "isAlive for active statuses" {
    SessionStatus.isAlive SessionStatus.Starting |> Expect.isTrue "Starting is alive"
    SessionStatus.isAlive SessionStatus.Ready |> Expect.isTrue "Ready is alive"
    SessionStatus.isAlive SessionStatus.Evaluating |> Expect.isTrue "Evaluating is alive"
    SessionStatus.isAlive SessionStatus.Restarting |> Expect.isTrue "Restarting is alive"
    SessionStatus.isAlive SessionStatus.Faulted |> Expect.isFalse "Faulted is not alive"
    SessionStatus.isAlive SessionStatus.Stopped |> Expect.isFalse "Stopped is not alive"
  }
  test "toSessionState maps all cases" {
    SessionStatus.toSessionState SessionStatus.Starting |> Expect.equal "Starting" SessionState.WarmingUp
    SessionStatus.toSessionState SessionStatus.Ready |> Expect.equal "Ready" SessionState.Ready
    SessionStatus.toSessionState SessionStatus.Evaluating |> Expect.equal "Evaluating" SessionState.Evaluating
    SessionStatus.toSessionState SessionStatus.Faulted |> Expect.equal "Faulted" SessionState.Faulted
    SessionStatus.toSessionState SessionStatus.Restarting |> Expect.equal "Restarting" SessionState.WarmingUp
    SessionStatus.toSessionState SessionStatus.Stopped |> Expect.equal "Stopped" SessionState.Faulted
  }
]

let workerDiagnosticTests = testList "WorkerDiagnostic" [
  test "toDiagnostic preserves all fields" {
    let wd : WorkerDiagnostic = {
      Severity = Features.Diagnostics.DiagnosticSeverity.Error
      Message = "boom"
      StartLine = 1; StartColumn = 2; EndLine = 3; EndColumn = 4
    }
    let d = WorkerDiagnostic.toDiagnostic wd
    d.Message |> Expect.equal "message" "boom"
    d.Severity |> Expect.equal "severity" Features.Diagnostics.DiagnosticSeverity.Error
    d.Range.StartLine |> Expect.equal "startLine" 1
    d.Range.StartColumn |> Expect.equal "startCol" 2
    d.Range.EndLine |> Expect.equal "endLine" 3
    d.Range.EndColumn |> Expect.equal "endCol" 4
    d.Subcategory |> Expect.equal "subcategory" ""
  }
]

let sessionLifecycleTests = testList "SessionLifecycle" [
  let policy = RestartPolicy.defaultPolicy
  let now = DateTime(2024, 1, 1, 12, 0, 0)

  testList "onWorkerExited" [
    test "exit code 0 is Graceful" {
      match SessionLifecycle.onWorkerExited policy RestartPolicy.emptyState 0 now with
      | SessionLifecycle.ExitOutcome.Graceful -> ()
      | other -> failwithf "Expected Graceful, got %A" other
    }
    test "exit code 1 with fresh state triggers restart" {
      match SessionLifecycle.onWorkerExited policy RestartPolicy.emptyState 1 now with
      | SessionLifecycle.ExitOutcome.RestartAfter(delay, newState) ->
        (delay.TotalMilliseconds, 0.0) |> Expect.isGreaterThan "delay > 0"
        newState.RestartCount |> Expect.equal "count" 1
      | other -> failwithf "Expected RestartAfter, got %A" other
    }
    test "exceeding max restarts triggers Abandoned" {
      let exhaustedState : RestartPolicy.State = {
        RestartCount = policy.MaxRestarts
        LastRestartAt = Some now
        WindowStart = Some now
      }
      match SessionLifecycle.onWorkerExited policy exhaustedState 1 now with
      | SessionLifecycle.ExitOutcome.Abandoned _ -> ()
      | other -> failwithf "Expected Abandoned, got %A" other
    }
    test "window expiry resets count allowing restart" {
      let oldState : RestartPolicy.State = {
        RestartCount = policy.MaxRestarts
        LastRestartAt = Some now
        WindowStart = Some (now.AddMinutes(-10.0))
      }
      let later = now.AddMinutes(10.0)
      match SessionLifecycle.onWorkerExited policy oldState 1 later with
      | SessionLifecycle.ExitOutcome.RestartAfter _ -> ()
      | other -> failwithf "Expected RestartAfter after window reset, got %A" other
    }
  ]

  testList "statusAfterExit" [
    test "Graceful maps to Stopped" {
      SessionLifecycle.statusAfterExit SessionLifecycle.ExitOutcome.Graceful
      |> Expect.equal "stopped" SessionStatus.Stopped
    }
    test "RestartAfter maps to Restarting" {
      SessionLifecycle.statusAfterExit
        (SessionLifecycle.ExitOutcome.RestartAfter(TimeSpan.FromSeconds 1.0, RestartPolicy.emptyState))
      |> Expect.equal "restarting" SessionStatus.Restarting
    }
    test "Abandoned maps to Faulted" {
      SessionLifecycle.statusAfterExit (SessionLifecycle.ExitOutcome.Abandoned SageFsError.PipeClosed)
      |> Expect.equal "faulted" SessionStatus.Faulted
    }
  ]
]

let keyComboTests = testList "KeyCombo" [
  testList "tryParse and format roundtrip" [
    let roundtrips = [
      "Ctrl+Q"; "Alt+Up"; "Ctrl+Shift+Z"; "Enter"; "Escape"
      "Tab"; "Space"; "Backspace"; "Delete"; "Home"; "End"
      "PageUp"; "PageDown"; "Ctrl+A"; "Alt+F"; "Shift+Enter"
      "Ctrl+Alt+T"; "Ctrl+Shift+A"; "Up"; "Down"; "Left"; "Right"
    ]
    for input in roundtrips do
      test (sprintf "roundtrip '%s'" input) {
        match KeyCombo.tryParse input with
        | Some kc ->
          let formatted = KeyCombo.format kc
          match KeyCombo.tryParse formatted with
          | Some kc2 -> kc2 |> Expect.equal (sprintf "roundtrip %s" input) kc
          | None -> failwithf "format produced unparseable: '%s'" formatted
        | None -> failwithf "tryParse failed on '%s'" input
      }
  ]
  testList "aliases" [
    test "Ctrl and Control parse the same" {
      KeyCombo.tryParse "Ctrl+A"
      |> Expect.equal "same combo" (KeyCombo.tryParse "Control+A")
    }
    test "Esc and Escape parse the same" {
      KeyCombo.tryParse "Esc"
      |> Expect.equal "same" (KeyCombo.tryParse "Escape")
    }
    test "case insensitive" {
      KeyCombo.tryParse "ctrl+a"
      |> Expect.equal "case" (KeyCombo.tryParse "CTRL+A")
    }
  ]
  testList "rejects" [
    test "empty string" {
      KeyCombo.tryParse "" |> Expect.isNone "empty"
    }
    test "only modifiers no key" {
      KeyCombo.tryParse "Ctrl+Alt" |> Expect.isNone "mods only"
    }
  ]
  testList "format" [
    test "Ctrl+Q formats correctly" {
      KeyCombo.format (KeyCombo.ctrl ConsoleKey.Q)
      |> Expect.equal "format" "Ctrl+Q"
    }
    test "special keys format nicely" {
      KeyCombo.format (KeyCombo.plain ConsoleKey.UpArrow)
      |> Expect.equal "up" "Up"
      KeyCombo.format (KeyCombo.plain ConsoleKey.Spacebar)
      |> Expect.equal "space" "Space"
    }
  ]
]

let uiActionTests = testList "UiAction" [
  testList "tryParse reachability" [
    let reachableCases = [
      "Quit", UiAction.Quit
      "CycleFocus", UiAction.CycleFocus
      "FocusLeft", UiAction.FocusDir Direction.Left
      "FocusRight", UiAction.FocusDir Direction.Right
      "FocusUp", UiAction.FocusDir Direction.Up
      "FocusDown", UiAction.FocusDir Direction.Down
      "ScrollUp", UiAction.ScrollUp
      "ScrollDown", UiAction.ScrollDown
      "Redraw", UiAction.Redraw
      "FontSizeUp", UiAction.FontSizeUp
      "FontSizeDown", UiAction.FontSizeDown
      "Submit", UiAction.Editor EditorAction.Submit
      "NewLine", UiAction.Editor EditorAction.NewLine
      "Cancel", UiAction.Editor EditorAction.Cancel
      "Undo", UiAction.Editor EditorAction.Undo
      "Redo", UiAction.Editor EditorAction.Redo
      "DeleteBackward", UiAction.Editor EditorAction.DeleteBackward
      "DeleteForward", UiAction.Editor EditorAction.DeleteForward
      "DeleteWord", UiAction.Editor EditorAction.DeleteWord
      "DeleteToEndOfLine", UiAction.Editor EditorAction.DeleteToEndOfLine
      "MoveWordForward", UiAction.Editor EditorAction.MoveWordForward
      "MoveWordBackward", UiAction.Editor EditorAction.MoveWordBackward
      "MoveToLineStart", UiAction.Editor EditorAction.MoveToLineStart
      "MoveToLineEnd", UiAction.Editor EditorAction.MoveToLineEnd
      "MoveUp", UiAction.Editor (EditorAction.MoveCursor Direction.Up)
      "MoveDown", UiAction.Editor (EditorAction.MoveCursor Direction.Down)
      "MoveLeft", UiAction.Editor (EditorAction.MoveCursor Direction.Left)
      "MoveRight", UiAction.Editor (EditorAction.MoveCursor Direction.Right)
      "SelectAll", UiAction.Editor EditorAction.SelectAll
      "SelectWord", UiAction.Editor EditorAction.SelectWord
      "TriggerCompletion", UiAction.Editor EditorAction.TriggerCompletion
      "AcceptCompletion", UiAction.Editor EditorAction.AcceptCompletion
      "DismissCompletion", UiAction.Editor EditorAction.DismissCompletion
      "NextCompletion", UiAction.Editor EditorAction.NextCompletion
      "PreviousCompletion", UiAction.Editor EditorAction.PreviousCompletion
      "HistoryPrevious", UiAction.Editor EditorAction.HistoryPrevious
      "HistoryNext", UiAction.Editor EditorAction.HistoryNext
      "ListSessions", UiAction.Editor EditorAction.ListSessions
      "ToggleSessionPanel", UiAction.Editor EditorAction.ToggleSessionPanel
      "CreateSession", UiAction.Editor (EditorAction.CreateSession [])
      "ResetSession", UiAction.Editor EditorAction.ResetSession
      "HardResetSession", UiAction.Editor EditorAction.HardResetSession
      "SessionNavUp", UiAction.Editor EditorAction.SessionNavUp
      "SessionNavDown", UiAction.Editor EditorAction.SessionNavDown
      "SessionSelect", UiAction.Editor EditorAction.SessionSelect
      "SessionDelete", UiAction.Editor EditorAction.SessionDelete
      "SessionStopOthers", UiAction.Editor EditorAction.SessionStopOthers
      "SessionCycleNext", UiAction.Editor EditorAction.SessionCycleNext
      "SessionCyclePrev", UiAction.Editor EditorAction.SessionCyclePrev
      "ClearOutput", UiAction.Editor EditorAction.ClearOutput
      "PromptConfirm", UiAction.Editor EditorAction.PromptConfirm
      "PromptCancel", UiAction.Editor EditorAction.PromptCancel
      "CycleTheme", UiAction.CycleTheme
      "HotReloadWatchAll", UiAction.HotReloadWatchAll
      "HotReloadUnwatchAll", UiAction.HotReloadUnwatchAll
      "EnableLiveTesting", UiAction.EnableLiveTesting
      "DisableLiveTesting", UiAction.DisableLiveTesting
      "CycleRunPolicy", UiAction.CycleRunPolicy
      "ToggleCoverage", UiAction.ToggleCoverage
      "ResizeHGrow", UiAction.ResizeH 1
      "ResizeHShrink", UiAction.ResizeH -1
      "ResizeVGrow", UiAction.ResizeV 1
      "ResizeVShrink", UiAction.ResizeV -1
      "ResizeRGrow", UiAction.ResizeR 1
      "ResizeRShrink", UiAction.ResizeR -1
      "TogglePane.editor", UiAction.TogglePane "editor"
      "Layout.wide", UiAction.LayoutPreset "wide"
    ]
    for (input, expected) in reachableCases do
      test (sprintf "parses '%s'" input) {
        UiAction.tryParse input
        |> Expect.equal (sprintf "'%s'" input) (Some expected)
      }
  ]
  test "unknown returns None" {
    UiAction.tryParse "NotAValidAction" |> Expect.isNone "unknown"
  }
]

let sageFsErrorTests = testList "SageFsError.describe" [
  let allCases : (SageFsError * string list) list = [
    SageFsError.ToolNotAvailable("eval", SessionState.Faulted, ["list_sessions"]),
      ["eval"; "Faulted"; "list_sessions"]
    SageFsError.SessionNotFound "abc123",
      ["abc123"; "list_sessions"]
    SageFsError.NoActiveSessions,
      ["No active sessions"; "create_session"]
    SageFsError.AmbiguousSessions ["session1"; "session2"],
      ["Multiple sessions"; "session1"; "session2"]
    SageFsError.SessionCreationFailed "out of memory",
      ["create session"; "out of memory"]
    SageFsError.SessionStopFailed("s1", "busy"),
      ["s1"; "busy"]
    SageFsError.WorkerCommunicationFailed("s2", "timeout"),
      ["s2"; "timeout"]
    SageFsError.WorkerSpawnFailed "no dotnet",
      ["worker"; "no dotnet"]
    SageFsError.PipeClosed,
      ["Pipe closed"]
    SageFsError.EvalFailed "syntax error",
      ["Evaluation failed"; "syntax error"]
    SageFsError.ResetFailed "locked",
      ["Reset failed"; "locked"]
    SageFsError.HardResetFailed "rebuild failed",
      ["Hard reset failed"; "rebuild failed"]
    SageFsError.ScriptLoadFailed "not found",
      ["Script load failed"; "not found"]
    SageFsError.WarmupOpenFailed("System.IO", "missing"),
      ["System.IO"; "missing"]
    SageFsError.RestartLimitExceeded(5, 5.0),
      ["5"; "times"; "5"; "minutes"]
    SageFsError.DaemonStartFailed "port in use",
      ["daemon"; "port in use"]
    SageFsError.Unexpected(exn "kaboom"),
      ["Unexpected"; "kaboom"]
  ]
  for (error, expectedSubstrings) in allCases do
    let name =
      sprintf "%A" error
      |> fun s -> if s.Length > 60 then s.[..59] + "..." else s
    test (sprintf "%s" name) {
      let desc = SageFsError.describe error
      desc.Length |> fun len -> (len, 0) |> Expect.isGreaterThan "non-empty"
      for sub in expectedSubstrings do
        desc.Contains(sub, StringComparison.OrdinalIgnoreCase)
        |> Expect.isTrue (sprintf "should contain '%s' in '%s'" sub desc)
    }
]

let scoreCandidateTests = testList "scoreCandidate" [
  test "prefix match scores higher than non-prefix" {
    let prefixScore = scoreCandidate "add" "addNumbers"
    let nonPrefixScore = scoreCandidate "add" "xAddNumbers"
    (prefixScore, nonPrefixScore) |> Expect.isGreaterThan "prefix > non-prefix"
  }
  test "exact match scores highest" {
    let exact = scoreCandidate "add" "add"
    let prefix = scoreCandidate "add" "addNumbers"
    let unrelated = scoreCandidate "add" "multiply"
    (exact, prefix) |> Expect.isGreaterThanOrEqual "exact >= prefix"
    (exact, unrelated) |> Expect.isGreaterThan "exact > unrelated"
  }
  test "score is non-negative" {
    let score = scoreCandidate "x" "superlongcandidatename"
    (score, 0) |> Expect.isGreaterThanOrEqual "non-negative"
  }
  test "shorter candidates score higher via length penalty" {
    let shortScore = scoreCandidate "a" "ab"
    let longScore = scoreCandidate "a" "abcdefghijklmnop"
    (shortScore, longScore) |> Expect.isGreaterThan "short > long"
  }
  test "empty entered word still produces score" {
    let score = scoreCandidate "" "anything"
    (score, 0) |> Expect.isGreaterThanOrEqual "non-negative for empty"
  }
]

[<Tests>]
let tests = testList "CoverageBoost" [
  sessionStatusPredicateTests
  workerDiagnosticTests
  sessionLifecycleTests
  keyComboTests
  uiActionTests
  sageFsErrorTests
  scoreCandidateTests
]
