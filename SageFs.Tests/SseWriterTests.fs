module SageFs.Tests.SseWriterTests

open System.IO
open System.Text
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs.SseWriter

[<Tests>]
let sseTests = testList "SSE Writer" [
  testList "formatSseEvent" [
    testCase "formats single-line event" <| fun () ->
      formatSseEvent "test_summary" """{"total":5}"""
      |> Expect.equal "should format correctly"
           "event: test_summary\ndata: {\"total\":5}\n\n"

    testCase "handles empty data" <| fun () ->
      formatSseEvent "ping" ""
      |> Expect.equal "should format with empty data"
           "event: ping\ndata: \n\n"
  ]

  testList "formatSseEventMultiline" [
    testCase "formats multiline event" <| fun () ->
      formatSseEventMultiline "update" [ "line1"; "line2"; "line3" ]
      |> Expect.equal "should format each line with data:"
           "event: update\ndata: line1\ndata: line2\ndata: line3\n\n"

    testCase "handles empty lines list" <| fun () ->
      formatSseEventMultiline "empty" []
      |> Expect.equal "should format with no data lines"
           "event: empty\n\n"

    testCase "handles single line" <| fun () ->
      formatSseEventMultiline "single" [ "only" ]
      |> Expect.equal "should format single line"
           "event: single\ndata: only\n\n"
  ]

  testList "trySendBytes" [
    testCase "writes bytes to stream successfully" <| fun () ->
      use ms = new MemoryStream()
      let bytes = Encoding.UTF8.GetBytes("hello")
      let result = trySendBytes ms bytes |> Async.AwaitTask |> Async.RunSynchronously
      result |> Expect.isOk "should succeed"
      ms.ToArray() |> Encoding.UTF8.GetString
      |> Expect.equal "should have written content" "hello"

    testCase "returns Error on disposed stream" <| fun () ->
      let ms = new MemoryStream()
      ms.Dispose()
      let bytes = Encoding.UTF8.GetBytes("hello")
      let result = trySendBytes ms bytes |> Async.AwaitTask |> Async.RunSynchronously
      result |> Expect.isError "should fail on disposed stream"
  ]

  testList "trySendSseEvent" [
    testCase "sends formatted SSE event to stream" <| fun () ->
      use ms = new MemoryStream()
      let result = trySendSseEvent ms "test" "data" |> Async.AwaitTask |> Async.RunSynchronously
      result |> Expect.isOk "should succeed"
      ms.ToArray() |> Encoding.UTF8.GetString
      |> Expect.equal "should have formatted SSE" "event: test\ndata: data\n\n"
  ]

  testList "formatTestSummaryEvent" [
    testCase "serializes TestSummary to SSE" <| fun () ->
      let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
      let summary: SageFs.Features.LiveTesting.TestSummary = {
        Total = 10; Passed = 8; Failed = 1; Stale = 1; Running = 0; Disabled = 0; Enabled = true
      }
      let result = formatTestSummaryEvent opts summary
      result |> Expect.stringContains "should contain event type" "event: test_summary"
      result |> Expect.stringContains "should contain total" "\"total\":10"
      result |> Expect.stringContains "should contain passed" "\"passed\":8"
      result |> Expect.stringContains "should end with double newline" "\n\n"
  ]
]
