module SageFs.Tests.DebugAttachJourneyTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Tests.LtStream

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// The full debug attach, end to end, on the real path.
///
/// A test runs in the process that loaded the code, so the daemon holds it there and hands the
/// editor that process to attach a debugger to. The repo's own tests drove both routes with a
/// fake worker, so the part only a real run can show was never checked: that the process the
/// daemon names is a real process, that a real debugger can attach to it, that the held test
/// runs when the debugger lets it go, and that a debugger stopped at a breakpoint is named as
/// what holds the host.
///
/// The debugger is netcoredbg (Samsung), driven through its command-line interpreter:
/// `netcoredbg --interpreter=cli --attach <pid>`. It stops the process it attaches to
/// (`stopped, reason: interrupted`), takes `continue` (`^running`) and `detach`, and sets
/// breakpoints with `break <file>:<line>` (hand run, 2026-11-10, netcoredbg 3.2.0-1092; the plan is
/// docs/plans/debug-row-journeys-run-green.md). A machine without it fails these cases when
/// SAGEFS_REQUIRE_DEBUGGER=1 and says SKIPPED otherwise.

let client () = new HttpClient(BaseAddress = baseUrl (), Timeout = TestTimeouts.debugHoldRequest)

/// The one switch that turns a missing debugger from a skip into a failure. The gate and CI set it.
let debuggerRequired () : bool = Environment.GetEnvironmentVariable "SAGEFS_REQUIRE_DEBUGGER" = "1"

let installCommand = "install netcoredbg (https://github.com/Samsung/netcoredbg/releases) and put it on PATH"

/// The debugger, if this machine has one. `dotnet-debug` is what the dotnet tool installs, and it is an inspector.
let debuggerOnPath () : string option =
  let path = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue ""
  [ "netcoredbg"; "dotnet-debug" ]
  |> List.tryPick (fun name ->
    let dirs = path.Split(Path.PathSeparator)
    dirs
    |> Array.exists (fun dir -> File.Exists(Path.Combine(dir, name)))
    |> function
      | true -> Some name
      | false -> None)

/// What a debugger on a machine can do to a held host, from running each against a real hold on 2026-11-09 and 2026-11-10.
///
/// `dotnet-debug attach <pid>` is a read-only inspector (the console of `dotnet-dump analyze`: `threads`, `clrstack`, `dumpheap`,
/// no `continue`, no breakpoints). While it is attached the host's runtime is suspended and answers nothing, it gives the host back
/// when it quits, and a debugger that is killed instead leaves the host frozen for good. It never makes `Debugger.IsAttached`
/// true, so a release with it attached ends in `released_without_debugger` and the test does not run.
///
/// A managed debugger (netcoredbg) takes over the process through the runtime's debugger transport, which is what the host
/// looks for: a test released under one runs and has a verdict (`attached`, outcome `passed`).
type DebuggerKind =
  | Inspector
  | ManagedDebugger

module DebuggerKind =
  /// The kind of the debugger a command name starts. A name that is not netcoredbg is the inspector this machine has.
  let ofCommand (command: string) : DebuggerKind =
    match command with
    | "netcoredbg" -> ManagedDebugger
    | _ -> Inspector

  /// The status the daemon's release answers for a test released with this kind of debugger attached and let go cleanly.
  let releaseStatus (kind: DebuggerKind) : string =
    match kind with
    | Inspector -> "released_without_debugger"
    | ManagedDebugger -> "attached"

/// The managed debugger this machine can attach with, or None after saying why not (and failing, when one is required). The
/// inspector never qualifies: attached, it freezes the host and the release ends without a debugger.
let managedDebuggerOrNone (what: string) : string option =
  match debuggerOnPath () |> Option.filter (fun name -> DebuggerKind.ofCommand name = ManagedDebugger) with
  | Some debugger -> Some debugger
  | None ->
    match debuggerRequired () with
    | true ->
      failtestf "prerequisite missing: no managed debugger on PATH (dotnet-debug is an inspector and cannot run a released test). Fix: %s" installCommand
    | false ->
      printfn "SKIPPED %s: no managed debugger on PATH (%s)" what installCommand
      None

