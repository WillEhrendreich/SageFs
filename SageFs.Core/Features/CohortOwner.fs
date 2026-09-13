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
/// Landing effects (`Rebase`/`ComputeAffected`/`RunTests`/`FastForward`) are
/// returned to the caller as data, per D5 — this actor never performs them.
/// A later slice wires a performer that runs them and posts the typed
/// completion commands (`RebaseCompleted` etc.) back to this owner, the same
/// `RebuildCompleted` pattern `SessionManager.fs:79-82` already uses.
module CohortOwner =

  type internal Command =
    | Apply of CohortCommand<MemberId> * reply: (Result<CohortEvent<MemberId> list * CohortEffect<MemberId> list, CohortError<MemberId>> -> unit)
    | Flush of AsyncReplyChannel<unit>

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

  let internal handle
    (logger: Utils.ILogger)
    (ledger: LedgerPort<MemberId>)
    (clock: unit -> DateTime)
    (entropy: unit -> byte[])
    (getSessionTestOutcomes: string -> SessionTestOutcomes)
    (publish: CohortFrame<MemberId> -> unit)
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
          notify logger reply (Ok(events, effects))
          return { Cohort = newState; NextSeq = seq + 1L<ledgerSeq> }
        | Error err ->
          notify logger reply (Error err)
          return owner
      | Command.Flush reply ->
        reply.Reply ()
        return owner
    }

  /// A running owner for one cohort.
  type Handle internal (mailbox: MailboxProcessor<Command>, readFrame: unit -> CohortFrame<MemberId>) =
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
  let start
    (logger: Utils.ILogger)
    (ledger: LedgerPort<MemberId>)
    (clock: unit -> DateTime)
    (entropy: unit -> byte[])
    (getSessionTestOutcomes: string -> SessionTestOutcomes)
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
    let step = ResilientActor.wrapLoop logger "cohort-owner" (handle logger ledger clock entropy getSessionTestOutcomes publish)
    let mailbox =
      MailboxProcessor.Start(fun inbox ->
        let rec loop owner = async {
          let! command = inbox.Receive()
          let! next = step owner command
          return! loop next
        }
        loop { Cohort = head.State; NextSeq = initialNextSeq })
    new Handle(mailbox, fun () -> frameRef.Value)
