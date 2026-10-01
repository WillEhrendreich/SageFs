namespace SageFs.Simulation

open System
open System.Collections.Generic
open SageFs.Features
open SageFs.Features.MetadataDelta
open SageFs.Features.PatchConfirmation
open SageFs.Features.ReloadOutcome
open SageFs.Middleware.EntryProbes

/// Deterministic Simulation Testing for a run_app app patched by metadata delta.
///
/// What goes wrong with this route is order, not arithmetic. A delta is computed against the module a process
/// loaded and every delta after it against the one before; the runtime cannot take one back; a process can be
/// replaced under a chain; a second save can arrive while one is being applied; someone can build between two
/// saves; and "the patch is live" is only worth saying once a call has entered the NEW body. A real run shows one
/// interleaving. This folds the REAL decisions through every one of them, thousands a second:
///
///   * the REAL rule for what a save does (`DeltaSession.start`, `precheck`, `decide`, `settle`, `landingOf`), the same
///     functions `RunAppDelta.Save` calls;
///   * the REAL confirmation (`ProbeRegistry`, `PatchConfirmation.start` and `settle`), the same ones the worker's
///     announcer folds.
///
/// What it fakes is the world around them: a program that is one method whose body is a version number, a build that
/// produces the version the source has, a chain that remembers which build the process runs, a runtime that takes a delta
/// or refuses it. Ground truth is kept separately and never reads what the route believes: the process runs what the last
/// delta that landed made it run, a delta computed against anything else corrupts it, a delta the runtime refused leaves
/// it in a state nobody knows, and a body has run only when a call entered it after it landed. Chaos is data: the op
/// order IS the scheduler, a seed picks the ops. Same seed, same trace.
///
/// Twins put back the naive rules the invariants exist to catch (see `Policy`).
module DeltaRouteSim =

  /// What can happen, in any order.
  [<RequireQualifiedAccess>]
  type Op =
    /// The source file changes: the next build makes the next version.
    | Edit
    /// A save is picked up: the project is built, a delta is prepared against the chain and the route decides.
    | Prepare of save: int
    /// A prepared delta is handed to the runtime.
    | Apply of save: int
    /// The runtime will refuse the next delta it is given.
    | ArmFault
    /// The worker is replaced by something that was not a restart the route asked for. `told` says whether the route
    /// learns of it (a new process starts a new route) or keeps what it had.
    | ReplaceProcess of told: bool
    /// The daemon brings the app back after a restart verdict.
    | Respawn
    | AttachDebugger
    | DetachDebugger
    /// A request enters the patched method, on whichever body runs now.
    | RequestStarts of request: int
    /// A request leaves it.
    | RequestEnds of request: int
    /// The confirmation wait for a landed save runs out.
    | BoundElapses of save: int

  /// `Rude` is the set of source versions the emitter cannot carry.
  type Scenario = { Seed: int; Rude: Set<int>; Ops: Op list }

  /// What the route told the user at a step.
  [<RequireQualifiedAccess>]
  type Said =
    | Nothing
    | Unchanged
    /// The runtime took the delta, and the process now runs this version.
    | Patched of save: int * target: int
    | Restarted of why: string
    /// The new body of this save, which took the process to `target`, was seen running.
    | Confirmed of save: int * target: int
    /// The bound passed and it was not.
    | NotConfirmed of save: int * target: int

  /// One delta handed to the runtime and taken, as ground truth saw it.
  type Application =
    { Epoch: int
      Save: int
      /// The build the delta was computed against.
      Basis: int
      /// What the process ran at that moment.
      ProcessRan: int
      Target: int
      /// The module the chain the delta came from was started for, and the one running when it was applied.
      PreparedFor: int
      RanIn: int
      /// Whether a delta had already failed in this process.
      ProcessWasBroken: bool }

  /// What ground truth says is so after a step.
  type Truth =
    { Source: int
      Disk: int
      Epoch: int
      Module: int
      Running: int
      Applied: int
      Broken: bool
      FaultArmed: bool
      Debugger: bool
      PendingRestart: bool
      /// Versions a request has entered since they landed in this process.
      Ran: Set<int> }

  type Observed =
    { Standing: Standing
      /// The generation the route's chain is at.
      /// The module the route's chain was started for, as the route remembers it.
      Tracks: int
      ChainGeneration: int }

  type StepRecord =
    { Index: int
      Op: Op
      Said: Said
      /// A delta the runtime refused at this step.
      RuntimeRefused: bool
      Truth: Truth
      Observed: Observed }

  type Trace =
    { Scenario: Scenario
      Steps: StepRecord list
      Applications: Application list
      /// The version each landed save took the process to.
      Landed: Map<int, int>
      Reducer: string }

  [<RequireQualifiedAccess>]
  type private Policy =
    /// The real behaviour.
    | Real
    /// TWIN: the decision is given the module the chain remembers, not the one that is running.
    | IgnoresTheRunningModule
    /// TWIN: a delta the runtime refused leaves the route tracking, so the next save is applied to a process that
    /// may hold part of one.
    | ForgetsAFailure
    /// TWIN: a delta is checked against the chain only when it is prepared, not again when it is applied.
    | ChecksOnlyWhenPrepared
    /// TWIN: the delta is computed against the last build, not the one the process runs.
    | DiffsAgainstTheLastBuild
    /// TWIN: the chain advances before the runtime has answered, so a refusal leaves it ahead of the process.
    | AdvancesBeforeTheAnswer
    /// TWIN: a request finishing after the apply is taken as the new body running.
    | ConfirmsOnAnyCompletion

  /// What the route holds: the real standing, and a chain that is a build number and a generation.
  type private Route =
    { Standing: Standing
      /// The module the standing tracks, as the route remembers it.
      Remembered: int
      Previous: int
      Generation: int }

  type private Candidate = { From: int; Basis: int; Target: int; PreparedFor: int }

  type private Slot =
    { Decision: SaveDecision
      Candidate: Candidate }

  type private Watch =
    { Probe: EntryProbe
      Target: int
      Watching: PatchWatch
      Settled: bool }

  type private World =
    { Truth: Truth
      Route: Route
      Slots: Map<int, Slot>
      Watches: Map<int, Watch>
      /// Per request: the body it entered on, and whether it has finished.
      Requests: Map<int, int * bool>
      Registry: ProbeRegistry
      Landed: Map<int, int>
      Applications: Application list }

  let private rudeCause = RudeCause.FieldsChanged "Handlers+clo@25"

  let private gapsOf (truth: Truth) : CapabilityGap list =
    match truth.Debugger with
    | true -> [ CapabilityGap.DebuggerAttached ]
    | false -> []

  /// A module is a number, and the runtime's Mvid is a Guid made from it.
  let mvid (n: int) : Guid = Guid(n, 0s, 0s, Array.zeroCreate<byte> 8)

  let private startRoute (truth: Truth) : Route =
    { Standing = DeltaSession.start (mvid truth.Module) (gapsOf truth)
      Remembered = truth.Module
      Previous = truth.Running
      Generation = 0 }

  let private freshProcess (truth: Truth) : Truth =
    { truth with
        Epoch = truth.Epoch + 1
        Module = truth.Module + 1
        // The daemon rebuilds, then starts the process from the build.
        Disk = truth.Source
        Running = truth.Source
        Applied = 0
        Broken = false
        FaultArmed = false
        PendingRestart = false
        Ran = Set.empty }

  /// A world of its own: the probe registry is mutable, so a run never shares one.
  let private initial () : World =
    let truth =
      { Source = 0
        Disk = 0
        Epoch = 0
        Module = 1
        Running = 0
        Applied = 0
        Broken = false
        FaultArmed = false
        Debugger = false
        PendingRestart = false
        Ran = Set.empty }
    { Truth = truth
      Route = startRoute truth
      Slots = Map.empty
      Watches = Map.empty
      Requests = Map.empty
      Registry = ProbeRegistry()
      Landed = Map.empty
      Applications = [] }

  let private modulo (n: int) (size: int) : int = ((n % size) + size) % size

  /// Saves, requests and the like are numbered in a small range so ops land on one another.
  [<Literal>]
  let slotCount = 3

  let private restarted (why: string) (world: World) : World * Said =
    { world with Truth = { world.Truth with PendingRestart = true } }, Said.Restarted why

  let private whyOf (refusal: Refusal) : string = DeltaSession.describeRefusal refusal

  let private step (policy: Policy) (scenario: Scenario) (world: World) (op: Op) : World * Said * bool =
    let truth = world.Truth
    let route = world.Route
    let running = match policy with | Policy.IgnoresTheRunningModule -> mvid route.Remembered | _ -> mvid truth.Module
    let gaps = gapsOf truth
    match op with
    | Op.Edit -> { world with Truth = { truth with Source = truth.Source + 1 } }, Said.Nothing, false
    | Op.Prepare save ->
      let save = modulo save slotCount
      // The save builds the project: the disk has what the source says.
      let target = truth.Source
      let basis = match policy with | Policy.DiffsAgainstTheLastBuild -> truth.Disk | _ -> route.Previous
      let preparation =
        match target = basis, Set.contains target scenario.Rude with
        | true, _ -> Preparation.NothingChanged
        | false, true -> Preparation.Refused [ rudeCause ]
        | false, false -> Preparation.Ready route.Generation
      let decision = DeltaSession.decide route.Standing running gaps preparation
      let built = { world with Truth = { truth with Disk = target } }
      let candidate = { From = route.Generation; Basis = basis; Target = target; PreparedFor = route.Remembered }
      let withSlot = { built with Slots = Map.add save { Decision = decision; Candidate = candidate } built.Slots }
      match decision with
      | SaveDecision.Unchanged -> withSlot, Said.Unchanged, false
      | SaveDecision.Apply -> withSlot, Said.Nothing, false
      | SaveDecision.Restart(first, _) ->
        let after, said = restarted (whyOf first) withSlot
        after, said, false
    | Op.Apply save ->
      let save = modulo save slotCount
      match Map.tryFind save world.Slots with
      | Some { Decision = SaveDecision.Apply; Candidate = candidate } ->
        let consumed = { world with Slots = Map.remove save world.Slots }
        // The delta is looked at again now, against the chain as it is NOW: a save prepared while another was being
        // applied was prepared from a generation that is not the process's any more.
        let again =
          match policy with
          | Policy.ChecksOnlyWhenPrepared -> SaveDecision.Apply
          | _ -> DeltaSession.decide route.Standing running gaps (Preparation.Ready candidate.From)
        match again with
        | SaveDecision.Unchanged -> consumed, Said.Nothing, false
        | SaveDecision.Restart(first, _) ->
          let after, said = restarted (whyOf first) consumed
          after, said, false
        | SaveDecision.Apply ->
          let refused = truth.FaultArmed
          let outcome = match refused with | true -> ApplyOutcome.Rejected "injected fault" | false -> ApplyOutcome.Applied
          let landing = DeltaSession.landingOf outcome
          let standing =
            match policy, landing with
            | Policy.ForgetsAFailure, Landing.DidNotLand _ -> route.Standing
            | _ -> DeltaSession.settle route.Standing landing
          let landed = (match landing with | Landing.DidNotLand _ -> false | _ -> true)
          let advanced = landed || policy = Policy.AdvancesBeforeTheAnswer
          let route' =
            { route with
                Standing = standing
                Previous = (match advanced with | true -> candidate.Target | false -> route.Previous)
                Generation = (match advanced with | true -> route.Generation + 1 | false -> route.Generation) }
          let truth' =
            match landed with
            | true ->
              { truth with
                  Running = candidate.Target
                  Applied = truth.Applied + 1
                  FaultArmed = false }
            | false -> { truth with Broken = true; FaultArmed = false }
          let applications =
            match landed with
            | true ->
              { Epoch = truth.Epoch
                Save = save
                Basis = candidate.Basis
                ProcessRan = truth.Running
                Target = candidate.Target
                PreparedFor = candidate.PreparedFor
                RanIn = truth.Module
                ProcessWasBroken = truth.Broken }
              :: world.Applications
            | false -> world.Applications
          let withTruth = { consumed with Truth = truth'; Route = route'; Applications = applications }
          match landed with
          | false ->
            let after, said = restarted "the runtime did not take the delta" withTruth
            after, said, true
          | true ->
            // This save's body is the one that runs now: it gets a probe, which an earlier one of the method never entered
            // is superseded by.
            let probe = world.Registry.Allocate "m"
            world.Registry.Commit probe
            let outcome' : SageFs.Features.ReloadOutcome.ReloadOutcome =
              SageFs.Features.ReloadOutcome.ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.Pending(1, 1))
            let begun = PatchConfirmation.start [ { Declaration = "m"; Probes = [ probe.Id ] } ] outcome'
            let watches =
              match begun with
              | Begun.Watching(_, watching) ->
                Map.add save { Probe = probe; Target = candidate.Target; Watching = watching; Settled = false } withTruth.Watches
              | Begun.NothingToWatch _ -> withTruth.Watches
            { withTruth with Watches = watches; Landed = Map.add save candidate.Target withTruth.Landed },
            Said.Patched(save, candidate.Target),
            false
      | Some _
      | None -> world, Said.Nothing, false
    | Op.ArmFault -> { world with Truth = { truth with FaultArmed = true } }, Said.Nothing, false
    | Op.ReplaceProcess told ->
      let fresh = freshProcess truth
      let replaced = { world with Truth = fresh; Slots = Map.empty; Watches = Map.empty; Requests = Map.empty; Registry = ProbeRegistry() }
      match told with
      | true -> { replaced with Route = startRoute fresh }, Said.Nothing, false
      | false -> replaced, Said.Nothing, false
    | Op.Respawn ->
      match truth.PendingRestart with
      | false -> world, Said.Nothing, false
      | true ->
        let fresh = freshProcess truth
        { world with Truth = fresh; Route = startRoute fresh; Slots = Map.empty; Watches = Map.empty; Requests = Map.empty; Registry = ProbeRegistry() },
        Said.Nothing,
        false
    | Op.AttachDebugger -> { world with Truth = { truth with Debugger = true } }, Said.Nothing, false
    | Op.DetachDebugger -> { world with Truth = { truth with Debugger = false } }, Said.Nothing, false
    | Op.RequestStarts request ->
      let request = modulo request slotCount
      match Map.containsKey request world.Requests with
      | true -> world, Said.Nothing, false
      | false ->
        // The first instruction of a patched body records its probe.
        let entered =
          world.Watches
          |> Map.toList
          |> List.filter (fun (_, w) -> w.Target = truth.Running)
        for _, w in entered do
          world.Registry.Enter w.Probe.Id
        let ran =
          match entered with
          | [] -> truth.Ran
          | _ -> Set.add truth.Running truth.Ran
        { world with Requests = Map.add request (truth.Running, false) world.Requests; Truth = { truth with Ran = ran } }, Said.Nothing, false
    | Op.RequestEnds request ->
      let request = modulo request slotCount
      match Map.tryFind request world.Requests with
      | Some (body, false) ->
        match policy with
        | Policy.ConfirmsOnAnyCompletion ->
          // A call returning proves nothing about which body it ran. This twin says it does.
          for _, w in Map.toList world.Watches do
            world.Registry.Enter w.Probe.Id
        | _ -> ()
        { world with Requests = Map.add request (body, true) world.Requests }, Said.Nothing, false
      | Some _
      | None -> world, Said.Nothing, false
    | Op.BoundElapses save ->
      let save = modulo save slotCount
      match Map.tryFind save world.Watches with
      | Some watch when not watch.Settled ->
        let reading = world.Registry.Read [ watch.Probe.Id ]
        let said =
          match PatchConfirmation.settle reading watch.Watching with
          | WatchStep.Settled(ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.Patched _)) -> Said.Confirmed(save, watch.Target)
          | WatchStep.Settled(ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.NeverEntered _)) -> Said.NotConfirmed(save, watch.Target)
          | WatchStep.Settled _
          | WatchStep.StillWaiting _
          | WatchStep.Abandoned -> Said.Nothing
        { world with Watches = Map.add save { watch with Settled = true } world.Watches }, said, false
      | Some _
      | None -> world, Said.Nothing, false

  let private fold (policy: Policy) (reducer: string) (scenario: Scenario) : Trace =
    let mutable world = initial ()
    let steps = List<StepRecord>()
    scenario.Ops
    |> List.iteri (fun index op ->
      let next, said, refused = step policy scenario world op
      world <- next
      steps.Add
        { Index = index
          Op = op
          Said = said
          RuntimeRefused = refused
          Truth = world.Truth
          Observed = { Standing = world.Route.Standing; Tracks = world.Route.Remembered; ChainGeneration = world.Route.Generation } })
    { Scenario = scenario
      Steps = List.ofSeq steps
      Applications = List.rev world.Applications
      Landed = world.Landed
      Reducer = reducer }

  /// The real decisions, folded through the scenario.
  let run (scenario: Scenario) : Trace = fold Policy.Real "real" scenario

  let runIgnoresTheRunningModule (scenario: Scenario) : Trace = fold Policy.IgnoresTheRunningModule "ignores-the-running-module" scenario
  let runForgetsAFailure (scenario: Scenario) : Trace = fold Policy.ForgetsAFailure "forgets-a-failure" scenario
  let runChecksOnlyWhenPrepared (scenario: Scenario) : Trace = fold Policy.ChecksOnlyWhenPrepared "checks-only-when-prepared" scenario
  let runDiffsAgainstTheLastBuild (scenario: Scenario) : Trace = fold Policy.DiffsAgainstTheLastBuild "diffs-against-the-last-build" scenario
  let runAdvancesBeforeTheAnswer (scenario: Scenario) : Trace = fold Policy.AdvancesBeforeTheAnswer "advances-before-the-answer" scenario
  let runConfirmsOnAnyCompletion (scenario: Scenario) : Trace = fold Policy.ConfirmsOnAnyCompletion "confirms-on-any-completion" scenario
