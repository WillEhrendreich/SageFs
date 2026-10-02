module LemScore.Tests.LegacyTests

open System
open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip
open LemRun
open LemScore.Tests.Samples

let private events () : JsonElement list =
  sampleLines "claude-stream.ndjson" |> Array.filter (fun l -> l.Trim() <> "") |> Array.map (fun l -> JsonDocument.Parse(l).RootElement.Clone()) |> Array.toList

[<Tests>]
let claude =
  testList "the Claude Code harness (run-lemming and score), ported" [
    testCase "its summary is exactly what the jq script it replaced printed for the same stream" <| fun _ ->
      // claude-summary.expected.json was produced by the original scripts/lemmings/score (bash and jq)
      // from claude-stream.ndjson, claude-files.diff and claude-residue.json, before it was removed.
      let diff =
        sampleLines "claude-files.diff"
        |> Array.filter (fun l -> l.StartsWith "< " || l.StartsWith "> ")
        |> Array.map (fun l -> Text.RegularExpressions.Regex.Replace(l, "^[<>] [0-9a-f]+  ", ""))
        |> Array.distinct |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> Array.toList
      let json =
        Legacy.summary (events ())
          { Id = "demo-id"; Model = "sonnet"; ClaudeExit = 0; Seconds = 42; Graceful = "stopped"; Forced = "no"; FixtureTests = "pass"
            ChangedFiles = diff; ResidueSessions = Legacy.residueCount (readSample "claude-residue.json") }
      json.TrimEnd() |> Expect.equal "byte for byte" ((readSample "claude-summary.expected.json").TrimEnd())

    testCase "a stream with no result event still scores, as no-result-event, with nulls where the stream had nothing" <| fun _ ->
      let json =
        Legacy.summary [] { Id = "x"; Model = "m"; ClaudeExit = 124; Seconds = 1; Graceful = "stop-failed"; Forced = "yes"; FixtureTests = "skipped"; ChangedFiles = []; ResidueSessions = None }
      use doc = JsonDocument.Parse json
      doc.RootElement.GetProperty("outcome").GetString() |> Expect.equal "outcome" "no-result-event"
      doc.RootElement.GetProperty("turns").ValueKind |> Expect.equal "turns" JsonValueKind.Null
      doc.RootElement.GetProperty("tools").ValueKind |> Expect.equal "no tools" JsonValueKind.Null
      doc.RootElement.GetProperty("residueSessionsBeforeTeardown").ValueKind |> Expect.equal "unreadable residue" JsonValueKind.Null

    testCase "the residue file may be a list, an object with sessions, or unreadable" <| fun _ ->
      Legacy.residueCount "[{},{}]" |> Expect.equal "array" (Some 2)
      Legacy.residueCount """{"sessions":[{"id":"a"}]}""" |> Expect.equal "object" (Some 1)
      Legacy.residueCount "null" |> Expect.equal "null is none" (Some 0)
      Legacy.residueCount "not json" |> Expect.isNone "unreadable"
  ]
