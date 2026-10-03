/// One cohort owner PER SCOPE, so one daemon serves many repositories.
///
/// ## Why this exists
///
/// A cohort is about ONE repository, and `join_cohort` has always derived its scope
/// from the caller's `working_directory` — so two agents in two repositories were
/// already meant to get two cohorts with two conductor seats. The LEDGER was
/// already scope-keyed for exactly that reason (`CohortLedger.LedgerPort`: every
/// operation names its cohort, and the store keeps different scopes apart).
///
/// What was missing was the OWNER. `DaemonMode.fs` started one `CohortOwner`, bound
/// at startup to the scope of the directory the daemon was launched from, and every
/// command was dispatched through it. So an agent in another repository computed a
/// correct scope, built a correct cohort command, and had it refused as a scope
/// collision — against a cohort the caller never wanted. The refusal text was even
/// right ("a scope collision, not a permissions problem"); the fault was that ONE
/// owner cannot represent several cohorts.
///
/// This registry is that missing piece: `ownerFor` starts an owner for a scope the
/// first time that scope is asked for, and returns the SAME owner every time after.
/// Two repositories therefore get two owners, two conductor seats and two ledgers,
/// from one daemon process.
///
/// ## Why it is safe
///
/// Each `CohortOwner` is already self-contained: `startCore` reads only its own
/// scope's rows out of the shared ledger (`ledger.ReadAll scope`) and keeps its own
/// `stateRef`/`frameRef`. So two owners over one scope-keyed store do not share
/// mutable state — they share a file, filtered by scope. The registry adds no
/// ordering of its own: creation is guarded by a lock and each owner keeps its own
/// mailbox.
///
/// ## What it deliberately does NOT do
///
/// It does not make one owner serve several scopes, and it does not pick a "best"
/// owner for a caller. A command is about exactly one scope, and it is dispatched to
/// that scope's owner. Anything else would reintroduce the collision this removes.

module SageFs.Features.CohortOwners

open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Features
open SageFs.Features.CohortLedger
open SageFs.Features.CohortOwner
open System

/// THE registry contract: how to start an owner for a scope that has none yet.
///
/// Injected rather than referenced so this module stays pure and a test can hand it a
/// factory with no daemon, no ledger and no process behind it.
type Factory = CohortScope -> CohortOwner.Handle

/// A set of cohort owners, one per scope, created on demand.
///
/// NOT cached per scope in a way that can go stale: an owner lives for the process,
/// exactly as the single owner used to. `TryFind` is for callers that need to ask
/// whether a cohort already exists without creating one — the dashboard's cohort
/// panel, for instance, which should show the cohorts that exist rather than mint an
/// empty one per repository it hears about.
///
/// `onOwnerStarted` exists because owners are created LAZILY. A subscriber that wanted
/// every cohort's events had no way to attach one: looping the scopes at startup sees
/// only the cohorts that already exist, so the second repository's panel would stay
/// stale forever. The hook fires as each owner appears, so a subscriber registered
/// once covers every cohort the daemon will ever serve.
type Owners =
  { /// The owner for `scope`, started with `factory` if this is the first request for it.
    OwnerFor: CohortScope -> CohortOwner.Handle
    /// The scopes that have an owner, sorted. Never creates one.
    Scopes: unit -> CohortScope list
    /// The owner for `scope` if one already exists. Never creates one.
    TryFind: CohortScope -> CohortOwner.Handle option
    /// Called once for each owner as it is created, with the scope it belongs to. Set this
    /// BEFORE the first `OwnerFor`, or the scopes created earlier are missed.
    SetOnOwnerStarted: (CohortScope -> CohortOwner.Handle -> unit) -> unit
    /// Dispose the owner for `scope` and forget it, so the next request for it starts a
    /// fresh one from the ledger. This is how a scope that no longer has work stops costing
    /// a mailbox: an owner is not a scarce resource, but an unbounded set of them is a
    /// leak in a daemon meant to run for months.
    ///
    /// Returns whether there WAS one, so a caller can report an eviction it did not need to
    /// perform rather than claiming a reclaim it never made.
    ///
    /// The disposed handle is NOT used again — `decide` compares a command's scope against
    /// `handle.Scope`, which the disposed mailbox can no longer serve, so a command arriving
    /// afterwards is answered by the fresh owner instead of silently vanishing into a dead
    /// mailbox.
    Evict: CohortScope -> bool
    /// Dispose EVERY owner. The daemon's shutdown path: an owner holds a `MailboxProcessor`,
    /// which is a live object with a queue, so leaving them undisposed keeps the process's
    /// work-alive set populated after the daemon has stopped serving.
    ///
    /// Named rather than hidden behind an interface: the daemon already holds `DisposeAll`
    /// beside the other owner operations, so calling it is the same kind of act as
    /// `Evict`. Nothing here implements `IDisposable` — composition, not inheritance, and
    /// no `use` binding that would dispose the registry at an unrelated scope's end.
    DisposeAll: unit -> unit }

