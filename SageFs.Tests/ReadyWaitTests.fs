module SageFs.Tests.ReadyWaitTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

let private worker : WorkerHandle = { Pid = 7; Port = Some 6000 }
let private rebuilding = LastRebuild.Latest (RebuildOutcome.InProgress DateTime.UtcNow)
let private rebuilt = LastRebuild.Latest (RebuildOutcome.Succeeded DateTime.UtcNow)
let private failedStillServing = LastRebuild.Latest (RebuildOutcome.FailedStillServing (SageFsError.HardResetFailed "x", DateTime.UtcNow))
let private failedNotServing = LastRebuild.Latest (RebuildOutcome.FailedNotServing (SageFsError.HardResetFailed "x", DateTime.UtcNow))

let private labelOf (verdict: ReadyWait.Verdict) : string =
  match verdict with
  | ReadyWait.Verdict.KeepParked -> "KeepParked"
  | ReadyWait.Verdict.Ready -> "Ready"
  | ReadyWait.Verdict.Failed _ -> "Failed"

let private statuses : SessionLifecycleStatus list =
  [ SessionLifecycleStatus.Starting { Pid = 7; Port = None }
    SessionLifecycleStatus.Ready worker
    SessionLifecycleStatus.Evaluating worker
    SessionLifecycleStatus.Building ("compiling", worker)
    SessionLifecycleStatus.Faulted (FaultReason.report "boom")
    SessionLifecycleStatus.Restarting PreviousWorker.ColdStart
    SessionLifecycleStatus.Stopped ]

let private rebuilds : LastRebuild list =
  [ LastRebuild.NeverRebuilt; rebuilding; rebuilt; failedStillServing; failedNotServing ]

[<Tests>]
let readyWaitTests =
  testList "ReadyWait" [

    testList "ofSession" [
      testCase "a Ready or Evaluating session with a rebuild running keeps the caller parked" <| fun _ ->
        for status in [ SessionLifecycleStatus.Ready worker; SessionLifecycleStatus.Evaluating worker ] do
          ReadyWait.ofSession status rebuilding |> labelOf |> Expect.equal (sprintf "%A" status) "KeepParked"

      testCase "a Ready session whose last rebuild is over, however it ended, or that was never rebuilt, is Ready" <| fun _ ->
        for rebuild in [ LastRebuild.NeverRebuilt; rebuilt; failedStillServing; failedNotServing ] do
          ReadyWait.ofSession (SessionLifecycleStatus.Ready worker) rebuild |> labelOf |> Expect.equal (sprintf "%A" rebuild) "Ready"

      testCase "a rebuild in progress is waited on whatever the lifecycle says, except for a stopped session" <| fun _ ->
        for status in statuses do
          let verdict = ReadyWait.ofSession status rebuilding |> labelOf
          match status with
          | SessionLifecycleStatus.Stopped -> verdict |> Expect.equal "a stopped session has nothing to rebuild into" "Failed"
          | _ -> verdict |> Expect.equal (sprintf "%A" status) "KeepParked"

      testCase "a session on its way to Ready keeps the caller parked" <| fun _ ->
        for status in [ SessionLifecycleStatus.Starting { Pid = 7; Port = None }; SessionLifecycleStatus.Restarting PreviousWorker.ColdStart; SessionLifecycleStatus.Building ("compiling", worker) ] do
          ReadyWait.ofSession status LastRebuild.NeverRebuilt |> labelOf |> Expect.equal (sprintf "%A" status) "KeepParked"

      testCase "a Faulted or Stopped session fails the wait, with a reason" <| fun _ ->
        for status in [ SessionLifecycleStatus.Faulted (FaultReason.report "boom"); SessionLifecycleStatus.Stopped ] do
          match ReadyWait.ofSession status LastRebuild.NeverRebuilt with
          | ReadyWait.Verdict.Failed error -> SageFsError.describe error |> Expect.isNotEmpty "the failure says why"
          | other -> failtestf "%A should fail the wait, got %A" status other
    ]

    testList "ofRebuildEnd" [
      testCase "a rebuild that failed fails the wait with the build's own error, whether or not a worker still serves" <| fun _ ->
        let error = SageFsError.BuildFailed(1, [ BuildDiagnostic.ofLine "Hello.fs(3,5): error FS0001: expected int" ])
        for outcome in [ RebuildOutcome.FailedStillServing (error, DateTime.UtcNow); RebuildOutcome.FailedNotServing (error, DateTime.UtcNow) ] do
          match ReadyWait.ofRebuildEnd outcome with
          | ReadyWait.Verdict.Failed found -> found |> Expect.equal "the error the build gave" error
          | other -> failtestf "%A should fail the wait, got %A" outcome other

      testCase "a rebuild that succeeded keeps the caller parked: the replacement worker is still coming up" <| fun _ ->
        ReadyWait.ofRebuildEnd (RebuildOutcome.Succeeded DateTime.UtcNow) |> labelOf |> Expect.equal "the status decides from here" "KeepParked"

      testCase "a rebuild still in progress keeps the caller parked" <| fun _ ->
        ReadyWait.ofRebuildEnd (RebuildOutcome.InProgress DateTime.UtcNow) |> labelOf |> Expect.equal "not over" "KeepParked"
    ]

    testList "plan" [
      testCase "a caller is parked exactly when the session would keep it parked, for every status and every rebuild" <| fun _ ->
        for status in statuses do
          for rebuild in rebuilds do
            let parks =
              match ReadyWait.plan status rebuild with
              | ReadyWait.Plan.Park -> true
              | ReadyWait.Plan.DoNotPark -> false
            let kept =
              match ReadyWait.ofSession status rebuild with
              | ReadyWait.Verdict.KeepParked -> true
              | ReadyWait.Verdict.Ready
              | ReadyWait.Verdict.Failed _ -> false
            parks |> Expect.equal (sprintf "%A with %A" status rebuild) kept

      testCase "a rebuild in progress is never DoNotPark on a Ready session" <| fun _ ->
        ReadyWait.plan (SessionLifecycleStatus.Ready worker) rebuilding
        |> Expect.equal "it is waited on" ReadyWait.Plan.Park
    ]
  ]
