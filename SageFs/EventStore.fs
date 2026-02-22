module SageFs.Server.EventStore

#nowarn "44" // Marten deprecates GeneratedCodeMode, but CritterStackDefaults() requires DI

open System
open System.IO
open Marten
open SageFs.Features.Events

/// TextWriter wrapper that passes everything through except JasperFx assembly reference noise.
type private FilteringTextWriter(inner: TextWriter) =
  inherit TextWriter()
  static let isNoise (s: string) =
    not (isNull s)
    && (s.Contains("Could not make an assembly reference to")
        || (s.Contains("System.IO.FileNotFoundException") && s.Contains("Microsoft.Build")))
  override _.Encoding = inner.Encoding
  override _.Write(value: char) = inner.Write(value)
  override _.Write(value: string) = if not (isNoise value) then inner.Write(value)
  override _.WriteLine(value: string) = if not (isNoise value) then inner.WriteLine(value)
  override _.WriteLine() = inner.WriteLine()
  override _.Flush() = inner.Flush()

let private installFilter =
  lazy (Console.SetOut(new FilteringTextWriter(Console.Out)))

/// Configure a Marten DocumentStore for SageFs event sourcing.
let configureStore (connectionString: string) : IDocumentStore =
  installFilter.Force()
  DocumentStore.For(fun (o: StoreOptions) ->
    o.Connection(connectionString)
    o.Events.StreamIdentity <- JasperFx.Events.StreamIdentity.AsString
    o.AutoCreateSchemaObjects <- JasperFx.AutoCreate.All
    o.GeneratedCodeMode <- JasperFx.CodeGeneration.TypeLoadMode.Auto
    o.UseSystemTextJsonForSerialization(
      configure = fun opts ->
        opts.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
    )
  )

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