/// What a caller wires into an `McpContext`.
///
/// WHY A WRAPPER: `Owners` is a whole registry, and a record field typed
/// `Owners option` forces every construction site that never mentions cohorts to write
/// `= None`. There are ~37 of those, almost all tool tests, and asking each to state a
/// fact about cohorts is noise that buries the sites which must actually answer it.
/// This says the same thing in a form where "not wired" is the DEFAULT, so a caller that
/// says nothing compiles unchanged.
[<RequireQualifiedAccess>]
type Wiring =
  /// No cohort support at all — cohort tools report a structured error rather than
  /// throwing. Every unit test that predates cohort support, and the tests that are
  /// about something else entirely.
  ///
  /// Named `Unwired` rather than `None` so a caller cannot confuse "no registry" with
  /// "an `option` that happens to be empty" — the two read identically in a match and
  /// mean different things.
  | Unwired
  /// A registry of one owner per scope, plus the scope the DAEMON started in. The
  /// second is the fallback for a caller that named no directory, and it is what a
  /// caller with no directory is asking about.
  | Wired of Owners * own: CohortScope
  /// Exactly ONE owner, for a caller that has no registry and never wants one — a
  /// test that starts a real owner and hands it to the context. Distinct from `Wired`
  /// because it makes no claim to serve several scopes, and `OwnerFor` on it is the
  /// owner itself rather than a lookup that could mint another.
  | Single of owner: CohortOwner.Handle * own: CohortScope

  /// The default, so a construction site that omits the field still compiles.
  static member Default = Wiring.Unwired

  /// The registry, when one is wired.
  member this.Owners : Owners option =
    match this with
    | Wired(owners, _) -> Some owners
    | _ -> None

  /// The owner for `scope`, when this wiring has one.
  member this.OwnerFor (scope: CohortScope) : CohortOwner.Handle option =
    match this with
    | Wired(owners, _) -> Some(owners.OwnerFor scope)
    | Single(owner, own) -> if scope = own then Some owner else None
    | Unwired -> None

  /// The scope the daemon started in, which is what a caller naming no directory means.
  member this.Own : CohortScope option =
    match this with
    | Wired(_, own) -> Some own
    | Single(_, own) -> Some own
    | Unwired -> None

/// The seam the registry needs to create an owner, given the daemon's own wiring.
///
/// Kept as a record so `DaemonMode.fs` supplies the real ledger, clock, entropy,
/// session-test-outcome reader and landing performer once, and this module stays free
/// of any dependency on them.
type Deps =
  { Ledger: CohortLedger.LedgerPort<MemberId>
    Clock: unit -> System.DateTime
    Entropy: unit -> byte[]
    GetSessionTestOutcomes: string -> CohortOwner.SessionTestOutcomes
    Performer: CohortOwner.LandingPerformer<MemberId>
    Logger: SageFs.Utils.ILogger }

