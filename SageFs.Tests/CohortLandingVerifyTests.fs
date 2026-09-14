/// Item 14d (sagefs-multiagent-vision.md) — RED/GREEN tests for
/// `SageFs.Features.CohortLandingVerify`, the blocking test-run primitive
/// cohort landing verification needs.
///
/// The pure helpers (`isGenerationComplete`, `failingOf`) are exercised
/// against hand-built `LiveTestState` fixtures — no daemon, no FSI, no I/O.
/// `runTestsInSession`'s session-trust gate is exercised the same way, by
/// injecting an untrustworthy `SessionObservation` and asserting the
/// dispatched-tests counter never moves. A fake `ElmRuntime` (a
/// `MailboxProcessor`-free record backed by a mutable ref, driven by a
/// background fiber standing in for the daemon's real reducer) proves the
/// dispatch → generation-capture → poll-to-completion → failing-tests
/// orchestration end to end, without a live daemon.
module SageFs.Tests.CohortLandingVerifyTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.LiveTesting
open SageFs.Features.Verification
open SageFs.Features.CohortLandingVerify

let private mkTestCase (id: string) : TestCase =
  { Id = TestId.TestId id
    FullName = id
    DisplayName = id
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

let private mkResult (testId: TestId) (result: TestResult) : TestRunResult =
  { TestId = testId
    TestName = TestId.value testId
    Result = result
    Timestamp = DateTimeOffset.UtcNow
    Output = None }

let private trustedObservation (sessionId: string) : SessionTrust.SessionObservation =
  { MatchingSessionIds = [ sessionId ]
    SessionStatus = Some (WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 111; Port = Some 4000 })
    LoadedState = Some (LoadedDefinitionState.ConfirmedCurrent "n/a")
    TypeIdentityDiagnostic = None }

let private missingObservation : SessionTrust.SessionObservation =
  { MatchingSessionIds = []
    SessionStatus = None
    LoadedState = None
    TypeIdentityDiagnostic = None }

let private warmingUpObservation (sessionId: string) : SessionTrust.SessionObservation =
  { MatchingSessionIds = [ sessionId ]
    SessionStatus = Some (WorkerProtocol.SessionLifecycleStatus.Starting { Pid = 111; Port = None })
    LoadedState = Some (LoadedDefinitionState.ConfirmedCurrent "n/a")
    TypeIdentityDiagnostic = None }

/// A fake `ElmRuntime` backed by a mutable ref instead of the real
/// mailbox-driven Elm loop. `onRunTestsRequested` plays the role of
/// `SageFsApp.fs`'s `RunTestsRequested` handler — it decides what the
/// "reducer" does to `LiveTestState` when a run is requested, and is handed
/// an `applyState` callback so it can (like the real daemon, via a later
/// `TestRunCompleted`) mutate the model again asynchronously once the
/// simulated run "finishes" — so tests can drive the fake precisely without
/// depending on the daemon's own reducer (which lives in a file this item
/// must not touch).
let private mkFakeRuntime
  (initialState: LiveTestState)
  (onRunTestsRequested: (LiveTestState -> unit) -> TestCase array -> LiveTestState -> LiveTestState)
  : ElmRuntime<SageFsModel, SageFsMsg, RenderRegion> * (unit -> int) =
  let baseModel = SageFsModel.initial ()
  let modelRef = ref { baseModel with LiveTesting = { baseModel.LiveTesting with TestState = initialState } }
  let applyState (newState: LiveTestState) =
    let cycle = modelRef.Value.LiveTesting
    modelRef.Value <- { modelRef.Value with LiveTesting = { cycle with TestState = newState } }
  let dispatchCount = ref 0
  let dispatch (msg: SageFsMsg) =
    match msg with
    | SageFsMsg.Event (TuiEvent.RunTestsRequested tests) ->
      System.Threading.Interlocked.Increment dispatchCount |> ignore
      let running = onRunTestsRequested applyState tests modelRef.Value.LiveTesting.TestState
      applyState running
    | _ -> ()
  let runtime : ElmRuntime<SageFsModel, SageFsMsg, RenderRegion> =
    { Dispatch = dispatch
      GetModel = fun () -> modelRef.Value
      GetRegions = fun () -> [] }
  runtime, (fun () -> dispatchCount.Value)

/// Mirrors `RunTestsRequested`'s real shape (SageFsApp.fs:1423): bump the
/// shared generation counter, mark every session the requested tests are
/// attributed to as `Running` that generation.
let private startRunReducer (tests: TestCase array) (state: LiveTestState) : LiveTestState =
  let gen = RunGeneration.next state.LastGeneration
  let sessionIds = LiveTestState.ownerSessionId state |> Option.toArray
  let phases = sessionIds |> Array.fold (fun m sid -> Map.add sid (TestRunPhase.Running gen) m) state.RunPhases
  { state with LastGeneration = gen; RunPhases = phases }

