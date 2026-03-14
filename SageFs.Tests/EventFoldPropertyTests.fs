module SageFs.Tests.EventFoldPropertyTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.WarmUp
open SageFs.Features.Events
open SageFs.Features.Replay
open SageFs.SessionEvents

// ── FsCheck Generators ──

let genEventSource =
  Gen.oneof [
    Gen.constant Console
    Gen.elements [ "claude"; "copilot"; "agent-1" ] |> Gen.map McpAgent
    Gen.elements [ "Main.fs"; "Lib.fs"; "test.fsx" ] |> Gen.map FileSync
    Gen.constant EventSource.System
  ]

let genSageFsEvent =
  let genTs = Gen.constant (TimeSpan.FromMilliseconds 50.0)
  Gen.oneof [
    gen {
      let! at = Gen.constant (DateTimeOffset.UtcNow)
      return SessionStarted {| Config = Map.empty; StartedAt = at |}
    }
    gen {
      let! dur = genTs
      return SessionWarmUpCompleted {| Duration = dur; Errors = [] |}
    }
    Gen.constant SessionReady
    gen {
      let! rebuild = Gen.elements [ true; false ]
      return SessionHardReset {| Rebuild = rebuild |}
    }
    Gen.constant SessionReset
    gen {
      let! err = Gen.elements [ "critical"; "timeout"; "OOM" ]
      let! trace = Gen.oneof [ Gen.constant None; Gen.constant (Some "at X.Y()") ]
      return SessionFaulted {| Error = err; StackTrace = trace |}
    }
    gen {
      let! code = Gen.elements [ "let x = 1"; "printfn \"hi\""; "1 + 1" ]
      let! src = genEventSource
      return EvalRequested {| Code = code; Source = src |}
    }
    gen {
      let! code = Gen.elements [ "let x = 1"; "printfn \"hi\"" ]
      let! result = Gen.elements [ "val x: int = 1"; "hi" ]
      let! dur = genTs
      return EvalCompleted {| Code = code; Result = result; TypeSignature = None; Duration = dur |}
    }
    gen {
      let! code = Gen.elements [ "bad"; "let x =" ]
      let! err = Gen.elements [ "parse error"; "type mismatch" ]
      return EvalFailed {| Code = code; Error = err; Diagnostics = [] |}
    }
    gen {
      let! code = Gen.elements [ "let x"; "open Foo" ]
      let! src = genEventSource
      return DiagnosticsChecked {| Code = code; Diagnostics = []; Source = src |}
    }
    Gen.constant DiagnosticsCleared
    gen {
      let! fp = Gen.elements [ "test.fsx"; "main.fsx" ]
      let! count = Gen.choose (1, 10)
      let! src = genEventSource
      return ScriptLoaded {| FilePath = fp; StatementCount = count; Source = src |}
    }
    gen {
      let! fp = Gen.elements [ "bad.fsx"; "missing.fsx" ]
      let! err = Gen.elements [ "not found"; "parse error" ]
      return ScriptLoadFailed {| FilePath = fp; Error = err |}
    }
    gen {
      let! src = genEventSource
      let! content = Gen.elements [ "let y = 2"; "open System" ]
      return McpInputReceived {| Source = src; Content = content |}
    }
    gen {
      let! src = genEventSource
      let! content = Gen.elements [ "val y: int = 2"; "ok" ]
      return McpOutputSent {| Source = src; Content = content |}
    }
    gen {
      let! sessionId = Gen.elements [ "s1"; "s2"; "s3" ]
      let! projects = Gen.elements [ [ "App.fsproj" ]; [ "Lib.fsproj"; "Tests.fsproj" ] ]
      return DaemonSessionCreated {| SessionId = sessionId; Projects = projects; WorkingDir = "/test"; CreatedAt = DateTimeOffset.UtcNow |}
    }
    gen {
      let! sessionId = Gen.elements [ "s1"; "s2" ]
      return DaemonSessionStopped {| SessionId = sessionId; StoppedAt = DateTimeOffset.UtcNow |}
    }
    gen {
      let! fromId = Gen.oneof [ Gen.constant None; Gen.elements [ "s1"; "s2" ] |> Gen.map Some ]
      let! toId = Gen.elements [ "s2"; "s3" ]
      return DaemonSessionSwitched {| FromId = fromId; ToId = toId; SwitchedAt = DateTimeOffset.UtcNow |}
    }
  ]

let genTimestampedStream =
  gen {
    let! count = Gen.choose (0, 30)
    let! events = Gen.listOfLength count genSageFsEvent
    let baseTime = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    return events |> List.mapi (fun i e -> baseTime.AddMinutes(float i), e)
  }

