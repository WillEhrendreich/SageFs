/// Event-sourced history for tweaks: an append-only, never-mutated event
/// stream per session, with pure folds for the current source, the dirty
/// set and the undo cursor, plus a rollback OPERATION built on top that
/// never guesses when something changed underneath it.
///
/// This is deliberately NOT a resurrection of the per-session event sourcing
/// the roast found removed (`Replay.fs`, called by nothing). That decision
/// stands. This is the one place the pattern earns its keep: a tuning
/// session's own history, which nothing else in the product already covers.
///
/// The git-projection layer (private refs, `hash-object`/`write-tree`
/// plumbing, `git merge-file` three-way rollback) is IO and stays out of
/// this module on purpose. What's here are the two seams it plugs into:
/// `SegmentIO` (persist a compaction plan; a real implementation writes the
/// new segment, syncs, then deletes the old one atomically) and
/// `ThreeWayMerge` (what a file-level rollback falls back to when the
/// structural inverse can't apply). Both are function-shaped placeholders,
/// nothing in this file calls them, so nothing here does IO.
module SageFs.Features.Tweak.TweakLog

open System
open System.IO
open SageFs
open SageFs.Features.Tweak.TweakAddress

// ── bounded growth: named limits, Timeouts.fs-style ──

/// Every number this module compares a log against, named and in one place
/// rather than scattered as literals. A real deployment can make these
/// settable later the way `Timeouts` reads environment overrides; nothing
/// here depends on that, so it isn't built until something needs it.
[<RequireQualifiedAccess>]
module TweakLogLimits =
  /// Sim-clock ticks a scrubbed value must sit still (no new tick on that
  /// address) before it settles into one `TweakApplied` without an explicit
  /// release. Time here is always the caller's clock, a virtual sim clock
  /// in tests, never `DateTime.Now`, so this is a tick COUNT, not a real
  /// duration.
  let scrubSettleIdleTicks = 3L

  /// How many of the most recent undoable operations (`TweakApplied` /
  /// `TweakSaved`) compaction always keeps raw, regardless of dirtiness.
  let undoWindow = 200

  /// Per-session ceiling on event COUNT before compaction is due.
  let maxEventsPerSession = 5_000

  /// Per-session ceiling on the log's encoded BYTE size before compaction
  /// is due.
  let maxBytesPerSession = 4L * 1024L * 1024L

  /// A soft, informational ceiling across every session's log combined.
  /// Nothing in this module enforces it across sessions (a log only ever
  /// knows about itself), it exists so a caller wiring several sessions'
  /// logs together has a named number to check against instead of picking
  /// one on the spot.
  let maxTotalBytesAcrossSessions = 64L * 1024L * 1024L

// ── fingerprints: is a log/snapshot even readable by THIS build? ──

/// How much a stored fingerprint can be trusted, the same three-way split
/// Elm's time-travel debugger uses when it imports an old session:
/// structurally readable and about the same target (`Fine`), structurally
/// readable but something else has moved (`Risky`, proceed with a
/// caveat), or not safely decodable at all (`Impossible`, never folded).
[<RequireQualifiedAccess>]
type LogGrade =
  | Fine
  | Risky
  | Impossible

/// Stamped on every log segment and every snapshot. `SchemaVersion` names
/// the BYTE LAYOUT (bump it and old bytes may not even parse safely).
/// `FoldVersion` names the FOLD SEMANTICS over that layout (bump it when
/// `foldEvents`'s rules change meaning without the bytes themselves
/// changing shape). `TargetHash` names the file/binding this log is
/// actually about, so a log surviving from a DIFFERENT version of the
/// target file is caught even when the bytes and the fold both still
/// agree perfectly with each other.
type Fingerprint =
  { SchemaVersion: int
    FoldVersion: int
    TargetHash: string }

[<RequireQualifiedAccess>]
module Fingerprint =
  /// Bump on a byte-layout change to `TweakLogFormat`.
  let schemaVersion = 1

  /// Bump on a change to what `foldEvents`'s rules MEAN, even if no byte
  /// layout changed (the UserEditObserved dirty-preserving rule earlier in
  /// this file is exactly the kind of change that would deserve a bump).
  let foldVersion = 1

  let current (targetHash: string) : Fingerprint =
    { SchemaVersion = schemaVersion; FoldVersion = foldVersion; TargetHash = targetHash }

  /// How much a stored fingerprint can be trusted against what THIS build
  /// understands, in the order Elm's debugger import check uses: the byte
  /// layout is either readable or it isn't (there is no partial credit for
  /// schema), and everything else is a "proceed with the caveat shown"
  /// grade rather than an outright refusal.
  let grade (current: Fingerprint) (stored: Fingerprint) : LogGrade =
    match stored.SchemaVersion = current.SchemaVersion with
    | false -> LogGrade.Impossible
    | true ->
      match stored.FoldVersion = current.FoldVersion && stored.TargetHash = current.TargetHash with
      | true -> LogGrade.Fine
      | false -> LogGrade.Risky

// ── the events ──

[<RequireQualifiedAccess>]
type TweakLogEvent =
  | TweakApplied of address: TweakAddress * textBefore: string * textAfter: string * hashAfter: string
  | TweakSaved of address: TweakAddress * textBefore: string * textAfter: string * hashAfter: string * fileHashBefore: string
  | UserEditObserved of address: TweakAddress * textNow: string * hashNow: string
  | ReformatObserved of fileHashBefore: string * fileHashAfter: string
  | HotReloadObserved of fileHashBefore: string * fileHashAfter: string
  | ConflictRaised of address: TweakAddress * wrote: string * now: string * before: string
  | ConflictResolved of address: TweakAddress
  | RolledBack of eventId: int

/// One event, stamped with its own id (unique, ascending) and the sim
/// clock's reading when it was appended.
type LoggedEvent = { Id: int; At: int64; Event: TweakLogEvent }

