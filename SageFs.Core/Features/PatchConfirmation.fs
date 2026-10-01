/// Deciding when a patch is live.
///
/// A save re-points functions, and the planner says which ones landed. That
/// settles "the detour is in place", not "the app runs the new body": a caller
/// that had a function inlined keeps running its own copy of the old body.
/// The claim "live" therefore waits for evidence, which is the new body running
/// (`EntryProbes`), and everything here is the pure decision over that
/// evidence:
///
///   * `start` turns the planner's pending patch into a watch over the
///     functions it landed.
///   * `step` folds one sighting (a probe entered, or superseded by a newer
///     save) or the bound passing into the watch.
///   * a watch ends in `Patched` (every function it still answers for ran),
///     `NeverEntered` (the bound passed first) or nothing (every function was
///     replaced by a newer save, which reports for itself).
///
/// No clock and no IO: the host's registry supplies sightings, the announcer
/// supplies the bound, and the simulation folds this directly.
module SageFs.Features.PatchConfirmation

open System
open SageFs.Features.ReloadOutcome
open SageFs.Features.ReloadPlanning
open SageFs.Middleware.EntryProbes

/// One function a save re-pointed, and the probes watching its new body. A
/// function with no probe (its stub could not be built) can never be seen
/// running, so it can only end as never-entered.
type private Outcome = SageFs.Features.ReloadOutcome.ReloadOutcome

type WatchedDecl = { Declaration: string; Probes: int64 list }

[<RequireQualifiedAccess>]
type DeclWatch =
  /// Not seen running yet.
  | Unseen of WatchedDecl
  /// Its new body ran.
  | Seen of declaration: string
  /// A newer save replaced it before it ran. That save reports for it now.
  | Replaced of declaration: string

/// What one save is waiting to see. At least one function, by construction.
type PatchWatch =
  { /// What the save put in front of the process (the planner's count).
    Considered: int
    /// Live values the save kept; they appear in whatever the watch resolves into.
    Kept: KeptValue list
    /// How the patch reached the process: what it settles into says so, as the pending word did.
    Mechanism: PatchMechanism
    First: DeclWatch
    Rest: DeclWatch list }

[<RequireQualifiedAccess>]
type Begun =
  /// There is nothing to confirm: the outcome is not a pending patch, or a
  /// pending patch that cannot be watched and so is not claimed.
  | NothingToWatch of ReloadOutcome
  /// Tell the page `pending` now, and wait for sightings.
  | Watching of pending: ReloadOutcome * watch: PatchWatch

[<RequireQualifiedAccess>]
type WatchEvent =
  /// The host's word about one probe.
  | Sighted of probe: int64 * status: ProbeStatus
  /// The wait ran out.
  | BoundElapsed

[<RequireQualifiedAccess>]
type WatchStep =
  | StillWaiting of PatchWatch
  | Settled of ReloadOutcome
  /// Every function was replaced by a newer save; this watch has nothing to say.
  | Abandoned

let private decls (watch: PatchWatch) : DeclWatch list = watch.First :: watch.Rest

let private ofDecls (template: PatchWatch) (all: DeclWatch list) : PatchWatch =
  match all with
  | first :: rest -> { template with First = first; Rest = rest }
  | [] -> template

/// The functions a planner-confirmed save landed, each with the probes that
/// watch the new body of a function it names.
let watchedOfLanded (landed: SourceDecl list) (probes: EntryProbe list) : WatchedDecl list =
  landed
  |> List.distinctBy _.Name
  |> List.map (fun f ->
    { Declaration = f.Name
      Probes = probes |> List.filter (fun p -> reachedBy [ p.Declaration ] f) |> List.map _.Id })

/// The functions an interactive eval re-pointed, by the names the detour
/// report gives them. The report's names carry the FSI wrapper that the new
/// method's own name does not, so a probe matches by suffix.
let watchedOfRedirected (redirected: string list) (probes: EntryProbe list) : WatchedDecl list =
  redirected
  |> List.distinct
  |> List.map (fun name ->
    { Declaration = name
      Probes =
        probes
        |> List.filter (fun p -> name.EndsWith(p.Declaration, StringComparison.Ordinal))
        |> List.map _.Id })

