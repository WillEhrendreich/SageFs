namespace SageFs.Simulation

open SageFs.Features.ReloadOutcome
open SageFs.Features.PatchConfirmation
open SageFs.Middleware.EntryProbes

/// Deterministic Simulation Testing for the one claim a save makes that the
/// save itself cannot check: "the new code is live".
///
/// A patch is reported Patched only after the new body has been seen running.
/// Until then it is pending, and when the bound passes it is either confirmed
/// or NeverEntered. Saves land, the app calls functions, newer saves replace
/// older ones, and the bound of each wait fires at some point among all of it.
/// The decision folded here is the REAL one (`PatchConfirmation.start`,
/// `.step`, `.settle`), and the host's bookkeeping is the REAL `ProbeRegistry`.
///
/// Same design rules as `FsiEmitSim` and `WatcherLifecycleSim`:
///   * Chaos is DATA: a `Scenario` is an ordered op list. Same ops, same trace.
///     The op order IS the scheduler: an `Enter` before a `Bound` means the
///     function ran in time, after it means the wait gave up first.
///   * Ground truth is tracked by an independent fold (`Truth`) that never reads
///     the decision under test: which probes were really entered, and which
///     were really replaced by a newer save.
///   * Twins reintroduce the bugs the invariants exist to catch.
module PatchConfirmationSim =

  /// Function indices 0..2 have a probe. Index 3 is a function that could not
  /// be given one (its stub could not be built), so it can never be observed.
  [<Literal>]
  let unprobeableDecl = 3

  [<RequireQualifiedAccess>]
  type Op =
    /// A save re-points these functions (indices into the pool). Each gets a
    /// new probe, and an older probe for the same function that never ran is
    /// superseded.
    | Save of decls: int list
    /// The app calls this function. The call goes through the newest stub.
    | Enter of decl: int
    /// The wait for this save (by order of arrival, modulo the saves so far)
    /// reaches its bound.
    | Bound of save: int

  type Scenario = { Seed: int; Ops: Op list }

  /// What the save claimed to watch, by the simulation's own records.
  type SaveRecord =
    { Index: int
      Decls: int list
      /// The probe each probed function was given by this save.
      Probes: Map<int, int64> }

  [<RequireQualifiedAccess>]
  type WatchState =
    | Waiting of PatchWatch
    | Done

  /// One watch's end, with the ground truth at the moment it ended.
  type Settlement =
    { Save: int
      Step: WatchStep
      EnteredAtSettle: Set<int64>
      SupersededAtSettle: Set<int64> }

  /// Ground truth, written only by the ops themselves.
  type Truth =
    { Entered: Set<int64>
      Superseded: Set<int64>
      /// The newest probe per function: where an `Enter` lands.
      Newest: Map<int, int64> }

  type SimState =
    { Saves: SaveRecord list
      Watches: Map<int, WatchState>
      Truth: Truth
      Settlements: Settlement list }

  type Trace =
    { Scenario: Scenario
      Final: SimState
      /// Every save's claim as it stood when made, for the invariants.
      Reducer: string }

  [<RequireQualifiedAccess>]
  type private Policy =
    /// The real behaviour.
    | Real
    /// TWIN: a save claims Patched the moment it lands, as every save did
    /// before probes existed.
    | ClaimsAtSave
    /// TWIN: the bound never fires, so a function that never runs leaves its
    /// save pending forever.
    | BoundNeverFires

  let private declName (decl: int) = sprintf "Sim.f%d" decl

  let private emptyTruth = { Entered = Set.empty; Superseded = Set.empty; Newest = Map.empty }

  let private initial : SimState = { Saves = []; Watches = Map.empty; Truth = emptyTruth; Settlements = [] }

  let private statusOf (truth: Truth) (probe: int64) : ProbeStatus =
    match Set.contains probe truth.Entered, Set.contains probe truth.Superseded with
    | true, _ -> ProbeStatus.Entered
    | false, true -> ProbeStatus.Superseded
    | false, false -> ProbeStatus.NotEntered

  let private settleNow (registry: ProbeRegistry) (state: SimState) (save: int) (watch: PatchWatch) : SimState =
    let step = PatchConfirmation.settle (registry.Read(PatchConfirmation.probesOf watch)) watch
    let settlement =
      { Save = save; Step = step; EnteredAtSettle = state.Truth.Entered; SupersededAtSettle = state.Truth.Superseded }
    { state with
        Watches = Map.add save WatchState.Done state.Watches
        Settlements = settlement :: state.Settlements }

  /// A wait ends early when every probe it waits on has a sighting (Entered or
  /// Superseded), which is what `ProbeRegistry.Await` does.
  let private settleReady (registry: ProbeRegistry) (state: SimState) : SimState =
    state.Watches
    |> Map.toList
    |> List.fold
      (fun acc (save, w) ->
        match w with
        | WatchState.Done -> acc
        | WatchState.Waiting watch ->
          let ids = PatchConfirmation.probesOf watch
          let reading = registry.Read ids
          let allSighted = reading.Sightings |> List.forall (fun s -> s.Status <> ProbeStatus.NotEntered)
          match not (List.isEmpty ids) && allSighted with
          | true -> settleNow registry acc save watch
          | false -> acc)
      state

  let private step (policy: Policy) (registry: ProbeRegistry) (state: SimState) (op: Op) : SimState =
    match op with
    | Op.Save decls ->
      let decls = decls |> List.distinct |> List.sort
      let index = List.length state.Saves
      let allocated =
        decls
        |> List.choose (fun d ->
          match d = unprobeableDecl with
          | true -> None
          | false -> Some(d, registry.Allocate(declName d)))
      // Commit supersedes older probes of the same function that never ran.
      for _, probe in allocated do
        registry.Commit probe
      let superseded =
        allocated
        |> List.fold
          (fun acc (d, probe) ->
            match Map.tryFind d state.Truth.Newest with
            | Some older when not (Set.contains older state.Truth.Entered) -> Set.add older acc
            | _ -> acc)
          state.Truth.Superseded
      let truth =
        { state.Truth with
            Superseded = superseded
            Newest = allocated |> List.fold (fun m (d, p) -> Map.add d p.Id m) state.Truth.Newest }
      let record = { Index = index; Decls = decls; Probes = allocated |> List.map (fun (d, p) -> d, p.Id) |> Map.ofList }
      let watched : WatchedDecl list =
        decls
        |> List.map (fun d ->
          { Declaration = declName d
            Probes =
              match Map.tryFind d record.Probes with
              | Some id -> [ id ]
              | None -> [] })
      let n = List.length decls
      let state' = { state with Saves = state.Saves @ [ record ]; Truth = truth }
      match policy with
      | Policy.ClaimsAtSave ->
        let settlement =
          { Save = index
            Step = WatchStep.Settled(ReloadOutcome.Patched(n, n))
            EnteredAtSettle = truth.Entered
            SupersededAtSettle = truth.Superseded }
        { state' with Watches = Map.add index WatchState.Done state'.Watches; Settlements = settlement :: state'.Settlements }
      | Policy.Real
      | Policy.BoundNeverFires ->
        match PatchConfirmation.start watched (ReloadOutcome.PatchPending(n, n, [])) with
        | Begun.Watching(_, watch) -> settleReady registry { state' with Watches = Map.add index (WatchState.Waiting watch) state'.Watches }
        | Begun.NothingToWatch _ -> { state' with Watches = Map.add index WatchState.Done state'.Watches }
    | Op.Enter decl ->
      match Map.tryFind decl state.Truth.Newest with
      | None -> state
      | Some probe ->
        registry.Enter probe
        let truth = { state.Truth with Entered = Set.add probe state.Truth.Entered }
        settleReady registry { state with Truth = truth }
    | Op.Bound save ->
      match policy, List.length state.Saves with
      | Policy.BoundNeverFires, _ -> state
      | _, 0 -> state
      | _, count ->
        let index = ((save % count) + count) % count
        match Map.tryFind index state.Watches with
        | Some(WatchState.Waiting watch) -> settleNow registry state index watch
        | Some WatchState.Done
        | None -> state

  let private finalBounds (state: SimState) : Op list =
    [ for record in state.Saves -> Op.Bound record.Index ]

  let private runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let registry = ProbeRegistry()
    let folded = scenario.Ops |> List.fold (step policy registry) initial
    // Fairness: every wait's bound fires eventually. The real system gets this
    // from a timer; the simulation gets it from appending the bounds.
    let final = finalBounds folded |> List.fold (step policy registry) folded
    { Scenario = scenario; Final = { final with Settlements = List.rev final.Settlements }; Reducer = name }

  /// The real decision over the real registry.
  let run (scenario: Scenario) : Trace = runWith "real (PatchConfirmation over ProbeRegistry)" Policy.Real scenario

  /// TWIN: claims Patched at save time.
  let runClaimsAtSave (scenario: Scenario) : Trace = runWith "twin-claims-at-save" Policy.ClaimsAtSave scenario

  /// TWIN: the bound never fires.
  let runBoundNeverFires (scenario: Scenario) : Trace = runWith "twin-bound-never-fires" Policy.BoundNeverFires scenario
