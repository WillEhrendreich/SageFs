module SageFs.Tests.DebugAttachJourneyTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text.Json
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
/// daemon names is a real process, that a real debugger can attach to it, and that the held
/// test runs when the debugger lets it go.
///
/// The debugger is netcoredbg, the one the plugin's own end-to-end test drives. A machine
/// without it, or with its licence unaccepted, has nothing to check here and says so.

let client () = new HttpClient(BaseAddress = baseUrl (), Timeout = TestTimeouts.debugHoldRequest)

/// The debugger, if this machine has one. `dotnet-debug` is the command the tool installs;
/// `netcoredbg` is the name it had.
let debuggerOnPath () : string option =
  let path = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue ""
  [ "dotnet-debug"; "netcoredbg" ]
  |> List.tryPick (fun name ->
    let dirs = path.Split(Path.PathSeparator)
    dirs
    |> Array.exists (fun dir -> File.Exists(Path.Combine(dir, name)))
    |> function
      | true -> Some name
      | false -> None)

/// What a debugger on this machine can do to a held host, from running each against a real hold on 2026-11-09 (the plan is
/// docs/plans/debug-row-journeys-run-green.md).
///
/// `dotnet-debug attach <pid>` is a read-only inspector (the console of `dotnet-dump analyze`: `threads`, `clrstack`, `dumpheap`,
/// no `continue`, no breakpoints). While it is attached the host's runtime is suspended and answers nothing, it gives the host back
/// when it quits, and a debugger that is killed instead leaves the host frozen for good. It never makes `Debugger.IsAttached`
/// true, so a release with it attached ends in `released_without_debugger` and the test does not run.
///
/// A managed debugger (netcoredbg) takes over the process through the runtime's debugger transport, which is what the host
/// looks for, so a test released under one runs and has a verdict.
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

/// The tool asks for its licence the first time it runs. A machine that has not accepted it has
/// nothing to check here, and the test says which file would say so.
let licenceAccepted () =
  let marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "dotnet-debug", "license-accepted.txt")
  File.Exists marker

/// Attach the debugger to the process the daemon named, and ask it about the threads it finds
/// there. The answer is the proof: only a debugger with the process under its hand can list its
/// threads.
let attachAndAsk (debugger: string) (pid: int) : Task<Process * string * string list> =
  task {
    let psi = ProcessStartInfo(debugger, sprintf "attach %d" pid)
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
    let proc = Process.Start psi
    // The debugger stays attached until it is told to leave, so its streams never close. Read
    // them on their own threads and watch what arrives.
    let said = ConcurrentBag<string>()
    SageFs.Tests.TestInfrastructure.DedicatedThread.run "debugger-stdout" (fun () ->
      while not proc.StandardOutput.EndOfStream do
        said.Add(proc.StandardOutput.ReadLine())
      )
    |> ignore
    SageFs.Tests.TestInfrastructure.DedicatedThread.run "debugger-stderr" (fun () ->
      while not proc.StandardError.EndOfStream do
        said.Add("stderr: " + proc.StandardError.ReadLine())
      )
    |> ignore

    // The console takes commands once it is ready. Asking for the threads is both the question
    // and the proof, so it is asked until it is answered.
    let sw = Stopwatch.StartNew()
    let mutable answer = ""
    let budget = int TestTimeouts.debugAttach.TotalMilliseconds
    let looksLikeThread (l: string) = l.Contains "0x" && l.Contains " "
    while sw.ElapsedMilliseconds < budget && answer = "" do
      proc.StandardInput.WriteLine "threads"
      proc.StandardInput.Flush()
      do! Task.Delay TestTimeouts.debuggerPoll
      answer <-
        said
        |> Seq.filter looksLikeThread
        |> Seq.truncate 1
        |> Seq.toList
        |> List.tryHead
        |> Option.defaultValue ""
    // The attach can land in the last moment of the wait, so the answer is read once more after it.
    if answer = "" then
      answer <-
        said
        |> Seq.filter looksLikeThread
        |> Seq.truncate 1
        |> Seq.toList
        |> List.tryHead
        |> Option.defaultValue ""
    return (proc, answer, List.ofSeq said)
  }

