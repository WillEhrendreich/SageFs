namespace SageFs.Simulation

open System
open SageFs.Cohort
open SageFs.Features
open SageFs.Features.LocalDataRetention

/// Deterministic Simulation Testing for local data retention: seeded months of
/// days passing, SageFs upgrades, friction bursts, daemon restarts and cohort
/// traffic, folded through the REAL `LocalDataRetention.decide`,
/// `aggregateVersionsToDrop` and `decideLedger`, and the real `Cohort.decide`
/// for the ledger. Same rules as the other DST harnesses here: chaos is data
/// (a seeded event list), the real decision functions are the subject, and a
/// twin shows the invariants catch something.
///
/// The store is modelled as what the SQLite adapter holds: the raw rows, the
/// per-version aggregate, and the cohort ledger. A prune does exactly what
/// `FrictionSqlite.Store.Prune` does with a decision: add the counts, delete
/// the rows, drop old versions' aggregates.
module RetentionSim =

  /// Small caps so a few dozen events actually hit every one of them.
  /// Production's are 30 days / 5,000 rows / 20 versions.
  let policy : FrictionPolicy =
    { MaxAge = TimeSpan.FromDays 3.0; MaxRows = 40; MaxAggregateVersions = 3 }

  let ledgerRetention = TimeSpan.FromDays 2.0

  [<RequireQualifiedAccess>]
  type SimEvent =
    | PassHours of int
    | PassDays of int
    /// `count` friction rows written now, by the running version.
    | Burst of count: int
    /// A new SageFs version is installed and the daemon restarts on it.
    | Upgrade
    /// The daemon restarts on the same version.
    | Restart
    /// The running daemon's prune timer fires.
    | PeriodicPrune
    | CohortJoin of who: string
    | CohortDepart of who: string
    /// The cohort reaper's tick: departs anyone silent past the lease window.
    | CohortTick

  type Scenario = { Seed: int; Events: SimEvent list }

  /// Which friction decision the prune uses.
  [<RequireQualifiedAccess>]
  type FrictionBehavior =
    | Real
    /// TWIN: prunes by age and row cap only, never by version.
    | AgeOnlyTwin

  /// What the last prune saw and did, so an invariant can compare before and
  /// after at the exact step it happened.
  [<RequireQualifiedAccess>]
  type PruneObservation =
    | NoPruneYet
    | Pruned of step: int * rowsBefore: RetainedRow list * ledgerBefore: LedgerEntry<string> list * at: DateTimeOffset * version: string

  type State = {
    Step: int
    Clock: DateTimeOffset
    VersionNumber: int
    NextId: int64
    Rows: RetainedRow list
    Aggregate: Map<AggregateKey, int>
    AggregateLastSeen: Map<string, DateTimeOffset>
    /// Every row ever written, per version. Ground truth for "no count lost".
    Written: Map<string, int>
    /// Versions whose aggregate was dropped to stay under the version cap.
    /// Their counts are allowed to be gone; nobody else's are.
    AggregateDropped: Set<string>
    Ledger: LedgerEntry<string> list
    CohortNow: CohortState<string>
    LastPrune: PruneObservation
  }

  let versionOf (n: int) = sprintf "0.9.%d" n

  let initial : State =
    { Step = 0
      Clock = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
      VersionNumber = 1
      NextId = 1L
      Rows = []
      Aggregate = Map.empty
      AggregateLastSeen = Map.empty
      Written = Map.empty
      AggregateDropped = Set.empty
      Ledger = []
      CohortNow = CohortState.empty ()
      LastPrune = PruneObservation.NoPruneYet }

  let private kinds = [| "get_fsi_status/CompletedCleanly"; "create_session/EncounteredBlocker"; "hard_reset_fsi_session/RecoveredVia"; "feedback/ToolIntentWasUnclear" |]

  let private pruneFriction (behavior: FrictionBehavior) (s: State) : State =
    let version = versionOf s.VersionNumber
    let decision =
      match behavior with
      | FrictionBehavior.Real -> LocalDataRetention.decide policy s.Clock version s.Rows
      | FrictionBehavior.AgeOnlyTwin -> decideByAgeOnlyTwin policy s.Clock version s.Rows
    let aggregate =
      decision.Aggregated
      |> Map.fold (fun acc key count -> Map.add key (count + (Map.tryFind key acc |> Option.defaultValue 0)) acc) s.Aggregate
    let lastSeen =
      decision.LastSeen
      |> Map.fold (fun acc v seen ->
        match Map.tryFind v acc with
        | Some existing when existing >= seen -> acc
        | _ -> Map.add v seen acc) s.AggregateLastSeen
    let dropVersions = aggregateVersionsToDrop policy version lastSeen |> Set.ofList
    { s with
        Rows = decision.Kept
        Aggregate = aggregate |> Map.filter (fun k _ -> not (Set.contains k.Version dropVersions))
        AggregateLastSeen = lastSeen |> Map.filter (fun v _ -> not (Set.contains v dropVersions))
        AggregateDropped = Set.union s.AggregateDropped dropVersions }

  /// A daemon start: the ledger retention check (before the owner replays),
  /// then the friction prune. Same order as `DaemonMode`.
  let private start (behavior: FrictionBehavior) (s: State) : State =
    let observed = PruneObservation.Pruned(s.Step, s.Rows, s.Ledger, s.Clock, versionOf s.VersionNumber)
    let ledgerAfter =
      match decideLedger ledgerRetention s.Clock.UtcDateTime s.Ledger with
      | LedgerDecision.Clear _ -> []
      | LedgerDecision.NothingStored
      | LedgerDecision.KeepActive _
      | LedgerDecision.KeepRecent _ -> s.Ledger
    // The owner replays whatever is left, exactly like CohortOwner.startCore.
    let s = { s with Ledger = ledgerAfter; CohortNow = replay ledgerAfter }
    { pruneFriction behavior s with LastPrune = observed }

  let private cohortCommand (s: State) (command: CohortCommand<string>) : State =
    let seq = s.Ledger |> List.tryLast |> Option.map (fun e -> e.Seq + 1L<SageFs.Measures.ledgerSeq>) |> Option.defaultValue 0L<SageFs.Measures.ledgerSeq>
    let entropy = BitConverter.GetBytes s.Step
    match SageFs.Cohort.decide s.Clock.UtcDateTime entropy s.CohortNow command with
    | Ok(next, events, _) ->
      let entry = { Seq = seq; Clock = s.Clock.UtcDateTime; Entropy = entropy; Command = command; Events = events }
      { s with CohortNow = next; Ledger = s.Ledger @ [ entry ] }
    | Error _ -> s

  let step (behavior: FrictionBehavior) (s: State) (ev: SimEvent) : State =
    let s = { s with Step = s.Step + 1 }
    match ev with
    | SimEvent.PassHours h -> { s with Clock = s.Clock.AddHours(float h) }
    | SimEvent.PassDays d -> { s with Clock = s.Clock.AddDays(float d) }
    | SimEvent.Burst count ->
      let version = versionOf s.VersionNumber
      let rows =
        [ for i in 0 .. count - 1 ->
            { Id = s.NextId + int64 i
              OccurredAt = s.Clock.AddSeconds(float i)
              Version = version
              Kind = kinds.[(int s.NextId + i) % kinds.Length] } ]
      { s with
          Rows = s.Rows @ rows
          NextId = s.NextId + int64 count
          Written = Map.add version (count + (Map.tryFind version s.Written |> Option.defaultValue 0)) s.Written
          Clock = s.Clock.AddSeconds(float count) }
    | SimEvent.Upgrade -> start behavior { s with VersionNumber = s.VersionNumber + 1 }
    | SimEvent.Restart -> start behavior s
    | SimEvent.PeriodicPrune ->
      { pruneFriction behavior s with
          LastPrune = PruneObservation.Pruned(s.Step, s.Rows, s.Ledger, s.Clock, versionOf s.VersionNumber) }
    | SimEvent.CohortJoin who -> cohortCommand s (CohortCommand.Join(who, JoinableRole.Implementer, None))
    | SimEvent.CohortDepart who -> cohortCommand s (CohortCommand.Depart who)
    | SimEvent.CohortTick -> cohortCommand s CohortCommand.Tick

  /// Every state, oldest first, including the initial one.
  let trace (behavior: FrictionBehavior) (scenario: Scenario) : State list =
    scenario.Events |> List.scan (step behavior) initial

  let private members = [| "ada"; "bo"; "cy" |]

  /// A pure function of `seed`: replaying a seed gives the identical trace.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let n = 20 + rng.Next 60
    let events =
      [ for _ in 1 .. n ->
          match rng.Next 12 with
          | 0 | 1 -> SimEvent.PassHours(1 + rng.Next 30)
          | 2 -> SimEvent.PassDays(1 + rng.Next 6)
          | 3 | 4 -> SimEvent.Burst(1 + rng.Next 30)
          | 5 -> SimEvent.Upgrade
          | 6 -> SimEvent.Restart
          | 7 -> SimEvent.PeriodicPrune
          | 8 -> SimEvent.CohortJoin members.[rng.Next members.Length]
          | 9 -> SimEvent.CohortDepart members.[rng.Next members.Length]
          | _ -> SimEvent.CohortTick ]
    { Seed = seed; Events = events }