/// What a debugger printed, in order. A wait for a line ends the moment the line arrives, and the whole history says what the
/// debugger did when a line never came.
[<Sealed>]
type DebuggerOutput() =
  let lines = Channel.CreateUnbounded<string>()
  let history = ConcurrentQueue<string>()
  member _.Add(line: string) : unit =
    history.Enqueue line
    lines.Writer.TryWrite line |> ignore
  member _.History : string list = List.ofSeq history
  /// The first line from here on that `matches`. A line that does not match is passed over and stays in the history.
  member this.Await (what: string) (matches: string -> bool) (budget: TimeSpan) : Task<string> =
    task {
      use cts = new CancellationTokenSource(budget)
      let mutable found : string option = None
      try
        while found.IsNone do
          let! line = lines.Reader.ReadAsync cts.Token
          match matches line with
          | true -> found <- Some line
          | false -> ()
      with :? OperationCanceledException -> ()
      match found with
      | Some line -> return line
      | None ->
        return
          failtestf
            "the debugger never said %s within %ds. It said: %s"
            what
            (int budget.TotalSeconds)
            (this.History |> List.map (fun l -> "[" + l + "]") |> String.concat " | ")
    }

/// A debugger attached to a host, with its console open.
type AttachedDebugger =
  { Process: Process
    Output: DebuggerOutput }

let private send (debugger: AttachedDebugger) (command: string) : unit =
  debugger.Process.StandardInput.WriteLine command
  debugger.Process.StandardInput.Flush()

let private startsWith (prefix: string) (line: string) : bool = line.StartsWith(prefix, StringComparison.Ordinal)

/// Start netcoredbg on the process the daemon named. It stops the process as it attaches and says so, and that line is the
/// proof that it has the process in its hand.
let attachAndStop (command: string) (pid: int) : Task<AttachedDebugger * string> =
  task {
    let psi = ProcessStartInfo(command)
    psi.ArgumentList.Add "--interpreter=cli"
    psi.ArgumentList.Add "--attach"
    psi.ArgumentList.Add(string pid)
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    // netcoredbg writes its command history, `.netcoredbg_hist`, into its working directory: the temp area, not the checkout.
    psi.WorkingDirectory <- Path.GetTempPath()
    SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
    let proc = Process.Start psi
    let output = DebuggerOutput()
    // The debugger stays attached until it is told to leave, so its streams never close. Each is read on a thread of its own.
    SageFs.Tests.TestInfrastructure.DedicatedThread.run "debugger-stdout" (fun () ->
      while not proc.StandardOutput.EndOfStream do
        output.Add(proc.StandardOutput.ReadLine()))
    |> ignore
    SageFs.Tests.TestInfrastructure.DedicatedThread.run "debugger-stderr" (fun () ->
      while not proc.StandardError.EndOfStream do
        output.Add("stderr: " + proc.StandardError.ReadLine()))
    |> ignore
    let debugger = { Process = proc; Output = output }
    let! stopped = output.Await "the stop at the attach" (startsWith "stopped, reason:") TestTimeouts.debugAttach
    return debugger, stopped
  }

/// Let the stopped process go on. The host answers nothing while it is stopped, so this comes before the release.
let resume (debugger: AttachedDebugger) : Task<unit> =
  task {
    send debugger "continue"
    let! _ = debugger.Output.Await "the process running again" (startsWith "^running") TestTimeouts.debugAttach
    return ()
  }

/// End a debugger the way it asks to be ended: `detach` on its console, then wait for it to exit. A debugger that is killed
/// while it holds a runtime stopped leaves the host frozen, so the kill is only for one that did not exit.
let detach (debugger: AttachedDebugger) : Task<unit> =
  task {
    try
      send debugger "detach"
      use cts = new CancellationTokenSource(TestTimeouts.childExit)
      do! debugger.Process.WaitForExitAsync cts.Token
    with _ -> ()
    try
      if not debugger.Process.HasExited then debugger.Process.Kill true
    with _ -> ()
    debugger.Process.Dispose()
  }

let text (body: string) (name: string) : string =
  use doc = JsonDocument.Parse body
  match doc.RootElement.TryGetProperty name with
  | true, el -> el.ToString()
  | _ -> "<missing>"

let number (body: string) (name: string) : int =
  use doc = JsonDocument.Parse body
  match doc.RootElement.TryGetProperty name with
  | true, el when el.ValueKind = JsonValueKind.Number -> el.GetInt32()
  | _ -> -1