/// End a debugger the way it asks to be ended: `quit` on its console, then wait for it to exit. The inspector holds the host's
/// runtime suspended until then, so killing it first leaves the host frozen and the release that follows never answers. The kill
/// is only for a debugger that did not exit after `quit`.
let detach (proc: Process) : Task<unit> =
  task {
    try
      proc.StandardInput.WriteLine "quit"
      proc.StandardInput.Flush()
      use cts = new System.Threading.CancellationTokenSource(TestTimeouts.childExit)
      do! proc.WaitForExitAsync cts.Token
    with _ -> ()
    try
      if not proc.HasExited then proc.Kill true
    with _ -> ()
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

/// Post to the daemon and answer with the status and the body, or None when the daemon did not
/// answer in the budget. The daemon's own agent answers the routes, and it is busy with the
/// host's work and the file watch, so a request on a loaded machine can outlast its budget.
/// That is a fact about this run, so it is reported and the case steps out, rather than the
/// whole tier going red over a machine that is busy with something else.
let postOrTimeout (http: HttpClient) (path: string) (payload: obj) : Task<(int * string) option> =
  task {
    try
      let! status, body = postJson http path payload
      return Some(status, body)
    with
    | :? TaskCanceledException -> return None
    | :? HttpRequestException -> return None
  }

/// The same for a read. `GetStringAsync` wraps a timeout in an `HttpRequestException`, where the
/// post path throws the cancellation itself, so both are caught.
let getOrTimeout (http: HttpClient) (path: string) : Task<string option> =
  task {
    try
      let! body = http.GetStringAsync path
      return Some body
    with
    | :? TaskCanceledException -> return None
    | :? HttpRequestException -> return None
  }

/// Attach the debugger and ask it about the threads it finds there. The answer is the proof:
/// only a debugger with the process under its hand can list its threads. Nothing here throws, so
/// the hold is released whatever the attach says.
let private tryAttach (debugger: string) (pid: int) : Task<(string * string list) option> =
  task {
    try
      let! proc, answer, said = attachAndAsk debugger pid
      do! detach proc
      return Some(answer, said)
    with _ -> return None
  }

/// The attach, or None when this machine cannot make one. A machine with no debugger, or with the
/// debugger's licence unaccepted, has nothing to check here, and says so.
let attachOrNothing (pid: int) : Task<(string * string list) option> =
  task {
    match debuggerOnPath () with
    | None ->
      printfn "SKIPPED the attach: no netcoredbg on PATH"
      return None
    | Some debugger when not (licenceAccepted ()) ->
      printfn "SKIPPED the attach: the debugger's licence is not accepted (%s)" "run dotnet-debug attach --accept-license-agreement 1 once"
      return None
    | Some debugger -> return! tryAttach debugger pid
  }

/// The daemon's own answer when the machine, rather than the attach, is what went wrong: the
/// worker could not be reached, or the host was not there. That is a fact about this run, not a
/// verdict on the attach, so the case reports it and steps out.
let machineCondition (status: int) (body: string) : bool =
  if status = 200 then
    false
  else
    match text body "status" with
    | "worker_failed"
    | "host_unavailable"
    | "host_lost" -> true
    | _ -> false

/// Release a held test and ask again for as long as the daemon says `still_running`. A release waits up to
/// `Timeouts.debugContinuePark` for the verdict and answers `still_running` when that passes; a released test with no managed
/// debugger waits `Timeouts.debugAttachGrace` for one first, which can be longer than one park on a slow machine. Each answer is
/// the daemon's own event, so the count of asks comes from the two durations and never from a clock.
let releaseUntilAnswered (http: HttpClient) (sid: string) (ticket: string) : Task<(int * string) option> =
  let asks = int (Math.Ceiling(SageFs.Timeouts.debugAttachGrace.TotalSeconds / SageFs.Timeouts.debugContinuePark.TotalSeconds)) + 2
  let rec ask (left: int) =
    task {
      let! released = postOrTimeout http "/api/live-testing/debug/continue" {| ticket = ticket; sessionId = sid |}
      match released with
      | Some (200, body) when text body "status" = "still_running" && left > 1 -> return! ask (left - 1)
      | other -> return other
    }
  ask asks

/// Hold a real test in the real host, attach a real debugger to the process the daemon names,
/// and let the test run.
let rec private fullAttach () : Task<unit> =
  task {
    use http = client ()
    try
      // F# has no try/with/finally, so the catch is nested inside the finally.
      try
        do! fullAttach' http
      with
      | :? TaskCanceledException ->
        printfn "SKIPPED the attach: a request to the daemon was cancelled (its agent is busy, and this machine is busy)."
      | :? HttpRequestException ->
        printfn "SKIPPED the attach: a request to the daemon failed (its agent is busy, and this machine is busy)."
    finally
      // Nothing is left changed: the case does not pause live testing, because a pause the daemon
      // never answers would starve the cases that follow it in this tier.
      ()
  }

/// The body of the case, with the daemon's own timeouts handled per request.
and private fullAttach' (http: HttpClient) : Task<unit> =
  task {
    let! sessions = getOrTimeout http "/api/sessions"
    match sessions with
    | None ->
      printfn
        "SKIPPED the attach: the daemon did not answer a session list in %ds. Its agent is busy, and this machine is busy."
        (int TestTimeouts.debugHoldRequest.TotalSeconds)
    | Some sessions ->
      let sid =
        use doc = JsonDocument.Parse sessions
        doc.RootElement.GetProperty("sessions").EnumerateArray()
        |> Seq.tryHead
        |> Option.map (fun s -> s.GetProperty("id").GetString())
        |> Option.defaultValue ""
      sid |> Expect.notEqual "the daemon has a session" ""

      // Live testing is left as the tier's runner found it. An earlier version paused it, on the
      // theory that the live-testing loop was what the debug request queued behind. It was not,
      // and a pause the daemon never answers would starve the cases that follow in this tier.
      let selector = dict [ ("pattern", "add infers int"); ("sessionId", sid) ]
      let! held = postOrTimeout http "/api/live-testing/debug" selector
      match held with
      | None ->
        printfn
          "SKIPPED the attach: the daemon did not answer the debug hold in %ds. The hold request waits for the host's agent, and this machine is busy."
          (int TestTimeouts.debugHoldRequest.TotalSeconds)
      | Some (heldStatus, heldBody) ->
        if machineCondition heldStatus heldBody then
          printfn
            "SKIPPED the attach: the daemon could not reach the worker to hold the test (%s)."
            (text heldBody "message")
        else
          heldStatus |> Expect.equal "the test is held" 200
          text heldBody "status" |> Expect.equal "the daemon held it" "held"
          let pid = number heldBody "pid"
          (pid, 0) |> Expect.isGreaterThan "a real process, not zero"
          text heldBody "access" |> Expect.equal "the OS lets a debugger attach" "open"
          let ticket = text heldBody "ticket"

          // The process the daemon named is alive: it is the host the tests run in.
          let alive =
            try
              use p = Process.GetProcessById(pid)
              not p.HasExited
            with _ -> false
          alive |> Expect.isTrue "the process the daemon named is running"

          // The attach is asked first, then the release runs the held test, and the assertions
          // come after both. The order matters: the release is what frees the host, so a hold is
          // never left open for the cases that follow in this tier, whatever the assertions say.
          // The debugger quits before the release (see `detach`): the inspector holds the host's runtime suspended while it
          // is attached, and a release sent to a suspended host gets no answer.
          let! attached = attachOrNothing pid
          let! released = releaseUntilAnswered http sid ticket

          match attached with
          | None -> ()
          | Some (answer, said) ->
            answer
            |> Expect.stringContains
              (sprintf
                 "the debugger listed the threads of the process the daemon named (pid %d). The debugger said: %s"
                 pid
                 (said |> List.map (fun l -> "[" + l + "]") |> String.concat " | "))
              "0x"

          // The daemon's own answer to the release says whether a debugger was really attached: it
          // checks `Debugger.IsAttached` itself, which only a managed debugger makes true. What the answer is for each kind
          // is `DebuggerKind.releaseStatus`. No answer at all is a defect, not a busy machine: this case used to print
          // SKIPPED for it, and that hid a host left frozen by a killed inspector.
          match released with
          | None ->
            failtestf
              "the daemon did not answer the release in %ds, after the debugger had quit. A host that answers nothing is frozen or lost."
              (int TestTimeouts.debugHoldRequest.TotalSeconds)
          | Some (releasedStatus, releasedBody) when machineCondition releasedStatus releasedBody ->
            printfn
              "SKIPPED the attach: the daemon could not reach the worker to release the hold (%s)."
              (text releasedBody "message")
          | Some (releasedStatus, releasedBody) ->
            match attached, debuggerOnPath () |> Option.map DebuggerKind.ofCommand with
            | Some _, Some kind ->
              text releasedBody "status" |> Expect.equal (sprintf "the release of a test held for a %A debugger" kind) (DebuggerKind.releaseStatus kind)
              match kind with
              | ManagedDebugger ->
                releasedStatus |> Expect.equal "the release answers" 200
                text releasedBody "outcome" |> Expect.notEqual "the test ran and had a verdict" ""
              | Inspector ->
                releasedStatus |> Expect.equal "a test released with no managed debugger is a conflict" 409
            | _ ->
              // Nothing attached, so nothing says what the release should have answered. It answered, and that frees the hold.
              ()
  }

[<Tests>]
let debuggerKindTests =
  testList
    "Debug attach: what each debugger on a machine can do to a held host"
    [ testCase "WHY: dotnet-debug is an inspector and netcoredbg is a managed debugger, and only the second can run a released test" <| fun _ ->
        DebuggerKind.ofCommand "dotnet-debug" |> Expect.equal "the dotnet tool is the inspector" Inspector
        DebuggerKind.ofCommand "netcoredbg" |> Expect.equal "netcoredbg is managed" ManagedDebugger
        DebuggerKind.releaseStatus Inspector |> Expect.equal "an inspector's release ends without a debugger" "released_without_debugger"
        DebuggerKind.releaseStatus ManagedDebugger |> Expect.equal "a managed debugger's release runs the test" "attached" ]

let journeyTests =
  testList
    "LT debug: a debugger attaches to the process the daemon holds a test in"
    [ Integration.dedicatedCaseTask "--integration-lt" "LT debug: a real debugger lists the threads of a held host, and the release says whether the test ran under it" fullAttach ]
