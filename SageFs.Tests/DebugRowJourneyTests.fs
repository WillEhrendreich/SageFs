module SageFs.Tests.DebugRowJourneyTests

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Tests.LtStream
open SageFs.Tests.DebugAttachJourneyTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

// What these check is what the roadmap item promises: after a test is debugged, the row the user would see (the row behind
// GET /api/live-testing/entries, which every client reads) shows the verdict the debugger run produced, with the provenance
// the daemon named when it held the test, and no normal run happened in between. The HTTP answers are the means.
//
// The debugger is netcoredbg (see DebugAttachJourneyTests for what it does and how it is driven). They ran first in the
// release gate on 2026-11-08 with no debugger on the machine and failed; the hand run of 2026-11-10 with netcoredbg showed the
// protocol (attach stops the host, `continue` lets it go, the release then runs the test) and they are written from it.
//
// Run with:  dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-lt --summary
// With SAGEFS_REQUIRE_DEBUGGER=1 a machine without netcoredbg FAILS with where to get it, where otherwise it says SKIPPED.

/// The duration a debug verdict records. The host measured the time the test sat at breakpoints too, so the daemon records none,
/// and a row that carries a real duration was written by a normal run.
let private noDuration = "00:00:00"

/// The row of one test as the daemon serves it: the status case, the provenance case and the duration the verdict carries, or
/// None when it has no row.
let private rowOf (http: HttpClient) (sid: string) (displayName: string) : Task<(string * string * string) option> =
  task {
    let! body = http.GetStringAsync(sprintf "/api/live-testing/entries?session=%s" (Uri.EscapeDataString sid))
    use doc = JsonDocument.Parse body
    return
      doc.RootElement.EnumerateArray()
      |> Seq.tryFind (fun e -> e.GetProperty("DisplayName").GetString() = displayName)
      |> Option.map (fun e ->
        let status = e.GetProperty("Status")
        let provenance =
          match e.TryGetProperty "Provenance" with
          | true, p -> p.GetProperty("Case").GetString()
          | false, _ -> ""
        let duration =
          match status.TryGetProperty "Fields" with
          | true, fields when fields.GetArrayLength() > 0 -> fields[0].ToString()
          | _ -> ""
        status.GetProperty("Case").GetString(), provenance, duration)
  }

/// What the continue answered for a test, as the row's status case.
let private statusCaseOf (outcome: string) : string =
  match outcome with
  | "passed" -> "Passed"
  | "failed" -> "Failed"
  | other -> failtestf "the debug run's outcome was %s, which is not a verdict a row can show" other

/// What the hold said about the code, as the row's provenance case.
let private provenanceCaseOf (symbols: string) : string =
  match symbols with
  | "compiled" -> "Compiled"
  | "eval" -> "Evaluated"
  | other -> failtestf "the hold named symbols %s" other

/// No confirming build is still working for the session, so a journey starts from rest whatever ran before it. The journey before
/// restores Hello.fs, which asks a build to confirm; that build replaces the worker, and a test held for a debugger dies with the
/// worker it was held in (in the gate the release answered 502 `worker_failed`, "Connection reset by peer"). The wait is on the
/// daemon's own status and the stream's frames, never a timer. How many tests fail is no concern of this journey (an earlier
/// case may leave one red), so the suite's verdict is not waited for, only the confirmation phase to be `idle`.
let private fromRest (http: HttpClient) : Task<unit> =
  task {
    let feed = openFeed ()
    return!
      ensuring (fun () -> feed.Stop()) (fun () ->
        task {
          let! _ = expectFrame feed "SSE feed connecting" (fun f -> f.Event = "session") TestTimeouts.patience
          do! awaitConfirmed feed http TestTimeouts.buildConfirmation
        })
  }

/// Debug one test with the debugger already attached: hold it, release it, and answer what the hold and the release said.
let private debugUnderAttach (http: HttpClient) (sid: string) (name: string) : Task<string * string> =
  task {
    let! held = holdTest http sid name
    let! status, released = release http sid (text held "ticket")
    status |> Expect.equal (sprintf "%s: the release answers (%s)" name released) 200
    text released "status" |> Expect.equal (sprintf "%s ran under the debugger" name) "attached"
    return held, released
  }

/// One test, debugged: its row shows the verdict the debugger run gave it, from the code the hold named.
let private oneTestRow () : Task<unit> =
  task {
    match managedDebuggerOrNone "the row journey" with
    | None -> ()
    | Some command ->
      use http = client ()
      do! fromRest http
      let! sid = sessionId http
      sid |> Expect.notEqual "the daemon has a session" ""
      let name = "add infers int"
      do!
        withAttachedHold command http sid name (fun held debugger ->
          task {
            do! resume debugger
            let! status, released = release http sid (text held "ticket")
            status |> Expect.equal (sprintf "the release answers (%s)" released) 200
            text released "status" |> Expect.equal "the test ran under the debugger" "attached"
            let! row = rowOf http sid name
            row
            |> Expect.equal
              "the row shows the debugger run's verdict and where its code came from, with no normal run in between"
              (Some(statusCaseOf (text released "outcome"), provenanceCaseOf (text held "symbols"), noDuration))
          })
  }

/// Three tests, debugged one after another under one attach: each row shows its own verdict.
let private threeTestsOneAttach () : Task<unit> =
  task {
    match managedDebuggerOrNone "the row journey" with
    | None -> ()
    | Some command ->
      use http = client ()
      do! fromRest http
      let! sid = sessionId http
      sid |> Expect.notEqual "the daemon has a session" ""
      let! entries = http.GetStringAsync(sprintf "/api/live-testing/entries?session=%s" (Uri.EscapeDataString sid))
      let names =
        use doc = JsonDocument.Parse entries
        doc.RootElement.EnumerateArray()
        |> Seq.map (fun e -> e.GetProperty("DisplayName").GetString())
        |> Seq.distinct
        |> Seq.truncate 3
        |> Seq.toList
      names.Length |> Expect.equal "the sample has at least three tests to debug" 3
      // The first hold names the process; the debugger then stays on it for all three, and the host takes the next test when
      // the one before it has ended.
      do!
        withAttachedHold command http sid (List.head names) (fun firstHeld debugger ->
          task {
            do! resume debugger
            let! firstStatus, firstReleased = release http sid (text firstHeld "ticket")
            firstStatus |> Expect.equal (sprintf "%s: the release answers (%s)" (List.head names) firstReleased) 200
            text firstReleased "status" |> Expect.equal "the first test ran under the debugger" "attached"
            let verdicts = ResizeArray<string * string * string>()
            verdicts.Add((List.head names, text firstHeld "symbols", text firstReleased "outcome"))
            for name in List.tail names do
              let! held, released = debugUnderAttach http sid name
              verdicts.Add((name, text held "symbols", text released "outcome"))
            for name, symbols, outcome in verdicts do
              let! row = rowOf http sid name
              row
              |> Expect.equal (sprintf "%s: the row shows its own debug verdict" name) (Some(statusCaseOf outcome, provenanceCaseOf symbols, noDuration))
          })
  }

let journeyTests =
  testList
    "LT debug rows: a debugged test's row shows the verdict, on every client, without a normal run"
    [ Integration.dedicatedCaseTask "--integration-lt" "LT debug rows: one debugged test's row shows its verdict and provenance" oneTestRow
      Integration.dedicatedCaseTask "--integration-lt" "LT debug rows: three tests debugged under one attach each show their own verdict" threeTestsOneAttach ]
