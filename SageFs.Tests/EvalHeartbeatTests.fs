module SageFs.Tests.EvalHeartbeatTests

open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs.SseWriter

let private opts =
  let o = JsonSerializerOptions()
  o.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
  o

// ── formatEvalHeartbeatEvent ──────────────────────────────────────────────────

[<Tests>]
let heartbeatFormatterTests = testList "EvalHeartbeat.formatter" [

  testCase "produces eval_heartbeat event type" <| fun () ->
    let sse = formatEvalHeartbeatEvent opts None "src/Foo.fs" 5 1234L
    sse |> Expect.stringContains "should contain event type" "event: eval_heartbeat"

  testCase "contains elapsedMs field" <| fun () ->
    let sse = formatEvalHeartbeatEvent opts None "src/Foo.fs" 5 2500L
    sse |> Expect.stringContains "should contain elapsedMs" "\"ElapsedMs\":2500"

  testCase "contains filePath field" <| fun () ->
    let sse = formatEvalHeartbeatEvent opts None "src/Foo.fs" 5 1000L
    sse |> Expect.stringContains "should contain filePath" "\"FilePath\":\"src/Foo.fs\""

  testCase "contains blockStartLine field" <| fun () ->
    let sse = formatEvalHeartbeatEvent opts None "src/Foo.fs" 42 1000L
    sse |> Expect.stringContains "should contain blockStartLine" "\"BlockStartLine\":42"

  testCase "injects SessionId when provided" <| fun () ->
    let sse = formatEvalHeartbeatEvent opts (Some "sess-abc") "src/Foo.fs" 1 500L
    sse |> Expect.stringContains "should inject session id" "\"SessionId\":\"sess-abc\""

  testCase "ends with double newline per SSE spec" <| fun () ->
    let sse = formatEvalHeartbeatEvent opts None "src/Foo.fs" 1 100L
    sse |> Expect.stringEnds "should end with \\n\\n" "\n\n"
]

// ── allSseEventTypes registry ─────────────────────────────────────────────────

[<Tests>]
let heartbeatRegistryTests = testList "EvalHeartbeat.registry" [

  testCase "eval_heartbeat is in allSseEventTypes" <| fun () ->
    allSseEventTypes
    |> Expect.contains "eval_heartbeat should be registered" "eval_heartbeat"
]
