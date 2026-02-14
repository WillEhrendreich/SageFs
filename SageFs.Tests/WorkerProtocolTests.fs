module SageFs.Tests.WorkerProtocolTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

let private roundTrip<'T> (value: 'T) =
  let json = Serialization.serialize value
  let result = Serialization.deserialize<'T> json
  json, result

[<Tests>]
let workerProtocolTests =
  testList "WorkerProtocol" [

    testList "WorkerMessage round-trip" [

      testCase "EvalCode round-trips"
      <| fun _ ->
        let msg = WorkerMessage.EvalCode("let x = 42", "r1")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "CheckCode round-trips"
      <| fun _ ->
        let msg = WorkerMessage.CheckCode("let x = 42", "r2")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "GetCompletions round-trips"
      <| fun _ ->
        let msg = WorkerMessage.GetCompletions("System.", 7, "r3")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "CancelEval round-trips"
      <| fun _ ->
        let _, result = roundTrip<WorkerMessage> WorkerMessage.CancelEval
        result |> Expect.equal "should round-trip" WorkerMessage.CancelEval

      testCase "LoadScript round-trips"
      <| fun _ ->
        let msg = WorkerMessage.LoadScript(@"C:\test.fsx", "r4")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "ResetSession round-trips"
      <| fun _ ->
        let msg = WorkerMessage.ResetSession "r5"
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "HardResetSession round-trips"
      <| fun _ ->
        let msg = WorkerMessage.HardResetSession(true, "r6")
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "GetStatus round-trips"
      <| fun _ ->
        let msg = WorkerMessage.GetStatus "r7"
        let _, result = roundTrip<WorkerMessage> msg
        result |> Expect.equal "should round-trip" msg

      testCase "Shutdown round-trips"
      <| fun _ ->
        let _, result = roundTrip<WorkerMessage> WorkerMessage.Shutdown
        result |> Expect.equal "should round-trip" WorkerMessage.Shutdown
    ]

    testList "WorkerResponse round-trip" [

      testCase "EvalResult success round-trips"
      <| fun _ ->
        let resp = WorkerResponse.EvalResult("r1", Ok "val x: int = 42", [])
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "EvalResult with diagnostics round-trips"
      <| fun _ ->
        let diag = {
          Severity = SageFs.Features.Diagnostics.Warning
          Message = "unused variable"
          StartLine = 1
          StartColumn = 0
          EndLine = 1
          EndColumn = 5
        }
        let resp = WorkerResponse.EvalResult("r2", Ok "val x = 42", [diag])
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "EvalResult error round-trips"
      <| fun _ ->
        let resp = WorkerResponse.EvalResult("r3", Error (SageFsError.EvalFailed "type mismatch"), [])
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "CheckResult round-trips"
      <| fun _ ->
        let diag = {
          Severity = SageFs.Features.Diagnostics.Error
          Message = "undefined value"
          StartLine = 3
          StartColumn = 4
          EndLine = 3
          EndColumn = 10
        }
        let resp = WorkerResponse.CheckResult("r4", [diag])
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "CompletionResult round-trips"
      <| fun _ ->
        let resp = WorkerResponse.CompletionResult("r5", ["Console"; "Convert"; "Char"])
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "StatusResult round-trips"
      <| fun _ ->
        let status = {
          Status = SessionStatus.Ready
          EvalCount = 10
          AvgDurationMs = 150L
          MinDurationMs = 5L
          MaxDurationMs = 1000L
        }
        let resp = WorkerResponse.StatusResult("r6", status)
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "EvalCancelled round-trips"
      <| fun _ ->
        let resp = WorkerResponse.EvalCancelled true
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "ResetResult round-trips"
      <| fun _ ->
        let resp = WorkerResponse.ResetResult("r7", Ok ())
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "HardResetResult round-trips"
      <| fun _ ->
        let resp = WorkerResponse.HardResetResult("r8", Ok "rebuilt in 3.2s")
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "ScriptLoaded round-trips"
      <| fun _ ->
        let resp = WorkerResponse.ScriptLoaded("r9", Ok "loaded successfully")
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp

      testCase "WorkerReady round-trips"
      <| fun _ ->
        let _, result = roundTrip<WorkerResponse> WorkerResponse.WorkerReady
        result |> Expect.equal "should round-trip" WorkerResponse.WorkerReady

      testCase "WorkerShuttingDown round-trips"
      <| fun _ ->
        let _, result = roundTrip<WorkerResponse> WorkerResponse.WorkerShuttingDown
        result |> Expect.equal "should round-trip" WorkerResponse.WorkerShuttingDown

      testCase "WorkerError round-trips"
      <| fun _ ->
        let resp = WorkerResponse.WorkerError (SageFsError.EvalFailed "something went wrong")
        let _, result = roundTrip<WorkerResponse> resp
        result |> Expect.equal "should round-trip" resp
    ]

    testList "SessionInfo" [

      testCase "displayName uses solution root directory name"
      <| fun _ ->
        let info = {
          Id = "session-abc"
          Projects = ["Tests.fsproj"]
          WorkingDirectory = @"C:\Code\Repos\SageFs\SageFs.Tests"
          SolutionRoot = Some @"C:\Code\Repos\SageFs"
          CreatedAt = DateTime(2026, 1, 1)
          LastActivity = DateTime(2026, 1, 1)
          Status = SessionStatus.Ready
          WorkerPid = Some 1234
        }
        SessionInfo.displayName info
        |> Expect.equal "should use solution root dir name" "SageFs"

      testCase "displayName falls back to working directory name"
      <| fun _ ->
        let info = {
          Id = "session-def"
          Projects = ["App.fsproj"]
          WorkingDirectory = @"C:\Code\MyApp"
          SolutionRoot = None
          CreatedAt = DateTime(2026, 1, 1)
          LastActivity = DateTime(2026, 1, 1)
          Status = SessionStatus.Ready
          WorkerPid = None
        }
        SessionInfo.displayName info
        |> Expect.equal "should use working dir name" "MyApp"

      testCase "findSolutionRoot finds slnx in ancestor"
      <| fun _ ->
        let result = SessionInfo.findSolutionRoot @"C:\Code\Repos\SageFs\SageFs.Tests"
        result |> Expect.isSome "should find solution root"
        result |> Expect.equal "should be repo root" (Some @"C:\Code\Repos\SageFs")

      testCase "SessionInfo round-trips through JSON"
      <| fun _ ->
        let info = {
          Id = "session-xyz"
          Projects = ["A.fsproj"; "B.fsproj"]
          WorkingDirectory = @"C:\Code\Repos\Test"
          SolutionRoot = Some @"C:\Code\Repos\Test"
          CreatedAt = DateTime(2026, 2, 13, 12, 0, 0)
          LastActivity = DateTime(2026, 2, 13, 12, 30, 0)
          Status = SessionStatus.Evaluating
          WorkerPid = Some 5678
        }
        let _, result = roundTrip<SessionInfo> info
        result |> Expect.equal "should round-trip" info
    ]
  ]
