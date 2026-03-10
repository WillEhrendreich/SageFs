module SageFs.Tests.SessionPredicatesAndUiTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.WorkerProtocol
open SageFs.Features.AutoCompletion
open SageFs.Tests.SharedGenerators

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
  testList "tryParse reachability (from allFixedEntries)" [
    for (input, expected) in UiAction.allFixedEntries do
      test (sprintf "parses '%s'" input) {
        UiAction.tryParse input
        |> Expect.equal (sprintf "'%s'" input) (Some expected)
      }
  ]
  testList "tryParse prefix handlers" [
    test "TogglePane.editor" {
      UiAction.tryParse "TogglePane.editor"
      |> Expect.equal "TogglePane" (Some (UiAction.TogglePane PaneId.Editor))
    }
    test "Layout.wide" {
      UiAction.tryParse "Layout.wide"
      |> Expect.equal "Layout" (Some (UiAction.LayoutPreset "wide"))
    }
  ]
  test "unknown returns None" {
    UiAction.tryParse "NotAValidAction" |> Expect.isNone "unknown"
  }
  test "every DU case tag is covered by allFixedEntries or prefix handlers" {
    let allCaseTags =
      FSharp.Reflection.FSharpType.GetUnionCases(typeof<UiAction>)
      |> Array.map (fun c -> c.Name)
      |> Set.ofArray
    let mapCoveredTags =
      UiAction.allFixedEntries
      |> List.map (fun (_, action) ->
        let case, _ = FSharp.Reflection.FSharpValue.GetUnionFields(action, typeof<UiAction>)
        case.Name)
      |> Set.ofList
    // TogglePane and LayoutPreset are covered by prefix matching
    let prefixCoveredTags = set [ "TogglePane"; "LayoutPreset" ]
    let allCovered = Set.union mapCoveredTags prefixCoveredTags
    let missing = Set.difference allCaseTags allCovered
    missing |> Set.isEmpty
    |> Expect.isTrue (sprintf "UiAction DU cases missing from parse: %A" missing)
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

let propertyTests = testList "properties" [
  testPropertyWithConfig propConfig "SessionStatus label/parse roundtrip" <|
    Prop.forAll (Arb.fromGen genSessionStatus) (fun status ->
      SessionStatus.label status
      |> SessionStatus.parse
      |> Expect.equal "roundtrip" (Ok status))

  testPropertyWithConfig propConfig "Building with adversarial reasons roundtrips" <|
    Prop.forAll (Arb.fromGen genBuildReason) (fun reason ->
      let status = SessionStatus.Building reason
      SessionStatus.label status
      |> SessionStatus.parse
      |> Expect.equal (sprintf "roundtrip for '%s'" reason) (Ok status))

  testPropertyWithConfig propConfig "isAlive and isTerminal are complementary" <|
    Prop.forAll (Arb.fromGen genSessionStatus) (fun status ->
      let alive = SessionStatus.isAlive status
      let terminal = match status with SessionStatus.Faulted | SessionStatus.Stopped -> true | _ -> false
      alive |> Expect.notEqual "alive ≠ terminal" terminal)

  testPropertyWithConfig propConfig "isOperational implies isAlive" <|
    Prop.forAll (Arb.fromGen genSessionStatus) (fun status ->
      match SessionStatus.isOperational status with
      | true -> SessionStatus.isAlive status |> Expect.isTrue "operational ⇒ alive"
      | false -> ())

  testPropertyWithConfig propConfig "toSessionState is total — never throws" <|
    Prop.forAll (Arb.fromGen genSessionStatus) (fun status ->
      let _ = SessionStatus.toSessionState status
      true |> Expect.isTrue "no exception")

  testPropertyWithConfig propConfig "parse rejects arbitrary non-status strings" <|
    fun (NonEmptyString s) ->
      match SessionStatus.parse s with
      | Ok _ -> ()
      | Error msg ->
        msg.Contains("Unknown", StringComparison.Ordinal)
        |> Expect.isTrue "error mentions 'Unknown'"

  testPropertyWithConfig propConfig "scoreCandidate is non-negative" <|
    fun (NonEmptyString candidate) ->
      let entered = candidate.[..min 2 (candidate.Length - 1)]
      let score = scoreCandidate entered candidate
      (score, 0) |> Expect.isGreaterThanOrEqual "non-negative"

  testPropertyWithConfig propConfig "scoreCandidate: exact match >= prefix match" <|
    fun (NonEmptyString s) ->
      let exactScore = scoreCandidate s s
      let prefixScore = scoreCandidate s (s + "Suffix")
      (exactScore, prefixScore) |> Expect.isGreaterThanOrEqual "exact >= prefix"
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
  propertyTests
]