/// Ascending by `Id`, oldest first. Append-only: nothing in this module
/// offers a way to remove or edit an entry once it's in `Events`, the
/// closest thing to deletion is compaction, which MOVES entries into a
/// snapshot, never drops what they said.
///
/// `NextId` is carried explicitly, separately from `Events`, because
/// `Events` can get SHORTER after compaction (its oldest entries move into a
/// `Snapshot`) while ids must keep counting up regardless, deriving the
/// next id from `Events`'s own tail would hand out an id already used by a
/// now-compacted-away entry the moment the tail is empty or short.
type EventLog = { Events: LoggedEvent list; NextId: int }

[<RequireQualifiedAccess>]
module EventLog =
  let empty = { Events = []; NextId = 1 }

  let append (log: EventLog) (at: int64) (event: TweakLogEvent) : EventLog * LoggedEvent =
    let logged = { Id = log.NextId; At = at; Event = event }
    { log with Events = log.Events @ [ logged ]; NextId = log.NextId + 1 }, logged

// ── scrub coalescing: a stream of drag ticks on one address becomes ONE
//    TweakApplied when it settles. Ticks themselves are never logged. ──

type ScrubState<'Value> =
  { Address: TweakAddress
    TextBefore: string
    Latest: 'Value
    LatestText: string
    LastTickAt: int64 }

