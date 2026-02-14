module SageFs.Tests.EventStoreTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.Events

/// Testcontainers-based PostgreSQL container with persisted volume, reused across test runs
let sharedContainer = lazy(
  let container =
    Testcontainers.PostgreSql.PostgreSqlBuilder()
      .WithDatabase("SageFs_test")
      .WithUsername("postgres")
      .WithPassword("SageFs")
      .WithImage("postgres:18")
      .WithVolumeMount("sagefs-test-pgdata", "/var/lib/postgresql")
      .WithReuse(true)
      .Build()
  container.StartAsync().GetAwaiter().GetResult()
  container
)

/// Create a Marten DocumentStore with schema isolation for test independence
let createStore schemaName =
  let container = sharedContainer.Value
  Marten.DocumentStore.For(fun (o: Marten.StoreOptions) ->
    o.Connection(container.GetConnectionString())
    o.DatabaseSchemaName <- schemaName
    o.Events.StreamIdentity <- JasperFx.Events.StreamIdentity.AsString
    o.UseSystemTextJsonForSerialization(
      configure = fun opts ->
        opts.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
    )
  ) :> Marten.IDocumentStore

[<Tests>]
let eventStoreTests =
  testList "[Integration] Event Store with Marten" [

    testCase "can append and retrieve a SessionStarted event"
    <| fun _ ->
      task {
        let store = createStore "test_append"
        use session = store.LightweightSession()
        let streamId = sprintf "session-%s" (Guid.NewGuid().ToString("N").[..7])
        let evt = SessionStarted {| Config = Map.ofList ["port", "1234"]; StartedAt = DateTimeOffset.UtcNow |}
        session.Events.Append(streamId, evt :> obj) |> ignore
        do! session.SaveChangesAsync()

        use readSession = store.LightweightSession()
        let! events = readSession.Events.FetchStreamAsync(streamId)
        events.Count
        |> Expect.equal "should have one event" 1
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "can append multiple events and retrieve in order"
    <| fun _ ->
      task {
        let store = createStore "test_multi"
        use session = store.LightweightSession()
        let streamId = sprintf "session-%s" (Guid.NewGuid().ToString("N").[..7])
        let evt1 = SessionStarted {| Config = Map.empty; StartedAt = DateTimeOffset.UtcNow |}
        let evt2 = SessionReady
        let evt3 = EvalCompleted {| Code = "let x = 1"; Result = "val x: int = 1"; TypeSignature = Some "int"; Duration = TimeSpan.FromMilliseconds(50.) |}
        session.Events.Append(streamId, evt1 :> obj, evt2 :> obj, evt3 :> obj) |> ignore
        do! session.SaveChangesAsync()

        use readSession = store.LightweightSession()
        let! events = readSession.Events.FetchStreamAsync(streamId)
        events.Count
        |> Expect.equal "should have three events" 3
        events.[0].Version
        |> Expect.equal "first event version" 1L
        events.[2].Version
        |> Expect.equal "third event version" 3L
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "domain events roundtrip correctly"
    <| fun _ ->
      task {
        let store = createStore "test_roundtrip"
        use session = store.LightweightSession()
        let streamId = sprintf "session-%s" (Guid.NewGuid().ToString("N").[..7])
        let evalEvt = EvalCompleted {|
          Code = "let x = 42"
          Result = "val x: int = 42"
          TypeSignature = Some "int"
          Duration = TimeSpan.FromMilliseconds(123.4)
        |}
        session.Events.Append(streamId, evalEvt :> obj) |> ignore
        do! session.SaveChangesAsync()

        use readSession = store.LightweightSession()
        let! events = readSession.Events.FetchStreamAsync(streamId)
        let data = events.[0].Data
        match data with
        | :? SageFsEvent as evt ->
          match evt with
          | EvalCompleted ec ->
            ec.Code |> Expect.equal "code should roundtrip" "let x = 42"
            ec.Result |> Expect.equal "result should roundtrip" "val x: int = 42"
          | _ -> failtest "expected EvalCompleted"
        | _ -> failtest "expected SageFsEvent"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "DiagnosticsChecked event roundtrips with diagnostics list"
    <| fun _ ->
      task {
        let store = createStore "test_diag_rt"
        use session = store.LightweightSession()
        let streamId = sprintf "session-%s" (Guid.NewGuid().ToString("N").[..7])
        let diagEvt = DiagnosticsChecked {|
          Code = "let x: int = \"oops\""
          Diagnostics = [
            { Message = "Type mismatch"; Severity = SageFs.Features.Diagnostics.DiagnosticSeverity.Error
              StartLine = 1; StartColumn = 14; EndLine = 1; EndColumn = 20 }
          ]
          Source = McpAgent "copilot"
        |}
        session.Events.Append(streamId, diagEvt :> obj) |> ignore
        do! session.SaveChangesAsync()

        use readSession = store.LightweightSession()
        let! events = readSession.Events.FetchStreamAsync(streamId)
        match events.[0].Data with
        | :? SageFsEvent as evt ->
          match evt with
          | DiagnosticsChecked dc ->
            dc.Diagnostics.Length |> Expect.equal "should have one diagnostic" 1
            dc.Diagnostics.[0].Message |> Expect.equal "message should roundtrip" "Type mismatch"
            dc.Source |> Expect.equal "source should roundtrip" (McpAgent "copilot")
          | _ -> failtest "expected DiagnosticsChecked"
        | _ -> failtest "expected SageFsEvent"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously
  ]

[<Tests>]
let persistenceTests =
  testList "[Integration] Event Store persistence across runs" [

    testCase "events survive container reuse (stream grows across runs)"
    <| fun _ ->
      task {
        let store = createStore "test_persistence"
        let streamId = "persistence-canary"
        let marker = sprintf "run-%s" (Guid.NewGuid().ToString("N").[..7])

        // Read existing events before appending
        use readBefore = store.LightweightSession()
        let! before = readBefore.Events.FetchStreamAsync(streamId)
        let countBefore = before.Count

        // Append a new event with a unique marker
        use session = store.LightweightSession()
        let evt = EvalCompleted {|
          Code = marker
          Result = sprintf "run at %O" DateTimeOffset.UtcNow
          TypeSignature = None
          Duration = TimeSpan.Zero
        |}
        session.Events.Append(streamId, evt :> obj) |> ignore
        do! session.SaveChangesAsync()

        // Read back — should have one more than before
        use readAfter = store.LightweightSession()
        let! after = readAfter.Events.FetchStreamAsync(streamId)
        after.Count
        |> Expect.equal "stream should grow by one" (countBefore + 1)

        // The latest event should contain our marker
        let latest = after.[after.Count - 1].Data
        match latest with
        | :? SageFsEvent as evt ->
          match evt with
          | EvalCompleted ec ->
            ec.Code |> Expect.equal "latest event should be our marker" marker
          | _ -> failtest "expected EvalCompleted"
        | _ -> failtest "expected SageFsEvent"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "fetchStream returns complete history including previous sessions"
    <| fun _ ->
      task {
        let store = createStore "test_history"

        // Write events to two separate session streams
        let sid1 = sprintf "history-a-%s" (Guid.NewGuid().ToString("N").[..7])
        let sid2 = sprintf "history-b-%s" (Guid.NewGuid().ToString("N").[..7])

        use s1 = store.LightweightSession()
        s1.Events.Append(sid1, SessionStarted {| Config = Map.empty; StartedAt = DateTimeOffset.UtcNow |} :> obj) |> ignore
        s1.Events.Append(sid1, SessionReady :> obj) |> ignore
        do! s1.SaveChangesAsync()

        use s2 = store.LightweightSession()
        s2.Events.Append(sid2, SessionStarted {| Config = Map.empty; StartedAt = DateTimeOffset.UtcNow |} :> obj) |> ignore
        do! s2.SaveChangesAsync()

        // Each stream has its own complete history
        use r1 = store.LightweightSession()
        let! stream1 = r1.Events.FetchStreamAsync(sid1)
        stream1.Count
        |> Expect.equal "stream 1 should have 2 events" 2

        use r2 = store.LightweightSession()
        let! stream2 = r2.Events.FetchStreamAsync(sid2)
        stream2.Count
        |> Expect.equal "stream 2 should have 1 event" 1
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously
  ]
