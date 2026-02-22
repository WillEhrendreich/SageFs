module SageFs.EventStore

#nowarn "44" // Marten deprecates GeneratedCodeMode, but CritterStackDefaults() requires DI

open System
open System.IO
open Marten
open SageFs.Features.Events

/// TextWriter wrapper that passes everything through except JasperFx assembly reference noise.
/// JasperFx.RuntimeCompiler walks all loaded assemblies during code gen and Console.WriteLines
/// when it can't resolve transitive deps (e.g. Ionide.ProjInfo → Microsoft.Build.Framework).
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

/// Install the filtering writer once, idempotently
let private installFilter =
  lazy (Console.SetOut(new FilteringTextWriter(Console.Out)))

/// Configure a Marten DocumentStore for SageFs event sourcing.
/// Installs a Console.Out filter to suppress JasperFx assembly noise from code gen,
/// and uses Auto code gen mode to minimize unnecessary compilation.
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
  let config = RetryPolicy.defaults
  let rec attempt n =
    task {
      try
        use session = store.LightweightSession()
        for evt in events do
          session.Events.Append(streamId, evt :> obj) |> ignore
        do! session.SaveChangesAsync()
        return Ok ()
      with ex ->
        match RetryPolicy.decide config n ex with
        | RetryPolicy.RetryAfter delayMs ->
          do! System.Threading.Tasks.Task.Delay(delayMs)
          return! attempt (n + 1)
        | RetryPolicy.GiveUp ex ->
          return Error (sprintf "Event append failed after %d attempts: %s" (n + 1) ex.Message)
        | RetryPolicy.Success -> return Ok ()
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