[<RequireQualifiedAccess>]
module ScrubCoalescer =
  /// Start (or restart, on a fresh drag) coalescing scrub ticks for one
  /// address.
  let start (address: TweakAddress) (textBefore: string) (at: int64) (value: 'Value) (text: string) : ScrubState<'Value> =
    { Address = address; TextBefore = textBefore; Latest = value; LatestText = text; LastTickAt = at }

  /// One more tick on the SAME address while still dragging: latest wins,
  /// nothing is logged yet.
  let tick (s: ScrubState<'Value>) (at: int64) (value: 'Value) (text: string) : ScrubState<'Value> =
    { s with Latest = value; LatestText = text; LastTickAt = at }

  /// The value has sat still since `at`, for at least the settle threshold.
  let isIdleAt (s: ScrubState<'Value>) (at: int64) : bool =
    at - s.LastTickAt >= TweakLogLimits.scrubSettleIdleTicks

  /// Turn the coalesced drag into the one event it becomes, whether it
  /// settled by going idle or by an explicit release (letting go of the
  /// knob). Either way, this is the only place a scrub produces a
  /// `TweakLogEvent`, every intermediate tick stays out of the log.
  let settle (s: ScrubState<'Value>) : TweakLogEvent =
    TweakLogEvent.TweakApplied(s.Address, s.TextBefore, s.LatestText, contentHash s.LatestText)

// ── projections: pure folds over the stream ──

/// What every projection tracks per address: the text the log currently
/// believes is live, and the text last confirmed durable on disk. Dirty is
/// derived (`Known <> Saved`), never stored as its own flag, so it can never
/// drift out of sync with the two texts it's about.
type Projection =
  { Known: Map<TweakAddress, string>
    Saved: Map<TweakAddress, string>
    OpenConflicts: Set<TweakAddress>
    LastFileHash: string option }

[<RequireQualifiedAccess>]
module Projection =
  let empty =
    { Known = Map.empty; Saved = Map.empty; OpenConflicts = Set.empty; LastFileHash = None }

  let dirtySet (p: Projection) : Set<TweakAddress> =
    p.Known
    |> Map.toList
    |> List.choose (fun (addr, known) ->
      match p.Saved |> Map.tryFind addr with
      | Some saved when saved = known -> None
      | _ -> Some addr)
    |> Set.ofList

let private addressOf (e: TweakLogEvent) : TweakAddress option =
  match e with
  | TweakLogEvent.TweakApplied(addr, _, _, _) -> Some addr
  | TweakLogEvent.TweakSaved(addr, _, _, _, _) -> Some addr
  | TweakLogEvent.UserEditObserved(addr, _, _) -> Some addr
  | TweakLogEvent.ConflictRaised(addr, _, _, _) -> Some addr
  | TweakLogEvent.ConflictResolved addr -> Some addr
  | TweakLogEvent.ReformatObserved _
  | TweakLogEvent.HotReloadObserved _
  | TweakLogEvent.RolledBack _ -> None

/// The address, before and after of any mutating event ANYWHERE in
/// `events`, resolved by id, RECURSIVELY: a `RolledBack targetId` is
/// itself a state transition (it moved the address from the target's
/// `after` back to its `before`), so its own effect is the target's
/// effect FLIPPED. This is what makes redo possible with no new event
/// case: redoing an undo is rolling back the ROLLBACK, and `effectOf`
/// tells `rollback` what that means without it needing to know how many
/// levels of undo/redo came before.
///
/// Scope limit, stated plainly (same shape as `whyKept`'s): this walks
/// `events` as given. Called with `log.Events` (the whole in-memory log,
/// what `rollback`/`performUndo`/`performRedo` always have), multi-level
/// undo/redo resolves correctly. A `RolledBack` in a POST-COMPACTION tail
/// whose target crossed the snapshot boundary only resolves one level
/// (via `Snapshot.Origins`, address+before only) — a second undo/redo
/// cycle on something already compacted away is not this pass's problem
/// to solve.
let rec private effectOf (events: LoggedEvent list) (id: int) : (TweakAddress * string * string) option =
  events
  |> List.tryFind (fun e -> e.Id = id)
  |> Option.bind (fun e ->
    match e.Event with
    | TweakLogEvent.TweakApplied(addr, before, after, _) -> Some(addr, before, after)
    | TweakLogEvent.TweakSaved(addr, before, after, _, _) -> Some(addr, before, after)
    | TweakLogEvent.RolledBack targetId -> effectOf events targetId |> Option.map (fun (addr, before, after) -> addr, after, before)
    | _ -> None)

/// The (address, textBefore) a mutating event recorded, the piece a
/// `RolledBack eventId` needs from whatever event it targets. One level
/// only (see `effectOf` for the recursive version `rollback` itself uses).
let private originOf (e: TweakLogEvent) : (TweakAddress * string) option =
  match e with
  | TweakLogEvent.TweakApplied(addr, before, _, _) -> Some(addr, before)
  | TweakLogEvent.TweakSaved(addr, before, _, _, _) -> Some(addr, before)
  | _ -> None

/// Fold `events` onto `seed`, resolving a `RolledBack eventId` by asking
/// `lookupOrigin`, which lets the SAME fold serve both a full-history
/// projection (look the id up in the whole log) and a snapshot+tail
/// projection (look it up in the snapshot's own `Origins` first, falling
/// back to the tail).
let private foldEvents (lookupOrigin: int -> (TweakAddress * string) option) (seed: Projection) (events: LoggedEvent list) : Projection =
  events
  |> List.fold
    (fun p e ->
      match e.Event with
      | TweakLogEvent.TweakApplied(addr, _before, after, _hash) -> { p with Known = p.Known |> Map.add addr after }
      | TweakLogEvent.TweakSaved(addr, _before, after, _hash, _fileHashBefore) ->
        { p with Known = p.Known |> Map.add addr after; Saved = p.Saved |> Map.add addr after }
      | TweakLogEvent.UserEditObserved(addr, textNow, _hash) ->
        // A direct edit to the file always moves disk truth (`Saved`). Its
        // effect on `Known` (the log's belief about the LIVE app value)
        // depends on whether there was a pending tweak to protect:
        //   * nothing pending (Known already = Saved, or the address was
        //     never tweaked at all): the edit becomes the new agreed
        //     baseline for BOTH, same as any other external change to a
        //     clean file.
        //   * something pending (a tweak was Applied but never Saved):
        //     the edit must NOT launder that pending tweak into looking
        //     clean. Only `Saved` moves, so `Known` keeps diverging from
        //     it, `dirtySet` still says "you have something unsaved",
        //     which is now also genuinely true of the file itself.
        let wasDirty =
          match p.Known |> Map.tryFind addr, p.Saved |> Map.tryFind addr with
          | Some k, Some s -> k <> s
          | Some _, None -> true
          | None, _ -> false
        match wasDirty with
        | true -> { p with Saved = p.Saved |> Map.add addr textNow }
        | false -> { p with Known = p.Known |> Map.add addr textNow; Saved = p.Saved |> Map.add addr textNow }
      | TweakLogEvent.ReformatObserved(_, afterHash) -> { p with LastFileHash = Some afterHash }
      | TweakLogEvent.HotReloadObserved(_, afterHash) -> { p with LastFileHash = Some afterHash }
      | TweakLogEvent.ConflictRaised(addr, _, _, _) -> { p with OpenConflicts = p.OpenConflicts |> Set.add addr }
      | TweakLogEvent.ConflictResolved addr -> { p with OpenConflicts = p.OpenConflicts |> Set.remove addr }
      | TweakLogEvent.RolledBack targetId ->
        match lookupOrigin targetId with
        | Some(addr, before) -> { p with Known = p.Known |> Map.add addr before; Saved = p.Saved |> Map.add addr before }
        | None -> p)
    seed

/// The full-history projection: every event, folded from empty.
let project (log: EventLog) : Projection =
  let lookup id = log.Events |> List.tryFind (fun e -> e.Id = id) |> Option.bind (fun e -> originOf e.Event)
  foldEvents lookup Projection.empty log.Events

let dirtySet (log: EventLog) : Set<TweakAddress> = project log |> Projection.dirtySet

/// One tweak a crash left unsaved, worth offering to re-apply. Pure data,
/// same shape the log already carries, nothing re-derived by guessing.
type RecoverableTweak =
  { EventId: int
    Address: TweakAddress
    TextBefore: string
    TextAfter: string
    /// True for exactly one entry (if any exist at all): the single most
    /// recent unsaved tweak in the log, the one that was live and unsaved
    /// at the moment the process died. Offered UNCHECKED by default,
    /// because it's the one least sure to still be what the user wanted;
    /// everything else here defaults to checked.
    WasInFlightAtCrash: bool }

/// A pure function from the log to an ordered (log order) list of
/// recoverable tweaks — exactly what `dirtySet` already tracks, restated
/// as an offer instead of a set. Uses the SAME projection/fold path
/// everything else in this module does; there is no separate "recovery"
/// logic to drift from replay or the dirty-set query. An unsettled drag is
/// never a candidate here, because `ScrubCoalescer` never journals one:
/// only `settle`'s single `TweakApplied` reaches the log at all.
let recoveryOffer (log: EventLog) : RecoverableTweak list =
  let dirty = dirtySet log
  let latestUnsavedPerAddress =
    log.Events
    |> List.choose (fun e ->
      match e.Event with
      | TweakLogEvent.TweakApplied(addr, before, after, _) when dirty.Contains addr -> Some(e.Id, addr, before, after)
      | _ -> None)
    |> List.groupBy (fun (_, addr, _, _) -> addr)
    // The LATEST Applied per address is the only one still relevant — an
    // earlier one for the same address was superseded before the crash,
    // not independently recoverable.
    |> List.map (fun (_, xs) -> xs |> List.maxBy (fun (id, _, _, _) -> id))
    |> List.sortBy (fun (id, _, _, _) -> id)
  let mostRecentId =
    match latestUnsavedPerAddress with
    | [] -> None
    | xs -> xs |> List.map (fun (id, _, _, _) -> id) |> List.max |> Some
  latestUnsavedPerAddress
  |> List.map (fun (id, addr, before, after) ->
    { EventId = id; Address = addr; TextBefore = before; TextAfter = after; WasInFlightAtCrash = Some id = mostRecentId })

/// Is there an unresolved `ConflictRaised` on this address right now? An
/// open conflict is exclusive: it blocks further saves and rollbacks on
/// THAT address until a `ConflictResolved` closes it, so two
/// half-understood decisions never stack on top of each other.
let hasOpenConflict (log: EventLog) (address: TweakAddress) : bool =
  (project log).OpenConflicts |> Set.contains address

// ── rollback: an operation on top of the log, not itself an event ──

[<RequireQualifiedAccess>]
type RollbackOutcome =
  /// The address still hashed to what the target op wrote, the rollback
  /// applied cleanly. `Source` is the new file text; the caller appends a
  /// `RolledBack` event for it (this function never mutates the log).
  | Applied of source: string
  /// It changed since. Nothing is guessed or overwritten: the three
  /// versions in play, so the caller can show them side by side.
  | Conflict of wrote: string * now: string * before: string

[<RequireQualifiedAccess>]
type RollbackError =
  | NoSuchOperation of eventId: int
  /// The target event exists but isn't a save/apply (nothing to invert).
  | NotAnOperation of eventId: int
  | AddressGone of ResolveError
  /// This address already has an unresolved conflict open. Resolve that
  /// one first, rather than layering a second decision on top of it.
  | BlockedByOpenConflict of address: TweakAddress

/// Roll back the write `opId` made, against `currentSource` as it is RIGHT
/// NOW. Only ever applies (or reports a conflict), never guesses, and
/// refuses outright when the target address already has an open conflict.
/// Rolls back whatever `opId` did — a `TweakApplied`/`TweakSaved` (an
/// ordinary undo), OR a `RolledBack` (rolling back a rollback IS redo,
/// `effectOf` already flips its before/after, so this function does not
/// need to know which case it's looking at).
let rollback (log: EventLog) (opId: int) (currentSource: string) : Result<RollbackOutcome, RollbackError> =
  match log.Events |> List.tryFind (fun e -> e.Id = opId) with
  | None -> Error(RollbackError.NoSuchOperation opId)
  | Some _ ->
    match effectOf log.Events opId with
    | None -> Error(RollbackError.NotAnOperation opId)
    | Some(addr, before, after) ->
      match hasOpenConflict log addr with
      | true -> Error(RollbackError.BlockedByOpenConflict addr)
      | false ->
        match resolve currentSource addr with
        | Error e -> Error(RollbackError.AddressGone e)
        | Ok resolved when resolved.Hash = contentHash after ->
          Ok(RollbackOutcome.Applied(replaceRange currentSource resolved.Range before))
        | Ok resolved -> Ok(RollbackOutcome.Conflict(after, resolved.Text, before))

/// The guard a save has to consult before it writes a `TweakSaved`: refused
/// while the address has an open, unresolved conflict.
let canSave (log: EventLog) (address: TweakAddress) : Result<unit, string> =
  match hasOpenConflict log address with
  | true -> Error(sprintf "%A has an open conflict; resolve it before saving again" address)
  | false -> Ok()

/// Reproduce a file from `baseSource` by replaying an ordered list of
/// (address, textAfter) writes, exactly the shape `TweakApplied`/
/// `TweakSaved` carry. Re-resolves the address at each step against the
/// EVOLVING source, so a write that landed after a reformat still finds its
/// target the same way the real save did.
let replay (baseSource: string) (ops: (TweakAddress * string) list) : Result<string, string> =
  ops
  |> List.fold
    (fun acc (addr, after) ->
      acc
      |> Result.bind (fun src ->
        match resolve src addr with
        | Error e -> Error(sprintf "replay: address no longer resolves (%A): %A" addr e)
        | Ok resolved -> Ok(replaceRange src resolved.Range after)))
    (Ok baseSource)

// ── undo / redo cursor ──

[<RequireQualifiedAccess>]
type UndoCursor =
  | AtHead
  | At of eventId: int
  /// Asked to go further back than compaction kept. Named honestly instead
  /// of clamping to a value that would be wrong.
  | Compacted of oldestAvailable: int

[<RequireQualifiedAccess>]
module UndoCursor =
  let private undoableIds (events: LoggedEvent list) : int list =
    events
    |> List.choose (fun e ->
      match e.Event with
      | TweakLogEvent.TweakApplied _
      | TweakLogEvent.TweakSaved _ -> Some e.Id
      | _ -> None)

  /// Move one step further into the past: from `AtHead`, the most recent
  /// op; from `At id`, the op just BEFORE `id`; from the oldest op (or
  /// already `Compacted`), report the boundary honestly rather than
  /// pretending there's somewhere further to go.
  let undo (log: EventLog) (cursor: UndoCursor) : UndoCursor =
    let ids = undoableIds log.Events
    match cursor with
    | UndoCursor.Compacted oldest -> UndoCursor.Compacted oldest
    | UndoCursor.AtHead ->
      match List.tryLast ids with
      | Some last -> UndoCursor.At last
      | None -> UndoCursor.AtHead
    | UndoCursor.At id ->
      match ids |> List.tryFindIndex ((=) id) with
      | Some 0 -> UndoCursor.Compacted ids.[0]
      | Some i -> UndoCursor.At ids.[i - 1]
      | None -> UndoCursor.AtHead

  let redo (log: EventLog) (cursor: UndoCursor) : UndoCursor =
    let ids = undoableIds log.Events
    match cursor with
    | UndoCursor.At id ->
      match ids |> List.tryFindIndex ((=) id) with
      | Some i when i + 1 < ids.Length -> UndoCursor.At ids.[i + 1]
      | _ -> UndoCursor.AtHead
    | _ -> cursor

/// Move the cursor back one step AND perform the real rollback: the
/// compound action Ctrl+Z is, in one call. Storage stays append-only —
/// this appends exactly one `RolledBack` event, it never rewrites
/// anything already in the log.
let performUndo (log: EventLog) (cursor: UndoCursor) (currentSource: string) (at: int64) : Result<EventLog * UndoCursor * string, string> =
  match UndoCursor.undo log cursor with
  | UndoCursor.AtHead -> Error "nothing to undo"
  | UndoCursor.Compacted oldest -> Error(sprintf "can't undo past the retained window (oldest available id %d)" oldest)
  | UndoCursor.At id as newCursor ->
    match rollback log id currentSource with
    | Error e -> Error(sprintf "%A" e)
    | Ok(RollbackOutcome.Conflict(wrote, now, before)) ->
      Error(sprintf "the address changed since (wrote %s, now %s, before %s)" wrote now before)
    | Ok(RollbackOutcome.Applied newSource) ->
      let log', _ = EventLog.append log at (TweakLogEvent.RolledBack id)
      Ok(log', newCursor, newSource)

/// Move the cursor forward one step AND perform the reapply: the compound
/// action redo is. Implemented as rolling back the MOST RECENT
/// `RolledBack` that targeted the op the cursor is currently sitting on —
/// `effectOf` flips that rollback's own before/after, so "rolling back a
/// rollback" already means "put the tweak back". Still append-only: this
/// appends a SECOND `RolledBack`, on top of the first, never edits it.
let performRedo (log: EventLog) (cursor: UndoCursor) (currentSource: string) (at: int64) : Result<EventLog * UndoCursor * string, string> =
  match cursor with
  | UndoCursor.At undoneId ->
    let mostRecentUndoOfIt =
      log.Events
      |> List.filter (fun e -> match e.Event with TweakLogEvent.RolledBack t -> t = undoneId | _ -> false)
      |> List.tryLast
    match mostRecentUndoOfIt with
    | None -> Error "nothing to redo"
    | Some rolledBackEvent ->
      match rollback log rolledBackEvent.Id currentSource with
      | Error e -> Error(sprintf "%A" e)
      | Ok(RollbackOutcome.Conflict(wrote, now, before)) ->
        Error(sprintf "the address changed since (wrote %s, now %s, before %s)" wrote now before)
      | Ok(RollbackOutcome.Applied newSource) ->
        let log', _ = EventLog.append log at (TweakLogEvent.RolledBack rolledBackEvent.Id)
        Ok(log', UndoCursor.redo log' cursor, newSource)
  | _ -> Error "nothing to redo"

// ── compaction: bounded growth ──

/// Versioned like an event, not a disposable cache: once compaction has
/// run, `Snapshot` PLUS whatever tail follows it IS the truth for
/// everything before the tail, not merely a speed-up over replaying from
/// scratch. `Fingerprint` is what makes it upcastable, a caller can grade a
/// persisted snapshot the exact same way it grades a log segment before
/// trusting it.
type Snapshot =
  { UpToEventId: int
    Fingerprint: Fingerprint
    Projection: Projection
    /// Origin (address, textBefore) for each compacted `TweakApplied`/
    /// `TweakSaved`, so a `RolledBack` in the tail that targets a
    /// pre-snapshot id can still resolve without keeping that whole event.
    Origins: Map<int, TweakAddress * string> }

[<RequireQualifiedAccess>]
module Snapshot =
  /// The starting snapshot before any compaction has happened. Its
  /// fingerprint is never graded (nothing has decoded it from bytes), so
  /// an empty target hash is a safe placeholder, not a real claim about
  /// any file.
  let empty = { UpToEventId = 0; Fingerprint = Fingerprint.current ""; Projection = Projection.empty; Origins = Map.empty }

/// The projection from a snapshot plus whatever tail comes after it, must
/// equal `project` over the full, uncompacted stream for every address the
/// snapshot+tail can still answer for.
let projectFromSnapshot (snapshot: Snapshot) (tail: LoggedEvent list) : Projection =
  let lookup id =
    match snapshot.Origins |> Map.tryFind id with
    | Some o -> Some o
    | None -> tail |> List.tryFind (fun e -> e.Id = id) |> Option.bind (fun e -> originOf e.Event)
  foldEvents lookup snapshot.Projection tail

/// Named retention rules, as data, see `whyKept` for the query that
/// explains, per event, which rule (if any) is protecting it.
type RetentionPolicy =
  { UndoWindow: int
    MaxEvents: int
    MaxBytes: int64 }

[<RequireQualifiedAccess>]
module RetentionPolicy =
  let defaults : RetentionPolicy =
    { UndoWindow = TweakLogLimits.undoWindow
      MaxEvents = TweakLogLimits.maxEventsPerSession
      MaxBytes = TweakLogLimits.maxBytesPerSession }

[<RequireQualifiedAccess>]
type KeepReason =
  | WithinUndoWindow
  | OpenConflict
  | UnsavedTweak
  | NamedPreset
  | Compactable

/// Why compaction would (or wouldn't) fold this specific event into the
/// snapshot right now, given the CURRENT full-history projection. A pure
/// query, nothing here mutates anything, so a caller (or a test) can ask
/// "why is this still here?" without re-deriving compaction's own logic.
///
/// Scope limit, stated plainly: this checks dirtiness by folding `fullEvents`
/// from EMPTY, not from a real snapshot's `Origins`. That's sound for a
/// FIRST compaction (`fullEvents` really is the whole history) and sound in
/// general for the DIRTY check itself, because a snapshot can never hold a
/// dirty address to begin with, `compact` only ever folds a compactable
/// (already-clean) prefix into one, by construction. What it does NOT get
/// right is a `RolledBack` inside a SECOND round's tail whose target id was
/// already folded into an earlier snapshot: this fold can't see that id's
/// origin (it only knows the tail it was given), so that particular
/// rollback is treated as a no-op for the purposes of THIS must-keep check.
/// `compact` itself resolves `RolledBack` correctly against the real
/// `Snapshot.Origins`, only this standalone diagnostic query has the gap.
let whyKept
  (policy: RetentionPolicy)
  (fullEvents: LoggedEvent list)
  (presetAddresses: Set<TweakAddress>)
  (e: LoggedEvent)
  : KeepReason =
  let undoRetained =
    fullEvents
    |> List.choose (fun ev ->
      match ev.Event with
      | TweakLogEvent.TweakApplied _
      | TweakLogEvent.TweakSaved _ -> Some ev.Id
      | _ -> None)
    |> List.rev
    |> List.truncate policy.UndoWindow
    |> Set.ofList
  match undoRetained.Contains e.Id with
  | true -> KeepReason.WithinUndoWindow
  | false ->
    let p = foldEvents (fun id -> fullEvents |> List.tryFind (fun ev -> ev.Id = id) |> Option.bind (fun ev -> originOf ev.Event)) Projection.empty fullEvents
    match addressOf e.Event with
    | Some a when p.OpenConflicts.Contains a -> KeepReason.OpenConflict
    | Some a when (Projection.dirtySet p).Contains a -> KeepReason.UnsavedTweak
    | Some a when presetAddresses.Contains a -> KeepReason.NamedPreset
    | _ -> KeepReason.Compactable

/// Fold the longest PREFIX of `events` that's safe to compact into
/// `snapshot`, stopping at the first event `whyKept` says must stay,
/// never past it, so compaction can never drop something it must keep
/// while leaving an earlier, less-important copy of it compacted away.
/// Returns the advanced snapshot and the retained tail (everything from
/// the first must-keep event onward, unchanged).
let compact
  (snapshot: Snapshot)
  (events: LoggedEvent list)
  (policy: RetentionPolicy)
  (presetAddresses: Set<TweakAddress>)
  : Snapshot * LoggedEvent list =
  let mustKeep (e: LoggedEvent) = whyKept policy events presetAddresses e <> KeepReason.Compactable
  let splitIndex = events |> List.tryFindIndex mustKeep |> Option.defaultValue events.Length
  match splitIndex with
  | 0 -> snapshot, events
  | _ ->
    let compactedAway, tail = events |> List.splitAt splitIndex
    let lookup id =
      match snapshot.Origins |> Map.tryFind id with
      | Some o -> Some o
      | None -> compactedAway |> List.tryFind (fun e -> e.Id = id) |> Option.bind (fun e -> originOf e.Event)
    let newProjection = foldEvents lookup snapshot.Projection compactedAway
    let newOrigins =
      compactedAway
      |> List.choose (fun e -> originOf e.Event |> Option.map (fun o -> e.Id, o))
      |> List.fold (fun m (id, o) -> Map.add id o m) snapshot.Origins
    let newUpTo = compactedAway |> List.last |> _.Id
    { UpToEventId = newUpTo; Fingerprint = snapshot.Fingerprint; Projection = newProjection; Origins = newOrigins }, tail

/// When compaction is even allowed to run. `OnSessionClose` (the default):
/// while a session is live, the events are the only truth, and it
/// compacts to a versioned snapshot only when the session closes.
/// `LiveOnBudget`: today's behavior, compacts as soon as the budget is
/// crossed, even mid-session.
[<RequireQualifiedAccess>]
type CompactionMode =
  | OnSessionClose
  | LiveOnBudget

/// What `replay` promises. `SageFsWritesOnly` (the default): replay
/// reproduces exactly every range SageFs itself wrote; a user's own edits
/// and reformats are observed (their hashes, and any conflict they cause)
/// but their bytes are never stored, so the log stays small and never
/// records a change SageFs didn't make. `EverythingAsDiffs`: `UserEditObserved`/
/// `ReformatObserved` also carry the file's full text at that point, so
/// replay can reproduce the WHOLE file byte for byte, at the cost of those
/// events counting their full size toward the byte budget like anything
/// else.
[<RequireQualifiedAccess>]
type ReplayScope =
  | SageFsWritesOnly
  | EverythingAsDiffs

/// One named place for every tunable this module reads, instead of a
/// value picked on the spot at each call site.
type TweakLogSettings =
  { CompactionMode: CompactionMode
    ReplayScope: ReplayScope
    Retention: RetentionPolicy }

[<RequireQualifiedAccess>]
module TweakLogSettings =
  let defaults : TweakLogSettings =
    { CompactionMode = CompactionMode.OnSessionClose
      ReplayScope = ReplayScope.SageFsWritesOnly
      Retention = RetentionPolicy.defaults }

/// Whether THIS instant is due for a compaction pass, given `settings`.
/// `OnSessionClose` never says yes, however far over budget the log is,
/// a live session stays fully event-sourced by design; only
/// `closeSession` ever compacts it. `LiveOnBudget` is the budget check
/// this module always had.
let shouldCompact (settings: TweakLogSettings) (encodedBytes: int64) (eventCount: int) : bool =
  match settings.CompactionMode with
  | CompactionMode.OnSessionClose -> false
  | CompactionMode.LiveOnBudget -> eventCount > settings.Retention.MaxEvents || encodedBytes > settings.Retention.MaxBytes

/// Compact unconditionally, whatever `settings.CompactionMode` says — the
/// one moment truth always becomes snapshot-plus-tail, a session ending.
let closeSession
  (snapshot: Snapshot)
  (events: LoggedEvent list)
  (settings: TweakLogSettings)
  (presetAddresses: Set<TweakAddress>)
  : Snapshot * LoggedEvent list =
  compact snapshot events settings.Retention presetAddresses

// ── segment model: WHAT to write/delete. Real file IO plugs in behind this. ──

[<RequireQualifiedAccess>]
type SegmentOp =
  | WriteSnapshot of Snapshot
  | WriteTail of LoggedEvent list
  | DeleteSegment of label: string

type CompactionPlan = { Ops: SegmentOp list }

/// Compact, and describe it as a plan of segments to write/delete. Applying
/// the plan (writing the new snapshot+tail segments, THEN deleting the old
/// ones) is IO and lives behind `SegmentIO` below, this function only ever
/// decides WHAT should happen.
let planCompaction
  (snapshot: Snapshot)
  (events: LoggedEvent list)
  (policy: RetentionPolicy)
  (presetAddresses: Set<TweakAddress>)
  : CompactionPlan * Snapshot * LoggedEvent list =
  let newSnapshot, tail = compact snapshot events policy presetAddresses
  let plan =
    { Ops =
        [ SegmentOp.WriteSnapshot newSnapshot
          SegmentOp.WriteTail tail
          SegmentOp.DeleteSegment "old-snapshot-and-tail" ] }
  plan, newSnapshot, tail

/// The IO seam a real persistence layer plugs into: apply a plan's writes
/// and deletes, reporting whether it committed as a whole. A real
/// implementation writes the new segments, fsyncs, then deletes the old
/// ones, so a crash mid-plan leaves either the pre-plan segments (nothing
/// written yet, or a write in progress that never got renamed in) or the
/// fully-written post-plan segments, never a mix. Nothing in this module
/// calls it; it exists so that layer has a typed shape to implement against.
type SegmentIO = CompactionPlan -> bool

/// The IO seam a file-level rollback falls back to when the structural
/// inverse can't apply cleanly: a three-way merge of "undo this change"
/// onto the file as it stands now, given the common ancestor
/// (ours -> theirs -> baseText -> merged-or-conflict). Real implementation
/// is `git merge-file`; this module never calls it.
type ThreeWayMerge = string -> string -> string -> Result<string, string>

// ── binary encoding: length-prefixed, versioned, CRC per record ──
//
// Reuses SageFs.BinaryPrimitives/Crc32, the same machinery `.sagefm` uses,
// rather than inventing a second wire format. Pure over byte arrays: nothing
// here opens a file.

[<RequireQualifiedAccess>]
module TweakLogFormat =

  let private formatVersion = 1uy

  let private pathStepTag =
    function
    | PathStep.RecordField _ -> 0uy
    | PathStep.TupleItem _ -> 1uy
    | PathStep.ListItem _ -> 2uy
    | PathStep.AppArg _ -> 3uy
    | PathStep.IfCond -> 4uy
    | PathStep.IfThen -> 5uy
    | PathStep.IfElse -> 6uy
    | PathStep.BinOpLeft -> 7uy
    | PathStep.BinOpRight -> 8uy

  let private writePathStep (bw: BinaryWriter) (step: PathStep) =
    BinaryPrimitives.writeU8 bw (pathStepTag step)
    match step with
    | PathStep.RecordField name -> BinaryPrimitives.writeLpString bw name
    | PathStep.TupleItem i
    | PathStep.ListItem i
    | PathStep.AppArg i -> BinaryPrimitives.writeU32 bw (uint32 i)
    | PathStep.IfCond
    | PathStep.IfThen
    | PathStep.IfElse
    | PathStep.BinOpLeft
    | PathStep.BinOpRight -> ()

  let private readPathStep (br: BinaryReader) : PathStep =
    match br.ReadByte() with
    | 0uy -> PathStep.RecordField(BinaryPrimitives.readLpString br)
    | 1uy -> PathStep.TupleItem(int (br.ReadUInt32()))
    | 2uy -> PathStep.ListItem(int (br.ReadUInt32()))
    | 3uy -> PathStep.AppArg(int (br.ReadUInt32()))
    | 4uy -> PathStep.IfCond
    | 5uy -> PathStep.IfThen
    | 6uy -> PathStep.IfElse
    | 7uy -> PathStep.BinOpLeft
    | 8uy -> PathStep.BinOpRight
    | other -> failwithf "unknown PathStep tag %d" other

  let private writeStringList (bw: BinaryWriter) (xs: string list) =
    BinaryPrimitives.writeU32 bw (uint32 xs.Length)
    for x in xs do BinaryPrimitives.writeLpString bw x

  let private readStringList (br: BinaryReader) : string list =
    let n = int (br.ReadUInt32())
    [ for _ in 1 .. n -> BinaryPrimitives.readLpString br ]

  let private writeAddress (bw: BinaryWriter) (a: TweakAddress) =
    writeStringList bw a.ModulePath
    BinaryPrimitives.writeLpString bw a.BindingName
    BinaryPrimitives.writeU32 bw (uint32 a.Path.Length)
    for step in a.Path do writePathStep bw step

  let private readAddress (br: BinaryReader) : TweakAddress =
    let modulePath = readStringList br
    let bindingName = BinaryPrimitives.readLpString br
    let n = int (br.ReadUInt32())
    let path = [ for _ in 1 .. n -> readPathStep br ]
    { ModulePath = modulePath; BindingName = bindingName; Path = path }

  let private eventTag =
    function
    | TweakLogEvent.TweakApplied _ -> 0uy
    | TweakLogEvent.TweakSaved _ -> 1uy
    | TweakLogEvent.UserEditObserved _ -> 2uy
    | TweakLogEvent.ReformatObserved _ -> 3uy
    | TweakLogEvent.HotReloadObserved _ -> 4uy
    | TweakLogEvent.ConflictRaised _ -> 5uy
    | TweakLogEvent.ConflictResolved _ -> 6uy
    | TweakLogEvent.RolledBack _ -> 7uy

  let private writeEvent (bw: BinaryWriter) (e: TweakLogEvent) =
    BinaryPrimitives.writeU8 bw (eventTag e)
    match e with
    | TweakLogEvent.TweakApplied(addr, before, after, hash) ->
      writeAddress bw addr
      BinaryPrimitives.writeLpString bw before
      BinaryPrimitives.writeLpString bw after
      BinaryPrimitives.writeLpString bw hash
    | TweakLogEvent.TweakSaved(addr, before, after, hash, fileHashBefore) ->
      writeAddress bw addr
      BinaryPrimitives.writeLpString bw before
      BinaryPrimitives.writeLpString bw after
      BinaryPrimitives.writeLpString bw hash
      BinaryPrimitives.writeLpString bw fileHashBefore
    | TweakLogEvent.UserEditObserved(addr, textNow, hashNow) ->
      writeAddress bw addr
      BinaryPrimitives.writeLpString bw textNow
      BinaryPrimitives.writeLpString bw hashNow
    | TweakLogEvent.ReformatObserved(before, after) ->
      BinaryPrimitives.writeLpString bw before
      BinaryPrimitives.writeLpString bw after
    | TweakLogEvent.HotReloadObserved(before, after) ->
      BinaryPrimitives.writeLpString bw before
      BinaryPrimitives.writeLpString bw after
    | TweakLogEvent.ConflictRaised(addr, wrote, now, before) ->
      writeAddress bw addr
      BinaryPrimitives.writeLpString bw wrote
      BinaryPrimitives.writeLpString bw now
      BinaryPrimitives.writeLpString bw before
    | TweakLogEvent.ConflictResolved addr -> writeAddress bw addr
    | TweakLogEvent.RolledBack eventId -> BinaryPrimitives.writeU32 bw (uint32 eventId)

  let private readEvent (br: BinaryReader) : TweakLogEvent =
    match br.ReadByte() with
    | 0uy ->
      let addr = readAddress br
      let before = BinaryPrimitives.readLpString br
      let after = BinaryPrimitives.readLpString br
      let hash = BinaryPrimitives.readLpString br
      TweakLogEvent.TweakApplied(addr, before, after, hash)
    | 1uy ->
      let addr = readAddress br
      let before = BinaryPrimitives.readLpString br
      let after = BinaryPrimitives.readLpString br
      let hash = BinaryPrimitives.readLpString br
      let fileHashBefore = BinaryPrimitives.readLpString br
      TweakLogEvent.TweakSaved(addr, before, after, hash, fileHashBefore)
    | 2uy ->
      let addr = readAddress br
      let textNow = BinaryPrimitives.readLpString br
      let hashNow = BinaryPrimitives.readLpString br
      TweakLogEvent.UserEditObserved(addr, textNow, hashNow)
    | 3uy ->
      let before = BinaryPrimitives.readLpString br
      let after = BinaryPrimitives.readLpString br
      TweakLogEvent.ReformatObserved(before, after)
    | 4uy ->
      let before = BinaryPrimitives.readLpString br
      let after = BinaryPrimitives.readLpString br
      TweakLogEvent.HotReloadObserved(before, after)
    | 5uy ->
      let addr = readAddress br
      let wrote = BinaryPrimitives.readLpString br
      let now = BinaryPrimitives.readLpString br
      let before = BinaryPrimitives.readLpString br
      TweakLogEvent.ConflictRaised(addr, wrote, now, before)
    | 6uy -> TweakLogEvent.ConflictResolved(readAddress br)
    | 7uy -> TweakLogEvent.RolledBack(int (br.ReadUInt32()))
    | other -> failwithf "unknown TweakLogEvent tag %d" other

  /// One record's PAYLOAD bytes (version, id, at, then the event), without
  /// the outer length prefix or CRC, which `encodeStream` adds per record.
  let private encodePayload (e: LoggedEvent) : byte[] =
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    BinaryPrimitives.writeU8 bw formatVersion
    BinaryPrimitives.writeU32 bw (uint32 e.Id)
    BinaryPrimitives.writeI64 bw e.At
    writeEvent bw e.Event
    ms.ToArray()

  let private decodePayload (bytes: byte[]) : LoggedEvent =
    use ms = new MemoryStream(bytes)
    use br = new BinaryReader(ms)
    let version = br.ReadByte()
    match version with
    | 1uy ->
      let id = int (br.ReadUInt32())
      let at = br.ReadInt64()
      let ev = readEvent br
      { Id = id; At = at; Event = ev }
    | other -> failwithf "unsupported TweakLog record version %d" other

  /// [u32 payload length][payload][u32 crc32-of-payload], one record.
  let private encodeRecord (e: LoggedEvent) : byte[] =
    let payload = encodePayload e
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    BinaryPrimitives.writeU32 bw (uint32 payload.Length)
    bw.Write payload
    BinaryPrimitives.writeU32 bw (Crc32.computeAll payload)
    ms.ToArray()

  /// Every event, concatenated as records, the exact bytes a crash-safe
  /// append-only file holds.
  let encodeStream (events: LoggedEvent list) : byte[] =
    use ms = new MemoryStream()
    for e in events do
      let record = encodeRecord e
      ms.Write(record, 0, record.Length)
    ms.ToArray()

  /// Decode as many whole, CRC-valid records as `bytes` holds, stopping the
  /// instant one can't be fully formed or its CRC doesn't match, that
  /// remainder is a torn tail (a crash mid-append), and it is NEVER
  /// partially decoded into a corrupt event. Returns the decoded events plus
  /// whether a tail was torn off.
  let decodeStream (bytes: byte[]) : LoggedEvent list * bool =
    let rec loop (offset: int) (acc: LoggedEvent list) =
      match bytes.Length - offset >= 4 with
      | false -> List.rev acc, offset < bytes.Length
      | true ->
        let payloadLen = BitConverter.ToUInt32(bytes, offset) |> int
        let recordLen = 4 + payloadLen + 4
        match payloadLen < 0 || offset + recordLen > bytes.Length with
        | true -> List.rev acc, true
        | false ->
          let payload = bytes.[offset + 4 .. offset + 4 + payloadLen - 1]
          let storedCrc = BitConverter.ToUInt32(bytes, offset + 4 + payloadLen)
          match storedCrc = Crc32.computeAll payload with
          | false -> List.rev acc, true
          | true ->
            let decoded = try Some(decodePayload payload) with _ -> None
            match decoded with
            | Some e -> loop (offset + recordLen) (e :: acc)
            | None -> List.rev acc, true
    loop 0 []

  // ── segments: a fingerprint header in front of the event stream ──

  let private writeFingerprint (bw: BinaryWriter) (fp: Fingerprint) =
    BinaryPrimitives.writeU32 bw (uint32 fp.SchemaVersion)
    BinaryPrimitives.writeU32 bw (uint32 fp.FoldVersion)
    BinaryPrimitives.writeLpString bw fp.TargetHash

  let private readFingerprint (br: BinaryReader) : Fingerprint =
    { SchemaVersion = int (br.ReadUInt32())
      FoldVersion = int (br.ReadUInt32())
      TargetHash = BinaryPrimitives.readLpString br }

  /// A fingerprint header, then the event stream, exactly what a real
  /// `.SageFs/tweaks/<session>.events` file holds on disk.
  let encodeSegment (fingerprint: Fingerprint) (events: LoggedEvent list) : byte[] =
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    writeFingerprint bw fingerprint
    bw.Flush()
    let header = ms.ToArray()
    Array.append header (encodeStream events)

  type DecodedSegment =
    { Grade: LogGrade
      Fingerprint: Fingerprint
      Events: LoggedEvent list
      TornTail: bool }

  /// Read the fingerprint header first and grade it against `current`
  /// before touching a single event byte. An `Impossible` grade is NEVER
  /// folded: the header still decodes (so the grade itself is always
  /// knowable, even for a totally foreign schema), but `Events` comes back
  /// empty rather than risking a decode of bytes this build doesn't
  /// understand the layout of.
  let decodeSegment (current: Fingerprint) (bytes: byte[]) : Result<DecodedSegment, string> =
    try
      use ms = new MemoryStream(bytes)
      use br = new BinaryReader(ms)
      let fp = readFingerprint br
      let grade = Fingerprint.grade current fp
      match grade with
      | LogGrade.Impossible -> Ok { Grade = grade; Fingerprint = fp; Events = []; TornTail = false }
      | LogGrade.Fine
      | LogGrade.Risky ->
        let headerLength = int ms.Position
        let events, tornTail = decodeStream bytes.[headerLength ..]
        Ok { Grade = grade; Fingerprint = fp; Events = events; TornTail = tornTail }
    with ex -> Error ex.Message
