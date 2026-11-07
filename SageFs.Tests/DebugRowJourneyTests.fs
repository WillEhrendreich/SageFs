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

// NEVER RUN YET. Written 2026-11-07 on a machine with no debugger (dotnet-debug present, licence not accepted), so these two
// cases compile and are registered in `--integration-lt` and have not executed. Until one has, each is a hypothesis.
//
// What they check is what the roadmap item promises: after a test is debugged, the row the user would see (the row behind
// GET /api/live-testing/entries, which every client reads) shows the verdict the debugger run produced, with the provenance
// the daemon named when it held the test, and no normal run happened in between. The HTTP answers are the means.
//
// Run with:  dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-lt --summary
// The debugger must be installed and its licence accepted once: `dotnet-debug attach --accept-license-agreement 1`.
// With SAGEFS_REQUIRE_DEBUGGER=1 a machine without one FAILS with that command, where otherwise it says SKIPPED.

/// The one switch that turns a missing debugger from a skip into a failure (the self-hosted gate sets it).
let private debuggerRequired () : bool = Environment.GetEnvironmentVariable "SAGEFS_REQUIRE_DEBUGGER" = "1"

let private installCommand = "dotnet tool install -g dotnet-debug, then dotnet-debug attach --accept-license-agreement 1"

/// The debugger this machine can attach with, or None after saying why not (and failing, when one is required).
let private debuggerOrNone () : string option =
  match debuggerOnPath (), licenceAccepted () with
  | Some debugger, true -> Some debugger
  | None, _ ->
    match debuggerRequired () with
    | true -> failtestf "prerequisite missing: no dotnet-debug or netcoredbg on PATH. Install it: %s" installCommand
    | false ->
      printfn "SKIPPED the row journey: no debugger on PATH (%s)" installCommand
      None
  | Some _, false ->
    match debuggerRequired () with
    | true -> failtestf "prerequisite missing: the debugger's licence is not accepted. Run: %s" installCommand
    | false ->
      printfn "SKIPPED the row journey: the debugger's licence is not accepted (%s)" installCommand
      None

let private sessionId (http: HttpClient) : Task<string> =
  task {
    let! body = http.GetStringAsync "/api/sessions"
    use doc = JsonDocument.Parse body
    return
      doc.RootElement.GetProperty("sessions").EnumerateArray()
      |> Seq.tryHead
      |> Option.map (fun s -> s.GetProperty("id").GetString())
      |> Option.defaultValue ""
  }

