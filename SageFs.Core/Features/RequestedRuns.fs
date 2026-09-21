namespace SageFs.Features.LiveTesting
open System
open System.IO
open System.Numerics
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open SageFs.Measures

// Split from LiveTestingTypes.fs (file-size ratchet): the requested-run lifecycle is its own slice.
/// The lifecycle of an explicitly requested run, as pure transitions over
/// `LiveTestState`. Found by the cohort landing verification DST: an explicit
/// run used to be bumped TWICE (once on request, again when the worker
/// started it), and results carried no run identity, so a verifier could
/// declare its run finished before it reported and read a previous run's
/// results as its own: a false green. Here a run's generation is allocated
/// once, carried by the run, and stamped on the results it produces.
module RequestedRuns =
  /// Bound on tracked requests: oldest generations are dropped first.
  let maxTracked = 64

  let private bounded (requests: Map<RunRequestId, RequestedRun>) =
    match requests.Count > maxTracked with
    | false -> requests
    | true ->
      requests
      |> Map.toList
      |> List.sortByDescending (fun (_, r) -> RunGeneration.value r.RequestedGeneration)
      |> List.truncate maxTracked
      |> Map.ofList

  /// Allocate the run's generation (its ONLY bump) and, when the caller gave a
  /// request id, record the run as Pending. The phase is NOT set here: until
  /// the worker actually starts this run, another run's results may still be
  /// arriving and must not be stamped with this generation.
  let request (requestId: RunRequestId option) (session: string) (state: LiveTestState) : LiveTestState * RunGeneration =
    let generation = RunGeneration.next state.LastGeneration
    let requests =
      match requestId with
      | Some rid ->
        state.RunRequests
        |> Map.add rid { RequestedSession = session; RequestedGeneration = generation; RequestedStatus = RequestedRunStatus.Pending }
        |> bounded
      | None -> state.RunRequests
    { state with LastGeneration = generation; RunRequests = requests }, generation

  let private setStatus (session: string) (generation: RunGeneration) (status: RequestedRunStatus) (state: LiveTestState) =
    { state with
        RunRequests =
          state.RunRequests
          |> Map.map (fun _ r ->
            match r.RequestedSession = session && r.RequestedGeneration = generation with
            | true -> { r with RequestedStatus = status }
            | false -> r) }

  /// A run with `generation` is starting in `session`: any OTHER requested run
  /// still Running there has been displaced. The completion that follows will
  /// be the new run's, so without this the displaced run would wait forever;
  /// it is Completed now, and its verdict fails closed on whatever results it
  /// never got.
  let supersede (session: string) (generation: RunGeneration) (state: LiveTestState) : LiveTestState =
    { state with
        RunRequests =
          state.RunRequests
          |> Map.map (fun _ r ->
            match r.RequestedSession = session && r.RequestedGeneration <> generation && r.RequestedStatus = RequestedRunStatus.Running with
            | true -> { r with RequestedStatus = RequestedRunStatus.Completed }
            | false -> r) }

  /// The worker started the pre-allocated run `generation` in `session`.
  let started (session: string) (generation: RunGeneration) (state: LiveTestState) : LiveTestState =
    let last =
      match RunGeneration.value generation > RunGeneration.value state.LastGeneration with
      | true -> generation
      | false -> state.LastGeneration
    { supersede session generation state with
        RunPhases = state.RunPhases |> Map.add session (TestRunPhase.Running generation)
        LastGeneration = last }
    |> setStatus session generation RequestedRunStatus.Running

  /// Stamp each arriving result with the run that produced it: the session's
  /// running generation at arrival. A result with no run in flight carries no
  /// generation, so it can never satisfy a run's verdict.
  let stampResults (session: string option) (results: TestRunResult seq) (state: LiveTestState) : LiveTestState =
    let running =
      session
      |> Option.bind (fun s -> Map.tryFind s state.RunPhases)
      |> Option.bind (fun phase ->
        match phase with
        | TestRunPhase.Running g | TestRunPhase.RunningButEdited g -> Some g
        | TestRunPhase.Idle -> None)
    let stamped =
      results
      |> Seq.fold (fun (m: Map<TestId, RunGeneration>) r ->
        match running with
        | Some g -> m.Add(r.TestId, g)
        | None -> m.Remove r.TestId) state.ResultGenerations
    { state with ResultGenerations = stamped }

  /// A completion arrived for `session`: the run it was running is Completed.
  /// Call with the state BEFORE the phase returns to Idle.
  let completed (session: string) (state: LiveTestState) : LiveTestState =
    match Map.tryFind session state.RunPhases with
    | Some (TestRunPhase.Running g) | Some (TestRunPhase.RunningButEdited g) ->
      setStatus session g RequestedRunStatus.Completed state
    | _ -> state

  /// The verdict of run `generation` for `tests`: every test WITHOUT a Passed
  /// result produced by that very run. Fail-closed: a missing result, a result
  /// from another run, or any non-pass outcome all count as failing.
  let failingIn (generation: RunGeneration) (tests: TestId list) (state: LiveTestState) : TestId list =
    tests
    |> List.filter (fun id ->
      match Map.tryFind id state.ResultGenerations, Map.tryFind id state.LastResults with
      | Some g, Some { Result = TestResult.Passed _ } when g = generation -> false
      | _ -> true)

