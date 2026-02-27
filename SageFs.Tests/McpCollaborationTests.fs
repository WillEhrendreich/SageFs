module SageFs.Tests.McpCollaborationTests

open Expecto
open System
open SageFs.Features.Events

// Tests for collaborative MCP mode — now backed by Marten event store
// Ensures that both MCP clients and human users see all activity

/// Create a fresh EventPersistence backed by a Marten store for each test
let mkPersistence schemaName =
  let store = EventStoreTests.createStore schemaName
  SageFs.EventStore.EventPersistence.postgres store

[<Tests>]
let tests =
  testList "[Integration] MCP Collaboration tests" [

    testCase "trackInput records input from console"
    <| fun _ ->
      let p = mkPersistence "collab_console_input"
      let sessionId = SageFs.EventStore.createSessionId ()
      SageFs.EventTracking.trackInput p sessionId Console "let x = 1" |> fun t -> t.Wait()

      let events = SageFs.EventTracking.getAllEvents p sessionId |> fun t -> t.GetAwaiter().GetResult()
      Expect.equal events.Length 1 "Should have 1 event"
      let (_, src, content) = events.[0]
      Expect.equal src "console" "Should be console source"
      Expect.equal content "let x = 1" "Should have correct content"

    testCase "trackInput records input from MCP agent"
    <| fun _ ->
      let p = mkPersistence "collab_mcp_input"
      let sessionId = SageFs.EventStore.createSessionId ()
      SageFs.EventTracking.trackInput p sessionId (McpAgent "claude") "let y = 2" |> fun t -> t.Wait()

      let events = SageFs.EventTracking.getAllEvents p sessionId |> fun t -> t.GetAwaiter().GetResult()
      Expect.equal events.Length 1 "Should have 1 event"
      let (_, src, _) = events.[0]
      Expect.equal src "mcp:claude" "Should indicate MCP source"

    testCase "trackOutput records output events"
    <| fun _ ->
      let p = mkPersistence "collab_output"
      let sessionId = SageFs.EventStore.createSessionId ()
      SageFs.EventTracking.trackInput p sessionId Console "printfn \"hello\"" |> fun t -> t.Wait()
      SageFs.EventTracking.trackOutput p sessionId Console "hello" |> fun t -> t.Wait()

      let events = SageFs.EventTracking.getAllEvents p sessionId |> fun t -> t.GetAwaiter().GetResult()
      Expect.equal events.Length 2 "Should have 2 events"

    testCase "events maintain order"
    <| fun _ ->
      let p = mkPersistence "collab_order"
      let sessionId = SageFs.EventStore.createSessionId ()
      SageFs.EventTracking.trackInput p sessionId Console "let a = 1" |> fun t -> t.Wait()
      SageFs.EventTracking.trackOutput p sessionId Console "val a: int = 1" |> fun t -> t.Wait()
      SageFs.EventTracking.trackInput p sessionId (McpAgent "copilot") "let b = 2" |> fun t -> t.Wait()
      SageFs.EventTracking.trackOutput p sessionId Console "val b: int = 2" |> fun t -> t.Wait()

      let events = SageFs.EventTracking.getAllEvents p sessionId |> fun t -> t.GetAwaiter().GetResult()
      Expect.equal events.Length 4 "Should have 4 events in order"
      let contents = events |> List.map (fun (_, _, txt) -> txt)
      Expect.equal contents.[0] "let a = 1" "First event"
      Expect.equal contents.[1] "val a: int = 1" "Second event"
      Expect.equal contents.[2] "let b = 2" "Third event"
      Expect.equal contents.[3] "val b: int = 2" "Fourth event"

    testCase "getRecentEvents limits results"
    <| fun _ ->
      let p = mkPersistence "collab_recent"
      let sessionId = SageFs.EventStore.createSessionId ()
      for i in 1..10 do
        SageFs.EventTracking.trackInput p sessionId Console (sprintf "event %d" i) |> fun t -> t.Wait()

      let recent = SageFs.EventTracking.getRecentEvents p sessionId 3 |> fun t -> t.GetAwaiter().GetResult()
      Expect.equal recent.Length 3 "Should limit to 3 events"
      let contents = recent |> List.map (fun (_, _, txt) -> txt)
      Expect.equal contents.[0] "event 8" "Should be 8th event (3rd from end)"
      Expect.equal contents.[1] "event 9" "Should be 9th event (2nd from end)"
      Expect.equal contents.[2] "event 10" "Should be 10th event (most recent)"

    testCase "EventSource.ToString formats console source"
    <| fun _ ->
      let source = Console
      Expect.equal (source.ToString()) "console" "Should be console"

    testCase "EventSource.ToString formats MCP agent source"
    <| fun _ ->
      let source = McpAgent "claude"
      Expect.equal (source.ToString()) "mcp:claude" "Should show MCP prefix with agent name"

    testCase "EventSource.ToString formats file sync source"
    <| fun _ ->
      let source = FileSync "script.fsx"
      Expect.equal (source.ToString()) "file:script.fsx" "Should show file prefix"

    testCase "Multiple sources can contribute to same session"
    <| fun _ ->
      let p = mkPersistence "collab_multi_source"
      let sessionId = SageFs.EventStore.createSessionId ()

      SageFs.EventTracking.trackInput p sessionId Console "let userValue = 42" |> fun t -> t.Wait()
      SageFs.EventTracking.trackOutput p sessionId Console "val userValue: int = 42" |> fun t -> t.Wait()
      SageFs.EventTracking.trackInput p sessionId (McpAgent "ai-agent") "userValue * 2" |> fun t -> t.Wait()
      SageFs.EventTracking.trackOutput p sessionId Console "val it: int = 84" |> fun t -> t.Wait()
      SageFs.EventTracking.trackInput p sessionId (FileSync "helper.fsx") "let helper x = x + 1" |> fun t -> t.Wait()

      let events = SageFs.EventTracking.getAllEvents p sessionId |> fun t -> t.GetAwaiter().GetResult()
      Expect.equal events.Length 5 "Should have all 5 events"

      let sources =
        events |> List.map (fun (_, src, _) -> src) |> List.distinct
      Expect.equal sources.Length 3 "Should have 3 different sources"

    testCase "getEventCount tracks total events"
    <| fun _ ->
      let p = mkPersistence "collab_count"
      let sessionId = SageFs.EventStore.createSessionId ()

      Expect.equal (SageFs.EventTracking.getEventCount p sessionId |> fun t -> t.GetAwaiter().GetResult()) 0 "Should start with 0"

      SageFs.EventTracking.trackInput p sessionId Console "let x = 1" |> fun t -> t.Wait()
      Expect.equal (SageFs.EventTracking.getEventCount p sessionId |> fun t -> t.GetAwaiter().GetResult()) 1 "Should have 1 event"

      SageFs.EventTracking.trackOutput p sessionId Console "val x: int = 1" |> fun t -> t.Wait()
      Expect.equal (SageFs.EventTracking.getEventCount p sessionId |> fun t -> t.GetAwaiter().GetResult()) 2 "Should have 2 events"

    testCase "handles empty string content"
    <| fun _ ->
      let p = mkPersistence "collab_empty"
      let sessionId = SageFs.EventStore.createSessionId ()
      SageFs.EventTracking.trackInput p sessionId Console "" |> fun t -> t.Wait()
      SageFs.EventTracking.trackOutput p sessionId Console "" |> fun t -> t.Wait()

      let events = SageFs.EventTracking.getAllEvents p sessionId |> fun t -> t.GetAwaiter().GetResult()
      Expect.equal events.Length 2 "Should track empty strings"
      let (_, _, content1) = events.[0]
      let (_, _, content2) = events.[1]
      Expect.equal content1 "" "Should preserve empty input"
      Expect.equal content2 "" "Should preserve empty output"

    testCase "getRecentEvents with count greater than total events"
    <| fun _ ->
      let p = mkPersistence "collab_recent_gt"
      let sessionId = SageFs.EventStore.createSessionId ()
      SageFs.EventTracking.trackInput p sessionId Console "event 1" |> fun t -> t.Wait()
      SageFs.EventTracking.trackInput p sessionId Console "event 2" |> fun t -> t.Wait()

      let recent = SageFs.EventTracking.getRecentEvents p sessionId 10 |> fun t -> t.GetAwaiter().GetResult()
      Expect.equal recent.Length 2 "Should return all events when count > total"

    testCase "getRecentEvents with count = 0"
    <| fun _ ->
      let p = mkPersistence "collab_recent_zero"
      let sessionId = SageFs.EventStore.createSessionId ()
      SageFs.EventTracking.trackInput p sessionId Console "event 1" |> fun t -> t.Wait()

      let recent = SageFs.EventTracking.getRecentEvents p sessionId 0 |> fun t -> t.GetAwaiter().GetResult()
      Expect.equal recent.Length 0 "Should return empty list for count = 0"

    testCase "getAllEvents on empty session"
    <| fun _ ->
      let p = mkPersistence "collab_empty_session"
      let sessionId = SageFs.EventStore.createSessionId ()
      let events = SageFs.EventTracking.getAllEvents p sessionId |> fun t -> t.GetAwaiter().GetResult()
      Expect.equal events.Length 0 "Empty session should return no events"
      Expect.equal (SageFs.EventTracking.getEventCount p sessionId |> fun t -> t.GetAwaiter().GetResult()) 0 "Empty session should have count 0"

    testCase "EventSource with special characters in agent name"
    <| fun _ ->
      let source = McpAgent "agent:with:colons"
      Expect.equal (source.ToString()) "mcp:agent:with:colons" "Should handle colons in name"

      let source2 = FileSync "path/to/file.fsx"
      Expect.equal (source2.ToString()) "file:path/to/file.fsx" "Should handle path separators"
  ]
