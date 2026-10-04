namespace SageFs.Simulation

open System
open System.Collections.Generic
open SageFs.Build.TierPlan

/// Deterministic Simulation Testing for the tier scheduler: a fake clock, a fake machine whose neighbours spike the CPU
/// and hold memory, units that complete or die (leaving memory behind), a machine that sometimes cannot be read at all,
/// all folded through the REAL `TierPlan.advanceWith` that ci-pipeline.fsx's `runTiers` calls. Same rules as the other
/// DST harnesses here: chaos is data (a seed makes a scenario), the real decision function is the subject, and each twin
/// shows one invariant has teeth.
module TierSched =

  [<RequireQualifiedAccess>]
  type UnitEnd =
    | Completes
    /// The unit's process dies mid-run and its daemon leaves a gigabyte behind for a while.
    | Dies

  /// A unit of the run: what the scheduler knows (the candidate) and what the world will do with it.
  type SimUnit = { Candidate: Candidate; Lasts: float; End: UnitEnd }

  /// Another tenant of the machine: busy on the CPU and holding memory between two moments.
  type Neighbour = { From: float; Until: float; CpuPercent: float; HeldBytes: int64 }

  type PressureFeed =
    | Readable
    /// /proc/pressure is missing or garbage: the reading says so and the scheduler must fall back.
    | PressureUnreadable

  /// `Units` are in the order the caller wants them to start in.
  type Scenario = { Seed: int; Units: SimUnit list; Neighbours: Neighbour list; Feed: PressureFeed }

  /// Which scheduler runs: the real one, or a twin that breaks exactly one rule.
  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    | IgnoresMemoryTwin
    | IgnoresCapTwin
    | NoPatienceTwin
    | StartsTwiceTwin
    | NoSettleTwin
    | OutOfOrderTwin
    | BlindIsIdleTwin
    | GuardAlwaysTwin

  /// One start, with the machine as the scheduler saw it.
  type Start =
    { At: float
      Label: string
      Basis: AdmitBasis
      Reading: Reading
      RunningBefore: int
      PreviousStart: LastAdmit
      PeakBytes: int64 }

  [<RequireQualifiedAccess>]
  type RunEnd =
    | Drained of at: float
    /// The line was still not drained at the cut-off: the run does not end.
    | CutOff of at: float

  type Trace =
    { Scenario: Scenario
      Behavior: Behavior
      Starts: Start list
      Ended: (string * float * UnitEnd) list
      RunEnd: RunEnd }

  let gib (n: int) = int64 n * Admission.bytesPerGiB

  /// The machine's memory.
  let machineBytes = gib 62

  /// The simulation's own limits: a cap low enough that a scenario's units can exceed it, a fallback below it.
  let simCap = 4
  let simFallback = 2
  let simLimits : Limits = { Admission.standard simFallback with MaxTierProcesses = simCap }

  /// What one running unit adds to the machine's CPU pressure.
  let pressurePerUnit = 3.0
  /// The memory a unit that died leaves behind, and for how long.
  let leakedBytes = gib 1
  let leakLasts = 30.0
  /// One tick of the fake clock.
  let tick = 1.0
  /// The run is cut off here: long enough for every unit to start one at a time behind a neighbour that never leaves.
  let cutOffSeconds = 40000.0
  /// A neighbour that never leaves.
  let forever = 1.0e9

  let private unitsOf (rng: Random) : SimUnit list =
    let count = 5 + rng.Next 9
    [ for i in 1 .. count ->
        { Candidate = { Label = sprintf "tier-%d" i; PeakBytes = gib (1 + rng.Next 4) }
          Lasts = float (20 + rng.Next 400)
          End = (match rng.Next 8 with 0 -> UnitEnd.Dies | _ -> UnitEnd.Completes) } ]
    // longest first: the order the real pipeline gives (orderByCriticalPath)
    |> List.sortByDescending (fun u -> u.Lasts)

  let private spikesOf (rng: Random) : Neighbour list =
    [ for _ in 1 .. rng.Next 7 ->
        let from = float (rng.Next 900)
        { From = from
          Until = from + float (5 + rng.Next 200)
          CpuPercent = float (rng.Next 90)
          HeldBytes = gib (rng.Next 56) } ]

  /// A neighbour that holds all but 6 GiB for good: less than the reserve plus any unit's peak, so nothing fits, ever.
  let hog : Neighbour = { From = 0.0; Until = forever; CpuPercent = 10.0; HeldBytes = machineBytes - gib 6 }

  /// A pure function of `seed`: units, neighbours' spikes and dips, and every seventh seed a neighbour that never leaves
  /// and every eleventh a machine whose pressure cannot be read.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let units = unitsOf rng
    let spikes = spikesOf rng
    { Seed = seed
      Units = units
      Neighbours = (match seed % 7 with 0 -> hog :: spikes | _ -> spikes)
      Feed = (match seed % 11 with 0 -> PressureUnreadable | _ -> Readable) }

  /// Four units behind a neighbour that never leaves.
  let hoggedScenario : Scenario =
    { Seed = -1
      Units =
        [ for i in 1 .. 4 ->
            { Candidate = { Label = sprintf "tier-%d" i; PeakBytes = gib 2 }; Lasts = 90.0; End = UnitEnd.Completes } ]
      Neighbours = [ hog ]
      Feed = Readable }

  /// The fake machine at `now`, with `running` units each ramping to its peak over `Limits.RampSeconds`.
  let readingAt (scenario: Scenario) (leaks: Neighbour list) (running: RunningUnit list) (now: float) : Reading =
    let active = scenario.Neighbours @ leaks |> List.filter (fun n -> n.From <= now && now < n.Until)
    let used =
      running
      |> List.sumBy (fun r ->
        let ramped = min 1.0 ((now - r.StartedAt) / simLimits.RampSeconds)
        int64 (float r.Unit.PeakBytes * ramped))
    let available = max 0L (machineBytes - (active |> List.sumBy (fun n -> n.HeldBytes)) - used)
    let percent = min 100.0 ((active |> List.sumBy (fun n -> n.CpuPercent)) + pressurePerUnit * float running.Length)
    { Pressure =
        (match scenario.Feed with
         | Readable -> Measured percent
         | PressureUnreadable -> NotMeasured "the pressure file cannot be read")
      Memory = AvailableBytes available }

  let private policyOf (behavior: Behavior) : Policy =
    match behavior with
    | Behavior.NoPatienceTwin -> ByPressure { simLimits with PatienceSeconds = infinity }
    | Behavior.NoSettleTwin -> ByPressure { simLimits with SettleSeconds = 0.0 }
    | Behavior.IgnoresCapTwin -> ByPressure { simLimits with MaxTierProcesses = Int32.MaxValue }
    | Behavior.Real
    | Behavior.IgnoresMemoryTwin
    | Behavior.StartsTwiceTwin
    | Behavior.OutOfOrderTwin
    | Behavior.BlindIsIdleTwin
    | Behavior.GuardAlwaysTwin -> ByPressure simLimits

  /// A twin's decision function: the real one with one rule broken.
  let private deciderOf (behavior: Behavior) =
    match behavior with
    | Behavior.IgnoresMemoryTwin ->
      fun policy (reading: Reading) now schedule head ->
        decide policy { reading with Memory = AvailableBytes Int64.MaxValue } now schedule head
    | Behavior.BlindIsIdleTwin ->
      fun policy (reading: Reading) now schedule head ->
        match reading.Pressure with
        | NotMeasured _ -> decide policy { reading with Pressure = Measured 0.0 } now schedule head
        | Measured _ -> decide policy reading now schedule head
    | Behavior.GuardAlwaysTwin ->
      fun policy reading now (schedule: Schedule) head ->
        match decide policy reading now schedule head, policy with
        | Wait (MemoryShort _), ByPressure limits when now - schedule.HeadSince >= limits.PatienceSeconds -> Admit StarvationGuard
        | decision, _ -> decision
    | Behavior.Real
    | Behavior.IgnoresCapTwin
    | Behavior.NoPatienceTwin
    | Behavior.StartsTwiceTwin
    | Behavior.NoSettleTwin
    | Behavior.OutOfOrderTwin -> decide

  /// Run a scenario deterministically. No IO; fully reproducible from the seed.
  let trace (behavior: Behavior) (scenario: Scenario) : Trace =
    let policy = policyOf behavior
    let decider = deciderOf behavior
    let waiting0 =
      match behavior with
      | Behavior.OutOfOrderTwin -> scenario.Units |> List.rev
      | _ -> scenario.Units
    let unitOf =
      scenario.Units |> List.map (fun u -> u.Candidate.Label, u) |> Map.ofList
    let startedOnce = HashSet<string>()
    let mutable schedule = startSchedule 0.0 (waiting0 |> List.map (fun u -> u.Candidate))
    let endsAt = Dictionary<string, float>()
    let mutable leaks : Neighbour list = []
    let mutable starts : Start list = []
    let mutable ended : (string * float * UnitEnd) list = []
    let mutable now = 0.0
    let mutable runEnd = RunEnd.CutOff cutOffSeconds
    let mutable finished = false
    while not finished do
      // units whose time has come
      for r in schedule.Running do
        match endsAt.TryGetValue r.Unit.Label with
        | true, at when at <= now ->
          let u = unitOf[r.Unit.Label]
          ended <- (r.Unit.Label, now, u.End) :: ended
          schedule <- finishUnit r.Unit.Label schedule
          match u.End with
          | UnitEnd.Dies -> leaks <- { From = now; Until = now + leakLasts; CpuPercent = 0.0; HeldBytes = leakedBytes } :: leaks
          | UnitEnd.Completes -> ()
        | _ -> ()
      // start what the machine has room for, head of the line first
      let mutable looking = true
      while looking do
        let reading = readingAt scenario leaks schedule.Running now
        match advanceWith decider policy reading now schedule with
        | Started (c, basis, next) ->
          starts <-
            { At = now
              Label = c.Label
              Basis = basis
              Reading = reading
              RunningBefore = schedule.Running.Length
              PreviousStart = schedule.LastAdmit
              PeakBytes = c.PeakBytes }
            :: starts
          endsAt[c.Label] <- now + unitOf[c.Label].Lasts
          schedule <-
            match behavior, startedOnce.Add c.Label with
            | Behavior.StartsTwiceTwin, true -> { next with Waiting = c :: next.Waiting }
            | _ -> next
        | Held _
        | Drained -> looking <- false
      match schedule.Waiting, schedule.Running with
      | [], [] ->
        runEnd <- RunEnd.Drained now
        finished <- true
      | _ ->
        now <- now + tick
        if now > cutOffSeconds then
          runEnd <- RunEnd.CutOff now
          finished <- true
    { Scenario = scenario
      Behavior = behavior
      Starts = List.rev starts
      Ended = List.rev ended
      RunEnd = runEnd }