/// The row of one test as the daemon serves it: the status case and the provenance case, or None when it has no row.
let private rowOf (http: HttpClient) (sid: string) (displayName: string) : Task<(string * string) option> =
  task {
    let! body = http.GetStringAsync(sprintf "/api/live-testing/entries?session=%s" (Uri.EscapeDataString sid))
    use doc = JsonDocument.Parse body
    return
      doc.RootElement.EnumerateArray()
      |> Seq.tryFind (fun e -> e.GetProperty("DisplayName").GetString() = displayName)
      |> Option.map (fun e ->
        let status = e.GetProperty("Status").GetProperty("Case").GetString()
        let provenance =
          match e.TryGetProperty "Provenance" with
          | true, p -> p.GetProperty("Case").GetString()
          | false, _ -> ""
        status, provenance)
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

/// Hold the test, release it once the debugger is attached, and answer the hold's body and the release's body.
let private holdAndRelease (http: HttpClient) (sid: string) (name: string) : Task<(string * string) option> =
  task {
    let! held = postOrTimeout http "/api/live-testing/debug" (dict [ "pattern", name; "sessionId", sid ])
    match held with
    | None -> return None
    | Some (status, heldBody) when machineCondition status heldBody -> return None
    | Some (status, heldBody) ->
      status |> Expect.equal (sprintf "%s is held" name) 200
      let! released = postOrTimeout http "/api/live-testing/debug/continue" {| ticket = text heldBody "ticket"; sessionId = sid |}
      match released with
      | Some (releasedStatus, releasedBody) when not (machineCondition releasedStatus releasedBody) -> return Some(heldBody, releasedBody)
      | _ -> return None
  }

/// One test, debugged: its row shows the verdict the debugger run gave it, from the code the hold named.
let private oneTestRow () : Task<unit> =
  task {
    match debuggerOrNone () with
    | None -> ()
    | Some debugger ->
      use http = client ()
      let! sid = sessionId http
      sid |> Expect.notEqual "the daemon has a session" ""
      let name = "add infers int"
      let! held = postOrTimeout http "/api/live-testing/debug" (dict [ "pattern", name; "sessionId", sid ])
      match held with
      | Some (200, heldBody) ->
        let! attached = attachAndAsk debugger (number heldBody "pid")
        let proc, _, _ = attached
        try
          let! released = postOrTimeout http "/api/live-testing/debug/continue" {| ticket = text heldBody "ticket"; sessionId = sid |}
          match released with
          | Some (200, releasedBody) ->
            text releasedBody "status" |> Expect.equal "the test ran under the debugger" "attached"
            let! row = rowOf http sid name
            row
            |> Expect.equal
              "the row shows the debugger run's verdict and where its code came from, with no normal run in between"
              (Some(statusCaseOf (text releasedBody "outcome"), provenanceCaseOf (text heldBody "symbols")))
          | other -> printfn "SKIPPED the row journey: the release was not answered with a verdict (%A)" (other |> Option.map fst)
        finally
          try proc.Kill true with _ -> ()
      | other -> printfn "SKIPPED the row journey: the hold was not granted (%A)" (other |> Option.map fst)
  }

/// Three tests, debugged one after another under one attach: each row shows its own verdict.
let private threeTestsOneAttach () : Task<unit> =
  task {
    match debuggerOrNone () with
    | None -> ()
    | Some debugger ->
      use http = client ()
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
      // The first hold names the process; the debugger then stays on it for all three.
      let! first = postOrTimeout http "/api/live-testing/debug" (dict [ "pattern", List.head names; "sessionId", sid ])
      match first with
      | Some (200, firstBody) ->
        let! attached = attachAndAsk debugger (number firstBody "pid")
        let proc, _, _ = attached
        try
          let! firstRelease = postOrTimeout http "/api/live-testing/debug/continue" {| ticket = text firstBody "ticket"; sessionId = sid |}
          let! rest =
            task {
              let results = ResizeArray<string * (string * string) option>()
              for name in List.tail names do
                let! both = holdAndRelease http sid name
                results.Add((name, both))
              return List.ofSeq results
            }
          let verdicts =
            (List.head names, firstRelease |> Option.map (fun (_, b) -> firstBody, b))
            :: (rest |> List.map (fun (n, both) -> n, both))
          for name, both in verdicts do
            match both with
            | Some (heldBody, releasedBody) when text releasedBody "status" = "attached" ->
              let! row = rowOf http sid name
              row
              |> Expect.equal
                (sprintf "%s: the row shows its own debug verdict" name)
                (Some(statusCaseOf (text releasedBody "outcome"), provenanceCaseOf (text heldBody "symbols")))
            | other -> failtestf "%s did not run under the one attach: %A" name other
        finally
          try proc.Kill true with _ -> ()
      | other -> printfn "SKIPPED the row journey: the first hold was not granted (%A)" (other |> Option.map fst)
  }

let journeyTests =
  testList
    "LT debug rows: a debugged test's row shows the verdict, on every client, without a normal run"
    [ Integration.dedicatedCaseTask "--integration-lt" "LT debug rows: one debugged test's row shows its verdict and provenance" oneTestRow
      Integration.dedicatedCaseTask "--integration-lt" "LT debug rows: three tests debugged under one attach each show their own verdict" threeTestsOneAttach ]
