module SageFs.Tests.EventExhaustivenessTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.Events
open SageFs.Features

/// Every SageFsEvent case with minimal data.
/// If a new case is added to the DU, this list will cause a
/// compiler warning (incomplete pattern) until updated.
let allEventCases : SageFsEvent list = [
  SessionStarted {| Config = Map.empty; StartedAt = DateTimeOffset.UtcNow |}
  SessionWarmUpCompleted {| Duration = TimeSpan.FromMilliseconds 100.0; Errors = [] |}
  SessionWarmUpProgress {| Step = 1; Total = 4; Message = "scanning" |}
  SessionReady
  SessionFaulted {| Error = "boom"; StackTrace = None |}
  SessionReset
  SessionHardReset {| Rebuild = false |}
  EvalRequested {| Code = "1+1;;"; Source = EventSource.Console |}
  EvalCompleted {| Code = "1+1;;"; Result = "2"; TypeSignature = Some "int"; Duration = TimeSpan.FromMilliseconds 50.0 |}
  EvalFailed {| Code = "bad"; Error = "parse error"; Diagnostics = [] |}
  EvalTraced {| Code = "1+1;;"; Stages = [ "Eval", 5.0 ]; TotalMs = 5.0 |}
  DiagnosticsChecked {| Code = "let x = 1"; Diagnostics = []; Source = EventSource.System |}
  DiagnosticsCleared
  ScriptLoaded {| FilePath = "init.fsx"; StatementCount = 3; Source = EventSource.System |}
  ScriptLoadFailed {| FilePath = "bad.fsx"; Error = "not found" |}
  McpInputReceived {| Source = EventSource.Console; Content = "hello" |}
  McpOutputSent {| Source = EventSource.System; Content = "world" |}
  DaemonSessionCreated {| SessionId = "s1"; Projects = ["p.fsproj"]; WorkingDir = "/tmp"; CreatedAt = DateTimeOffset.UtcNow |}
  DaemonSessionStopped {| SessionId = "s1"; StoppedAt = DateTimeOffset.UtcNow |}
  DaemonSessionSwitched {| FromId = None; ToId = "s2"; SwitchedAt = DateTimeOffset.UtcNow |}
]

/// Verify the list covers every DU case by checking the count matches
/// the number of union cases via reflection.
let expectedCaseCount =
  Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<SageFsEvent>).Length

[<Tests>]
let eventExhaustivenessTests = testList "Event exhaustiveness" [

  test "allEventCases covers every SageFsEvent union case" {
    let actualNames =
      allEventCases
      |> List.map (fun e ->
        let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(e, typeof<SageFsEvent>)
        case.Name)
      |> List.distinct
    actualNames
    |> Expect.hasLength "covers all cases" expectedCaseCount
  }

  test "no duplicate cases in allEventCases" {
    let names =
      allEventCases
      |> List.map (fun e ->
        let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(e, typeof<SageFsEvent>)
        case.Name)
    names |> List.distinct |> List.length
    |> Expect.equal "no duplicates" names.Length
  }

  testList "Replay.applyEvent handles every case without exception" [
    for evt in allEventCases do
      let caseName =
        let c, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(evt, typeof<SageFsEvent>)
        c.Name
      test (sprintf "Replay handles %s" caseName) {
        let state = Replay.SessionReplayState.empty
        let ts = DateTimeOffset.UtcNow
        let result = Replay.SessionReplayState.applyEvent ts state evt
        result |> ignore
      }
  ]

  testList "EventTracking.formatEvent handles every case without exception" [
    for evt in allEventCases do
      let caseName =
        let c, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(evt, typeof<SageFsEvent>)
        c.Name
      test (sprintf "formatEvent handles %s" caseName) {
        let ts = DateTimeOffset.UtcNow
        let (dt, source, content) = SageFs.EventTracking.formatEvent (ts, evt)
        dt |> ignore
        source |> Expect.isNotNull (sprintf "%s has source" caseName)
        content |> Expect.isNotNull (sprintf "%s has content" caseName)
      }
  ]
]
