namespace SageFs.Simulation

open System
open System.Collections.Generic
open System.Reflection
open System.Runtime.CompilerServices
open SageFs.Features

/// Deterministic Simulation Testing for the timing-sensitive half of a click's guards.
///
/// A click puts guards on what a getter can reach, asks the getter's thread to stop at the deadline, and takes the guards
/// off when it is over. Three things can land in any order around that: the getter's thread ends (before the deadline,
/// just after the stop was asked for, or in the instant before the click gives up), the click gives up on a thread that
/// is still running, and hot reload re-points a method a click holds. Two clicks can also want the same helper.
/// A real run shows one interleaving. This folds the REAL pieces through every one of them, thousands a second:
///
///   * the REAL reachability walk and patch registry (`GuardPatcher.prepareIn`, `PatchRegistry`), over a recording
///     backend instead of Harmony, so the registry's rules run without patching code;
///   * the REAL cells and global stop count (`GuardCell`, `Guard.RequestStop`, `Guard.Retire`, `Guard.Check`);
///   * the REAL rule for who lets the guards go (`ClickLifecycle.step`), the same function the evaluator calls.
///
/// Ground truth is kept separately and never reads any of that: a click holds what it reached for as long as its thread
/// lives or it is still waiting, a stop is pending while it was asked for and its thread lives, a re-pointed method is
/// never held. Chaos is data: the op order IS the scheduler, a seed picks the ops. Same seed, same trace.
///
/// Twins put back the naive rules the invariants exist to catch (see `Policy`).
module GuardSim =

  /// Five real methods for getters to reach. Their bodies do nothing: the sim never runs them, it patches and unpatches
  /// them through the registry's backend.
  [<AbstractClass; Sealed>]
  type SimPool =
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member M0() : int = 0
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member M1() : int = 1
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member M2() : int = 2
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member M3() : int = 3
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member M4() : int = 4

  /// How many methods there are.
  [<Literal>]
  let methodCount = 5

  /// How many clicks a scenario has room for.
  [<Literal>]
  let clickCount = 3

  let private methods : MethodBase[] =
    [| for i in 0 .. methodCount - 1 -> typeof<SimPool>.GetMethod(sprintf "M%d" i) :> MethodBase |]

  /// Who calls whom: 0 calls 1 and 2, both of which call 3. 4 is on its own.
  let private calls : Map<int, int list> =
    Map.ofList [ 0, [ 1; 2 ]; 1, [ 3 ]; 2, [ 3 ]; 3, []; 4, [] ]

  let private indexOf (m: MethodBase) : int = methods |> Array.findIndex (fun x -> x.Name = m.Name)

  [<RequireQualifiedAccess>]
  type Op =
    /// A click starts on this root: guards go on everything it reaches, its thread starts.
    | Start of click: int * root: int
    /// The click's deadline passes: its thread is asked to stop.
    | Deadline of click: int
    /// The click's thread leaves the getter, by a value, a throw, a guard or an interrupt.
    | ThreadEnds of click: int
    /// The click looks, sees its thread ended, and lets the guards go.
    | Finish of click: int
    /// The click's grace runs out and it stops waiting, whether or not the thread is still there.
    | GiveUp of click: int
    /// Hot reload re-points this method.
    | Reload of method: int

  type Scenario = { Seed: int; Ops: Op list }

  /// What a check on a click's thread did.
  [<RequireQualifiedAccess>]
  type Check =
    | Threw
    | Quiet

  /// What the real pieces show after a step.
  type Observed =
    { /// The methods the backend currently holds a patch on.
      Patched: Set<int>
      /// The global stop count, relative to where the run began.
      Pending: int
      /// What a check bound to each started click's cell does.
      StopChecks: Map<int, Check>
      /// What a check on a thread bound to no cell does.
      UnboundCheck: Check }

  /// What ground truth says it should show.
  type Expected =
    { Patched: Set<int>
      Pending: int
      StopChecks: Map<int, Check>
      /// What the clicks that gave up and whose threads still run must still have guarded.
      AbandonedStillRunning: Set<int>
      Detoured: Set<int> }

  type StepRecord =
    { Index: int
      Op: Op
      Observed: Observed
      Expected: Expected }

  type Final = { Patched: Set<int>; Pending: int }

  type Trace =
    { Scenario: Scenario
      Steps: StepRecord list
      Final: Final
      Reducer: string }

  // ---- ground truth ----------------------------------------------------------------------------------------------

  [<RequireQualifiedAccess>]
  type private Thread =
    | Alive
    | Gone

  [<RequireQualifiedAccess>]
  type private Waiting =
    | StillWaiting
    | MovedOn

  [<RequireQualifiedAccess>]
  type private Stop =
    | NotAsked
    | Asked

  [<RequireQualifiedAccess>]
  type private Stance =
    | HasNotGivenUp
    | GaveUpOnALiveThread
    | GaveUpOnAGoneThread

  [<RequireQualifiedAccess>]
  type private Release =
    | NotYet
    | Done

  /// One click: the real objects it holds, and what ground truth says of it.
  type private Click =
    { Lease: GuardLease
      Cell: GuardCell
      Phase: ClickPhase
      Released: Release
      // ground truth below, written only by the ops themselves
      Held: Set<int>
      Thread: Thread
      Waiting: Waiting
      Stop: Stop
      Stance: Stance }

  /// The backend the registry patches through: it records, and the sim reads the record.
  type private Recording() =
    let patched = HashSet<int>()
    member _.Patched : Set<int> = Set.ofSeq patched
    member _.Backend : PatchBackend =
      { Patch = fun m -> (patched.Add(indexOf m) |> ignore; PatchAttempt.Patched)
        Unpatch = fun m -> patched.Remove(indexOf m) |> ignore }
    member _.ForceUnpatch(m: int) : unit = patched.Remove m |> ignore

  type private Env =
    { Registry: PatchRegistry
      Ledger: Ledger
      Recording: Recording
      World: WalkWorld
      BasePending: int }

  [<RequireQualifiedAccess>]
  type private Policy =
    /// The real behaviour.
    | Real
    /// TWIN: the click lets its guards go the moment it gives up, as if the thread were gone. The thread may still be
    /// running guarded code, and now runs it unchecked.
    | ReleasesAtGiveUp
    /// TWIN: a thread that ends does not retire its cell, so a stop stays counted and every later check pays for it.
    | NeverRetires
    /// TWIN: letting go unpatches what the lease reached without asking who else holds it.
    | UnpatchesWithoutCounting
    /// TWIN: a reload marks the method re-pointed but leaves the guard on it.
    | ReloadLeavesThePatch

  /// Everything a click reaches from `root` by the calls above, not going into or past a re-pointed method.
  let private reach (detoured: Set<int>) (root: int) : Set<int> =
    let rec go (seen: Set<int>) (frontier: int list) : Set<int> =
      match frontier with
      | [] -> seen
      | next :: rest ->
        match Set.contains next seen || Set.contains next detoured with
        | true -> go seen rest
        | false -> go (Set.add next seen) (Map.find next calls @ rest)
    go Set.empty [ root ]

  let private world (ledger: Ledger) : WalkWorld =
    { Callees = fun m -> CalleeRead.Callees (Map.find (indexOf m) calls |> List.map (fun i -> methods.[i]))
      Implementations = fun _ -> []
      Created = fun _ -> []
      Owns = fun assembly -> obj.ReferenceEquals(assembly, typeof<SimPool>.Assembly)
      Detour = ledger.Resolve }

  let private checkOn (cell: GuardCell) : Check =
    Guard.Bind cell
    try
      try
        Guard.Check()
        Check.Quiet
      with :? GuardAbortedException -> Check.Threw
    finally
      Guard.Unbind()

  let private stopPending (click: Click) : bool =
    match click.Stop, click.Thread with
    | Stop.Asked, Thread.Alive -> true
    | Stop.Asked, Thread.Gone
    | Stop.NotAsked, _ -> false

  /// A click holds what it reached for as long as its thread lives or it is still waiting.
  let private holds (click: Click) : bool =
    match click.Thread, click.Waiting with
    | Thread.Alive, _
    | Thread.Gone, Waiting.StillWaiting -> true
    | Thread.Gone, Waiting.MovedOn -> false

  let private expected (clicks: Map<int, Click>) (detoured: Set<int>) : Expected =
    let list = clicks |> Map.toList |> List.map snd
    { Patched = list |> List.filter holds |> List.map _.Held |> Set.unionMany
      Pending = list |> List.filter stopPending |> List.length
      StopChecks =
        clicks |> Map.map (fun _ c -> match stopPending c with | true -> Check.Threw | false -> Check.Quiet)
      AbandonedStillRunning =
        list
        |> List.filter (fun c -> c.Stance = Stance.GaveUpOnALiveThread && c.Thread = Thread.Alive)
        |> List.map _.Held
        |> Set.unionMany
      Detoured = detoured }

  let private observed (env: Env) (clicks: Map<int, Click>) : Observed =
    { Patched = env.Recording.Patched
      Pending = Guard.Pending - env.BasePending
      StopChecks = clicks |> Map.map (fun _ c -> checkOn c.Cell)
      UnboundCheck =
        (try
          Guard.Check()
          Check.Quiet
         with :? GuardAbortedException -> Check.Threw) }

  type private SimState = { Clicks: Map<int, Click>; Detoured: Set<int> }

  /// A click lets its guards go, once.
  let private release (policy: Policy) (env: Env) (click: Click) : Click =
    match click.Released with
    | Release.Done -> click
    | Release.NotYet ->
      click.Lease.Release()
      match policy with
      | Policy.UnpatchesWithoutCounting -> for m in click.Held do env.Recording.ForceUnpatch m
      | Policy.Real
      | Policy.ReleasesAtGiveUp
      | Policy.NeverRetires
      | Policy.ReloadLeavesThePatch -> ()
      { click with Released = Release.Done }

  let private modulo (n: int) (size: int) : int = ((n % size) + size) % size

  let private step (policy: Policy) (env: Env) (state: SimState) (op: Op) : SimState =
    let update (c: int) (f: Click -> Click) : SimState =
      match Map.tryFind c state.Clicks with
      | Some click -> { state with Clicks = Map.add c (f click) state.Clicks }
      | None -> state
    match op with
    | Op.Start (c, r) ->
      let c = modulo c clickCount
      match Map.containsKey c state.Clicks with
      | true -> state
      | false ->
        let root = modulo r methodCount
        let lease = GuardPatcher.prepareIn env.Registry env.World WalkBudget.product methods.[root]
        let click =
          { Lease = lease
            Cell = GuardCell()
            Phase = ClickPhase.Running
            Released = Release.NotYet
            Held = reach state.Detoured root
            Thread = Thread.Alive
            Waiting = Waiting.StillWaiting
            Stop = Stop.NotAsked
            Stance = Stance.HasNotGivenUp }
        { state with Clicks = Map.add c click state.Clicks }
    | Op.Deadline c ->
      update (modulo c clickCount) (fun click ->
        match click.Thread, click.Stop with
        | Thread.Alive, Stop.NotAsked ->
          Guard.RequestStop click.Cell
          { click with Stop = Stop.Asked }
        | Thread.Alive, Stop.Asked
        | Thread.Gone, _ -> click)
    | Op.ThreadEnds c ->
      update (modulo c clickCount) (fun click ->
        match click.Thread with
        | Thread.Gone -> click
        | Thread.Alive ->
          (match policy with
           | Policy.NeverRetires -> ()
           | Policy.Real
           | Policy.ReleasesAtGiveUp
           | Policy.UnpatchesWithoutCounting
           | Policy.ReloadLeavesThePatch -> Guard.Retire click.Cell)
          let phase, duty = ClickLifecycle.step click.Phase ClickEvent.ThreadEnded
          let ended = { click with Phase = phase; Thread = Thread.Gone }
          match duty with
          | GuardDuty.ReleaseNow -> release policy env ended
          | GuardDuty.LeaveToTheClick
          | GuardDuty.LeaveToTheThread
          | GuardDuty.NothingToDo -> ended)
    | Op.Finish c ->
      update (modulo c clickCount) (fun click ->
        match click.Thread, click.Waiting with
        | Thread.Gone, Waiting.StillWaiting -> release policy env { click with Waiting = Waiting.MovedOn }
        | Thread.Gone, Waiting.MovedOn
        | Thread.Alive, _ -> click)
    | Op.GiveUp c ->
      update (modulo c clickCount) (fun click ->
        match click.Waiting, click.Stop with
        | Waiting.StillWaiting, Stop.Asked ->
          let phase, duty = ClickLifecycle.step click.Phase ClickEvent.GaveUp
          let stance =
            match click.Thread with
            | Thread.Alive -> Stance.GaveUpOnALiveThread
            | Thread.Gone -> Stance.GaveUpOnAGoneThread
          let moved = { click with Phase = phase; Waiting = Waiting.MovedOn; Stance = stance }
          match duty, policy with
          | GuardDuty.ReleaseNow, _ -> release policy env moved
          | GuardDuty.LeaveToTheThread, Policy.ReleasesAtGiveUp -> release policy env moved
          | GuardDuty.LeaveToTheThread, _
          | GuardDuty.LeaveToTheClick, _
          | GuardDuty.NothingToDo, _ -> moved
        | Waiting.StillWaiting, Stop.NotAsked
        | Waiting.MovedOn, _ -> click)
    | Op.Reload m ->
      let m = modulo m methodCount
      (match policy with
       | Policy.ReloadLeavesThePatch -> env.Ledger.MarkDetoured methods.[m]
       | Policy.Real
       | Policy.ReleasesAtGiveUp
       | Policy.NeverRetires
       | Policy.UnpatchesWithoutCounting -> env.Registry.ReleaseBeforeDetour methods.[m])
      { Clicks = state.Clicks |> Map.map (fun _ click -> { click with Held = Set.remove m click.Held })
        Detoured = Set.add m state.Detoured }

  /// Every click's thread eventually ends and every click eventually looks. The real system gets this from its own
  /// threads; the sim gets it by appending the ops.
  let private fairness : Op list =
    [ for c in 0 .. clickCount - 1 -> Op.ThreadEnds c ] @ [ for c in 0 .. clickCount - 1 -> Op.Finish c ]

  let private runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let recording = Recording()
    let ledger = Ledger()
    let env =
      { Registry = PatchRegistry(recording.Backend, ledger)
        Ledger = ledger
        Recording = recording
        World = world ledger
        BasePending = Guard.Pending }
    let all = scenario.Ops @ fairness
    let mutable state = { Clicks = Map.empty; Detoured = Set.empty }
    let steps = List<StepRecord>()
    for index, op in List.indexed all do
      state <- step policy env state op
      steps.Add
        { Index = index
          Op = op
          Observed = observed env state.Clicks
          Expected = expected state.Clicks state.Detoured }
    { Scenario = scenario
      Steps = List.ofSeq steps
      Final = { Patched = recording.Patched; Pending = Guard.Pending - env.BasePending }
      Reducer = name }

  /// The real pieces.
  let run (scenario: Scenario) : Trace = runWith "real (registry, cells, lifecycle)" Policy.Real scenario

  /// TWIN: the click lets its guards go at give-up.
  let runReleaseAtGiveUp (scenario: Scenario) : Trace = runWith "twin-release-at-give-up" Policy.ReleasesAtGiveUp scenario

  /// TWIN: a thread that ends does not retire its cell.
  let runNeverRetires (scenario: Scenario) : Trace = runWith "twin-never-retires" Policy.NeverRetires scenario

  /// TWIN: letting go unpatches without asking who else holds it.
  let runUnpatchesShared (scenario: Scenario) : Trace = runWith "twin-unpatches-shared" Policy.UnpatchesWithoutCounting scenario

  /// TWIN: a reload leaves the guard on the method it re-points.
  let runReloadKeepsPatch (scenario: Scenario) : Trace = runWith "twin-reload-keeps-patch" Policy.ReloadLeavesThePatch scenario
