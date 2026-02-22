module SageFs.Server.EventStore

open System
open Marten
open SageFs.Features.Events

/// Configure a Marten DocumentStore for SageFs event sourcing.
/// Temporarily redirects Console.Out during init to suppress JasperFx assembly reference warnings.
let configureStore (connectionString: string) : IDocumentStore =
  let origOut = System.Console.Out
  System.Console.SetOut(System.IO.TextWriter.Null)
  try
    DocumentStore.For(fun (o: StoreOptions) ->
      o.Connection(connectionString)
      o.Events.StreamIdentity <- JasperFx.Events.StreamIdentity.AsString
      o.AutoCreateSchemaObjects <- JasperFx.AutoCreate.All
      o.UseSystemTextJsonForSerialization(
        configure = fun opts ->
          opts.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
      )
    )
  finally
    System.Console.SetOut(origOut)

/// Append events to a session stream with retry on version conflict
let appendEvents (store: IDocumentStore) (streamId: string) (events: SageFsEvent list) =
  let config = SageFs.RetryPolicy.defaults
  let rec attempt n =
    task {
      try
        use session = store.LightweightSession()
        for evt in events do
          session.Events.Append(streamId, evt :> obj) |> ignore
        do! session.SaveChangesAsync()
        return Ok ()
      with ex ->
        match SageFs.RetryPolicy.decide config n ex with
        | SageFs.RetryPolicy.RetryAfter delayMs ->
          do! System.Threading.Tasks.Task.Delay(delayMs)
          return! attempt (n + 1)
        | SageFs.RetryPolicy.GiveUp ex ->
          return Error (sprintf "Event append failed after %d attempts: %s" (n + 1) ex.Message)
        | SageFs.RetryPolicy.Success -> return Ok ()
    }
  attempt 0

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
