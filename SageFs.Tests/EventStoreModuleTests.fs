module SageFs.Tests.EventStoreModuleTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.Events
open SageFs.EventStore

/// Reuse the shared Testcontainers Postgres from EventStoreTests
let container () = EventStoreTests.sharedContainer.Value

/// Shared store for all module tests (avoids concurrent DDL migration conflicts)
let sharedStore = lazy(
  let connStr = (container ()).GetConnectionString()
  configureStore connStr
)

[<Tests>]
let eventStoreModuleTests =
  testList "[Integration] EventStore module" [

    testCase "configureStore creates a working store from connection string"
    <| fun _ ->
      task {
        let store = sharedStore.Value
        let streamId = createSessionId ()
        do! appendEvents store streamId [SessionReady]
        let! events = fetchStream store streamId
        events |> List.map snd |> List.length
        |> Expect.equal "should have one event" 1
        events |> List.map snd |> List.head
        |> Expect.equal "should be SessionReady" SessionReady
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "appendEvents appends multiple events atomically"
    <| fun _ ->
      task {
        let store = sharedStore.Value
        let streamId = createSessionId ()
        let events = [
          SessionStarted {| Config = Map.ofList ["proj", "Test.fsproj"]; StartedAt = DateTimeOffset.UtcNow |}
          SessionReady
          EvalCompleted {| Code = "1 + 1"; Result = "val it: int = 2"; TypeSignature = Some "int"; Duration = TimeSpan.FromMilliseconds(10.) |}
        ]
        do! appendEvents store streamId events
        let! fetched = fetchStream store streamId
        fetched |> List.map snd |> List.length
        |> Expect.equal "should have three events" 3
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "fetchStream returns events in append order"
    <| fun _ ->
      task {
        let store = sharedStore.Value
        let streamId = createSessionId ()
        do! appendEvents store streamId [
          SessionStarted {| Config = Map.empty; StartedAt = DateTimeOffset.UtcNow |}
          SessionReady
          EvalCompleted {| Code = "let x = 42"; Result = "val x: int = 42"; TypeSignature = Some "int"; Duration = TimeSpan.FromMilliseconds(5.) |}
          SessionReset
        ]
        let! events = fetchStream store streamId
        match events |> List.map snd with
        | [SessionStarted _; SessionReady; EvalCompleted _; SessionReset] -> ()
        | other -> failtest (sprintf "unexpected event order: %A" other)
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "createSessionId generates unique IDs"
    <| fun _ ->
      let ids = List.init 100 (fun _ -> createSessionId ())
      ids |> List.distinct |> List.length
      |> Expect.equal "all 100 IDs should be unique" 100
  ]
