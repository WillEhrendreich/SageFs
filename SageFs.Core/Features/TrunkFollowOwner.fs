namespace SageFs.Features

open System
open System.Threading.Tasks
open SageFs
open SageFs.Features.TrunkFollow

/// The single owner of what the trunk has done with each landing (`TrunkFollow` is the decision, this is the shell).
///
/// One mailbox applies every event through `TrunkFollow.step` in arrival order, publishes the machine it produced for
/// wait-free reads, and performs the effects the step asked for OFF the mailbox, posting each completion back as an event.
/// The checkout is moved and the worker asked by an injected `TrunkPerformer`, so the machine and this loop run in tests
/// with a world of their own, and the daemon passes the real one.
module TrunkFollowOwner =

  /// What the shell does for the machine. Both are total: a failure is an answer (`NotMoved`, `Unreachable`), never an exception
  /// the loop has to survive.
  type TrunkPerformer =
    { /// Bring the trunk checkout to the landing's commit, and say what changed under it and who works in it.
      Move: LandedLanding -> Async<TrunkMove>
      /// Tell one trunk session which files changed and run its save pipeline over them.
      Deliver: LandedLanding -> string -> SavedFile list -> Async<SessionOutcome> }

  type internal Command =
    | Apply of TrunkEvent
    | Flush of AsyncReplyChannel<unit>

  let private dispatchEffects (logger: Utils.ILogger) (performer: TrunkPerformer) (post: Command -> unit) (effects: TrunkEffect list) : unit =
    for effect in effects do
      match effect with
      | TrunkEffect.MoveTrunk landing ->
        Async.Start(
          async {
            let! move =
              async {
                try return! performer.Move landing
                with ex ->
                  logger.LogWarning(sprintf "[trunk] moving the trunk checkout for landing %A threw: %s" landing.Landing ex.Message)
                  return TrunkMove.NotMoved ex.Message
              }
            post (Command.Apply (TrunkEvent.Moved (landing.Landing, move)))
          }
        )
      | TrunkEffect.Deliver (landing, session, files) ->
        Async.Start(
          async {
            let! outcome =
              async {
                try return! performer.Deliver landing session files
                with ex ->
                  logger.LogWarning(sprintf "[trunk] delivering landing %A to session %s threw: %s" landing.Landing session ex.Message)
                  return SessionOutcome.Unreachable ex.Message
              }
            post (Command.Apply (TrunkEvent.Answered (landing.Landing, session, outcome)))
          }
        )

  let internal handle
    (logger: Utils.ILogger)
    (performer: TrunkPerformer)
    (publish: TrunkMachine -> unit)
    (post: Command -> unit)
    (machine: TrunkMachine)
    (command: Command)
    : Async<TrunkMachine> =
    async {
      match command with
      | Command.Apply event ->
        let next, effects = step machine event
        match next = machine with
        | true -> ()
        | false -> publish next
        dispatchEffects logger performer post effects
        return next
      | Command.Flush reply ->
        reply.Reply ()
        return machine
    }

  /// A running trunk owner.
  type Handle internal (mailbox: MailboxProcessor<Command>, readMachine: unit -> TrunkMachine, changes: IEvent<TrunkMachine>) =
    /// Queue an event. The cohort's `LandingLanded` and the session's finished saves come in here.
    member _.Post(event: TrunkEvent) : unit = mailbox.Post (Command.Apply event)

    /// What the trunk has done so far. Wait-free: dereferences the published machine and never touches the mailbox.
    member _.Read() : TrunkMachine = readMachine ()

    /// Fires with the new machine each time an event changed it, after `Read` already returns it.
    member _.Changes : IEvent<TrunkMachine> = changes

    /// Completes once every event queued before it has been applied.
    member _.Flush() : Task = mailbox.PostAndAsyncReply Command.Flush |> Async.StartAsTask :> Task

    interface IDisposable with
      member _.Dispose() = (mailbox :> IDisposable).Dispose()

    interface IAsyncDisposable with
      member _.DisposeAsync() =
        (mailbox :> IDisposable).Dispose()
        ValueTask.CompletedTask

  let start (logger: Utils.ILogger) (performer: TrunkPerformer) : Handle =
    let machineRef = ref initial
    let publish (machine: TrunkMachine) = System.Threading.Interlocked.Exchange(machineRef, machine) |> ignore
    let changes = Event<TrunkMachine>()
    let mailbox =
      MailboxProcessor.Start(fun inbox ->
        let publishAndAnnounce (machine: TrunkMachine) =
          publish machine
          changes.Trigger machine
        let stepLoop =
          ResilientActor.wrapLoop logger "trunk-follow" (handle logger performer publishAndAnnounce inbox.Post)
        let rec loop (machine: TrunkMachine) = async {
          let! command = inbox.Receive()
          let! next = stepLoop machine command
          return! loop next
        }
        loop initial)
    new Handle(mailbox, (fun () -> machineRef.Value), changes.Publish)
