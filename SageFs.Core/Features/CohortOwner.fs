namespace SageFs.Features

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable
open SageFs.Features.CohortLedger

/// The single owner of one cohort's live state (Slice 1,
/// cohort-integration-plan.md D1/D2/D4/D5; template: ManifestOwner.fs).
///
/// It holds the `CohortState<MemberId>` in memory, applies every
/// `CohortCommand` through `Cohort.decide` in arrival order, appends the
/// resulting `LedgerEntry` to the injected `LedgerPort` before replying, and
/// publishes the projected `CohortFrame` via `Interlocked.Exchange` — the
/// same wait-free published-snapshot discipline `SessionManager` uses for
/// `QuerySnapshot` (SessionManager.fs:196-228,699,723). Reads go through
/// `Handle.ReadFrame()`, which dereferences that published pointer directly
/// and never touches the mailbox (D4).
///
/// `'m` is bound to `MemberTable.MemberId` here (D2) — the post-merge stitch
/// Phase 1 item 7 deliberately left open for this shell.
///
/// Landing effects (`Rebase`/`ComputeAffected`/`RunTests`/`FastForward`/
/// `Notify`) are returned by `decide` as data, per D5 — `decide` itself never
/// performs them. Item 14b closes the loop this doc comment used to describe
/// as future work: `handle` now dispatches every effect to an injected
/// `LandingPerformer`, off the mailbox, and posts the typed completion
/// command (`RebaseCompleted` etc.) back to THIS owner once the performer's
/// `Async` resolves — the same `RebuildCompleted` pattern
/// `SessionManager.fs:79-82,911-915` already uses (`Async.Start` off the
/// loop; the mailbox is the only place a completion is ever applied, so
/// completions are serialized in arrival order exactly like every other
/// command).
module CohortOwner =

  type internal Command =
    | Apply of CohortCommand<MemberId> * reply: (Result<CohortEvent<MemberId> list * CohortEffect<MemberId> list, CohortError<MemberId>> -> unit)
    | Flush of AsyncReplyChannel<unit>

  /// The injectable seam for actually PERFORMING a landing effect (item 14b
  /// of sagefs-multiagent-vision.md). `decide` (Cohort.fs) never runs git,
  /// never runs a test — it returns `CohortEffect<'m>` values as data; this
  /// record is how the shell performs them. Every field's argument shape
  /// mirrors what the matching `CohortEffect` case actually carries
  /// (`LandingId`/`ClaimId` unwrapped to their raw `string`, since that's
  /// what a real performer — e.g. the git-backed one in
  /// `SageFs/CohortGit.fs`, item 14a — naturally works with): the loop
  /// unwraps `LandingId` before calling in and re-wraps it when posting the
  /// completion back.
  ///  - `Rebase landingId onto` → `Ok newHeadSha` or `Error conflictFiles`,
  ///    exactly `CohortCommand.RebaseCompleted`'s payload shape.
  ///  - `ComputeAffected landingId baseSha headSha` → the `TestId`s a
  ///    diff between those two shas affects.
  ///  - `RunTests landingId tests` → the FAILING subset (empty = all
  ///    passed), exactly `TestsCompleted`'s payload.
  ///  - `FastForward landingId toSha` → `Ok committedSha` or `Error reason`.
  ///    `Ok` drives `CohortCommand.FastForwardCompleted`; `Error` drives
  ///    `CohortCommand.FastForwardFailed` (roast-6 #7b), which re-enters
  ///    `Cohort.decide` and moves the landing out of `Verifying` — either
  ///    `Blocked(HeadMoved ...)` when the integration head genuinely moved
  ///    concurrently, or back to `Rebasing` (a fresh `Rebase` effect) to
  ///    retry the pipeline when it has not. The landing is never left
  ///    stranded in `Verifying`.
  ///  - `Notify who event` is fire-and-forget: `decide` defines
  ///    `CohortEffect.Notify` but no `decide` case constructs one yet (grep
  ///    confirms zero call sites in Cohort.fs today), so this field exists
  ///    for forward compatibility and is exercised by no production path
  ///    yet.
  type LandingPerformer<'m> = {
    Rebase: string -> string -> Async<Result<string, string list>>
    /// `Ok tests` = the affected set was computed against a trustworthy,
    /// settled integration session. `Error reason` = the session could not be
    /// read reliably (e.g. still rebuilding after the rebase), so the affected
    /// set is unknowable right now. Returning `Ok []` in that case would be
    /// fail-OPEN — an empty affected set lands with nothing verified — so the
    /// dispatcher maps `Error` to `VerificationInconclusive` instead.
    ComputeAffected: string -> string -> string -> Async<Result<TestId list, string>>
    /// `Ok failing` = the run reached a verdict; `failing` is the subset of the
    /// requested tests that did NOT pass (empty = all passed). `Error reason` =
    /// the run could NOT reach a verdict (the session couldn't be trusted enough
    /// to run — e.g. still warming up after the rebase rebuild). The dispatcher
    /// maps `Ok` to `TestsCompleted` and `Error` to `VerificationInconclusive`,
    /// so a "couldn't verify" is never silently reported as "everything failed".
    RunTests: string -> TestId list -> Async<Result<TestId list, string>>
    FastForward: string -> string -> Async<Result<string, string>>
    Notify: 'm -> CohortEvent<'m> -> unit
  }

  module LandingPerformer =
    /// Performs nothing and completes nothing — the exact behavior `handle`
    /// had before item 14b: a `CohortEffect` is returned as data and NOTHING
    /// ever resolves it, so a landing that reaches `Rebasing`/`Verifying`
    /// simply stays there. Implemented with a `TaskCompletionSource` that is
    /// never completed (never a timer, never a blocked thread — just a
    /// continuation parked on an unresolved `Task`), so dispatching an
    /// effect against `stub` is observably identical to never having
    /// dispatched it at all. This is `start`'s default (unchanged public
    /// signature, so every existing caller — including `DaemonMode.fs` — is
    /// unaffected by this item); `startWithPerformer` is the new entry point
    /// a later slice (14c) uses to inject the real `CohortGit`-backed
    /// performer.
    let stub<'m> : LandingPerformer<'m> =
      { Rebase = fun _ _ -> Async.AwaitTask(TaskCompletionSource<Result<string, string list>>().Task)
        ComputeAffected = fun _ _ _ -> Async.AwaitTask(TaskCompletionSource<Result<TestId list, string>>().Task)
        RunTests = fun _ _ -> Async.AwaitTask(TaskCompletionSource<Result<TestId list, string>>().Task)
        FastForward = fun _ _ -> Async.AwaitTask(TaskCompletionSource<Result<string, string>>().Task)
        Notify = fun _ _ -> () }

  type internal OwnerState = {
    Cohort: CohortState<MemberId>
    NextSeq: int64<ledgerSeq>
  }

  /// One session's Pass/Fail/Stale `Cohort.TestId` lists plus its generation
  /// — exactly what `Cohort.SessionSnapshot` needs beyond `Member`/`SessionId`
  /// (which `frameOf` itself supplies, per member). The shell's closure
  /// (`DaemonMode.fs`) is a thin wrapper over
  /// `CohortTestProjection.projectSession` + `generationOf`.
  /// Public (unlike `handle`/`frameOf`): it appears in `start`'s public
  /// signature, the same way `SessionSnapshot` did before this item.
  type SessionTestOutcomes = Cohort.TestId list * Cohort.TestId list * Cohort.TestId list * int64

  /// The read model for `head` (item 13c of sagefs-multiagent-vision.md —
  /// completes item 13a's seam, which item 13a left hardcoded to `noSessions`
  /// because no session->member mapping existed yet). Item 13c's `Join`
  /// change gives every member a `Session: string option`
  /// (`MemberRecord.Session`), so `frameOf` builds the `SessionSnapshot[]`
  /// itself: one row per member with `Session = Some sid`, its outcomes
  /// fetched fresh via `getSessionTestOutcomes sid`. A member with
  /// `Session = None` contributes no row — it is a full member, just not
  /// attributed to any checkout's tests. The integration session
  /// (`Member = None`, row 0, Cohort.fs's `SessionSnapshot` doc) is item 14's
  /// concern, not built here.
  ///
  /// `getSessionTestOutcomes` is called fresh (once per bound member) on
  /// every `frameOf`, so the matrix reflects current per-session test state
  /// each time a cohort command lands or the owner (re)starts — NOT
  /// continuously on every test tick; a tick-driven refresh is a later item
  /// (see `handle`'s call site below).
  let internal frameOf
    (getSessionTestOutcomes: string -> SessionTestOutcomes)
    (head: LedgerHead<MemberId>)
    : CohortFrame<MemberId> =
    let snapshots =
      head.State.Members
      |> Map.toArray
      |> Array.choose (fun (m, record) ->
        match record.Session with
        | None -> None
        | Some sid ->
          let passing, failing, stale, gen = getSessionTestOutcomes sid
          Some {
            Member = Some m
            SessionId = sid
            Generation = gen
            PassingTests = passing
            FailingTests = failing
            StaleTests = stale
          })
    project head snapshots

  let private notify (logger: Utils.ILogger) (reply: Result<CohortEvent<MemberId> list * CohortEffect<MemberId> list, CohortError<MemberId>> -> unit) (result: Result<CohortEvent<MemberId> list * CohortEffect<MemberId> list, CohortError<MemberId>>) =
    try reply result
    with ex -> logger.LogWarning(sprintf "[cohort-owner] Commit callback threw: %s" ex.Message)

  /// Posts an effect-completion command back onto THIS owner's own mailbox
  /// (`post` is `inbox.Post`, wired in by `start`/`startWithPerformer`) — the
  /// exact `RebuildCompleted` shape `SessionManager.fs:79-82` uses. `decide`
  /// (Cohort.fs) already refuses a completion that is not at the front of
  /// the queue, or whose `onto` no longer matches `IntegrationHead`
  /// (`requireAtFrontOfQueue`, the `HeadMoved` check in
  /// `FastForwardCompleted`) — so a completion that arrives late (e.g. the
  /// old worker of a superseded landing) is rejected as a normal `Error`
  /// here, not a crash: log it and move on, per item 14b's guidance that no
  /// extra fencing is needed beyond what `decide` already enforces.
  let private postCompletion (logger: Utils.ILogger) (post: Command -> unit) (cmd: CohortCommand<MemberId>) : unit =
    post (
      Command.Apply(
        cmd,
        function
        | Ok _ -> ()
        | Error err ->
          logger.LogInfo(sprintf "[cohort-owner] landing completion %A refused (stale or out-of-order): %A" cmd err)
      )
    )

  /// Performs every effect `decide` returned, OFF the mailbox: each effect
  /// gets its own `Async.Start`'d worker that calls the matching
  /// `LandingPerformer` function and posts the typed completion command back
  /// via `postCompletion` once it resolves. The mailbox never awaits git or
  /// a test run inline — only `postCompletion`'s re-entry into `Command.Apply`
  /// touches the single writer, so concurrent effects from concurrent
  /// landings (there are none in v1's strictly-serial queue, but a withdraw
  /// racing a rebase is exactly this shape) are still serialized in
  /// arrival order by the mailbox itself.
  let private dispatchLandingEffects
    (logger: Utils.ILogger)
    (performer: LandingPerformer<MemberId>)
    (post: Command -> unit)
    (effects: CohortEffect<MemberId> list)
    : unit =
    let complete = postCompletion logger post
    for effect in effects do
      // Landing-effect tracing: each effect dispatched + (below) its result is
      // logged, so a landing's actual progress is visible in the daemon log —
      // the state transitions themselves only fire as SSE events, so without
      // this a stalled landing is invisible to anyone reading the log.
      logger.LogInfo(sprintf "[cohort-owner] dispatch %A" effect)
      match effect with
      | CohortEffect.Rebase(LandingId id, onto) ->
        Async.Start(
          async {
            let! result = performer.Rebase id onto
            logger.LogInfo(sprintf "[cohort-owner] Rebase(%s onto %s) -> %A" id onto result)
            complete (CohortCommand.RebaseCompleted(LandingId id, result))
          }
        )
      | CohortEffect.ComputeAffected(LandingId id, baseSha, headSha) ->
        Async.Start(
          async {
            match! performer.ComputeAffected id baseSha headSha with
            | Ok tests ->
              logger.LogInfo(sprintf "[cohort-owner] ComputeAffected(%s) -> %d tests" id (List.length tests))
              complete (CohortCommand.AffectedComputed(LandingId id, tests))
            | Error reason ->
              // The affected set could not be computed against a trustworthy
              // session. Never fall through to `AffectedComputed []` (which would
              // land the change with nothing verified) — report it as
              // inconclusive so `decide` un-jams the queue for a resubmission.
              logger.LogWarning(
                sprintf "[cohort-owner] ComputeAffected for landing %s could not read a trustworthy session: %s — re-entering Cohort.decide via VerificationInconclusive" id reason
              )
              complete (CohortCommand.VerificationInconclusive(LandingId id, reason))
          }
        )
      | CohortEffect.RunTests(LandingId id, tests) ->
        Async.Start(
          async {
            match! performer.RunTests id tests with
            | Ok failing ->
              logger.LogInfo(sprintf "[cohort-owner] RunTests(%s) -> %d failing" id (List.length failing))
              complete (CohortCommand.TestsCompleted(LandingId id, failing))
            | Error reason ->
              // The verifier could not reach a verdict (e.g. the integration
              // session was still warming up after the rebase rebuild). Report
              // it as INCONCLUSIVE — never as "all tests failed" — so `decide`
              // records `Blocked(Inconclusive)` and un-jams the queue instead of
              // permanently stranding it behind a false test failure.
              logger.LogWarning(
                sprintf "[cohort-owner] RunTests for landing %s could not verify: %s — re-entering Cohort.decide via VerificationInconclusive" id reason
              )
              complete (CohortCommand.VerificationInconclusive(LandingId id, reason))
          }
        )
      | CohortEffect.FastForward(LandingId id, toSha) ->
        Async.Start(
          async {
            let! result = performer.FastForward id toSha
            logger.LogInfo(sprintf "[cohort-owner] FastForward(%s -> %s) -> %A" id toSha result)
            match result with
            | Ok committedSha -> complete (CohortCommand.FastForwardCompleted(LandingId id, committedSha))
            | Error reason ->
              // Roast-6 #7b closed the gap this comment used to describe:
              // `Cohort.decide` now has a real completion command for a
              // FastForward infra failure (`FastForwardFailed`), which
              // re-enters the state machine — reapplying the same
              // Property-11 HeadMoved guard `FastForwardCompleted` uses, or
              // (when the head has not moved) retrying the whole
              // rebase -> verify -> fast-forward pipeline from `Rebasing`.
              // The landing is never left stranded in `Verifying` again.
              logger.LogWarning(
                sprintf "[cohort-owner] FastForward for landing %s failed: %s — re-entering Cohort.decide via FastForwardFailed" id reason
              )
              complete (CohortCommand.FastForwardFailed(LandingId id, reason))
          }
        )
      | CohortEffect.Notify(who, event) ->
        try performer.Notify who event
        with ex -> logger.LogWarning(sprintf "[cohort-owner] Notify performer threw: %s" ex.Message)

  let internal handle
    (logger: Utils.ILogger)
    (ledger: LedgerPort<MemberId>)
    (clock: unit -> DateTime)
    (entropy: unit -> byte[])
    (getSessionTestOutcomes: string -> SessionTestOutcomes)
    (publish: CohortFrame<MemberId> -> unit)
    (publishState: CohortState<MemberId> -> unit)
    (publishEvents: CohortEvent<MemberId> list -> unit)
    (performer: LandingPerformer<MemberId>)
    (post: Command -> unit)
    (owner: OwnerState)
    (command: Command)
    : Async<OwnerState> =
    async {
      match command with
      | Command.Apply(cmd, reply) ->
        let now = clock ()
        let bytes = entropy ()
        match decide now bytes owner.Cohort cmd with
        | Ok(newState, events, effects) ->
          let seq = owner.NextSeq
          ledger.Append { Seq = seq; Clock = now; Entropy = bytes; Command = cmd; Events = events }
          publish (frameOf getSessionTestOutcomes { Seq = seq; State = newState })
          // `CohortFrame` (Cohort.fs's `project`) does not carry
          // `Landings`/`Queue`/`IntegrationHead` — it projects only
          // members/claims/the test matrix. Landing progress therefore has
          // no read model yet; `publishState` is CohortOwner-local
          // observability (mirrors `publish`'s wait-free discipline) so a
          // caller — today, only this item's tests — can watch a landing
          // actually advance. It is NOT a second source of truth: it is
          // always `newState`, the exact value the ledger just recorded.
          publishState newState
          // Item 15a: fire the events this command produced AFTER the frame
          // and state are published, so a subscriber (McpServer's SSE
          // wiring) that reacts to an event by calling `ReadCohortState()`/
          // `ReadFrame()` always observes the state the event describes,
          // never the one before it.
          match events with
          | [] -> ()
          | _ -> publishEvents events
          notify logger reply (Ok(events, effects))
          dispatchLandingEffects logger performer post effects
          return { Cohort = newState; NextSeq = seq + 1L<ledgerSeq> }
        | Error err ->
          notify logger reply (Error err)
          return owner
      | Command.Flush reply ->
        reply.Reply ()
        return owner
    }

  /// A running owner for one cohort.
  type Handle
    internal
    (
      mailbox: MailboxProcessor<Command>,
      readFrame: unit -> CohortFrame<MemberId>,
      readCohortState: unit -> CohortState<MemberId>,
      events: IEvent<CohortEvent<MemberId> list>
    ) =
    /// Apply a command; completes once it is on the ledger (or refused).
    member _.Commit(cmd: CohortCommand<MemberId>) : Task<Result<CohortEvent<MemberId> list * CohortEffect<MemberId> list, CohortError<MemberId>>> =
      mailbox.PostAndAsyncReply(fun ch -> Command.Apply(cmd, ch.Reply)) |> Async.StartAsTask

    /// Queue a command without waiting. `onApplied` runs on the owner once it
    /// is applied (or refused).
    member _.Post(cmd: CohortCommand<MemberId>, onApplied: Result<CohortEvent<MemberId> list * CohortEffect<MemberId> list, CohortError<MemberId>> -> unit) : unit =
      mailbox.Post(Command.Apply(cmd, onApplied))

    /// The cohort's current read model. Wait-free (D4): dereferences the
    /// published frame pointer directly and never posts to the mailbox.
    member _.ReadFrame() : CohortFrame<MemberId> = readFrame ()

    /// The raw `CohortState` — `Landings`/`Queue`/`IntegrationHead`, none of
    /// which `CohortFrame` (Cohort.fs's `project`) currently projects.
    /// Wait-free, same discipline as `ReadFrame`. Exists so a landing's
    /// actual progress (item 14b — the effect-dispatch loop this Handle now
    /// drives) is observable without a mailbox round-trip; today's only
    /// consumer is this item's own tests, but any future landing-status UI
    /// reads from here rather than reaching into the mailbox.
    member _.ReadCohortState() : CohortState<MemberId> = readCohortState ()

    /// Fires once per successfully-applied command, carrying every
    /// `CohortEvent` that command produced (item 15a — the seam McpServer's
    /// SSE wiring subscribes to for `claim_changed`/`landing_changed`).
    /// Never fires for a refused command (`decide` returned `Error`) or for
    /// a command whose `events` list is empty (`Tick` with nothing expired,
    /// `ObserveSave` with no violation). By the time a subscriber observes
    /// this, `ReadFrame()`/`ReadCohortState()` already reflect the state the
    /// events describe (`handle` publishes both before firing this).
    member _.Events : IEvent<CohortEvent<MemberId> list> = events

    /// Completes once every command queued before it has been applied.
    member _.Flush() : Task =
      mailbox.PostAndAsyncReply Command.Flush |> Async.StartAsTask :> Task

    interface IDisposable with
      member _.Dispose() = (mailbox :> IDisposable).Dispose()

    interface IAsyncDisposable with
      member _.DisposeAsync() =
        (mailbox :> IDisposable).Dispose()
        ValueTask.CompletedTask

  /// Production entropy: 32 bytes from the OS CSPRNG. Called only here, at
  /// the shell — never from inside `Cohort.decide` (§7.1).
  let productionEntropy () : byte[] = RandomNumberGenerator.GetBytes 32

  /// Start the owner for one cohort's ledger. `clock`/`entropy` are injected
  /// (production defaults: `DateTime.UtcNow` and `productionEntropy`) so
  /// tests can drive `decide` with deterministic values — this shell is
  /// where real time and randomness enter; the pure core never reads them.
  /// `getSessionTestOutcomes` is the item-13c seam (completing item 13a's,
  /// which had no session->member mapping to read from): the shell's own
  /// per-session test-outcome lookup, called once per session-bound member
  /// on every frame this owner publishes (`frameOf`). Tests that don't care
  /// about the matrix pass `fun _ -> ([], [], [], 0L)`, matching Slice 1's
  /// original hardcoded (empty-matrix) behavior.
  /// Startup state is `Cohort.replay (ledger.ReadAll())`: the ledger is the
  /// only source of truth, so restarting the owner over the same ledger
  /// reconstructs an identical `CohortState`/`CohortFrame`.
  ///
  /// `step` (hence `handle`) is built INSIDE the `MailboxProcessor.Start`
  /// lambda, not before it, because `handle` needs `inbox.Post` itself as
  /// its completion-posting callback (item 14b) — `inbox` only exists once
  /// the mailbox's own body is running.
  let private startCore
    (logger: Utils.ILogger)
    (ledger: LedgerPort<MemberId>)
    (clock: unit -> DateTime)
    (entropy: unit -> byte[])
    (getSessionTestOutcomes: string -> SessionTestOutcomes)
    (performer: LandingPerformer<MemberId>)
    : Handle =
    let entries = ledger.ReadAll ()
    let head = replayHead entries
    let initialNextSeq =
      match entries with
      | [] -> 0L<ledgerSeq>
      | _ -> head.Seq + 1L<ledgerSeq>
    let frameRef = ref (frameOf getSessionTestOutcomes head)
    let publish (frame: CohortFrame<MemberId>) =
      Interlocked.Exchange(frameRef, frame) |> ignore
    let stateRef = ref head.State
    let publishState (state: CohortState<MemberId>) =
      Interlocked.Exchange(stateRef, state) |> ignore
    // Item 15a: a plain .NET event, not a wait-free published ref — unlike
    // `publish`/`publishState` this is a genuine push (SSE subscribers react
    // to it), so ordinary `Event<'T>.Trigger` (synchronous fan-out to
    // `Add`-ed handlers, called from the mailbox loop) is the right shape;
    // there is nothing to dereference wait-free here.
    let cohortEvents = Event<CohortEvent<MemberId> list>()
    let mailbox =
      MailboxProcessor.Start(fun inbox ->
        let step =
          ResilientActor.wrapLoop logger "cohort-owner"
            (handle logger ledger clock entropy getSessionTestOutcomes publish publishState cohortEvents.Trigger performer inbox.Post)
        let rec loop owner = async {
          let! command = inbox.Receive()
          let! next = step owner command
          return! loop next
        }
        loop { Cohort = head.State; NextSeq = initialNextSeq })
    new Handle(mailbox, (fun () -> frameRef.Value), (fun () -> stateRef.Value), cohortEvents.Publish)

  /// Start the owner for one cohort's ledger. `clock`/`entropy` are injected
  /// (production defaults: `DateTime.UtcNow` and `productionEntropy`) so
  /// tests can drive `decide` with deterministic values — this shell is
  /// where real time and randomness enter; the pure core never reads them.
  /// `getSessionTestOutcomes` is the item-13c seam (completing item 13a's,
  /// which had no session->member mapping to read from): the shell's own
  /// per-session test-outcome lookup, called once per session-bound member
  /// on every frame this owner publishes (`frameOf`). Tests that don't care
  /// about the matrix pass `fun _ -> ([], [], [], 0L)`, matching Slice 1's
  /// original hardcoded (empty-matrix) behavior.
  ///
  /// Unchanged signature (item 14b): landing effects are dispatched against
  /// `LandingPerformer.stub`, which performs nothing and completes nothing
  /// — identical, observably, to `decide`'s effects going unperformed
  /// altogether, exactly as before this item. Every existing caller
  /// (`DaemonMode.fs`, `CohortOwnerTests.fs`) is unaffected. Use
  /// `startWithPerformer` to inject a performer that actually runs landing
  /// effects (the real `CohortGit`-backed one is a later slice's wiring).
  let start
    (logger: Utils.ILogger)
    (ledger: LedgerPort<MemberId>)
    (clock: unit -> DateTime)
    (entropy: unit -> byte[])
    (getSessionTestOutcomes: string -> SessionTestOutcomes)
    : Handle =
    startCore logger ledger clock entropy getSessionTestOutcomes LandingPerformer.stub

  /// Same as `start`, plus an injected `LandingPerformer` that actually runs
  /// landing effects — this is the keystone item 14b adds: without a real
  /// performer, a landing decide moves into `Rebasing`/`Verifying` and never
  /// progresses past it. Tests drive the whole landing state machine
  /// end-to-end with a deterministic FAKE performer (no real git/sessions);
  /// a later slice passes the real `CohortGit`-backed one from `DaemonMode.fs`.
  let startWithPerformer
    (logger: Utils.ILogger)
    (ledger: LedgerPort<MemberId>)
    (clock: unit -> DateTime)
    (entropy: unit -> byte[])
    (getSessionTestOutcomes: string -> SessionTestOutcomes)
    (performer: LandingPerformer<MemberId>)
    : Handle =
    startCore logger ledger clock entropy getSessionTestOutcomes performer

  /// Roast-6 #7a: the daemon-held content-addressed test-result cache for
  /// landing verification (§5.4) used to be a bare `ref`, read-modify-written
  /// from the `RunTests` performer in `DaemonMode.fs`. That was safe only
  /// because v1's landing queue is strictly serial (`Cohort.fs`'s `Queue`
  /// doc comment: only `Queue.Head` may be `Rebasing`/`Verifying`), so at
  /// most one `RunTests` call is ever in flight — correctness rested on that
  /// invariant holding forever, not on anything structural. This module
  /// gives the cache a single owner (the same `MailboxProcessor`
  /// single-writer discipline `SessionManager`/`ManifestOwner` already use)
  /// so a `RunTests` performer talks to it via `Verify` — a message, not a
  /// shared mutable cell — and correctness no longer depends on the queue
  /// staying serial.
  module LandingCacheOwner =
    open SageFs.Features.LiveTesting

    type internal Command =
      | Verify of
          inputHashOf: (TestId -> string option) *
          sessionId: string *
          tests: TestId list *
          runMisses: (TestId list -> Async<Result<TestId list, string>>) *
          reply: AsyncReplyChannel<Result<TestId list, string>>

    /// A running owner for one daemon's landing-verification cache.
    type Handle internal (mailbox: MailboxProcessor<Command>) =
      /// Cache-aware verification (`LandingCache.verify`), run through the
      /// single owner instead of a shared `ref`. Queues behind any
      /// in-flight `Verify` exactly like every other single-mailbox owner
      /// in this codebase — the ordering guarantee the old `ref` only had
      /// by accident, this has by construction.
      member _.Verify
        (inputHashOf: TestId -> string option)
        (sessionId: string)
        (tests: TestId list)
        (runMisses: TestId list -> Async<Result<TestId list, string>>)
        : Task<Result<TestId list, string>> =
        mailbox.PostAndAsyncReply(fun reply -> Command.Verify(inputHashOf, sessionId, tests, runMisses, reply))
        |> Async.StartAsTask

      interface IDisposable with
        member _.Dispose() = (mailbox :> IDisposable).Dispose()

      interface IAsyncDisposable with
        member _.DisposeAsync() =
          (mailbox :> IDisposable).Dispose()
          ValueTask.CompletedTask

    /// Start a fresh, empty landing-cache owner. The cache is in-memory
    /// only — unchanged durability from the `ref` it replaces: losing it on
    /// a daemon restart just means the next landing re-verifies from
    /// scratch, exactly as before.
    let start (logger: Utils.ILogger) : Handle =
      let handle (cache: TestResultCache) (command: Command) : Async<TestResultCache> =
        async {
          match command with
          | Command.Verify(inputHashOf, sessionId, tests, runMisses, reply) ->
            // Every exception path still replies exactly once — a failed
            // `runMisses` must never leave the caller's
            // `PostAndAsyncReply` hanging, and must never poison this
            // owner's own cache with a half-applied result.
            try
              let! newCache, result = LandingCache.verify cache inputHashOf sessionId tests runMisses
              reply.Reply result
              return newCache
            with ex ->
              reply.Reply(Error(sprintf "landing cache verify threw: %s" ex.Message))
              return cache
        }
      let mailbox =
        MailboxProcessor<Command>.Start(fun inbox ->
          let step = ResilientActor.wrapLoop logger "landing-cache-owner" handle
          let rec loop cache = async {
            let! command = inbox.Receive()
            let! next = step cache command
            return! loop next
          }
          loop TestResultCache.empty)
      new Handle(mailbox)