/// Post to the daemon. A request the daemon does not answer in the client's own timeout is a failure naming the request: a
/// daemon that never answers is the defect, and a skip would read as a pass.
let postOrFail (http: HttpClient) (path: string) (payload: obj) (what: string) : Task<int * string> =
  task {
    try
      return! postJson http path payload
    with
    | :? TaskCanceledException
    | :? HttpRequestException as ex ->
      return failtestf "the daemon did not answer %s (POST %s) within the request's bound: %s" what path ex.Message
  }

/// Hold a test in the session's host. Anything but a granted hold fails with the daemon's own answer.
let holdTest (http: HttpClient) (sid: string) (name: string) : Task<string> =
  task {
    let! status, body = postOrFail http "/api/live-testing/debug" (dict [ "pattern", name; "sessionId", sid ]) (sprintf "the hold of %s" name)
    match status, text body "status" with
    | 200, "held" -> return body
    | _ -> return failtestf "%s was not held: HTTP %d %s" name status body
  }

/// Release a held test and ask again for as long as the daemon says `still_running`. A release waits up to
/// `Timeouts.debugContinuePark` for the verdict and answers `still_running` when that passes. Each answer is the daemon's own
/// event, so the number of asks comes from a bound on how long a test may take and never from a clock.
let release (http: HttpClient) (sid: string) (ticket: string) : Task<int * string> =
  let rec ask (left: int) =
    task {
      let! status, body = postOrFail http "/api/live-testing/debug/continue" {| ticket = ticket; sessionId = sid |} "the release"
      match status, text body "status" with
      | 200, "still_running" when left > 1 -> return! ask (left - 1)
      | _ -> return status, body
    }
  ask (int (Math.Ceiling(SageFs.Timeouts.debugHold.TotalSeconds / SageFs.Timeouts.debugContinuePark.TotalSeconds)))

/// End a hold a case left open, so a case that failed does not make every case after it answer `hold_already_open` until the
/// daemon's hold window passes. With the debugger gone the host waits its grace for one and answers `released_without_debugger`;
/// a hold that already ended answers at once. A release that cannot be answered is not this case's failure to report.
let private endHold (http: HttpClient) (sid: string) (ticket: string) : Task<unit> =
  task {
    try
      let! _ = release http sid ticket
      ()
    with
    | :? AssertException
    | :? TaskCanceledException
    | :? HttpRequestException -> ()
  }