/// Begin watching a planner outcome. Only a pending patch is watched; it is
/// announced as it is, and what it resolves into is `step`'s and `settle`'s.
let start (watched: WatchedDecl list) (outcome: ReloadOutcome) : Begun =
  let watching (considered: int) (kept: KeptValue list) (mechanism: PatchMechanism) : Begun =
    match watched with
    | [] -> Begun.NothingToWatch(Outcome.NoEffect(considered, []))
    | first :: rest ->
      Begun.Watching(
        outcome,
        { Considered = considered
          Kept = kept
          Mechanism = mechanism
          First = DeclWatch.Unseen first
          Rest = rest |> List.map DeclWatch.Unseen }
      )
  match outcome with
  | ReloadOutcome.PatchPending(_, considered, kept) -> watching considered kept PatchMechanism.Detour
  | ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.Pending(_, considered, _)) -> watching considered [] PatchMechanism.MetadataDelta
  | ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.Patched _)
  | ReloadOutcome.ByMetadataDelta(MetadataDeltaOutcome.NeverEntered _)
  | ReloadOutcome.Patched _
  | ReloadOutcome.NoEffect _
  | ReloadOutcome.Restarted _
  | ReloadOutcome.RestartRequired _
  | ReloadOutcome.CompileFailed _
  | ReloadOutcome.KeptLiveState _
  | ReloadOutcome.NeverEntered _ -> Begun.NothingToWatch outcome

/// Every probe the watch is still waiting on.
let probesOf (watch: PatchWatch) : int64 list =
  decls watch
  |> List.collect (function
    | DeclWatch.Unseen w -> w.Probes
    | DeclWatch.Seen _
    | DeclWatch.Replaced _ -> [])
  |> List.distinct

let private confirmed (watch: PatchWatch) (live: DeclWatch list) : ReloadOutcome =
  let applied = List.length live
  match watch.Mechanism, watch.Kept with
  | PatchMechanism.MetadataDelta, _ -> Outcome.ByMetadataDelta(MetadataDeltaOutcome.Patched(applied, watch.Considered))
  | _, [] -> Outcome.Patched(applied, watch.Considered)
  | _, first :: rest -> Outcome.KeptLiveState(applied, watch.Considered, first, rest)

/// The bound passed: whatever has not run is named.
let private expired (watch: PatchWatch) (live: DeclWatch list) : ReloadOutcome =
  let silent =
    live
    |> List.choose (function
      | DeclWatch.Unseen w -> Some w.Declaration
      | DeclWatch.Seen _
      | DeclWatch.Replaced _ -> None)
  let seen =
    live
    |> List.filter (function
      | DeclWatch.Seen _ -> true
      | DeclWatch.Unseen _
      | DeclWatch.Replaced _ -> false)
    |> List.length
  match silent, watch.Mechanism with
  | first :: rest, PatchMechanism.MetadataDelta -> Outcome.ByMetadataDelta(MetadataDeltaOutcome.NeverEntered(first, rest, seen, watch.Considered))
  | first :: rest, _ -> Outcome.NeverEntered(first, rest, seen, watch.Considered, watch.Kept)
  | [], _ -> confirmed watch live

let private isLive (d: DeclWatch) =
  match d with
  | DeclWatch.Replaced _ -> false
  | DeclWatch.Unseen _
  | DeclWatch.Seen _ -> true

let private isSeen (d: DeclWatch) =
  match d with
  | DeclWatch.Seen _ -> true
  | DeclWatch.Unseen _
  | DeclWatch.Replaced _ -> false

/// Fold one event into the watch.
let step (watch: PatchWatch) (event: WatchEvent) : WatchStep =
  let apply (d: DeclWatch) : DeclWatch =
    match d, event with
    | DeclWatch.Unseen w, WatchEvent.Sighted(probe, ProbeStatus.Entered) when List.contains probe w.Probes ->
      DeclWatch.Seen w.Declaration
    | DeclWatch.Unseen w, WatchEvent.Sighted(probe, ProbeStatus.Superseded) when List.contains probe w.Probes ->
      match w.Probes |> List.filter (fun p -> p <> probe) with
      | [] -> DeclWatch.Replaced w.Declaration
      | remaining -> DeclWatch.Unseen { w with Probes = remaining }
    | unchanged, _ -> unchanged
  let updated = decls watch |> List.map apply
  let live = updated |> List.filter isLive
  let next = ofDecls watch updated
  match event, live with
  | _, [] -> WatchStep.Abandoned
  | WatchEvent.BoundElapsed, _ -> WatchStep.Settled(expired next live)
  | WatchEvent.Sighted _, _ when live |> List.forall isSeen -> WatchStep.Settled(confirmed next live)
  | WatchEvent.Sighted _, _ -> WatchStep.StillWaiting next

/// The host answered (or the bound passed): take every sighting, then the
/// bound. Always ends the watch, so whatever is still silent is named.
let settle (reading: EntryReading) (watch: PatchWatch) : WatchStep =
  let sightings =
    reading.Sightings |> List.map (fun s -> WatchEvent.Sighted(s.Probe, s.Status))
  sightings @ [ WatchEvent.BoundElapsed ]
  |> List.fold
    (fun current event ->
      match current with
      | WatchStep.StillWaiting w -> step w event
      | WatchStep.Settled _
      | WatchStep.Abandoned -> current)
    (WatchStep.StillWaiting watch)