let genSessionEvent =
  let genSid = Gen.elements [ "session-1"; "session-2"; "abc-123" ]
  let genWarmupCtx =
    gen {
      let! files = Gen.choose (0, 100)
      let! dur = Gen.choose (0, 5000) |> Gen.map int64
      let! asmCount = Gen.choose (0, 5)
      let! asms =
        Gen.listOfLength asmCount (gen {
          let! n = Gen.elements [ "FSharp.Core"; "SageFs.Core"; "Expecto" ]
          return { Name = n; Path = sprintf "/%s.dll" n; NamespaceCount = 3; ModuleCount = 2 }
        })
      let! nsCount = Gen.choose (0, 5)
      let! nss =
        Gen.listOfLength nsCount (gen {
          let! n = Gen.elements [ "System"; "SageFs"; "Expecto" ]
          let! kind = Gen.elements [ OpenableKind.Module; OpenableKind.Namespace ]
          return { OpenedBinding.Name = n; Kind = kind; Source = "warmup"; DurationMs = 0.0 }
        })
      return {
        SourceFilesScanned = files
        AssembliesLoaded = asms
        NamespacesOpened = nss
        FailedOpens = []
        PhaseTiming = { ScanSourceFilesMs = 0L; ScanAssembliesMs = 0L; OpenNamespacesMs = 0L; TotalMs = dur }
        StartedAt = DateTimeOffset.UtcNow
      }
    }
  Gen.oneof [
    gen {
      let! sid = genSid
      let! ctx = genWarmupCtx
      return WarmupContextSnapshot(sid, ctx)
    }
    gen {
      let! sid = genSid
      let! files = Gen.listOfLength 3 (Gen.elements [ "Main.fs"; "Lib.fs"; "Test.fs" ])
      return HotReloadSnapshot(sid, files)
    }
    gen {
      let! sid = genSid
      let! file = Gen.elements [ "Main.fs"; "Lib.fs" ]
      let! watched = Gen.elements [ true; false ]
      return HotReloadFileToggled(sid, file, watched)
    }
    gen {
      let! sid = genSid
      return SessionActivated sid
    }
    gen {
      let! sid = genSid
      let! projs = Gen.listOfLength 2 (Gen.elements [ "App"; "Tests" ])
      return SessionCreated(sid, projs)
    }
    gen {
      let! sid = genSid
      return SessionStopped sid
    }
  ]

let propConfig = { FsCheckConfig.defaultConfig with maxTest = 200 }

// ── Property Tests ──

[<Tests>]
let replayEquivalenceTests = testList "Replay fold equivalence" [
  testPropertyWithConfig propConfig "replayStream ≡ List.fold applyEvent empty" <|
    fun () ->
      Prop.forAll (Arb.fromGen genTimestampedStream) (fun stream ->
        let viaReplay = SessionReplayState.replayStream stream
        let viaFold =
          stream
          |> List.fold (fun acc (ts, evt) -> SessionReplayState.applyEvent ts acc evt) SessionReplayState.empty
        viaReplay = viaFold
      )
]

[<Tests>]
let lastActivityMonotonicTests = testList "LastActivity monotonicity" [
  testPropertyWithConfig propConfig "LastActivity never goes backward" <|
    fun () ->
      Prop.forAll (Arb.fromGen genTimestampedStream) (fun stream ->
        let states =
          stream
          |> List.scan (fun acc (ts, evt) -> SessionReplayState.applyEvent ts acc evt) SessionReplayState.empty
        let activities =
          states |> List.choose (fun s -> s.LastActivity)
        match activities with
        | [] -> true
        | _ ->
          activities
          |> List.pairwise
          |> List.forall (fun (a, b) -> b >= a)
      )
]

[<Tests>]
let evalCountTests = testList "Eval counting" [
  testPropertyWithConfig propConfig "EvalCount matches EvalCompleted count" <|
    fun () ->
      Prop.forAll (Arb.fromGen genTimestampedStream) (fun stream ->
        let finalState = SessionReplayState.replayStream stream
        let expected =
          stream
          |> List.filter (fun (_, e) -> match e with EvalCompleted _ -> true | _ -> false)
          |> List.length
        finalState.EvalCount = expected
      )

  testPropertyWithConfig propConfig "FailedEvalCount matches EvalFailed count" <|
    fun () ->
      Prop.forAll (Arb.fromGen genTimestampedStream) (fun stream ->
        let finalState = SessionReplayState.replayStream stream
        let expected =
          stream
          |> List.filter (fun (_, e) -> match e with EvalFailed _ -> true | _ -> false)
          |> List.length
        finalState.FailedEvalCount = expected
      )
]

