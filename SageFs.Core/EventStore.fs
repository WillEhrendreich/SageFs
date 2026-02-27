module SageFs.EventStore

#nowarn "44" // Marten deprecates GeneratedCodeMode, but CritterStackDefaults() requires DI

open System
open System.IO
open Marten
open SageFs.Features.Events

/// TextWriter wrapper that passes everything through except JasperFx assembly reference noise.
/// JasperFx.RuntimeCompiler walks all loaded assemblies during code gen and Console.WriteLines
/// when it can't resolve transitive deps (e.g. Ionide.ProjInfo → Microsoft.Build.Framework).
type FilteringTextWriter(inner: TextWriter) =
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
let installFilter =
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
  let sw = System.Diagnostics.Stopwatch.StartNew()
  let rec attempt n =
    task {
      try
        use session = store.LightweightSession()
        for evt in events do
          session.Events.Append(streamId, evt :> obj) |> ignore
        do! session.SaveChangesAsync()
        sw.Stop()
        Instrumentation.eventstoreAppendDurationMs.Record(sw.Elapsed.TotalMilliseconds)
        return Ok ()
      with ex ->
        match RetryPolicy.decide config n ex with
        | RetryPolicy.RetryAfter delayMs ->
          Instrumentation.eventstoreAppendRetries.Add(1L)
          do! System.Threading.Tasks.Task.Delay(delayMs)
          return! attempt (n + 1)
        | RetryPolicy.GiveUp ex ->
          sw.Stop()
          Instrumentation.eventstoreAppendDurationMs.Record(sw.Elapsed.TotalMilliseconds)
          Instrumentation.eventstoreAppendFailures.Add(1L)
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

/// Persistence abstraction: InMemory (no-op) or PostgreSQL-backed.
type EventPersistence = {
  AppendEvents: string -> SageFsEvent list -> Threading.Tasks.Task<Result<unit, string>>
  FetchStream: string -> Threading.Tasks.Task<(DateTimeOffset * SageFsEvent) list>
  CountEvents: string -> Threading.Tasks.Task<int>
}

module EventPersistence =
  let inMemory () : EventPersistence =
    let streams = System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset * SageFsEvent) list>()
    {
      AppendEvents = fun streamId events ->
        let now = DateTimeOffset.UtcNow
        let stamped = events |> List.map (fun e -> (now, e))
        streams.AddOrUpdate(streamId, stamped, fun _ existing -> existing @ stamped) |> ignore
        Threading.Tasks.Task.FromResult (Ok ())
      FetchStream = fun streamId ->
        match streams.TryGetValue streamId with
        | true, events -> Threading.Tasks.Task.FromResult events
        | false, _ -> Threading.Tasks.Task.FromResult []
      CountEvents = fun streamId ->
        match streams.TryGetValue streamId with
        | true, events -> Threading.Tasks.Task.FromResult events.Length
        | false, _ -> Threading.Tasks.Task.FromResult 0
    }

  let postgres (store: IDocumentStore) : EventPersistence = {
    AppendEvents = fun streamId events -> appendEvents store streamId events
    FetchStream = fun streamId -> fetchStream store streamId
    CountEvents = fun streamId -> countEvents store streamId
  }
