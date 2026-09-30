/// A faulted session always says why. `Faulted` used to carry `string option`,
/// and every reader that met `None` invented its own sentence ("Session faulted
/// for an unspecified reason.", "faulted", "Session warmup timed out"), so the
/// same fault read differently on each surface and the last one was a claim
/// about a cause nobody had established. The reason is now a `FaultReason`:
/// either what was reported, or an `Unexplained` that names who faulted the
/// session without saying why.
module SageFs.Tests.FaultReasonTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.McpSessionRouting
open SageFs.WorkerProtocol

let private readyWorker : WorkerHandle = { Pid = 4242; Port = Some 5555 }

[<Tests>]
let tests =
  testList "FaultReason" [
    testProperty "WHY — whatever text a fault arrives with, what a reader is shown is never blank" <| fun (message: string) ->
      FaultReason.report message
      |> FaultReason.describe
      |> String.IsNullOrWhiteSpace
      |> not

    testProperty "WHY — a message that says something is shown as it was said" <| fun (NonEmptyString message) ->
      (not (String.IsNullOrWhiteSpace message)) ==>
        (FaultReason.report message |> FaultReason.describe = message)

    testCase "WHY — blank text is not a reason, so it becomes an Unexplained that says nothing was recorded" <| fun _ ->
      for blank in [ null; ""; "   "; "\t\n" ] do
        FaultReason.report blank
        |> Expect.equal (sprintf "%A is not a reason" blank) (FaultReason.Unexplained FaultOrigin.NotRecorded)

    testCase "WHY — each way of being unexplained reads differently, because they mean different things" <| fun _ ->
      let described =
        [ FaultOrigin.WorkerSelfReported; FaultOrigin.NotRecorded ]
        |> List.map (FaultReason.Unexplained >> FaultReason.describe)
      (List.distinct described |> List.length)
      |> Expect.equal "one sentence per origin" 2

    testCase "WHY — a worker that reports Faulted without a reason produces an Unexplained, not a made-up cause" <| fun _ ->
      SessionLifecycleStatus.ofWorkerReport (SessionLifecycleStatus.Ready readyWorker) SessionStatus.Faulted
      |> Expect.equal "the daemon says the worker reported it" (SessionLifecycleStatus.Faulted (FaultReason.Unexplained FaultOrigin.WorkerSelfReported))

    testCase "WHY — a fault the daemon already recorded is not overwritten by a later poll" <| fun _ ->
      let recorded = SessionLifecycleStatus.Faulted (FaultReason.Reported "Not all DLLs are found")
      SessionLifecycleStatus.ofWorkerReport recorded SessionStatus.Ready
      |> Expect.equal "still the recorded fault" recorded

    testCase "WHY — only a faulted session has a fault reason" <| fun _ ->
      let reason = FaultReason.Reported "boom"
      SessionLifecycleStatus.faultReason (SessionLifecycleStatus.Faulted reason)
      |> Expect.equal "faulted carries it" (Some reason)
      for status in [ SessionLifecycleStatus.Ready readyWorker; SessionLifecycleStatus.Stopped; SessionLifecycleStatus.Restarting PreviousWorker.ColdStart ] do
        SessionLifecycleStatus.faultReason status
        |> Expect.isNone (sprintf "%A has no fault" status)

    testCase "WHY — health for an unexplained fault says so in the same words every other surface uses" <| fun _ ->
      let reason = FaultReason.Unexplained FaultOrigin.NotRecorded
      match SessionHealth.classify (SessionLifecycleStatus.Faulted reason) [] None with
      | SessionHealth.Failed text -> text |> Expect.equal "same text as describe" (FaultReason.describe reason)
      | other -> failtestf "expected Failed, got %A" other

    testCase "WHY — an agent is told the recorded reason when there is one, and that none was recorded when there is not" <| fun _ ->
      FaultCause.ofStatus (SessionLifecycleStatus.Faulted (FaultReason.Reported "Not all DLLs are found"))
      |> Expect.equal "recorded" (FaultCause.Recorded "Not all DLLs are found")
      FaultCause.ofStatus (SessionLifecycleStatus.Faulted (FaultReason.Unexplained FaultOrigin.NotRecorded))
      |> Expect.equal "unexplained" FaultCause.NoReasonRecorded
  ]
