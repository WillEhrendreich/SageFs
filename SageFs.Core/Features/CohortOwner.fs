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

  let private noSessions: SessionSnapshot<MemberId>[] = [||]

  /// The read model for `head`, with no session test-outcome data folded in
  /// (Slice 1 owns membership/claims/landings only — session snapshots are a
  /// later item's concern).
  let internal frameOf (head: LedgerHead<MemberId>) : CohortFrame<MemberId> =
    project head noSessions

  let private notify (logger: Utils.ILogger) (reply: Result<CohortEvent<MemberId> list * CohortEffect<MemberId> list, CohortError<MemberId>> -> unit) (result: Result<CohortEvent<MemberId> list * CohortEffect<MemberId> list, CohortError<MemberId>>) =
    try reply result
    with ex -> logger.LogWarning(sprintf "[cohort-owner] Commit callback threw: %s" ex.Message)

  let internal handle
    (logger: Utils.ILogger)
    (ledger: LedgerPort<MemberId>)
    (clock: unit -> DateTime)
    (entropy: unit -> byte[])
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
          publish (frameOf { Seq = seq; State = newState })
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
  /// Startup state is `Cohort.replay (ledger.ReadAll())`: the ledger is the
  /// only source of truth, so restarting the owner over the same ledger
  /// reconstructs an identical `CohortState`/`CohortFrame`.
  let start
    (logger: Utils.ILogger)
    (ledger: LedgerPort<MemberId>)
    (clock: unit -> DateTime)
    (entropy: unit -> byte[])
    : Handle =
    let entries = ledger.ReadAll ()
    let head = replayHead entries
    let initialNextSeq =
      match entries with
      | [] -> 0L<ledgerSeq>
      | _ -> head.Seq + 1L<ledgerSeq>
    let frameRef = ref (frameOf head)
    let publish (frame: CohortFrame<MemberId>) =
      Interlocked.Exchange(frameRef, frame) |> ignore
    let step = ResilientActor.wrapLoop logger "cohort-owner" (handle logger ledger clock entropy publish)
    let mailbox =
      MailboxProcessor.Start(fun inbox ->
        let rec loop owner = async {
          let! command = inbox.Receive()
          let! next = step owner command
          return! loop next
        }
        loop { Cohort = head.State; NextSeq = initialNextSeq })
    new Handle(mailbox, fun () -> frameRef.Value)