/// Build the factory an `Owners` uses, from the daemon's dependencies.
///
/// Every scope's owner shares ONE scope-keyed ledger — that is the point: the store
/// keeps the cohorts apart by scope, so the owners need not each own a file.
let factoryOf (deps: Deps) : Factory =
  fun scope ->
    CohortOwner.startWithPerformer
      deps.Logger
      scope
      deps.Ledger
      deps.Clock
      deps.Entropy
      deps.GetSessionTestOutcomes
      deps.Performer

/// An empty set of owners, given the factory that starts one for a scope that has none
/// yet. Pure: no daemon, no clock, no process is referenced here.
let create (factory: Factory) : Owners =
  let owners = System.Collections.Concurrent.ConcurrentDictionary<CohortScope, CohortOwner.Handle>()
  // A mutable CELL rather than a ref in the record: the record is built after this, and the
  // daemon registers the hook before the first `OwnerFor`. `Volatile` so a hook set on one
  // thread is visible to the thread that later creates an owner.
  //
  // The hook DEFAULTs to a no-op. A registry is used by callers that never register one —
  // the dashboard and most tests only ever ask for an owner — and a
  // `Unchecked.defaultof` function invoked on the FIRST `OwnerFor` would be a
  // NullReferenceException on the commonest call there is. Registering a hook is opt-in;
  // omitting it is not a crash.
  let started = ref (fun (_: CohortScope) (_: CohortOwner.Handle) -> ())
  let notified = System.Collections.Concurrent.ConcurrentDictionary<CohortScope, bool>()

  let disposeOf (h: CohortOwner.Handle) =
    // `Handle` is `IDisposable`; disposing it releases the mailbox, and it does not throw if
    // the owner is already gone.
    try (h :> IDisposable).Dispose() with _ -> ()

  { OwnerFor =
      fun scope ->
        // `GetOrAdd` may run `factory` more than once under contention, so exactly one
        // Handle per scope is PUBLISHED and every caller gets that one. `factory` must
        // therefore be free of side effects beyond starting an owner — it is called for
        // a scope that has none, and a losing call's owner is simply not published.
        let owner = owners.GetOrAdd(scope, factory)
        // Fire the hook exactly ONCE per scope, for the call that created the owner.
        // Firing on every `OwnerFor` would attach another subscriber per request, so one
        // cohort change would redraw the dashboard N times after N requests.
        if notified.TryAdd(scope, true) then
          (!started) scope owner
        owner
    Scopes = fun () -> owners.Keys |> Seq.toList |> List.sort
    TryFind = fun scope ->
      match owners.TryGetValue scope with
      | true, h -> Some h
      | _ -> None
    SetOnOwnerStarted = fun f -> started.Value <- f
    Evict =
      fun scope ->
        // `TryRemove` is the atomic part: exactly one caller can win the removal, so a
        // scope is never disposed twice, and only the winner reports the eviction.
        match owners.TryRemove scope with
        | true, h ->
          // Forget that the hook fired, so a scope rebuilt after eviction subscribes AGAIN.
          // Leaving the marker would mean the rebuilt owner has no subscriber, and that
          // cohort's panel would silently stop updating — the exact "stale UI" this work
          // exists to prevent, arriving by the back door.
          match notified.TryRemove scope with
          | true, _ -> ()
          | _ -> ()
          disposeOf h
          true
        | _ -> false
    DisposeAll =
      fun () ->
        // Snapshot first: disposing while enumerating the live dictionary would mutate it
        // under the enumerator.
        let snapshot = owners.ToArray()
        owners.Clear()
        snapshot |> Array.iter (fun kv -> disposeOf kv.Value) }

/// `Owners` IS its own lifetime control: a daemon that holds one in a `use` disposes
/// every owner it started when it shuts down, with no separate call to remember — and
/// `Evict` handles the scopes that go away while the daemon is still running.