/// Item 14d (sagefs-multiagent-vision.md) — the blocking test-run primitive
/// cohort landing verification needs.
///
/// Today `POST /api/live-testing/run` (McpServer.fs `RunTestsRequested`
/// handler) is fire-and-forget: it dispatches `TuiEvent.RunTestsRequested`
/// and replies `{queued=N}` immediately — nothing awaits the pass/fail
/// verdict. A cohort-landing decision needs the opposite: run exactly a
/// cohort's tests in a specific session and BLOCK until that run's own
/// results are in, then answer which of those tests failed.
///
/// This module does not invent a new run mechanism — it reuses the same
/// `RunTestsRequested` event McpServer.fs dispatches, and correlates the
/// resulting run via `RunGeneration` (the daemon's own staleness fence,
/// `LiveTestingTypes.fs` `RunGeneration`/`TestRunPhase`), polling the Elm
/// model until that generation's run is no longer in flight.
///
/// `RunTestsRequested` now carries an explicit `targetSession` (mirrors
/// `CoverageBitmapCollected`/`TestRunStarted`'s per-session routing — see
/// `SageFsApp.fs`'s handler and `SageFsModel.cycleForSession`), so this
/// primitive routes every read AND the dispatch itself through `sessionId`'s
/// own live-testing cycle (Primary when it's the active session, its own
/// `PerSessionLiveTesting` slot otherwise) instead of assuming `sessionId` is
/// whatever session Primary happens to be tracking. A caller no longer has to
/// force `sessionId` to become Primary (e.g. via `SessionSwitched`) before
/// trusting this primitive's verdict — concurrent cohorts landing onto
/// different background sessions are each observed through their own cycle.
module SageFs.Features.CohortLandingVerify

open System
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Features.Verification

/// True once `generation`'s run is no longer in flight anywhere: no
/// session's `RunPhases` entry is still `Running`/`RunningButEdited` at
/// exactly this generation. `RunGeneration` is a single counter shared by
/// every session (`LiveTestState.LastGeneration`), so this needs no
/// `sessionId` — a generation belongs to exactly one run, however many
/// sessions it was routed across.
let isGenerationComplete (generation: RunGeneration) (state: LiveTestState) : bool =
  state.RunPhases
  |> Map.forall (fun _ phase ->
    match phase with
    | TestRunPhase.Idle -> true
    | TestRunPhase.Running g -> g <> generation
    | TestRunPhase.RunningButEdited g -> g <> generation)

/// Which of `tests` do NOT have a confirmed `Passed` result in `state`.
/// Fail-closed by design: `Failed`, `Skipped`, `NotRun`, `NoResult`, and "no
/// result at all" (never reported, or a stale id) all count as failing here.
/// This primitive exists to gate whether a cohort is safe to land — a
/// "we don't know" must never be read as a green light the way it is for
/// e.g. `FlakyDetection`'s `TestOutcome` window (which treats
/// Skipped/NotRun/NoResult as non-failures for flake scoring; that is a
/// different question — "is this test unreliable" — from this primitive's
/// "did this specific requested run of this test actually pass").
let failingOf (tests: TestId list) (state: LiveTestState) : TestId list =
  tests
  |> List.filter (fun testId ->
    match Map.tryFind testId state.LastResults with
    | Some { Result = TestResult.Passed _ } -> false
    | _ -> true)

/// Overall budget for a `runTestsInSession` call: the worker-side per-run
/// cancellation budget (`Timeouts.globalTestRun`, tunable via
/// `SAGEFS_TEST_RUN_TIMEOUT_MINUTES` — see `SageFs.Core/Timeouts.fs`) plus
/// slack for daemon-side dispatch latency and result aggregation.
let awaitBudget () : TimeSpan =
  Timeouts.globalTestRun () + TimeSpan.FromSeconds 30.0

let private pollDelayMs = 200

/// Human-readable reason a session isn't trustworthy enough to believe a
/// landing verdict from it. Mirrors the wording pattern in
/// `Verification.TargetedVerification.summarize`'s blocked-session cases,
/// specialized to this primitive's caller rather than reusing that function
/// directly (its message is phrased for a `targeted_verify` report, not for
/// "refusing to run tests").
let private describeUntrustworthy (trust: SessionTrust) : string =
  match trust with
  | SessionTrust.Trusted sessionId ->
    // Unreachable in practice — callers only reach this path when
    // `SessionTrust.isTrusted` is false — kept total rather than partial.
    sprintf "session '%s' is trusted" sessionId
  | SessionTrust.Ambiguous candidateSessionIds ->
    sprintf
      "multiple sessions match (%s) — pin one before trusting a landing verdict"
      (String.concat ", " candidateSessionIds)
  | SessionTrust.WarmingUp sessionId ->
    sprintf "session '%s' is still warming up" sessionId
  | SessionTrust.Unavailable (sessionId, status) ->
    sprintf "session '%s' is not available (%s)" sessionId status
  | SessionTrust.StaleDefinitions filePath ->
    sprintf "session still carries stale definitions for '%s'" filePath
  | SessionTrust.TypeIdentityCompromised diagnostic ->
    sprintf "type identity is compromised (%s)" diagnostic
  | SessionTrust.Missing ->
    "no matching session was found"