[<Tests>]
let resetCountTests = testList "Reset counting" [
  testPropertyWithConfig propConfig "ResetCount matches SessionReset count" <|
    fun () ->
      Prop.forAll (Arb.fromGen genTimestampedStream) (fun stream ->
        let finalState = SessionReplayState.replayStream stream
        let expected =
          stream
          |> List.filter (fun (_, e) -> match e with SessionReset -> true | _ -> false)
          |> List.length
        finalState.ResetCount = expected
      )

  testPropertyWithConfig propConfig "HardResetCount matches SessionHardReset count" <|
    fun () ->
      Prop.forAll (Arb.fromGen genTimestampedStream) (fun stream ->
        let finalState = SessionReplayState.replayStream stream
        let expected =
          stream
          |> List.filter (fun (_, e) -> match e with SessionHardReset _ -> true | _ -> false)
          |> List.length
        finalState.HardResetCount = expected
      )
]

[<Tests>]
let evalHistoryTests = testList "Eval history" [
  testPropertyWithConfig propConfig "EvalHistory length matches EvalCount" <|
    fun () ->
      Prop.forAll (Arb.fromGen genTimestampedStream) (fun stream ->
        let finalState = SessionReplayState.replayStream stream
        List.length finalState.EvalHistory = finalState.EvalCount
      )
]

[<Tests>]
let emptyStreamTests = testList "Empty stream" [
  test "empty stream produces empty state" {
    let result = SessionReplayState.replayStream []
    result |> Expect.equal "empty replay = empty state" SessionReplayState.empty
  }
]

[<Tests>]
let daemonNoEffectTests = testList "Daemon event no-ops" [
  testPropertyWithConfig propConfig "daemon-only events leave session state empty" <|
    fun () ->
      let genDaemonOnly =
        gen {
          let! count = Gen.choose (0, 20)
          let daemonGen =
            Gen.oneof [
              gen {
                let! sid = Gen.elements [ "s1"; "s2" ]
                let! projs = Gen.constant [ "App.fsproj" ]
                return DaemonSessionCreated {| SessionId = sid; Projects = projs; WorkingDir = "/test"; CreatedAt = DateTimeOffset.UtcNow |}
              }
              gen {
                let! sid = Gen.elements [ "s1"; "s2" ]
                return DaemonSessionStopped {| SessionId = sid; StoppedAt = DateTimeOffset.UtcNow |}
              }
              gen {
                let! f = Gen.oneof [ Gen.constant None; Gen.elements [ "s1"; "s2" ] |> Gen.map Some ]
                let! t = Gen.elements [ "s2"; "s3" ]
                return DaemonSessionSwitched {| FromId = f; ToId = t; SwitchedAt = DateTimeOffset.UtcNow |}
              }
            ]
          let! events = Gen.listOfLength count daemonGen
          let baseTime = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
          return events |> List.mapi (fun i e -> baseTime.AddMinutes(float i), e)
        }
      Prop.forAll (Arb.fromGen genDaemonOnly) (fun stream ->
        let result = SessionReplayState.replayStream stream
        result = SessionReplayState.empty
      )
]

[<Tests>]
let sessionEventRoundtripTests = testList "SessionEvent serialization" [
  testPropertyWithConfig propConfig "serializeSessionEvent produces valid JSON" <|
    fun () ->
      Prop.forAll (Arb.fromGen genSessionEvent) (fun evt ->
        let json = serializeSessionEvent evt
        let doc = System.Text.Json.JsonDocument.Parse(json)
        doc.RootElement.ValueKind = System.Text.Json.JsonValueKind.Object
      )

  testPropertyWithConfig propConfig "serializeSessionEvent always includes sessionId" <|
    fun () ->
      Prop.forAll (Arb.fromGen genSessionEvent) (fun evt ->
        let json = serializeSessionEvent evt
        let doc = System.Text.Json.JsonDocument.Parse(json)
        doc.RootElement.TryGetProperty("sessionId") |> fst
      )

  testPropertyWithConfig propConfig "formatSessionSseEvent wraps as SSE frame" <|
    fun () ->
      Prop.forAll (Arb.fromGen genSessionEvent) (fun evt ->
        let sse = formatSessionSseEvent evt
        sse.StartsWith("event: session\ndata: ") && sse.EndsWith("\n\n")
      )
]
