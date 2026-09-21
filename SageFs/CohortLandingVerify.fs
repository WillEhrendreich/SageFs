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
/// `RunTestsRequested` event McpServer.fs dispatches, tagged with a
/// `RunRequestId`, and follows THAT request's durable record
/// (`LiveTestState.RunRequests`: its one generation, allocated at request
/// time, and its status). The verdict counts only results stamped with that
/// generation (`RequestedRuns.failingIn`). It used to infer "its" run as "the
/// next generation" and read `LastResults` unconditionally, which let a
/// previous run's results decide a landing (CohortLandingVerifyDstTests).
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

/// What the verifier decides from a session's own cycle before dispatching
/// anything. Pure, so the async primitive below and the deterministic
/// simulation (CohortLandingVerifyDstTests) decide by the SAME function.
[<RequireQualifiedAccess>]
type Preflight =
  /// Requested tests whose session's discovery is not in the cycle resolved
  /// for it. Fail-closed: running them would verify some OTHER session's code.
  | NotAttributed of TestId list
  /// Attributed, but some requested ids are not among the discovered tests.
  | NotDiscovered
  /// Nothing was requested; there is nothing to verify.
  | NothingToRun
  /// Run these cases.
  | Dispatch of cases: TestCase array

/// Attribution is "did `sessionId` discover into this cycle":
/// `SessionDiscovery |> Map.containsKey sessionId`, NOT
/// `ownerSessionId = Some sessionId`. `ownerSessionId` is `Map.tryHead`, so a
/// cycle that legitimately carries `sessionId`'s discovery alongside another
/// key would be mis-rejected purely by key sort order.
let preflight (sessionId: string) (tests: TestId list) (testState: LiveTestState) : Preflight =
  match tests with
  | [] -> Preflight.NothingToRun
  | _ ->
    match testState.SessionDiscovery |> Map.containsKey sessionId with
    | false -> Preflight.NotAttributed tests
    | true ->
      let requestedIds = tests |> Set.ofList
      let cases = testState.DiscoveredTests |> Array.filter (fun tc -> requestedIds.Contains tc.Id)
      match cases.Length = tests.Length with
      | false -> Preflight.NotDiscovered
      | true -> Preflight.Dispatch cases

/// Where a dispatched verification run stands, read from ITS OWN request
/// record (`LiveTestState.RunRequests`), which the model keeps durably: a waiter
/// re-evaluates only on batched model-changed notifications and could miss a
/// short-lived phase. Identity is the request id, never "the next generation":
/// an explicit run used to be bumped twice and a verifier could decide on a
/// previous run's results (found by CohortLandingVerifyDstTests).
[<RequireQualifiedAccess>]
type RunProgress =
  /// Dispatched; the request is not recorded yet, or the worker has not started it.
  | Pending of requestId: RunRequestId
  /// The worker started it.
  | Started of requestId: RunRequestId
  | Finished of failing: TestId list

let advance (tests: TestId list) (progress: RunProgress) (testState: LiveTestState) : RunProgress =
  match progress with
  | RunProgress.Finished _ -> progress
  | RunProgress.Pending rid
  | RunProgress.Started rid ->
    match Map.tryFind rid testState.RunRequests with
    | Some { RequestedStatus = RequestedRunStatus.Completed; RequestedGeneration = g } ->
      RunProgress.Finished (RequestedRuns.failingIn g tests testState)
    | Some { RequestedStatus = RequestedRunStatus.Running } -> RunProgress.Started rid
    | Some { RequestedStatus = RequestedRunStatus.Pending } -> progress
    | None ->
      match progress with
      // Recorded once, then evicted from the bounded map: it can no longer be
      // attributed, so every requested test fails closed.
      | RunProgress.Started _ -> RunProgress.Finished tests
      | _ -> progress

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
  // Event-driven wait capability (`timeout -> condition -> Async<bool>`): fires
  // the instant a pure condition over the model holds, off the model-changed
  // notification — never a poll cadence. Injected so this module stays free of
  // the daemon's event plumbing (and could wait on a remote model owner later).
  (awaitCondition: System.TimeSpan -> (unit -> bool) -> Async<bool>)
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

    let stateNow () = (SageFsModel.cycleOwnedBySession sessionId (elmRuntime.GetModel())).TestState

    match preflight sessionId tests (stateNow ()) with
    | Preflight.NothingToRun -> return Ok []
    | Preflight.NotAttributed notAttributed ->
      let names = notAttributed |> List.map TestId.value |> String.concat ", "
      // Self-diagnosing (this was a CI-only flake with no visible state): dump
      // where the session's discovery actually lives at the refusal point.
      let model = elmRuntime.GetModel()
      let testState = stateNow ()
      let keysOf (s: LiveTestState) = s.SessionDiscovery |> Map.toList |> List.map fst |> String.concat ","
      let diag =
        sprintf
          "[diag active=%A primaryOwner=%A primaryKeys=[%s] perSessionKeys=[%s] resolvedKeys=[%s] resolvedTotal=%d]"
          model.Sessions.ActiveSessionId
          (LiveTestState.ownerSessionId model.LiveTesting.TestState)
          (keysOf model.LiveTesting.TestState)
          (model.PerSessionLiveTesting |> Map.toList |> List.map fst |> String.concat ",")
          (keysOf testState)
          testState.DiscoveredTests.Length
      return Error (
        sprintf
          "Refusing to run — %d of %d requested test(s) are not attributed to session '%s': %s %s"
          notAttributed.Length tests.Length sessionId names diag)
    | Preflight.NotDiscovered ->
      return Error (
        sprintf
          "Refusing to run — some requested test id(s) are not present in session '%s''s discovered tests."
          sessionId)
    | Preflight.Dispatch testCases ->

    let budget = awaitBudget ()
    let requestId = RunRequestId.fresh ()
    elmRuntime.Dispatch (SageFsMsg.Event (TuiEvent.RunTestsRequested (Some sessionId, testCases, Some requestId)))

    // Event-driven, pure conditions over `advance` — the SAME step function the
    // deterministic simulation folds, reading this run's own durable request
    // record. The condition can be evaluated from concurrent model-changed
    // notifications; the step is serialised so a racing, older observation can
    // never overwrite a newer progress value.
    let progress = ref (RunProgress.Pending requestId)
    let step () =
      lock progress (fun () ->
        progress.Value <- advance tests progress.Value (stateNow ())
        progress.Value)
    let! started =
      awaitCondition budget (fun () ->
        match step () with
        | RunProgress.Pending _ -> false
        | _ -> true)
    match started with
    | false -> return Error "Test run timed out before it started."
    | true ->
      let! completed =
        awaitCondition budget (fun () ->
          match step () with
          | RunProgress.Finished _ -> true
          | _ -> false)
      match completed, progress.Value with
      | true, RunProgress.Finished failing -> return Ok failing
      | _ -> return Error "Test run timed out."
  }