/// Run exactly `tests` in `sessionId` and AWAIT the pass/fail verdict — the
/// blocking async-readback primitive cohort landing needs; the existing
/// `/api/live-testing/run` path only queues and returns immediately.
///
/// Gated on session trust: `sessionObservation` is classified via the
/// existing `SessionTrust.classify` (the same pure decision `targeted_verify`
/// uses). An untrustworthy session (ambiguous, warming up, stale
/// definitions, compromised type identity, missing) returns `Error`
/// WITHOUT triggering a run — fail-closed, so a stale/ambiguous session can
/// never produce a false "all green".
///
/// On success, `Ok failingTests` — the subset of `tests` that did not come
/// back `Passed` (empty list = every requested test passed). On failure to
/// even start or complete the run in time, `Error reason`.
let runTestsInSession
  (elmRuntime: ElmRuntime<SageFsModel, SageFsMsg, RenderRegion>)
  (sessionObservation: SessionTrust.SessionObservation)
  (sessionId: string)
  (tests: TestId list)
  : Async<Result<TestId list, string>> =
  async {
    let trust = SessionTrust.classify sessionObservation
    match SessionTrust.isTrusted trust with
    | false ->
      return Error (sprintf "Refusing to run tests in session '%s': %s" sessionId (describeUntrustworthy trust))
    | true ->

    match tests with
    | [] ->
      return Ok []
    | _ ->

    let testState = (SageFsModel.cycleOwnedBySession sessionId (elmRuntime.GetModel())).TestState

    // Fail closed on a caller/routing mismatch instead of silently running
    // a different session's tests (or a subset of them): a cycle now belongs
    // wholly to one session (`LiveTestState.ownerSessionId`), so either every
    // requested test is attributed to `sessionId` or none of them are.
    let notAttributedToSession =
      match LiveTestState.ownerSessionId testState = Some sessionId with
      | true -> []
      | false -> tests

    match notAttributedToSession with
    | _ :: _ ->
      let names = notAttributedToSession |> List.map TestId.value |> String.concat ", "
      return Error (
        sprintf
          "Refusing to run — %d of %d requested test(s) are not attributed to session '%s': %s"
          notAttributedToSession.Length tests.Length sessionId names)
    | [] ->

    let requestedIds = tests |> Set.ofList
    let testCases =
      testState.DiscoveredTests
      |> Array.filter (fun testCase -> requestedIds.Contains testCase.Id)

    match testCases.Length = tests.Length with
    | false ->
      return Error (
        sprintf
          "Refusing to run — some requested test id(s) are not present in session '%s''s discovered tests."
          sessionId)
    | true ->

    let priorGeneration = testState.LastGeneration
    let deadline = DateTime.UtcNow + awaitBudget ()

    elmRuntime.Dispatch (SageFsMsg.Event (TuiEvent.RunTestsRequested (Some sessionId, testCases)))

    // Dispatch is asynchronous (ElmLoop.fs runs a dedicated drain thread), so
    // the model doesn't necessarily reflect the new run the instant Dispatch
    // returns — wait for the shared generation counter to move before
    // tracking completion of "our" generation.
    let rec awaitStart () : Async<Result<RunGeneration, string>> =
      async {
        let currentGeneration = (SageFsModel.cycleOwnedBySession sessionId (elmRuntime.GetModel())).TestState.LastGeneration
        match currentGeneration <> priorGeneration with
        | true -> return Ok currentGeneration
        | false ->
          match DateTime.UtcNow > deadline with
          | true -> return Error "Test run timed out before it started."
          | false ->
            do! Async.Sleep pollDelayMs
            return! awaitStart ()
      }

    let rec awaitCompletion (generation: RunGeneration) : Async<Result<TestId list, string>> =
      async {
        let state = (SageFsModel.cycleOwnedBySession sessionId (elmRuntime.GetModel())).TestState
        match isGenerationComplete generation state with
        | true -> return Ok (failingOf tests state)
        | false ->
          match DateTime.UtcNow > deadline with
          | true -> return Error "Test run timed out."
          | false ->
            do! Async.Sleep pollDelayMs
            return! awaitCompletion generation
      }

    let! started = awaitStart ()
    match started with
    | Error reason -> return Error reason
    | Ok generation -> return! awaitCompletion generation
  }
