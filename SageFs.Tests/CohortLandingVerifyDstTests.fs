/// Deterministic simulation of cohort-landing VERIFICATION: the step that turns
/// a session's live-testing state into a landing verdict.
///
/// `CohortLandingGate` (an [Integration] host suite, ~200s) proved this by
/// booting a daemon, building a fixture, waiting on real rebuilds and real
/// Expecto discovery. The existing landing DST (SageFs.Simulation
/// CohortLandingSim) SCRIPTS each landing's verdict up front, so it never
/// exercised how a verdict is derived. This file closes that gap in
/// milliseconds, and under every interleaving rather than the one a real run
/// happens to hit:
///
///   * The REAL reducer is the subject. Every event goes through
///     `SageFsUpdate.update`, the daemon's own Elm update, never a hand-written
///     mirror of it. The verifier's decisions are the REAL
///     `CohortLandingVerify.preflight` / `advance` that `runTestsInSession`
///     uses.
///   * The performer is faithful to production's order: a `RunAffectedTests`
///     effect makes the performer dispatch `TestRunStarted` FIRST (SageFsApp.fs
///     RunAffectedTests handler), then the worker streams `TestResultsBatch`es
///     and a `TestRunCompleted`, in FIFO order on the session's channel.
///   * Chaos is DATA: a seed picks where session switches, re-discoveries,
///     another session's traffic, a previous run's results and a competing
///     file-save run land. The same seed gives the same trace.
///   * The verifier observes after EVERY message, the worst case its
///     event-driven wait permits (it re-evaluates on each model change).
///   * Ground truth is independent: which requested tests truly fail in the
///     code being verified, scripted per scenario.
///   * Twins prove the invariants have teeth: the pre-9a73197a performer (which
///     forced `SessionSwitched` to the session before verifying) and a
///     non-fail-closed verdict are each caught.
module SageFs.Tests.CohortLandingVerifyDstTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Features.CohortLandingVerify

/// The landing's integration session and one unrelated session (valid 8-hex ids).
let integration = "1a2b3c4d"
let otherSession = "5e6f7a8b"

