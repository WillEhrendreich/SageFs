namespace SageFs.Features

open System
open SageFs.Cohort

/// What SageFs keeps on disk under its data dir, and for how long. Before
/// this, friction.db and cohort.ledger.db only ever grew: nothing pruned them
/// by version, age or size, and there was nowhere to see how big they'd got.
///
/// Everything in here is a pure decision. The SQLite adapters
/// (`SageFs/FrictionSqlite.fs`, `SageFs/CohortLedger.fs`) read rows, ask these
/// functions what to do, and do it. The simulation (`SageFs.Simulation/
/// RetentionSim.fs`) folds the same functions over seeded months of use.
module LocalDataRetention =

  /// One stored row, as retention sees it. Nothing else in the row matters to
  /// the decision, so the adapter only has to read these four columns.
  type RetainedRow = {
    Id: int64
    OccurredAt: DateTimeOffset
    Version: string
    /// What the row gets counted as when it's rolled into the aggregate:
    /// tool/outcome for a friction event, feedback kind for explicit feedback.
    Kind: string
  }

  type FrictionPolicy = {
    /// Rows older than this go, whatever version wrote them.
    MaxAge: TimeSpan
    /// Raw rows kept per table. The newest win.
    MaxRows: int
    /// How many SageFs versions keep an aggregate row set. The versions seen
    /// most recently win.
    MaxAggregateVersions: int
  }

  /// The production policy, from `DataRetention` (env-overridable).
  let defaultPolicy () : FrictionPolicy =
    { MaxAge = SageFs.DataRetention.frictionMaxAge
      MaxRows = SageFs.DataRetention.frictionMaxRows
      MaxAggregateVersions = SageFs.DataRetention.frictionMaxAggregateVersions }

  /// The aggregate a dropped row is counted into.
  type AggregateKey = { Version: string; Kind: string }

  [<RequireQualifiedAccess>]
  type DropReason =
    /// Written by a SageFs version that isn't the one running.
    | OtherVersion
    /// Current version, but older than `MaxAge`.
    | PastMaxAge
    /// Current version and inside the window, but more than `MaxRows` rows
    /// are, so the oldest go.
    | OverRowCap

  type FrictionDecision = {
    /// Oldest first.
    Kept: RetainedRow list
    Dropped: (RetainedRow * DropReason) list
    /// Counts to ADD to the aggregate. Every dropped row lands in here once,
    /// so a prune never loses a count, only the row behind it.
    Aggregated: Map<AggregateKey, int>
    /// The newest `OccurredAt` among the dropped rows of each version, so the
    /// aggregate knows how recently each version was seen.
    LastSeen: Map<string, DateTimeOffset>
  }

  let private chronological (rows: RetainedRow list) =
    rows |> List.sortBy (fun r -> r.OccurredAt, r.Id)

  let private summarize (dropped: (RetainedRow * DropReason) list) =
    let aggregated =
      dropped
      |> List.countBy (fun (r, _) -> { Version = r.Version; Kind = r.Kind })
      |> Map.ofList
    let lastSeen =
      dropped
      |> List.groupBy (fun (r, _) -> r.Version)
      |> List.map (fun (v, rs) -> v, rs |> List.map (fun (r, _) -> r.OccurredAt) |> List.max)
      |> Map.ofList
    aggregated, lastSeen

  /// Which rows stay and which get rolled into the aggregate and deleted.
  /// Rows from other versions always go. Current-version rows go when they're
  /// older than `MaxAge`, and then the oldest go until at most `MaxRows` are
  /// left.
  let decide (policy: FrictionPolicy) (now: DateTimeOffset) (currentVersion: string) (rows: RetainedRow list) : FrictionDecision =
    let ordered = chronological rows
    let otherVersion, current = ordered |> List.partition (fun r -> r.Version <> currentVersion)
    let aged, inWindow = current |> List.partition (fun r -> now - r.OccurredAt > policy.MaxAge)
    let overflow = max 0 (inWindow.Length - max 0 policy.MaxRows)
    let overCap, kept = List.splitAt overflow inWindow
    let dropped =
      [ for r in otherVersion -> r, DropReason.OtherVersion
        for r in aged -> r, DropReason.PastMaxAge
        for r in overCap -> r, DropReason.OverRowCap ]
    let aggregated, lastSeen = summarize dropped
    { Kept = kept; Dropped = dropped; Aggregated = aggregated; LastSeen = lastSeen }

  /// TWIN, frozen as a witness and never wired into a product path: prunes by
  /// age and row cap only and ignores the version entirely, so an upgrade
  /// leaves the old version's rows sitting there until they age out.
  /// `RetentionSimTests` shows the simulation catches it.
  let decideByAgeOnlyTwin (policy: FrictionPolicy) (now: DateTimeOffset) (_currentVersion: string) (rows: RetainedRow list) : FrictionDecision =
    let ordered = chronological rows
    let aged, inWindow = ordered |> List.partition (fun r -> now - r.OccurredAt > policy.MaxAge)
    let overflow = max 0 (inWindow.Length - max 0 policy.MaxRows)
    let overCap, kept = List.splitAt overflow inWindow
    let dropped =
      [ for r in aged -> r, DropReason.PastMaxAge
        for r in overCap -> r, DropReason.OverRowCap ]
    let aggregated, lastSeen = summarize dropped
    { Kept = kept; Dropped = dropped; Aggregated = aggregated; LastSeen = lastSeen }

  /// Versions whose aggregate rows go, given when each was last seen. Keeps
  /// the `MaxAggregateVersions` seen most recently. The running version is
  /// never dropped, since the rows it's still writing would land right back.
  let aggregateVersionsToDrop (policy: FrictionPolicy) (currentVersion: string) (lastSeen: Map<string, DateTimeOffset>) : string list =
    let others =
      lastSeen
      |> Map.toList
      |> List.filter (fun (v, _) -> v <> currentVersion)
      |> List.sortByDescending (fun (v, seen) -> seen, v)
    let room =
      match Map.containsKey currentVersion lastSeen with
      | true -> policy.MaxAggregateVersions - 1
      | false -> policy.MaxAggregateVersions
    others |> List.skip (min others.Length (max 0 room)) |> List.map fst

  // ── The cohort ledger ─────────────────────────────────────────────────

  /// What the ledger's replayed state says about the cohort right now.
  [<RequireQualifiedAccess>]
  type CohortActivity =
    /// Someone is still in it, holds a claim, or has a landing in flight.
    | Active of presentMembers: int * heldClaims: int * openLandings: int
    /// Nobody present, nothing held, nothing in flight. What's left is history.
    | Finished

  let cohortActivity (state: CohortState<'m>) : CohortActivity =
    let present =
      state.Members |> Map.filter (fun _ r -> r.Presence = MemberPresence.Present) |> Map.count
    let held =
      state.Claims
      |> Map.filter (fun _ c ->
        match c.State with
        | ClaimState.Held _ -> true
        | ClaimState.Orphaned _
        | ClaimState.Released _ -> false)
      |> Map.count
    let openLandings =
      state.Landings
      |> Map.filter (fun _ l ->
        match l.Settlement with
        | LandingSettlement.Unsettled -> true
        | LandingSettlement.SettledAt _ -> false)
      |> Map.count
    match present, held, openLandings with
    | 0, 0, 0 -> CohortActivity.Finished
    | _ -> CohortActivity.Active(present, held, openLandings)

  [<RequireQualifiedAccess>]
  type LedgerDecision =
    | NothingStored
    /// The cohort is still running. Its rows are never touched.
    | KeepActive of CohortActivity
    /// The cohort finished, but more recently than the retention window.
    | KeepRecent of finishedAt: DateTime
    /// The cohort finished longer ago than the retention window: clear it.
    | Clear of finishedAt: DateTime * rows: int

  /// Whether to clear the cohort ledger. The daemon keeps one cohort per
  /// ledger, so a finished cohort means the whole ledger. It's cleared only
  /// when replaying it shows nobody present, no claim held and no landing in
  /// flight, AND its last entry is older than `retention`. An active cohort is
  /// never touched, however old its rows are.
  let decideLedger (retention: TimeSpan) (now: DateTime) (entries: LedgerEntry<'m> list) : LedgerDecision =
    match List.tryLast entries with
    | None -> LedgerDecision.NothingStored
    | Some last ->
      match cohortActivity (replay entries) with
      | CohortActivity.Active _ as active -> LedgerDecision.KeepActive active
      | CohortActivity.Finished ->
        match now - last.Clock > retention with
        | true -> LedgerDecision.Clear(last.Clock, entries.Length)
        | false -> LedgerDecision.KeepRecent last.Clock
