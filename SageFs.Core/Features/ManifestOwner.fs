namespace SageFs.Features

open System
open System.Collections.Generic
open System.Threading.Tasks
open SageFs
open SageFs.Features.DaemonManifest
open SageFs.Features.ManifestTypes

/// The single owner of daemon.sagefm.
///
/// Every writer used to do its own read-merge-write straight against the file
/// (periodic save, shutdown save, purge, resume-forget, --prune), all through
/// one shared `.tmp` path. Two writers that read the same version lost each
/// other's changes — a purge racing a periodic save could resurrect the purged
/// session. The owner is now the only component that touches the file: it holds
/// the manifest in memory, applies each ManifestMutation in arrival order, and
/// writes the result atomically before replying. A reply means the change is
/// durable, and no change can be lost to an interleaving.
module ManifestOwner =

  /// Whether a committed mutation was written.
  [<RequireQualifiedAccess>]
  type Persisted =
    | WrittenTo of path: string
    /// The mutation changed nothing and the file already held this state.
    | Unchanged

  type Committed = {
    Previous: DaemonManifestState
    Current: DaemonManifestState
    Persisted: Persisted
  }

  [<RequireQualifiedAccess>]
  type CommitError =
    /// The existing manifest could not be read. Nothing was written: writing
    /// now would replace its history with only what this daemon knows.
    | BaseUnreadable of ManifestLoadError
    /// The write failed. The change is kept in memory and the next commit
    /// writes it, so it is delayed, not lost.
    | WriteFailed of reason: string
    /// The daemon's shutdown is already recorded; a live sync now would mark
    /// the sessions it just stopped as alive again.
    | AfterShutdown

  module CommitError =
    let describe (error: CommitError) : string =
      match error with
      | CommitError.BaseUnreadable ManifestLoadError.NotFound -> "the manifest was not found"
      | CommitError.BaseUnreadable (ManifestLoadError.IoError reason) -> sprintf "the manifest could not be read: %s" reason
      | CommitError.BaseUnreadable (ManifestLoadError.CorruptData reason) -> sprintf "the manifest is corrupt: %s" reason
      | CommitError.WriteFailed reason -> reason
      | CommitError.AfterShutdown ->
        "the daemon's shutdown is already recorded; a live sync now would mark its stopped sessions alive again"

  type CommitResult = Result<Committed, CommitError>

  [<RequireQualifiedAccess>]
  type internal Durability =
    | OnDisk
    | PendingWrite

  [<RequireQualifiedAccess>]
  type internal Holding =
    /// Not loaded yet, or the last load failed: load before the next command.
    | Unloaded
    | Owned of state: DaemonManifestState * durability: Durability

  [<RequireQualifiedAccess>]
  type internal Lifecycle =
    | Serving
    /// The shutdown sync has been applied: live syncs are refused from here on,
    /// so a late periodic save can never mark the stopped sessions alive again.
    | ShutDown

  type internal OwnerState = {
    Holding: Holding
    Lifecycle: Lifecycle
  }

  type internal Command =
    | Apply of ManifestMutation * reply: (CommitResult -> unit)
    | Read of AsyncReplyChannel<Result<DaemonManifestState, ManifestLoadError>>
    | QuarantineCorrupt of AsyncReplyChannel<bool>
    | Flush of AsyncReplyChannel<unit>

  let internal loadInto (sageFsDir: string) (holding: Holding) : Result<DaemonManifestState * Durability, ManifestLoadError> =
    match holding with
    | Holding.Owned (state, durability) -> Ok (state, durability)
    | Holding.Unloaded ->
      match DaemonPersistence.loadManifest sageFsDir with
      | Ok state -> Ok (state, Durability.OnDisk)
      | Error ManifestLoadError.NotFound -> Ok (DaemonManifestState.empty, Durability.OnDisk)
      | Error err -> Error err

  let private notify (logger: Utils.ILogger) (reply: CommitResult -> unit) (result: CommitResult) =
    try reply result
    with ex -> logger.LogWarning(sprintf "[manifest-owner] Commit callback threw: %s" ex.Message)

  let private write (logger: Utils.ILogger) (sageFsDir: string) (reply: CommitResult -> unit) (previous: DaemonManifestState) (current: DaemonManifestState) (durability: Durability) : Holding =
    let committed persisted = { Previous = previous; Current = current; Persisted = persisted }
    match current = previous, durability with
    | true, Durability.OnDisk ->
      notify logger reply (Ok (committed Persisted.Unchanged))
      Holding.Owned (current, Durability.OnDisk)
    | _ ->
      match DaemonPersistence.saveManifest sageFsDir current with
      | Ok path ->
        notify logger reply (Ok (committed (Persisted.WrittenTo path)))
        Holding.Owned (current, Durability.OnDisk)
      | Error reason ->
        Instrumentation.persistenceSaveErrors.Add(1L, KeyValuePair("format", box "sfm1"))
        notify logger reply (Error (CommitError.WriteFailed reason))
        Holding.Owned (current, Durability.PendingWrite)

  let private commit (logger: Utils.ILogger) (sageFsDir: string) (owner: OwnerState) (mutation: ManifestMutation) (reply: CommitResult -> unit) : OwnerState =
    match mutation, owner.Lifecycle with
    | ManifestMutation.SyncLive (_, _, _, LiveSync.Running), Lifecycle.ShutDown ->
      notify logger reply (Error CommitError.AfterShutdown)
      owner
    | _ ->
      match loadInto sageFsDir owner.Holding with
      | Error err ->
        notify logger reply (Error (CommitError.BaseUnreadable err))
        { owner with Holding = Holding.Unloaded }
      | Ok (previous, durability) ->
        let current = ManifestMutation.apply mutation previous
        let lifecycle =
          match mutation with
          | ManifestMutation.SyncLive (_, _, _, LiveSync.ShuttingDown) -> Lifecycle.ShutDown
          | _ -> owner.Lifecycle
        { Holding = write logger sageFsDir reply previous current durability
          Lifecycle = lifecycle }

  /// Rename the manifest aside only when it is genuinely corrupt — a readable
  /// file (or its readable backup) is never quarantined.
  let private quarantine (sageFsDir: string) (holding: Holding) : bool * Holding =
    match holding with
    | Holding.Owned _ -> false, holding
    | Holding.Unloaded ->
      match DaemonPersistence.loadManifest sageFsDir with
      | Error (ManifestLoadError.CorruptData _) ->
        match DaemonPersistence.renameCorruptManifest sageFsDir with
        | true -> true, Holding.Owned (DaemonManifestState.empty, Durability.OnDisk)
        | false -> false, Holding.Unloaded
      | Error (ManifestLoadError.IoError _) -> false, Holding.Unloaded
      | Error ManifestLoadError.NotFound -> false, Holding.Owned (DaemonManifestState.empty, Durability.OnDisk)
      | Ok state -> false, Holding.Owned (state, Durability.OnDisk)

  let internal handle (logger: Utils.ILogger) (sageFsDir: string) (owner: OwnerState) (command: Command) : Async<OwnerState> =
    async {
      match command with
      | Command.Apply (mutation, reply) ->
        return commit logger sageFsDir owner mutation reply
      | Command.Read reply ->
        match loadInto sageFsDir owner.Holding with
        | Ok (state, durability) ->
          reply.Reply (Ok state)
          return { owner with Holding = Holding.Owned (state, durability) }
        | Error err ->
          reply.Reply (Error err)
          return { owner with Holding = Holding.Unloaded }
      | Command.QuarantineCorrupt reply ->
        let renamed, holding = quarantine sageFsDir owner.Holding
        reply.Reply renamed
        return { owner with Holding = holding }
      | Command.Flush reply ->
        reply.Reply ()
        return owner
    }

  /// A running manifest owner for one SageFs data directory.
  type Handle internal (mailbox: MailboxProcessor<Command>) =
    /// Apply a mutation; completes once it is on disk (or has failed).
    member _.Commit(mutation: ManifestMutation) : Task<CommitResult> =
      mailbox.PostAndAsyncReply(fun ch -> Command.Apply (mutation, ch.Reply)) |> Async.StartAsTask

    /// Queue a mutation without waiting. `onCommitted` runs on the owner once it is applied.
    member _.Post(mutation: ManifestMutation, onCommitted: CommitResult -> unit) : unit =
      mailbox.Post (Command.Apply (mutation, onCommitted))

    /// The manifest as the owner holds it (loaded from disk on first use).
    member _.Read() : Task<Result<DaemonManifestState, ManifestLoadError>> =
      mailbox.PostAndAsyncReply Command.Read |> Async.StartAsTask

    /// Rename a corrupt manifest aside so later commits can write again.
    /// Returns true only when a corrupt file was actually renamed.
    member _.QuarantineCorrupt() : Task<bool> =
      mailbox.PostAndAsyncReply Command.QuarantineCorrupt |> Async.StartAsTask

    /// Completes once every command queued before it has been applied.
    member _.Flush() : Task =
      mailbox.PostAndAsyncReply Command.Flush |> Async.StartAsTask :> Task

    interface IDisposable with
      member _.Dispose() = (mailbox :> IDisposable).Dispose()

    interface IAsyncDisposable with
      member _.DisposeAsync() =
        (mailbox :> IDisposable).Dispose()
        ValueTask.CompletedTask

  /// Start the owner for `sageFsDir`. It must be the only writer of that
  /// directory's daemon.sagefm for as long as it runs.
  let start (logger: Utils.ILogger) (sageFsDir: string) : Handle =
    let step = ResilientActor.wrapLoop logger "manifest-owner" (handle logger sageFsDir)
    let mailbox =
      MailboxProcessor.Start(fun inbox ->
        let rec loop owner = async {
          let! command = inbox.Receive()
          let! next = step owner command
          return! loop next
        }
        loop { Holding = Holding.Unloaded; Lifecycle = Lifecycle.Serving })
    new Handle(mailbox)
