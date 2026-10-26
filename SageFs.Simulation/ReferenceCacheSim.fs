namespace SageFs.Simulation

open System
open SageFs
open SageFs.Features

/// Deterministic Simulation Testing for the memory `list_references` leaves behind.
///
/// A worker has a baseline of resident bytes. Each project a question checks adds the bytes the compiler caches for it,
/// and a warm project costs a tenth of a cold one. The decision folded is the REAL one: `ReferenceCacheRetention.CacheHolder`
/// over a simulated clock, a simulated heap and a simulated pressure, with the real `ReferenceGather.run` asking it
/// before every project check. The truth is a set of rules the heap must obey after every step, and none of them reads
/// how the holder reached its answer.
///
/// Same design rules as `ReferenceScopeSim`:
///   * Chaos is DATA: a `Scenario` is a policy and a list of steps. Same scenario, same trace.
///   * Twins reintroduce the bug the invariants exist to catch. The one that matters is the holder that never lets go,
///     which is what shipped: a cache bounded in projects and not in bytes.
module ReferenceCacheSim =

  let megabyte : int64 = 1048576L

  type ProjectCost =
    { Name: string
      /// Simulated seconds a cold check takes. A warm one takes a tenth, at least one second.
      ColdSeconds: int
      /// Bytes the compiler holds for the project once it is checked.
      CachedBytes: int64 }

  [<RequireQualifiedAccess>]
  type Step =
    /// A `list_references` question over these projects, in this order, under the scenario's budget.
    | Ask of ProjectCost list
    /// Nobody asks for this many seconds. The timer sweeps every `SweepEverySeconds`.
    | Idle of seconds: int
    /// The machine's memory pressure becomes this.
    | Squeeze of MemoryPressure

  type Scenario =
    { Seed: int
      BaselineBytes: int64
      CeilingBytes: int64
      RetentionSeconds: int
      SweepEverySeconds: int
      BudgetSeconds: int
      Steps: Step list }

  [<RequireQualifiedAccess>]
  type Moment =
    | AfterAsk
    | AfterSweep

  /// The heap right after one moment.
  type Snapshot =
    { Moment: Moment
      Now: TimeSpan
      CachedBytes: int64
      ResidentBytes: int64
      Pressure: MemoryPressure
      /// Seconds since the last ask finished.
      IdleSeconds: float
      /// Seconds the ask took by the simulated clock, for an ask; zero for a sweep.
      AskSeconds: float }

  type Trace =
    { Scenario: Scenario
      Snapshots: Snapshot list
      Releases: ReleaseReason list
      Reducer: string }

  [<RequireQualifiedAccess>]
  type Policy =
    /// The real behaviour.
    | Real
    /// TWIN: the holder never lets go of the cache, whatever the heap, the machine or the clock say.
    | NeverReleases
    /// TWIN: only the ceiling is ignored.
    | IgnoresCeiling
    /// TWIN: only idleness is ignored.
    | IgnoresIdle
    /// TWIN: only the machine's pressure is ignored.
    | IgnoresPressure

  let seconds (s: float) : TimeSpan = TimeSpan.FromSeconds s

  let policyOf (policy: Policy) (scenario: Scenario) : CachePolicy =
    let ceiling = scenario.CeilingBytes
    let retention = seconds (float scenario.RetentionSeconds)
    match policy with
    | Policy.Real
    | Policy.IgnoresPressure -> { CeilingBytes = ceiling; Retention = retention }
    | Policy.NeverReleases -> { CeilingBytes = Int64.MaxValue; Retention = TimeSpan.MaxValue }
    | Policy.IgnoresCeiling -> { CeilingBytes = Int64.MaxValue; Retention = retention }
    | Policy.IgnoresIdle -> { CeilingBytes = ceiling; Retention = TimeSpan.MaxValue }

  /// The simulated process: what the compiler has cached, and the clock.
  type World =
    { mutable Now: TimeSpan
      mutable Pressure: MemoryPressure
      mutable Cached: Map<string, int64>
      mutable Generation: int }

  let cachedTotal (world: World) : int64 = world.Cached |> Map.fold (fun total _ bytes -> total + bytes) 0L

  let run (policy: Policy) (scenario: Scenario) : Trace =
    let world : World = { Now = TimeSpan.Zero; Pressure = MemoryPressure.Normal; Cached = Map.empty; Generation = 0 }
    let releases = ResizeArray<ReleaseReason>()
    let snapshots = ResizeArray<Snapshot>()
    let lastAskEnded = ref TimeSpan.Zero
    let env : CacheEnv<int> =
      { Create =
          fun () ->
            world.Generation <- world.Generation + 1
            world.Generation
        Drop = fun _ -> world.Cached <- Map.empty
        ResidentBytes = fun () -> scenario.BaselineBytes + cachedTotal world
        Pressure =
          fun () ->
            match policy with
            | Policy.IgnoresPressure
            | Policy.NeverReleases -> MemoryPressure.Normal
            | Policy.Real
            | Policy.IgnoresCeiling
            | Policy.IgnoresIdle -> world.Pressure
        Now = fun () -> world.Now
        Policy = policyOf policy scenario
        Released = fun reason -> releases.Add reason }
    let holder = CacheHolder<int>(env)
    let snapshot (moment: Moment) (askSeconds: float) =
      snapshots.Add
        { Moment = moment
          Now = world.Now
          CachedBytes = cachedTotal world
          ResidentBytes = scenario.BaselineBytes + cachedTotal world
          Pressure = world.Pressure
          IdleSeconds = (world.Now - lastAskEnded.Value).TotalSeconds
          AskSeconds = askSeconds }
    let ask (projects: ProjectCost list) =
      let started = world.Now
      let specs = projects |> List.map (fun p -> p.Name, p) |> Map.ofList
      let check (project: string) (left: TimeSpan) : Async<ProjectCheck> =
        async {
          let spec = specs.[project]
          holder.Acquire() |> ignore
          let cost =
            match Map.containsKey project world.Cached with
            | true -> seconds (float (max 1 (spec.ColdSeconds / 10)))
            | false -> seconds (float spec.ColdSeconds)
          match cost <= left with
          | true ->
            world.Now <- world.Now + cost
            world.Cached <- Map.add project spec.CachedBytes world.Cached
            return ProjectCheck.Answered([], [])
          | false ->
            world.Now <- world.Now + left
            return ProjectCheck.CutOff
        }
      ReferenceGather.run
        (seconds (float scenario.BudgetSeconds))
        (fun () -> world.Now - started)
        check
        (projects |> List.map (fun p -> p.Name))
      |> Async.RunSynchronously
      |> ignore
      holder.Settle()
      lastAskEnded.Value <- world.Now
      snapshot Moment.AfterAsk (world.Now - started).TotalSeconds
    let idle (total: int) =
      let rec tick (left: int) =
        match left > 0 with
        | false -> ()
        | true ->
          let slice = min left scenario.SweepEverySeconds
          world.Now <- world.Now + seconds (float slice)
          holder.Sweep()
          snapshot Moment.AfterSweep 0.0
          tick (left - slice)
      tick total
    scenario.Steps
    |> List.iter (fun step ->
      match step with
      | Step.Ask projects -> ask projects
      | Step.Idle s -> idle s
      | Step.Squeeze pressure ->
        world.Pressure <- pressure
        // The machine changing says nothing to the holder until it is next asked, which a sweep or an ask does.
        ())
    { Scenario = scenario
      Snapshots = List.ofSeq snapshots
      Releases = List.ofSeq releases
      Reducer =
        (match policy with
         | Policy.Real -> "real (CacheHolder over ReferenceCacheRetention.decide)"
         | Policy.NeverReleases -> "twin-never-releases"
         | Policy.IgnoresCeiling -> "twin-ignores-ceiling"
         | Policy.IgnoresIdle -> "twin-ignores-idle"
         | Policy.IgnoresPressure -> "twin-ignores-pressure") }
