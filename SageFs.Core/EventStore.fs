module SageFs.EventStore

open System
open Marten
open SageFs.Features.Events

/// Configure a Marten DocumentStore for SageFs event sourcing
let configureStore (connectionString: string) : IDocumentStore =
  DocumentStore.For(fun (o: StoreOptions) ->
    o.Connection(connectionString)
    o.Events.StreamIdentity <- JasperFx.Events.StreamIdentity.AsString
    o.AutoCreateSchemaObjects <- JasperFx.AutoCreate.All
    o.UseSystemTextJsonForSerialization(
      configure = fun opts ->
        opts.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
    )
  )

/// Append events to a session stream
let appendEvents (store: IDocumentStore) (streamId: string) (events: SageFsEvent list) =
  task {
    use session = store.LightweightSession()
    for evt in events do
      session.Events.Append(streamId, evt :> obj) |> ignore
    do! session.SaveChangesAsync()
  }

/// Fetch all events from a session stream
let fetchStream (store: IDocumentStore) (streamId: string) =
  task {
    use session = store.LightweightSession()
    let! events = session.Events.FetchStreamAsync(streamId)
    return
      events
      |> Seq.choose (fun e ->
        match e.Data with
        | :? SageFsEvent as evt -> Some (e.Timestamp, evt)
        | _ -> None)
      |> Seq.toList
  }

/// Fetch recent events from a session stream (most recent N)
let fetchRecentEvents (store: IDocumentStore) (streamId: string) (count: int) =
  task {
    use session = store.LightweightSession()
    let! events = session.Events.FetchStreamAsync(streamId)
    return
      events
      |> Seq.choose (fun e ->
        match e.Data with
        | :? SageFsEvent as evt -> Some (e.Timestamp, evt)
        | _ -> None)
      |> Seq.toList
      |> List.rev
      |> List.truncate count
      |> List.rev
  }

/// Count events in a session stream
let countEvents (store: IDocumentStore) (streamId: string) =
  task {
    use session = store.LightweightSession()
    let! events = session.Events.FetchStreamAsync(streamId)
    return events.Count
  }

/// Create a session stream ID
let createSessionId () =
  sprintf "session-%s" (Guid.NewGuid().ToString("N").[..7])
