/// Tells clients what a patch did, in two steps: `pending` the moment it is
/// applied, then what the host saw.
///
/// The wait for the host is passed in as a function, so the confirmation
/// logic here is driven in tests by hand and in production by the session's
/// agent (`SessionAgent.AwaitEntries`).
module SageFs.Features.PatchAnnouncer

open System
open SageFs.Utils
open SageFs.Features.ReloadOutcome
open SageFs.Features.PatchConfirmation
open SageFs.Middleware.EntryProbes

/// What asking the host returned.
[<RequireQualifiedAccess>]
type EntryAnswer =
  /// The host answered: what it knows about the probes.
  | HostSaw of EntryReading
  /// The host could not be asked, so nothing was observed.
  | HostUnreachable of reason: string

/// Wait until every probe has been sighted or the bound passes, then say what
/// is known.
type EntryWaiter = int64 list -> TimeSpan -> Async<EntryAnswer>

/// Broadcast the first word on a patch NOW (the page must refresh without
/// waiting), and return the rest: waiting for the host, and broadcasting what
/// it saw. The caller starts the returned work in the background, so a save is
/// never held up by the wait.
///
/// Every way this can go wrong ends in `NeverEntered`, never in `Patched`: a
/// host that cannot be asked, or a wait that throws, observed nothing.
let announce (waiter: EntryWaiter) (bound: TimeSpan) (begun: Begun) : Async<unit> =
  match begun with
  | Begun.NothingToWatch outcome ->
    ReloadBroadcast.broadcastOutcome outcome
    async { return () }
  | Begun.Watching(pending, watch) ->
    ReloadBroadcast.broadcastOutcome pending
    async {
      let! answer = waiter (PatchConfirmation.probesOf watch) bound |> Async.Catch
      let reading =
        match answer with
        | Choice1Of2(EntryAnswer.HostSaw reading) -> reading
        | Choice1Of2(EntryAnswer.HostUnreachable reason) ->
          Log.warn "Hot reload: the running app could not be asked whether the patch ran (%s), so it is not confirmed" reason
          { Sightings = [] }
        | Choice2Of2 ex ->
          Log.warn "Hot reload: asking whether the patch ran failed (%s), so it is not confirmed" ex.Message
          { Sightings = [] }
      match PatchConfirmation.settle reading watch with
      | WatchStep.Settled outcome ->
        ReloadBroadcast.broadcastOutcome outcome
        Log.info "Hot reload: %s" (ReloadOutcome.describe outcome)
      | WatchStep.Abandoned
      | WatchStep.StillWaiting _ -> ()
    }