/// Mirrors `TestRunCompleted`'s real shape (SageFsApp.fs:1355): the
/// session's phase returns to `Idle` and the tests' final results land in
/// `LastResults`.
let private completeRunReducer
  (results: (TestId * TestResult) list)
  (tests: TestCase array)
  (state: LiveTestState)
  : LiveTestState =
  let sessionIds = LiveTestState.ownerSessionId state |> Option.toArray
  let phases = sessionIds |> Array.fold (fun m sid -> Map.add sid TestRunPhase.Idle m) state.RunPhases
  let lastResults =
    results
    |> List.fold (fun acc (testId, result) -> Map.add testId (mkResult testId result) acc) state.LastResults
  { state with RunPhases = phases; LastResults = lastResults }

[<Tests>]
let tests =
  testList "CohortLandingVerify" [

    testList "isGenerationComplete" [
      test "a session Running at exactly the target generation is incomplete" {
        let gen = RunGeneration.next RunGeneration.zero
        let state = { LiveTestState.empty with RunPhases = Map.ofList [ "sess1", TestRunPhase.Running gen ] }
        isGenerationComplete gen state
        |> Expect.isFalse "still running the target generation"
      }

      test "a session RunningButEdited at exactly the target generation is incomplete" {
        let gen = RunGeneration.next RunGeneration.zero
        let state = { LiveTestState.empty with RunPhases = Map.ofList [ "sess1", TestRunPhase.RunningButEdited gen ] }
        isGenerationComplete gen state
        |> Expect.isFalse "still running (edited) the target generation"
      }

      test "a session back to Idle is complete" {
        let gen = RunGeneration.next RunGeneration.zero
        let state = { LiveTestState.empty with RunPhases = Map.ofList [ "sess1", TestRunPhase.Idle ] }
        isGenerationComplete gen state
        |> Expect.isTrue "idle means nothing is running"
      }

      test "a session that moved on to a later generation is complete for the earlier one" {
        let gen1 = RunGeneration.next RunGeneration.zero
        let gen2 = RunGeneration.next gen1
        let state = { LiveTestState.empty with RunPhases = Map.ofList [ "sess1", TestRunPhase.Running gen2 ] }
        isGenerationComplete gen1 state
        |> Expect.isTrue "gen1 was superseded by gen2 — it is no longer in flight"
      }

      test "no recorded phase at all is complete (nothing to wait for)" {
        let gen = RunGeneration.next RunGeneration.zero
        let state = { LiveTestState.empty with RunPhases = Map.empty }
        isGenerationComplete gen state
        |> Expect.isTrue "an empty RunPhases map has nothing in flight"
      }

      test "one of several sessions still running the target generation keeps it incomplete" {
        let gen = RunGeneration.next RunGeneration.zero
        let state =
          { LiveTestState.empty with
              RunPhases = Map.ofList [ "sess1", TestRunPhase.Idle; "sess2", TestRunPhase.Running gen ] }
        isGenerationComplete gen state
        |> Expect.isFalse "sess2 is still running the target generation"
      }
    ]

    testList "failingOf" [
      test "a Passed result is not failing" {
        let t1 = TestId.TestId "t1"
        let state = { LiveTestState.empty with LastResults = Map.ofList [ t1, mkResult t1 (TestResult.Passed TimeSpan.Zero) ] }
        failingOf [ t1 ] state
        |> Expect.equal "passed tests are excluded" []
      }

      test "a Failed result is failing" {
        let t1 = TestId.TestId "t1"
        let failure = TestFailure.AssertionFailed "boom"
        let state = { LiveTestState.empty with LastResults = Map.ofList [ t1, mkResult t1 (TestResult.Failed(failure, TimeSpan.Zero)) ] }
        failingOf [ t1 ] state
        |> Expect.equal "failed tests are included" [ t1 ]
      }

      test "Skipped, NotRun, and NoResult are all treated as failing (fail-closed)" {
        let t1 = TestId.TestId "t1"
        let t2 = TestId.TestId "t2"
        let t3 = TestId.TestId "t3"
        let state =
          { LiveTestState.empty with
              LastResults =
                Map.ofList [
                  t1, mkResult t1 (TestResult.Skipped "policy disabled")
                  t2, mkResult t2 TestResult.NotRun
                  t3, mkResult t3 (TestResult.NoResult NoResultReason.StreamEnded)
                ] }
        failingOf [ t1; t2; t3 ] state
        |> Expect.equal "none of these count as a confirmed pass" [ t1; t2; t3 ]
      }

      test "a test with no result at all is failing (fail-closed on unknowns)" {
        let t1 = TestId.TestId "t1"
        let state = LiveTestState.empty
        failingOf [ t1 ] state
        |> Expect.equal "no evidence of a pass is not a pass" [ t1 ]
      }

      test "a mixed batch returns exactly the ones that did not pass, in request order" {
        let t1 = TestId.TestId "t1"
        let t2 = TestId.TestId "t2"
        let t3 = TestId.TestId "t3"
        let state =
          { LiveTestState.empty with
              LastResults =
                Map.ofList [
                  t1, mkResult t1 (TestResult.Passed TimeSpan.Zero)
                  t2, mkResult t2 (TestResult.Failed(TestFailure.AssertionFailed "x", TimeSpan.Zero))
                ] }
        failingOf [ t1; t2; t3 ] state
        |> Expect.equal "t2 failed, t3 never reported" [ t2; t3 ]
      }
    ]

    testList "runTestsInSession — session trust gate" [
      testAsync "an untrustworthy (missing) session is refused without dispatching a run" {
        let tc1 = mkTestCase "t1"
        let initial =
          { LiveTestState.empty with
              DiscoveredTests = [| tc1 |]
              SessionDiscovery = Map.ofList [ "sess1", DiscoveryProgress.Completed ] }
        let runtime, dispatchCount = mkFakeRuntime initial (fun _apply tests state -> startRunReducer tests state)
        let! result = runTestsInSession runtime missingObservation "sess1" [ tc1.Id ]
        match result with
        | Error _ -> ()
        | Ok _ -> failtest "expected the untrustworthy session to be refused"
        dispatchCount ()
        |> Expect.equal "no run was ever dispatched" 0
      }

      testAsync "a warming-up session is refused without dispatching a run" {
        let tc1 = mkTestCase "t1"
        let initial =
          { LiveTestState.empty with
              DiscoveredTests = [| tc1 |]
              SessionDiscovery = Map.ofList [ "sess1", DiscoveryProgress.Completed ] }
        let runtime, dispatchCount = mkFakeRuntime initial (fun _apply tests state -> startRunReducer tests state)
        let! result = runTestsInSession runtime (warmingUpObservation "sess1") "sess1" [ tc1.Id ]
        match result with
        | Error _ -> ()
        | Ok _ -> failtest "expected the warming-up session to be refused"
        dispatchCount ()
        |> Expect.equal "no run was ever dispatched" 0
      }

      testAsync "an empty test list trivially succeeds without dispatching a run" {
        let initial = LiveTestState.empty
        let runtime, dispatchCount = mkFakeRuntime initial (fun _apply tests state -> startRunReducer tests state)
        let! result = runTestsInSession runtime (trustedObservation "sess1") "sess1" []
        result |> Expect.equal "nothing to run, nothing failing" (Ok [])
        dispatchCount ()
        |> Expect.equal "no run was dispatched for an empty request" 0
      }

      testAsync "a test not attributed to the requested session is refused without dispatching a run" {
        let tc1 = mkTestCase "t1"
        let initial =
          { LiveTestState.empty with
              DiscoveredTests = [| tc1 |]
              SessionDiscovery = Map.ofList [ "otherSession", DiscoveryProgress.Completed ] }
        let runtime, dispatchCount = mkFakeRuntime initial (fun _apply tests state -> startRunReducer tests state)
        let! result = runTestsInSession runtime (trustedObservation "sess1") "sess1" [ tc1.Id ]
        match result with
        | Error _ -> ()
        | Ok _ -> failtest "expected a session-attribution mismatch to be refused"
        dispatchCount ()
        |> Expect.equal "no run was ever dispatched" 0
      }
    ]

    testList "runTestsInSession — end to end against a fake runtime" [
      testAsync "awaits the run and reports exactly the failing tests" {
        let tc1 = mkTestCase "t1"
        let tc2 = mkTestCase "t2"
        let initial =
          { LiveTestState.empty with
              DiscoveredTests = [| tc1; tc2 |]
              SessionDiscovery = Map.ofList [ "sess1", DiscoveryProgress.Completed ] }
        // `onRunTestsRequested` plays the daemon's reducer: it starts the run
        // synchronously (bumping the generation, marking sess1 Running) and
        // schedules a background fiber that, shortly after, applies the
        // "TestRunCompleted" state (phase back to Idle, results recorded) —
        // exactly mirroring how the real daemon starts a run synchronously
        // in `update` but only learns of completion later, asynchronously.
        let onRunTestsRequested (applyState: LiveTestState -> unit) (requested: TestCase array) (state: LiveTestState) =
          let running = startRunReducer requested state
          async {
            do! Async.Sleep 50
            let completed =
              completeRunReducer
                [ tc1.Id, TestResult.Passed TimeSpan.Zero
                  tc2.Id, TestResult.Failed(TestFailure.AssertionFailed "boom", TimeSpan.Zero) ]
                requested
                running
            applyState completed
          } |> Async.Start
          running
        let runtime, dispatchCount = mkFakeRuntime initial onRunTestsRequested
        let! result = runTestsInSession runtime (trustedObservation "sess1") "sess1" [ tc1.Id; tc2.Id ]
        result |> Expect.equal "t1 passed, t2 failed" (Ok [ tc2.Id ])
        dispatchCount ()
        |> Expect.equal "exactly one run was dispatched" 1
      }
    ]
  ]
