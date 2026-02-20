module SageFs.Tests.ReplayTests

open System
open Expecto
open SageFs.Features.Events
open SageFs.Features.Replay

let ts = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
let mkTs offset = ts.AddMinutes(float offset)

let sessionStarted at' =
  SessionStarted {| Config = Map.empty; StartedAt = at' |}

let evalCompleted code result dur =
  EvalCompleted {| Code = code; Result = result; TypeSignature = None; Duration = dur |}

let evalFailed code err =
  EvalFailed {| Code = code; Error = err; Diagnostics = [] |}

let evalRequested code =
  EvalRequested {| Code = code; Source = Console |}

let allEventTypes : (DateTimeOffset * SageFsEvent) list =
  [
    mkTs 0, sessionStarted (mkTs 0)
    mkTs 1, SessionWarmUpCompleted {| Duration = TimeSpan.FromSeconds 2.0; Errors = [] |}
    mkTs 2, SessionReady
    mkTs 3, evalRequested "let x = 1"
    mkTs 4, evalCompleted "let x = 1" "val x: int = 1" (TimeSpan.FromMilliseconds 50.0)
    mkTs 5, evalRequested "bad"
    mkTs 6, evalFailed "bad" "parse error"
    mkTs 7, SessionReset
    mkTs 8, SessionReady
    mkTs 9, SessionHardReset {| Rebuild = true |}
    mkTs 10, SessionReady
    mkTs 11, DiagnosticsChecked {| Code = "let x"; Diagnostics = []; Source = Console |}
    mkTs 12, DiagnosticsCleared
    mkTs 13, ScriptLoaded {| FilePath = "test.fsx"; StatementCount = 3; Source = Console |}
    mkTs 14, ScriptLoadFailed {| FilePath = "bad.fsx"; Error = "not found" |}
    mkTs 15, McpInputReceived {| Source = McpAgent "claude"; Content = "let y = 2" |}
    mkTs 16, McpOutputSent {| Source = McpAgent "claude"; Content = "val y: int = 2" |}
    mkTs 17, SessionFaulted {| Error = "critical"; StackTrace = Some "at X.Y()" |}
  ]

let makeRandomEvents (seed: int) =
  let r = Random(seed)
  let count = r.Next(1, 50)
  [ for i in 0 .. count - 1 ->
      mkTs i,
      match r.Next(14) with
      | 0 -> sessionStarted (mkTs i)
      | 1 -> SessionWarmUpCompleted {| Duration = TimeSpan.FromSeconds 1.0; Errors = [] |}
      | 2 -> SessionReady
      | 3 -> SessionFaulted {| Error = "err"; StackTrace = None |}
      | 4 -> SessionReset
      | 5 -> SessionHardReset {| Rebuild = true |}
      | 6 -> evalRequested "code"
      | 7 -> evalCompleted "code" "result" (TimeSpan.FromMilliseconds 10.0)
      | 8 -> evalFailed "code" "error"
      | 9 -> DiagnosticsChecked {| Code = "x"; Diagnostics = []; Source = Console |}
      | 10 -> DiagnosticsCleared
      | 11 -> ScriptLoaded {| FilePath = "s.fsx"; StatementCount = 1; Source = Console |}
      | 12 -> ScriptLoadFailed {| FilePath = "s.fsx"; Error = "err" |}
      | 13 -> McpInputReceived {| Source = Console; Content = "x" |}
      | _ -> McpOutputSent {| Source = Console; Content = "x" |}
  ]

let applyEventTests =
  testList "Replay.applyEvent" [
    testList "empty state" [
      testCase "starts with NotStarted status" <| fun _ ->
        Expect.equal SessionReplayState.empty.Status NotStarted "status"
      testCase "starts with zero eval count" <| fun _ ->
        Expect.equal SessionReplayState.empty.EvalCount 0 "evalCount"
      testCase "starts with empty history" <| fun _ ->
        Expect.isEmpty SessionReplayState.empty.EvalHistory "history"
      testCase "starts with no startedAt" <| fun _ ->
        Expect.isNone SessionReplayState.empty.StartedAt "startedAt"
    ]

    testList "SessionStarted" [
      testCase "sets status to WarmingUp" <| fun _ ->
        let s = SessionReplayState.applyEvent (mkTs 0) SessionReplayState.empty (sessionStarted (mkTs 0))
        Expect.equal s.Status WarmingUp "status"
      testCase "sets startedAt" <| fun _ ->
        let at' = mkTs 0
        let s = SessionReplayState.applyEvent at' SessionReplayState.empty (sessionStarted at')
        Expect.equal s.StartedAt (Some at') "startedAt"
    ]

    testList "SessionReady" [
      testCase "sets status to Ready" <| fun _ ->
        let s =
          SessionReplayState.empty
          |> fun st -> SessionReplayState.applyEvent (mkTs 0) st (sessionStarted (mkTs 0))
          |> fun st -> SessionReplayState.applyEvent (mkTs 1) st SessionReady
        Expect.equal s.Status Ready "status"
    ]

    testList "EvalCompleted" [
      testCase "increments eval count" <| fun _ ->
        let s =
          SessionReplayState.empty
          |> fun st -> SessionReplayState.applyEvent (mkTs 0) st (sessionStarted (mkTs 0))
          |> fun st -> SessionReplayState.applyEvent (mkTs 1) st SessionReady
          |> fun st -> SessionReplayState.applyEvent (mkTs 2) st (evalRequested "let x = 1")
          |> fun st -> SessionReplayState.applyEvent (mkTs 3) st (evalCompleted "let x = 1" "val x: int = 1" (TimeSpan.FromMilliseconds 50.0))
        Expect.equal s.EvalCount 1 "evalCount"
      testCase "sets status back to Ready" <| fun _ ->
        let s =
          SessionReplayState.empty
          |> fun st -> SessionReplayState.applyEvent (mkTs 0) st (sessionStarted (mkTs 0))
          |> fun st -> SessionReplayState.applyEvent (mkTs 1) st SessionReady
          |> fun st -> SessionReplayState.applyEvent (mkTs 2) st (evalRequested "let x = 1")
          |> fun st -> SessionReplayState.applyEvent (mkTs 3) st (evalCompleted "let x = 1" "val x: int = 1" (TimeSpan.FromMilliseconds 50.0))
        Expect.equal s.Status Ready "status"
      testCase "records in history" <| fun _ ->
        let s =
          SessionReplayState.empty
          |> fun st -> SessionReplayState.applyEvent (mkTs 0) st (sessionStarted (mkTs 0))
          |> fun st -> SessionReplayState.applyEvent (mkTs 1) st SessionReady
          |> fun st -> SessionReplayState.applyEvent (mkTs 2) st (evalRequested "let x = 1")
          |> fun st -> SessionReplayState.applyEvent (mkTs 3) st (evalCompleted "let x = 1" "val x: int = 1" (TimeSpan.FromMilliseconds 50.0))
        Expect.equal s.EvalHistory.Length 1 "history count"
        Expect.equal s.EvalHistory.[0].Code "let x = 1" "history code"
        Expect.equal s.EvalHistory.[0].Result "val x: int = 1" "history result"
      testCase "sets last result" <| fun _ ->
        let s =
          SessionReplayState.empty
          |> fun st -> SessionReplayState.applyEvent (mkTs 0) st (sessionStarted (mkTs 0))
          |> fun st -> SessionReplayState.applyEvent (mkTs 1) st SessionReady
          |> fun st -> SessionReplayState.applyEvent (mkTs 2) st (evalRequested "let x = 1")
          |> fun st -> SessionReplayState.applyEvent (mkTs 3) st (evalCompleted "let x = 1" "val x: int = 1" (TimeSpan.FromMilliseconds 50.0))
        Expect.equal s.LastEvalResult (Some "val x: int = 1") "last result"
    ]

    testList "EvalFailed" [
      testCase "increments failed count" <| fun _ ->
        let s =
          SessionReplayState.empty
          |> fun st -> SessionReplayState.applyEvent (mkTs 0) st (sessionStarted (mkTs 0))
          |> fun st -> SessionReplayState.applyEvent (mkTs 1) st SessionReady
          |> fun st -> SessionReplayState.applyEvent (mkTs 2) st (evalRequested "bad code")
          |> fun st -> SessionReplayState.applyEvent (mkTs 3) st (evalFailed "bad code" "syntax error")
        Expect.equal s.FailedEvalCount 1 "failedEvalCount"
        Expect.equal s.EvalCount 0 "evalCount unchanged"
      testCase "sets status back to Ready" <| fun _ ->
        let s =
          SessionReplayState.empty
          |> fun st -> SessionReplayState.applyEvent (mkTs 0) st (sessionStarted (mkTs 0))
          |> fun st -> SessionReplayState.applyEvent (mkTs 1) st SessionReady
          |> fun st -> SessionReplayState.applyEvent (mkTs 2) st (evalRequested "bad code")
          |> fun st -> SessionReplayState.applyEvent (mkTs 3) st (evalFailed "bad code" "syntax error")
        Expect.equal s.Status Ready "status"
    ]

    testList "SessionReset" [
      testCase "increments reset count" <| fun _ ->
        let s =
          SessionReplayState.empty
          |> fun st -> SessionReplayState.applyEvent (mkTs 0) st (sessionStarted (mkTs 0))
          |> fun st -> SessionReplayState.applyEvent (mkTs 1) st SessionReady
          |> fun st -> SessionReplayState.applyEvent (mkTs 2) st SessionReset
        Expect.equal s.ResetCount 1 "resetCount"
      testCase "sets status to WarmingUp" <| fun _ ->
        let s =
          SessionReplayState.empty
          |> fun st -> SessionReplayState.applyEvent (mkTs 0) st (sessionStarted (mkTs 0))
          |> fun st -> SessionReplayState.applyEvent (mkTs 1) st SessionReady
          |> fun st -> SessionReplayState.applyEvent (mkTs 2) st SessionReset
        Expect.equal s.Status WarmingUp "status"
    ]

    testList "SessionHardReset" [
      testCase "increments hard reset count" <| fun _ ->
        let s =
          SessionReplayState.empty
          |> fun st -> SessionReplayState.applyEvent (mkTs 0) st (sessionStarted (mkTs 0))
          |> fun st -> SessionReplayState.applyEvent (mkTs 1) st SessionReady
          |> fun st -> SessionReplayState.applyEvent (mkTs 2) st (SessionHardReset {| Rebuild = true |})
        Expect.equal s.HardResetCount 1 "hardResetCount"
      testCase "sets status to WarmingUp" <| fun _ ->
        let s =
          SessionReplayState.empty
          |> fun st -> SessionReplayState.applyEvent (mkTs 0) st (sessionStarted (mkTs 0))
          |> fun st -> SessionReplayState.applyEvent (mkTs 1) st SessionReady
          |> fun st -> SessionReplayState.applyEvent (mkTs 2) st (SessionHardReset {| Rebuild = false |})
        Expect.equal s.Status WarmingUp "status"
    ]

    testList "SessionFaulted" [
      testCase "sets status to Faulted" <| fun _ ->
        let s =
          SessionReplayState.empty
          |> fun st -> SessionReplayState.applyEvent (mkTs 0) st (sessionStarted (mkTs 0))
          |> fun st -> SessionReplayState.applyEvent (mkTs 1) st (SessionFaulted {| Error = "boom"; StackTrace = None |})
        Expect.equal s.Status (Faulted "boom") "status"
    ]

    testList "LastActivity" [
      testCase "every event updates lastActivity" <| fun _ ->
        let t0 = mkTs 0
        let t1 = mkTs 5
        let s =
          SessionReplayState.empty
          |> fun st -> SessionReplayState.applyEvent t0 st (sessionStarted t0)
          |> fun st -> SessionReplayState.applyEvent t1 st SessionReady
        Expect.equal s.LastActivity (Some t1) "lastActivity should be latest timestamp"
    ]
  ]

let replayStreamTests =
  testList "Replay.replayStream" [
    testCase "replaying same events twice produces identical state" <| fun _ ->
      let s1 = SessionReplayState.replayStream allEventTypes
      let s2 = SessionReplayState.replayStream allEventTypes
      Expect.equal s1 s2 "deterministic replay"

    testCase "replaying full lifecycle gives correct counts" <| fun _ ->
      let s = SessionReplayState.replayStream allEventTypes
      Expect.equal s.EvalCount 1 "one successful eval"
      Expect.equal s.FailedEvalCount 1 "one failed eval"
      Expect.equal s.ResetCount 1 "one soft reset"
      Expect.equal s.HardResetCount 1 "one hard reset"

    testCase "empty stream gives empty state" <| fun _ ->
      let s = SessionReplayState.replayStream []
      Expect.equal s SessionReplayState.empty "empty replay = empty state"

    testCase "started but no evals has zero counts" <| fun _ ->
      let events = [
        mkTs 0, sessionStarted (mkTs 0)
        mkTs 1, SessionReady
      ]
      let s = SessionReplayState.replayStream events
      Expect.equal s.EvalCount 0 "no evals"
      Expect.equal s.Status Ready "ready"
      Expect.isSome s.StartedAt "has startedAt"

    testCase "warmup failure leads to faulted" <| fun _ ->
      let events = [
        mkTs 0, sessionStarted (mkTs 0)
        mkTs 1, SessionFaulted {| Error = "DLL missing"; StackTrace = None |}
      ]
      let s = SessionReplayState.replayStream events
      Expect.equal s.Status (Faulted "DLL missing") "faulted status"

    testCase "last activity tracks most recent event" <| fun _ ->
      let t5 = mkTs 5
      let events = [
        mkTs 0, sessionStarted (mkTs 0)
        mkTs 1, SessionReady
        mkTs 2, evalRequested "let x = 1"
        t5, evalCompleted "let x = 1" "val x: int = 1" (TimeSpan.FromMilliseconds 50.0)
      ]
      let s = SessionReplayState.replayStream events
      Expect.equal s.LastActivity (Some t5) "lastActivity is timestamp of last event"
  ]

let invariantTests =
  testList "Replay invariants" [
    testCase "counts are never negative for 100 random event streams" <| fun _ ->
      for seed in 0 .. 99 do
        let s = SessionReplayState.replayStream (makeRandomEvents seed)
        Expect.isGreaterThanOrEqual s.EvalCount 0 (sprintf "seed %d: evalCount >= 0" seed)
        Expect.isGreaterThanOrEqual s.FailedEvalCount 0 (sprintf "seed %d: failedEvalCount >= 0" seed)
        Expect.isGreaterThanOrEqual s.ResetCount 0 (sprintf "seed %d: resetCount >= 0" seed)
        Expect.isGreaterThanOrEqual s.HardResetCount 0 (sprintf "seed %d: hardResetCount >= 0" seed)
        Expect.isGreaterThanOrEqual s.EvalHistory.Length 0 (sprintf "seed %d: history >= 0" seed)

    testCase "eval history length matches eval count for 100 random streams" <| fun _ ->
      for seed in 0 .. 99 do
        let s = SessionReplayState.replayStream (makeRandomEvents seed)
        Expect.equal s.EvalHistory.Length s.EvalCount (sprintf "seed %d: history.Length = evalCount" seed)

    testCase "determinism: replaying same stream twice always matches" <| fun _ ->
      for seed in 0 .. 49 do
        let events = makeRandomEvents seed
        let s1 = SessionReplayState.replayStream events
        let s2 = SessionReplayState.replayStream events
        Expect.equal s1 s2 (sprintf "seed %d: deterministic" seed)

    testCase "lastActivity is always Some after any single event" <| fun _ ->
      for (t, evt) in allEventTypes do
        let s = SessionReplayState.applyEvent t SessionReplayState.empty evt
        Expect.isSome s.LastActivity (sprintf "lastActivity after %A" evt)
  ]

let formatTests =
  testList "Replay.format" [
    testCase "formats empty state" <| fun _ ->
      let output = SessionReplayState.format SessionReplayState.empty
      Expect.stringContains output "Not Started" "contains status"
      Expect.stringContains output "Evals: 0 succeeded, 0 failed" "contains eval counts"
    testCase "formats faulted state" <| fun _ ->
      let s = { SessionReplayState.empty with Status = Faulted "critical error" }
      let output = SessionReplayState.format s
      Expect.stringContains output "Faulted: critical error" "contains faulted msg"
    testCase "formats state with evals" <| fun _ ->
      let s =
        { SessionReplayState.empty with
            EvalCount = 5
            FailedEvalCount = 2
            ResetCount = 1
            HardResetCount = 3
            LastEvalResult = Some "val x = 42" }
      let output = SessionReplayState.format s
      Expect.stringContains output "Evals: 5 succeeded, 2 failed" "eval counts"
      Expect.stringContains output "Resets: 1 soft, 3 hard" "reset counts"
      Expect.stringContains output "Last Result: val x = 42" "last result"
  ]

let daemonReplayTests =
  testList "DaemonReplayState.resumeStopsOldId" [
    testCase "alive session before stop" <| fun _ ->
      let events = [
        ts, DaemonSessionCreated {| SessionId = "old-id"; Projects = ["Foo.fsproj"]; WorkingDir = @"C:\Repos\Foo"; CreatedAt = ts |}
      ]
      let state = DaemonReplayState.replayStream events
      let alive = DaemonReplayState.aliveSessions state
      Expect.equal alive.Length 1 "1 alive session"
      Expect.equal alive.[0].SessionId "old-id" "correct id"

    testCase "stopped session is no longer alive" <| fun _ ->
      let events = [
        ts, DaemonSessionCreated {| SessionId = "old-id"; Projects = ["Foo.fsproj"]; WorkingDir = @"C:\Repos\Foo"; CreatedAt = ts |}
        mkTs 1, DaemonSessionStopped {| SessionId = "old-id"; StoppedAt = mkTs 1 |}
      ]
      let state = DaemonReplayState.replayStream events
      let alive = DaemonReplayState.aliveSessions state
      Expect.equal alive.Length 0 "no alive sessions after stop"

    testCase "resume pattern: old stopped + new created = only new alive" <| fun _ ->
      let events = [
        ts, DaemonSessionCreated {| SessionId = "old-id"; Projects = ["Foo.fsproj"]; WorkingDir = @"C:\Repos\Foo"; CreatedAt = ts |}
        mkTs 10, DaemonSessionCreated {| SessionId = "new-id"; Projects = ["Foo.fsproj"]; WorkingDir = @"C:\Repos\Foo"; CreatedAt = mkTs 10 |}
        mkTs 10, DaemonSessionStopped {| SessionId = "old-id"; StoppedAt = mkTs 10 |}
      ]
      let state = DaemonReplayState.replayStream events
      let alive = DaemonReplayState.aliveSessions state
      Expect.equal alive.Length 1 "only new session alive"
      Expect.equal alive.[0].SessionId "new-id" "new id is the alive one"

    testCase "BUG REPRO: resume without stopping old = zombie" <| fun _ ->
      let events = [
        ts, DaemonSessionCreated {| SessionId = "old-id"; Projects = ["Foo.fsproj"]; WorkingDir = @"C:\Repos\Foo"; CreatedAt = ts |}
        mkTs 10, DaemonSessionCreated {| SessionId = "new-id"; Projects = ["Foo.fsproj"]; WorkingDir = @"C:\Repos\Foo"; CreatedAt = mkTs 10 |}
      ]
      let state = DaemonReplayState.replayStream events
      let alive = DaemonReplayState.aliveSessions state
      Expect.equal alive.Length 2 "both alive without stop event (bug scenario)"

    testCase "stopping NEW session still leaves OLD zombie alive" <| fun _ ->
      let events = [
        ts, DaemonSessionCreated {| SessionId = "old-id"; Projects = ["Foo.fsproj"]; WorkingDir = @"C:\Repos\Foo"; CreatedAt = ts |}
        mkTs 10, DaemonSessionCreated {| SessionId = "new-id"; Projects = ["Foo.fsproj"]; WorkingDir = @"C:\Repos\Foo"; CreatedAt = mkTs 10 |}
        mkTs 20, DaemonSessionStopped {| SessionId = "new-id"; StoppedAt = mkTs 20 |}
      ]
      let state = DaemonReplayState.replayStream events
      let alive = DaemonReplayState.aliveSessions state
      Expect.equal alive.Length 1 "old zombie still alive"
      Expect.equal alive.[0].SessionId "old-id" "zombie is the old one"

    testCase "pruneAllSessions generates stop events for all alive" <| fun _ ->
      let events = [
        ts, DaemonSessionCreated {| SessionId = "s1"; Projects = ["A.fsproj"]; WorkingDir = @"C:\A"; CreatedAt = ts |}
        ts, DaemonSessionCreated {| SessionId = "s2"; Projects = ["B.fsproj"]; WorkingDir = @"C:\B"; CreatedAt = ts |}
      ]
      let state = DaemonReplayState.replayStream events
      let pruneEvents = DaemonReplayState.pruneAllSessions state
      Expect.equal pruneEvents.Length 2 "2 stop events generated"
  ]

[<Tests>]
let tests =
  testList "Replay" [
    applyEventTests
    replayStreamTests
    invariantTests
    formatTests
    daemonReplayTests
  ]