/// Run `body` with a debugger attached to the host the daemon holds `name` in, let the host go on, and detach whatever happens.
let withAttachedHold
  (command: string)
  (http: HttpClient)
  (sid: string)
  (name: string)
  (body: string -> AttachedDebugger -> Task<'a>)
  : Task<'a> =
  task {
    let! held = holdTest http sid name
    (number held "pid", 0) |> Expect.isGreaterThan "the daemon names a real process, not zero"
    text held "access" |> Expect.equal "the OS lets a debugger attach" "open"
    let! debugger, _ = attachAndStop command (number held "pid")
    let cleanup () : Task =
      task {
        do! detach debugger
        do! endHold http sid (text held "ticket")
      }
    return! ensuring cleanup (fun () -> body held debugger)
  }

/// Hold a real test in the real host, attach a real debugger to the process the daemon names, let the host go on, release the
/// test, and read the daemon's answer: the test ran under the debugger and has a verdict.
let private fullAttach () : Task<unit> =
  task {
    match managedDebuggerOrNone "the attach" with
    | None -> ()
    | Some command ->
      use http = client ()
      let! sid = sessionId http
      sid |> Expect.notEqual "the daemon has a session" ""
      do!
        withAttachedHold command http sid "add infers int" (fun held debugger ->
          task {
            let pid = number held "pid"
            let alive =
              try
                use p = Process.GetProcessById pid
                not p.HasExited
              with _ -> false
            alive |> Expect.isTrue "the process the daemon named is running"
            let stopped = debugger.Output.History |> List.filter (startsWith "stopped, reason:") |> String.concat " | "
            stopped
            |> Expect.stringContains
              (sprintf "the debugger stopped every thread of the process the daemon named (pid %d). It said: %s" pid stopped)
              "stopped threads: all"
            do! resume debugger
            let! status, body = release http sid (text held "ticket")
            status |> Expect.equal (sprintf "the release answers (%s)" body) 200
            text body "status"
            |> Expect.equal "a test released with a managed debugger attached ran under it" (DebuggerKind.releaseStatus ManagedDebugger)
            text body "outcome" |> Expect.notEqual "the test ran and had a verdict" ""
          })
  }

/// A test held at a breakpoint in the host stops the host. The release then has no host to answer it: the daemon says so within
/// its ceiling (`host_not_answering`, HTTP 504), names the debugger that holds the host, and keeps the ticket valid, so the same
/// ticket released after the debugger goes on gives the test's verdict.
///
/// The test has to run from compiled code for a breakpoint in it to bind: code an eval defined has no pdb, and the hold says so
/// with `symbols: eval`. The journeys that edit Hello.fs leave the tests evaluated for the rest of the session, so this case is
/// FIRST in the tier's tree (`LiveTestingShards.tree`), on the session as the runner made it, and it fails when the hold says `eval`.
let private breakpointHoldsTheHost () : Task<unit> =
  task {
    match managedDebuggerOrNone "the breakpoint case" with
    | None -> ()
    | Some command ->
      use http = client ()
      use silentHttp = new HttpClient(BaseAddress = baseUrl (), Timeout = TestTimeouts.debugSilentHostRequest)
      let! sid = sessionId http
      let addLine = lineOf greenAdd
      do! withAttachedHold command http sid affectedTest (fun held debugger ->
          task {
            text held "symbols"
            |> Expect.equal "the test runs from compiled code, so a breakpoint in it can bind" "compiled"
            send debugger (sprintf "break %s:%d" (helloPath ()) addLine)
            // Bound at once (no "pending"): the debugger found the assembly's symbols. The instrumented copy the session runs
            // used to name a pdb that did not exist, and the breakpoint stayed pending for ever.
            let! bound =
              debugger.Output.Await "the breakpoint bound to a line" (fun l -> l.Contains "Breakpoint" && l.Contains "Hello.fs") TestTimeouts.patience
            bound.Contains "pending" |> Expect.isFalse (sprintf "the breakpoint is bound, not pending: %s" bound)
            // " Breakpoint 2 at <file>:<line>": the number the debugger gave it.
            let breakpointNumber = bound.Trim().Split(' ').[1]
            do! resume debugger
            let! status, body = postOrFail silentHttp "/api/live-testing/debug/continue" {| ticket = text held "ticket"; sessionId = sid |} "the release of a test held at a breakpoint"
            let lastSaid = debugger.Output.History |> List.rev |> List.truncate 12 |> List.rev |> String.concat " | "
            status |> Expect.equal (sprintf "the release of a host the debugger stopped is a 504 (%s). The breakpoint was: %s. The debugger's last lines: %s" body bound lastSaid) 504
            text body "status" |> Expect.equal "the daemon says the host is not answering" "host_not_answering"
            text body "message" |> Expect.stringContains "the answer names the debugger that holds the host" "netcoredbg"
            let! _ = debugger.Output.Await "the stop at the breakpoint" (startsWith "stopped, reason: breakpoint") TestTimeouts.patience
            // The breakpoint comes out before the host goes on, so nothing else that runs `add` in this host stops it again.
            send debugger (sprintf "delete %s" breakpointNumber)
            do! resume debugger
            let! releasedStatus, releasedBody = release http sid (text held "ticket")
            releasedStatus |> Expect.equal (sprintf "the same ticket answers once the debugger goes on (%s)" releasedBody) 200
            text releasedBody "status" |> Expect.equal "the test ran under the debugger" "attached"
          })
  }

/// What the session's worker holds when the build confirms an edit. A swap of the worker would lose it, so a session that holds
/// something is confirmed in a throwaway worker and its own worker is left alone.
[<RequireQualifiedAccess>]
type SessionHolds =
  /// A value a person bound by hand in the REPL.
  | AHandBoundValue
  /// Nothing but the test a debugger is holding in the host.
  | OnlyTheHeldTest

/// The pid of the worker the daemon runs the session in (`GET /health`).
let private workerPidOf (http: HttpClient) : Task<int> =
  task {
    let! body = http.GetStringAsync "/health"
    return number body "workerPid"
  }

/// A test held for a debugger is not lost when a build confirms an edit. The edit is a valid one, so the daemon evaluates it and
/// a real build confirms it; the confirmation runs in a throwaway worker and the session's own worker keeps its pid, its
/// REPL state and the hold. The ticket then releases to a verdict. The cost of a swap instead was a 409 `no_such_hold`, because
/// the host the test was held in went with the replaced worker.
let private ticketSurvivesConfirmingBuild (holds: SessionHolds) () : Task<unit> =
  match managedDebuggerOrNone "the confirming-build case" with
  | None -> Task.FromResult()
  | Some command ->
    SageFs.Tests.LiveTestingJourneyTests.journey (fun http sid feed original ->
      task {
        match holds with
        | SessionHolds.AHandBoundValue ->
          let! status, body =
            postOrFail http "/exec" {| code = "let handBound = 42;;"; working_directory = LtEnv.fixtureDir.Value |} "the hand evaluation"
          status |> Expect.equal (sprintf "the REPL took the value (%s)" body) 200
        | SessionHolds.OnlyTheHeldTest -> ()
        let! workerBefore = workerPidOf http
        // The debugger is attached and running through the build, as it is for a person who leaves a test held while they edit.
        do! withAttachedHold command http sid affectedTest (fun held debugger ->
          task {
            do! resume debugger
            drain feed
            note feed "wrote an edit to disk that compiles: the eval runs the tests, a real build confirms them"
            File.WriteAllText(helloPath (), original.Replace(greenAdd, greenAdd + " + 0"))
            let! _ =
              expectFrame feed "the real build confirming the edit"
                (fun f -> provenanceIn affectedTest f = "VerifiedByBuild") TestTimeouts.buildConfirmation
            let! workerAfter = workerPidOf http
            workerAfter |> Expect.equal "the build confirmed in a throwaway worker and the session's own worker was left alone" workerBefore
            let pid = number held "pid"
            let alive =
              try
                use p = Process.GetProcessById pid
                not p.HasExited
              with _ -> false
            alive |> Expect.isTrue (sprintf "the host the test is held in (pid %d) is still running" pid)
            let! status, body = release http sid (text held "ticket")
            status |> Expect.equal (sprintf "the ticket held across the build still releases (%s)" body) 200
            text body "status" |> Expect.equal "the test ran under the debugger" "attached"
          })
        match holds with
        | SessionHolds.AHandBoundValue ->
          let! status, body =
            postOrFail http "/exec" {| code = "handBound + 1;;"; working_directory = LtEnv.fixtureDir.Value |} "the read of the bound value"
          status |> Expect.equal "the REPL answers" 200
          body |> Expect.stringContains "the value bound before the build is still in the session" "43"
        | SessionHolds.OnlyTheHeldTest -> ()
      })

[<Tests>]
let debuggerKindTests =
  testList
    "Debug attach: what each debugger on a machine can do to a held host"
    [ testCase "WHY: dotnet-debug is an inspector and netcoredbg is a managed debugger, and only the second can run a released test" <| fun _ ->
        DebuggerKind.ofCommand "dotnet-debug" |> Expect.equal "the dotnet tool is the inspector" Inspector
        DebuggerKind.ofCommand "netcoredbg" |> Expect.equal "netcoredbg is managed" ManagedDebugger
        DebuggerKind.releaseStatus Inspector |> Expect.equal "an inspector's release ends without a debugger" "released_without_debugger"
        DebuggerKind.releaseStatus ManagedDebugger |> Expect.equal "a managed debugger's release runs the test" "attached" ]

/// First in the tier's tree: it needs the tests to run from compiled code (see `breakpointHoldsTheHost`).
let breakpointJourneyTests =
  testList
    "LT debug breakpoint: a test held at a breakpoint stops the host"
    [ Integration.dedicatedCaseTask "--integration-lt" "LT debug: a test held at a breakpoint makes the release say the host is not answering, and the ticket still works after the debugger goes on" breakpointHoldsTheHost ]

let journeyTests =
  testList
    "LT debug: a debugger attaches to the process the daemon holds a test in"
    [ Integration.dedicatedCaseTask "--integration-lt" "LT debug: a real debugger stops the held host, lets it go, and the release says the test ran under it" fullAttach
      // In this order: the hand-bound value stays in the session's REPL, so the case that holds nothing else runs before it.
      Integration.dedicatedCaseTask "--integration-lt" "LT debug: a test held across a confirming build is not lost when the held test is all the session holds" (ticketSurvivesConfirmingBuild SessionHolds.OnlyTheHeldTest)
      Integration.dedicatedCaseTask "--integration-lt" "LT debug: a test held across a confirming build is not lost when the session holds a hand-bound value" (ticketSurvivesConfirmingBuild SessionHolds.AHandBoundValue) ]
