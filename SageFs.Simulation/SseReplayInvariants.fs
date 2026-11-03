namespace SageFs.Simulation

open SageFs
open SageFs.Simulation.SseReplaySim

/// Named invariants over an `SseReplaySim` trace. Same shape as `LeaseSimInvariants`: the oracle
/// reads what each client received and what the daemon sent, and never re-derives a replay.
module SseReplayInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant = { Id: string; Description: string; Check: State list -> Outcome }

  let firstClientViolation (states: State list) (check: int -> Client -> string option) : Outcome =
    (List.last states).Clients
    |> Map.toList
    |> List.tryPick (fun (clientId, client) -> check clientId client)
    |> function
       | Some message -> Outcome.Violated message
       | None -> Outcome.Holds

  /// nothing-missed-after-reconnect: for a client that is connected at the end of the run, every
  /// frame of the daemon's current run that the client may see was either delivered to it or is
  /// covered by a snapshot it was sent. Frames of an earlier run are not owed: a restart forgets
  /// them, and the snapshot at reconnect is how a client learns the state.
  let nothingMissedAfterReconnect : Invariant =
    { Id = "nothing-missed-after-reconnect"
      Description = "Every visible frame of the current run is delivered to a connected client or covered by its snapshot."
      Check = fun states ->
        let final = List.last states
        firstClientViolation states (fun clientId client ->
          match client.Connected with
          | false -> None
          | true ->
            final.Published
            |> List.filter (fun p -> p.Id.Epoch = final.Buffer.Epoch && SseFrame.visibleTo client.Stream p.Frame)
            |> List.tryFind (fun p ->
              let delivered = client.Received |> List.exists (fun r -> r.Id = p.Id)
              let covered =
                client.Snapshots |> List.exists (fun sn -> sn.Epoch = p.Id.Epoch && p.Id.Seq <= sn.Through)
              not delivered && not covered)
            |> Option.map (fun p ->
              sprintf "client %d never got %s and no snapshot covers it" clientId (EventId.toWire p.Id))) }

  /// delivered-once-in-order: within one run, the seqs a client received only ever go up.
  let deliveredOnceInOrder : Invariant =
    { Id = "delivered-once-in-order"
      Description = "Within a run, a client's received seqs strictly increase: no repeat, no reorder."
      Check = fun states ->
        firstClientViolation states (fun clientId client ->
          client.Received
          |> List.pairwise
          |> List.tryFind (fun (a, b) -> a.Id.Epoch = b.Id.Epoch && b.Id.Seq <= a.Id.Seq)
          |> Option.map (fun (a, b) ->
            sprintf "client %d got %s and then %s" clientId (EventId.toWire a.Id) (EventId.toWire b.Id))) }

  /// never-another-sessions-frames: a stream only ever receives frames it may see.
  let neverSeesAnotherSessionsFrames : Invariant =
    { Id = "never-sees-another-sessions-frames"
      Description = "Every frame a client received is visible to the stream it asked for."
      Check = fun states ->
        firstClientViolation states (fun clientId client ->
          client.Received
          |> List.tryFind (fun r -> not (SseFrame.visibleTo client.Stream r.Frame))
          |> Option.map (fun r ->
            sprintf "client %d (%A) was sent %s, which is %A's" clientId client.Stream (EventId.toWire r.Id) r.Frame.Scope)) }

  let all : Invariant list = [ nothingMissedAfterReconnect; deliveredOnceInOrder; neverSeesAnotherSessionsFrames ]

  /// Every invariant that fails, by id.
  let violations (states: State list) : (string * string) list =
    all
    |> List.choose (fun invariant ->
      match invariant.Check states with
      | Outcome.Violated message -> Some (invariant.Id, message)
      | Outcome.Holds -> None)
