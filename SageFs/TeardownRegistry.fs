/// Which sessions the daemon is taking down right now, and how.
///
/// "Stopping session id:..." used to be a patch written into the stop POST's own response and nowhere else. The
/// dashboard snapshot did not know a stop was running, so any stream push computed around the click put the
/// ordinary card back with live buttons while the session was still going down. A fact the page must keep
/// showing has to be one the daemon owns, so the teardown handler records it here before it asks for the stop
/// and clears it after, and every render of the sidebar reads it.
///
/// Pure of Falco and HTTP: this is state and a bracket around an action, nothing to do with how a card looks.
module SageFs.Server.TeardownRegistry

open System.Threading.Tasks
open SageFs

/// How a session is being taken down. The dashboard's two teardown levels: stop (also what dispose does, since
/// the manifest is the only durable state) and purge, which stops the session and then deletes what it left.
[<RequireQualifiedAccess>]
type Teardown =
  | Stopping
  | Purging

module Teardown =
  /// The word on the card. The one place a teardown becomes text.
  let describe (teardown: Teardown) : string =
    match teardown with
    | Teardown.Stopping -> "Stopping"
    | Teardown.Purging -> "Purging"

/// What one sidebar card knows about whether its session is going down.
[<RequireQualifiedAccess>]
type TeardownState =
  | NotTearingDown
  | TearingDown of Teardown

module TeardownState =
  let ofLookup (teardowns: Map<WorkerProtocol.SessionId, Teardown>) (id: WorkerProtocol.SessionId) : TeardownState =
    match Map.tryFind id teardowns with
    | Some teardown -> TeardownState.TearingDown teardown
    | None -> TeardownState.NotTearingDown

/// The teardowns in flight, by session. Two stops of one session (a double click) count twice, and the card
/// stays until the last of them is done: the first to finish must not put live buttons back under the second.
type Registry() =
  let gate = obj ()
  let inFlight = System.Collections.Generic.Dictionary<WorkerProtocol.SessionId, int * Teardown>()

  member _.Begin(id: WorkerProtocol.SessionId, teardown: Teardown) : unit =
    lock gate (fun () ->
      let running =
        match inFlight.TryGetValue id with
        | true, (count, _) -> count
        | false, _ -> 0
      inFlight.[id] <- (running + 1, teardown))

  member _.Finish(id: WorkerProtocol.SessionId) : unit =
    lock gate (fun () ->
      match inFlight.TryGetValue id with
      | true, (count, teardown) when count > 1 -> inFlight.[id] <- (count - 1, teardown)
      | true, _ -> inFlight.Remove id |> ignore
      | false, _ -> ())

  /// A copy: what is going down at this moment. Safe to render from.
  member _.Current() : Map<WorkerProtocol.SessionId, Teardown> =
    lock gate (fun () -> inFlight |> Seq.map (fun pair -> pair.Key, snd pair.Value) |> Map.ofSeq)

/// Runs a teardown with the registry saying so for exactly as long as it runs. Registered BEFORE `announce` so
/// the render the announcement triggers sees it, and cleared BEFORE the second `announce` so that one sees it
/// gone. Cleared on a refusal, on an exception and on success alike: a failed stop must never leave a card stuck.
let during
  (registry: Registry)
  (announce: unit -> unit)
  (id: WorkerProtocol.SessionId)
  (teardown: Teardown)
  (run: unit -> Task<'a>)
  : Task<'a> =
  task {
    registry.Begin(id, teardown)
    try
      announce ()
      return! run ()
    finally
      registry.Finish id
      announce ()
  }