let private mkCase (name: string) : TestCase =
  { Id = TestId.TestId name
    FullName = name
    DisplayName = name
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

let private result (id: TestId) (passes: bool) : TestRunResult =
  { TestId = id
    TestName = TestId.value id
    Result =
      match passes with
      | true -> TestResult.Passed TimeSpan.Zero
      | false -> TestResult.Failed (TestFailure.AssertionFailed "simulated failure", TimeSpan.Zero)
    Timestamp = DateTimeOffset.UnixEpoch
    Output = None }

/// Where the active pointer starts.
[<RequireQualifiedAccess>]
type Pointer =
  | Awaiting
  | OnIntegration
  | OnOther

/// One chaos event. Each maps to REAL messages; nothing here mutates the model.
[<RequireQualifiedAccess>]
type Chaos =
  /// The user switches the dashboard to a session (fromId = the current pointer).
  | SwitchTo of integrationSession: bool
  /// A rebuild re-discovers the integration session's (same) tests.
  | Rediscover
  /// The other session discovers its own tests.
  | OtherDiscovers
  /// The other session runs its tests to completion.
  | OtherRuns
  /// A PREVIOUS run's result batch for the integration session (all passing:
  /// the code before the landing). Delivered only before this run's own
  /// results, the order a FIFO worker channel allows.
  | StaleBatch
  /// A file-save run in the integration session (same code, same truth) that
  /// the worker executes before this verification's run.
  | CompetingRun

type Scenario =
  { Seed: int
    Pointer: Pointer
    /// Per requested test: does it truly pass in the code being verified?
    Truth: bool list
    /// A green baseline run already happened (LastResults all Passed).
    Baseline: bool
    /// This run's results arrive in this many batches (1 or 2).
    Batches: int
    /// Chaos, each at a 0-based slot in the protocol (see `schedule`).
    Chaos: (int * Chaos) list }

/// How the verification ended.
[<RequireQualifiedAccess>]
type Outcome =
  | Refused of reason: string
  | Verdict of failing: TestId list
  | Hung of progress: string

type Trace =
  { Scenario: Scenario
    Requested: TestId list
    TrulyFailing: TestId list
    Outcome: Outcome }

let private msg e = SageFsMsg.Event e

let private cases (n: int) = [ for i in 1 .. n -> mkCase (sprintf "Fixture.Util.test%d" i) ]
let private otherCases = [ mkCase "Other.suite.a"; mkCase "Other.suite.b" ]

/// The verifier under test: which pre-verification step the performer runs.
[<RequireQualifiedAccess>]
type Performer =
  /// Production: verify straight from the session's own cycle.
  | Real
  /// Pre-9a73197a: force `SessionSwitched(None, integration)` first.
  | SwitchFirstTwin

/// The verifier algorithm under test.
[<RequireQualifiedAccess>]
type VerdictFn =
  /// Production: follow ITS request's durable record; count only results
  /// stamped with its run's generation.
  | Real
  /// The exact pre-fix algorithm: "my run" is the first generation seen after
  /// dispatch, it is done when no session runs that generation, and the
  /// verdict reads `LastResults` whoever produced them.
  | GenerationGuessTwin

/// Which cycle the verifier reads its session's state from.
[<RequireQualifiedAccess>]
type Reader =
  /// Production: the cycle that OWNS the session (`cycleOwnedBySession`).
  | Owned
  /// Pre-`cycleOwnedBySession`: whatever the ACTIVE POINTER shows (Primary).
  | ActivePointerTwin

/// Run one scenario to completion and report what the verifier concluded.
let runWith (reader: Reader) (performer: Performer) (verdictFn: VerdictFn) (scenario: Scenario) : Trace =
  let requestedCases = cases scenario.Truth.Length
  let requested = requestedCases |> List.map _.Id
  let truth = List.zip requested scenario.Truth |> Map.ofList
  let trulyFailing = requested |> List.filter (fun id -> not truth[id])

  let apply (model: SageFsModel) (m: SageFsMsg) =
    let model', effects = SageFsUpdate.update m model
    model', effects

  // ── Setup: pointer, the integration session's discovery, optional baseline ──
  let pointerId =
    match scenario.Pointer with
    | Pointer.Awaiting -> None
    | Pointer.OnIntegration -> Some integration
    | Pointer.OnOther -> Some otherSession
  let mutable model = SageFsModel.initial ()
  match pointerId with
  | Some id -> model <- fst (apply model (msg (TuiEvent.SessionSwitched (None, id))))
  | None -> ()
  model <- fst (apply model (msg (TuiEvent.TestsDiscovered (integration, Array.ofList requestedCases))))
  if scenario.Baseline then
    model <- fst (apply model (msg (TuiEvent.TestResultsBatch (Some integration, requested |> List.map (fun id -> result id true) |> Array.ofList))))

  let currentPointer () =
    ActiveSession.sessionId model.Sessions.ActiveSessionId |> Option.map WorkerProtocol.SessionId.value

  let chaosAt slot = scenario.Chaos |> List.filter (fun (s, _) -> s = slot) |> List.map snd

  // Worker FIFO for the integration session: messages delivered in order.
  let workerQueue = Collections.Generic.Queue<SageFsMsg>()
  let mutable thisRunDelivered = false

  let stateNow () =
    match reader with
    | Reader.Owned -> (SageFsModel.cycleOwnedBySession integration model).TestState
    | Reader.ActivePointerTwin -> model.LiveTesting.TestState
  let mutable progress : RunProgress option = None
  // The twin's own state: None = not dispatched, Some (Choice1Of2 prior) =
  // awaiting a generation move, Some (Choice2Of2 g) = "in flight" on g.
  let mutable guess : Choice<RunGeneration, RunGeneration> option = None
  let observe () =
    match verdictFn, progress with
    | VerdictFn.Real, Some p -> progress <- Some (advance requested p (stateNow ()))
    | VerdictFn.GenerationGuessTwin, Some (RunProgress.Finished _) -> ()
    | VerdictFn.GenerationGuessTwin, Some _ ->
      let s = stateNow ()
      let rec step g =
        match g with
        | Some (Choice1Of2 prior) when s.LastGeneration <> prior -> step (Some (Choice2Of2 s.LastGeneration))
        | Some (Choice2Of2 gen) when isGenerationComplete gen s ->
          progress <- Some (RunProgress.Finished (failingOf requested s))
          g
        | other -> other
      guess <- step guess
    | _, None -> ()

  /// Deliver one message: real update, then the real performer's synchronous
  /// dispatch for `RunAffectedTests` (TestRunStarted first), then observe.
  let rec deliver (m: SageFsMsg) =
    let model', effects = apply model m
    model <- model'
    observe ()
    for effect in effects do
      let run =
        match effect with
        | SageFsEffect.TestCycle (TestCycleEffect.RunAffectedTests req) ->
          Some (req, TuiEvent.TestRunStarted (req.Tests |> Array.map _.Id, req.SessionId))
        | SageFsEffect.TestCycle (TestCycleEffect.RunRequestedTests (req, generation)) ->
          let ids = req.Tests |> Array.map _.Id
          match req.SessionId with
          | Some sid -> Some (req, TuiEvent.TestRunStartedAt (ids, sid, generation))
          | None -> Some (req, TuiEvent.TestRunStarted (ids, None))
        | _ -> None
      match run with
      | Some (req, startMessage) when not (Array.isEmpty req.Tests) ->
        // The real performer dispatches the start message synchronously,
        // before the run executes (SageFsApp.fs RunAffectedTests/RunRequestedTests).
        deliver (msg startMessage)
        // The worker streams this run's results (current code => truth), then completes.
        let results = req.Tests |> Array.map (fun tc -> result tc.Id (truth |> Map.tryFind tc.Id |> Option.defaultValue true))
        let batches =
          match scenario.Batches, results.Length with
          | 2, n when n >= 2 -> [ results[.. n / 2 - 1]; results[n / 2 ..] ]
          | _ -> [ results ]
        for b in batches do workerQueue.Enqueue(msg (TuiEvent.TestResultsBatch (req.SessionId, b)))
        workerQueue.Enqueue(msg (TuiEvent.TestRunCompleted req.SessionId))
      | _ -> ()

  let applyChaos (c: Chaos) =
    match c with
    | Chaos.SwitchTo toIntegration ->
      let target = match toIntegration with true -> integration | false -> otherSession
      deliver (msg (TuiEvent.SessionSwitched (currentPointer (), target)))
    | Chaos.Rediscover ->
      deliver (msg (TuiEvent.TestsDiscovered (integration, Array.ofList requestedCases)))
    | Chaos.OtherDiscovers ->
      deliver (msg (TuiEvent.TestsDiscovered (otherSession, Array.ofList otherCases)))
    | Chaos.OtherRuns ->
      let ids = otherCases |> List.map _.Id |> Array.ofList
      deliver (msg (TuiEvent.TestRunStarted (ids, Some otherSession)))
      deliver (msg (TuiEvent.TestResultsBatch (Some otherSession, ids |> Array.map (fun id -> result id true))))
      deliver (msg (TuiEvent.TestRunCompleted (Some otherSession)))
    | Chaos.StaleBatch ->
      match thisRunDelivered with
      | true -> ()
      | false ->
        deliver (msg (TuiEvent.TestResultsBatch (Some integration, requested |> List.map (fun id -> result id true) |> Array.ofList)))
    | Chaos.CompetingRun ->
      match thisRunDelivered with
      | true -> ()
      | false ->
        let ids = requested |> Array.ofList
        deliver (msg (TuiEvent.TestRunStarted (ids, Some integration)))
        deliver (msg (TuiEvent.TestResultsBatch (Some integration, ids |> Array.map (fun id -> result id truth[id]))))
        deliver (msg (TuiEvent.TestRunCompleted (Some integration)))

  // ── Protocol, with chaos at every slot ──
  // slot 0: before verification starts
  for c in chaosAt 0 do applyChaos c
  // The pre-9a73197a performer's extra step.
  match performer with
  | Performer.SwitchFirstTwin -> deliver (msg (TuiEvent.SessionSwitched (None, integration)))
  | Performer.Real -> ()
  // slot 1: between the performer's step and preflight
  for c in chaosAt 1 do applyChaos c
  let outcome =
    match preflight integration requested (stateNow ()) with
    | Preflight.NothingToRun -> Outcome.Verdict []
    | Preflight.NotAttributed ids -> Outcome.Refused (sprintf "%d not attributed" ids.Length)
    | Preflight.NotDiscovered -> Outcome.Refused "not discovered"
    | Preflight.Dispatch dispatchCases ->
      let requestId = RunRequestId.fresh ()
      progress <- Some (RunProgress.Pending requestId)
      guess <- Some (Choice1Of2 (stateNow ()).LastGeneration)
      deliver (msg (TuiEvent.RunTestsRequested (Some integration, dispatchCases, Some requestId)))
      // slots 2.. : between each worker message
      let mutable slot = 2
      for c in chaosAt slot do applyChaos c
      while workerQueue.Count > 0 do
        let m = workerQueue.Dequeue()
        match m with
        | SageFsMsg.Event (TuiEvent.TestResultsBatch _) -> thisRunDelivered <- true
        | _ -> ()
        deliver m
        slot <- slot + 1
        for c in chaosAt slot do applyChaos c
      match progress with
      | Some (RunProgress.Finished failing) -> Outcome.Verdict failing
      | Some p -> Outcome.Hung (sprintf "%A" p)
      | None -> Outcome.Hung "never dispatched"
  { Scenario = scenario; Requested = requested; TrulyFailing = trulyFailing; Outcome = outcome }

let run (performer: Performer) (verdictFn: VerdictFn) (scenario: Scenario) : Trace =
  runWith Reader.Owned performer verdictFn scenario

// ── Invariants ──

/// SAFETY: a verdict never omits a truly failing test, so a landing with a
/// failing test can never be judged green. Refusing, or over-reporting, is safe.
let safe (t: Trace) =
  match t.Outcome with
  | Outcome.Verdict failing -> t.TrulyFailing |> List.forall (fun id -> List.contains id failing)
  | Outcome.Refused _ | Outcome.Hung _ -> true

/// NO-HANG: once every message is delivered the verifier has decided.
let decided (t: Trace) =
  match t.Outcome with
  | Outcome.Hung _ -> false
  | _ -> true

/// EXACT: under benign chaos (switches, re-discovery, other-session traffic;
/// no stale batches, no competing run) the verdict is exactly the truly
/// failing set: never a refusal, never a false failure. This is the property
/// the pre-9a73197a flake broke ("N of N not attributed").
let exact (t: Trace) =
  match t.Outcome with
  | Outcome.Verdict failing -> Set.ofList failing = Set.ofList t.TrulyFailing
  | _ -> false

let private isBenign (c: Chaos) =
  match c with
  | Chaos.StaleBatch | Chaos.CompetingRun -> false
  | _ -> true

// ── Scenarios: deterministic from a seed ──

let scenarioOf (seed: int) (allowAdversarial: bool) : Scenario =
  let rng = Random(seed)
  let n = 1 + rng.Next 4
  let chaosKinds =
    [| yield Chaos.SwitchTo true
       yield Chaos.SwitchTo false
       yield Chaos.Rediscover
       yield Chaos.OtherDiscovers
       yield Chaos.OtherRuns
       if allowAdversarial then
         yield Chaos.StaleBatch
         yield Chaos.CompetingRun |]
  { Seed = seed
    Pointer = [| Pointer.Awaiting; Pointer.OnIntegration; Pointer.OnOther |][rng.Next 3]
    Truth = [ for _ in 1 .. n -> rng.Next 3 > 0 ]
    Baseline = rng.Next 2 = 0
    Batches = 1 + rng.Next 2
    Chaos = [ for _ in 1 .. rng.Next 5 -> rng.Next 6, chaosKinds[rng.Next chaosKinds.Length] ] }

let private seeds = [ 1 .. 400 ]

let private violations (check: Trace -> bool) (traces: Trace list) =
  traces |> List.filter (check >> not)

let private describe (t: Trace) =
  sprintf "seed=%d pointer=%A truth=%A baseline=%b batches=%d chaos=%A -> %A (truly failing %A)"
    t.Scenario.Seed t.Scenario.Pointer t.Scenario.Truth t.Scenario.Baseline t.Scenario.Batches
    t.Scenario.Chaos t.Outcome t.TrulyFailing

/// Fail with the first few counterexamples (seed + full scenario, so each is
/// replayable) and the total count, rather than a bare "should be empty".
let private expectNone (label: string) (bad: Trace list) =
  match bad with
  | [] -> ()
  | _ ->
    failtestf "%s: %d of %d seeds.\n%s" label bad.Length seeds.Length
      (bad |> List.truncate 6 |> List.map describe |> String.concat "\n")

[<Tests>]
let tests =
  testList "Cohort landing verification DST" [
    testCase "SAFETY — no interleaving lets the real verifier call a truly failing landing green" <| fun _ ->
      seeds
      |> List.map (fun s -> run Performer.Real VerdictFn.Real (scenarioOf s true))
      |> violations safe
      |> expectNone "false greens"

    testCase "NO-HANG — the real verifier always reaches a decision once its run's messages are in" <| fun _ ->
      seeds
      |> List.map (fun s -> run Performer.Real VerdictFn.Real (scenarioOf s true))
      |> violations decided
      |> expectNone "undecided verifications"

    testCase "EXACT — under benign chaos the real verifier returns exactly the truly failing tests" <| fun _ ->
      seeds
      |> List.map (fun s -> run Performer.Real VerdictFn.Real (scenarioOf s false))
      |> violations exact
      |> expectNone "refusals or wrong verdicts under benign chaos"

    // 9a73197a removed the performer's forced SessionSwitched because it
    // wiped the session's own discovery. With Primary single-owner and a
    // pointerless switch parking that owner, the same switch can no longer
    // wipe anything: the bug is gone by construction, not by avoidance.
    testCase "STRUCTURAL — even the pre-9a73197a performer's forced switch cannot break EXACT any more" <| fun _ ->
      seeds
      |> List.map (fun s -> run Performer.SwitchFirstTwin VerdictFn.Real (scenarioOf s false))
      |> violations exact
      |> expectNone "the forced switch still loses the session's state"

    testCase "TWIN — reading the active pointer's cycle instead of the session's own breaks EXACT" <| fun _ ->
      seeds
      |> List.map (fun s -> runWith Reader.ActivePointerTwin Performer.Real VerdictFn.Real (scenarioOf s false))
      |> violations exact
      |> Expect.isNonEmpty "EXACT must catch a verifier that follows the pointer rather than the session"

    testCase "TWIN — the pre-fix verifier ('my run is the next generation') still makes false greens" <| fun _ ->
      seeds
      |> List.map (fun s -> run Performer.Real VerdictFn.GenerationGuessTwin (scenarioOf s true))
      |> violations safe
      |> Expect.isNonEmpty "SAFETY must catch the algorithm the fix replaced"

    testCase "the scenarios are deterministic: the same seed gives the same trace" <| fun _ ->
      let a = run Performer.Real VerdictFn.Real (scenarioOf 42 true)
      let b = run Performer.Real VerdictFn.Real (scenarioOf 42 true)
      (sprintf "%A" a.Outcome) |> Expect.equal "replayable" (sprintf "%A" b.Outcome)
  ]
