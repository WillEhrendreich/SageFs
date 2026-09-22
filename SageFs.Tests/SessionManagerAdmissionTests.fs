module SageFs.Tests.SessionManagerAdmissionTests

/// Bounded-mailbox admission gate (2026-09-22 daemon lockup under 5
/// concurrent session warmups — Kestrel logging "heartbeat has been
/// running for 00:01:00", /health and /api/sessions timing out for a full
/// minute despite reading a lock-free snapshot). Root cause was thread-pool
/// starvation from long-lived blocking reads (see WarmupSimTests.fs's
/// sibling fix), not the mailbox itself — but the mailbox had no admission
/// bound at all, unlike ElmLoop's own 256-message high-watermark alarm.
/// `SageFsError.admissionDecision` is the pure decision;
/// `DaemonMode.checkMailboxAdmission` wires it to the real
/// `MailboxProcessor.CurrentQueueLength`.
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Tests.SharedGenerators

[<Tests>]
let tests =
  testList "SessionManager mailbox admission" [

    testList "admissionDecision examples" [
      test "well under capacity admits" {
        SageFsError.admissionDecision 10 256
        |> Expect.equal "Ok" (Result.Ok ())
      }
      test "exactly at capacity refuses" {
        match SageFsError.admissionDecision 256 256 with
        | Result.Error (SageFsError.SupervisorBusy(pending, capacity)) ->
          pending |> Expect.equal "reports actual pending" 256
          capacity |> Expect.equal "reports actual capacity" 256
        | other -> failtestf "expected SupervisorBusy at capacity, got %A" other
      }
      test "over capacity refuses" {
        match SageFsError.admissionDecision 300 256 with
        | Result.Error (SageFsError.SupervisorBusy _) -> ()
        | other -> failtestf "expected SupervisorBusy, got %A" other
      }
      test "one under capacity still admits — the gate never engages under real usage" {
        SageFsError.admissionDecision 255 256
        |> Expect.equal "Ok" (Result.Ok ())
      }
    ]

    testList "properties" [
      testPropertyWithConfig propConfig "admits iff pending < capacity" <|
        fun (NonNegativeInt pending) (PositiveInt capacity) ->
          let result = SageFsError.admissionDecision pending capacity
          match pending < capacity with
          | true -> result |> Expect.equal "should admit" (Result.Ok ())
          | false ->
            match result with
            | Result.Error (SageFsError.SupervisorBusy(p, c)) ->
              p |> Expect.equal "pending echoed back" pending
              c |> Expect.equal "capacity echoed back" capacity
            | other -> failtestf "should refuse with SupervisorBusy, got %A" other

      testPropertyWithConfig propConfig "the SupervisorBusy error is classified 503, never a client or server fault" <|
        fun (NonNegativeInt pending) (PositiveInt capacity) ->
          match pending >= capacity with
          | false -> () // not the shape this property checks
          | true ->
            match SageFsError.admissionDecision pending capacity with
            | Result.Error err ->
              SageFsError.toHttpStatus err |> Expect.equal "503 Service Unavailable" 503
              SageFsError.isClientError err |> Expect.isFalse "not the caller's fault"
              SageFsError.isServerError err |> Expect.isFalse "not an internal bug"
            | Result.Ok () -> failtest "expected a refusal"
    ]

    testCase "TWIN: an admission check with no bound at all never refuses, no matter how far behind the mailbox is" <| fun _ ->
      // The exact shape of the historical gap: MailboxProcessor's queue is
      // unbounded by construction, so "no check at all" is what production
      // had before this fix.
      let unboundedDecision (_pending: int) (_capacity: int) : Result<unit, SageFsError> = Result.Ok ()
      unboundedDecision 1_000_000 256
      |> Expect.equal "the old (absent) gate lets an arbitrarily large backlog through" (Result.Ok ())
      // ...and the REAL decision does not.
      match SageFsError.admissionDecision 1_000_000 256 with
      | Result.Error (SageFsError.SupervisorBusy _) -> ()
      | other -> failtestf "the REAL admissionDecision must refuse a million-deep backlog, got %A" other
  ]
