module SageFs.Server.EventStore

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
        | :? SageFsEvent as evt -> Some evt
        | _ -> None)
      |> Seq.toList
  }

/// Create a session stream ID
let createSessionId () =
  sprintf "session-%s" (Guid.NewGuid().ToString("N").[..7])

/// Try to create a store from environment variable, returning None if not configured
let tryCreateFromEnv () =
  match Environment.GetEnvironmentVariable("SageFs_CONNECTION_STRING") with
  | null | "" -> None
  | connStr ->
    try Some (configureStore connStr)
    with _ -> None
