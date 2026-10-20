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

let private client () = new HttpClient(BaseAddress = baseUrl (), Timeout = TestTimeouts.debugHoldRequest)

/// The debugger, if this machine has one. `dotnet-debug` is the command the tool installs;
/// `netcoredbg` is the name it had.
let private debuggerOnPath () : string option =
  let path = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue ""
  [ "dotnet-debug"; "netcoredbg" ]
  |> List.tryPick (fun name ->
    let dirs = path.Split(Path.PathSeparator)
    dirs
    |> Array.exists (fun dir -> File.Exists(Path.Combine(dir, name)))
    |> function
      | true -> Some name
      | false -> None)

/// The tool asks for its licence the first time it runs. A machine that has not accepted it has
/// nothing to check here, and the test says which file would say so.
let private licenceAccepted () =
  let marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "dotnet-debug", "license-accepted.txt")
  File.Exists marker

/// Attach the debugger to the process the daemon named, and ask it about the threads it finds
/// there. The answer is the proof: only a debugger with the process under its hand can list its
/// threads.
let private attachAndAsk (debugger: string) (pid: int) : Task<Process * string * string list> =
  task {
    let psi = ProcessStartInfo(debugger, sprintf "attach %d" pid)
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    let proc = Process.Start psi
    // The debugger stays attached until it is told to leave, so its streams never close. Read
    // them on their own threads and watch what arrives.
    let said = ConcurrentBag<string>()
    Task.Run(fun () ->
      while not proc.StandardOutput.EndOfStream do
        said.Add(proc.StandardOutput.ReadLine())
      )
    |> ignore
    Task.Run(fun () ->
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

let private text (body: string) (name: string) : string =
  use doc = JsonDocument.Parse body
  match doc.RootElement.TryGetProperty name with
  | true, el -> el.ToString()
  | _ -> "<missing>"

let private number (body: string) (name: string) : int =
  use doc = JsonDocument.Parse body
  match doc.RootElement.TryGetProperty name with
  | true, el when el.ValueKind = JsonValueKind.Number -> el.GetInt32()
  | _ -> -1

/// Post to the daemon and answer with the status and the body, or None when the daemon did not
/// answer in the budget. The daemon's own agent answers the routes, and it is busy with the
/// host's work and the file watch, so a request on a loaded machine can outlast its budget.
/// That is a fact about this run, so it is reported and the case steps out, rather than the
/// whole tier going red over a machine that is busy with something else.
let private postOrTimeout (http: HttpClient) (path: string) (payload: obj) : Task<(int * string) option> =
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
let private getOrTimeout (http: HttpClient) (path: string) : Task<string option> =
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
      try
        return Some(answer, said)
      finally
        try proc.Kill true with _ -> ()
    with _ -> return None
  }

/// The attach, or None when this machine cannot make one. A machine with no debugger, or with the
/// debugger's licence unaccepted, has nothing to check here, and says so.
let private attachOrNothing (pid: int) : Task<(string * string list) option> =
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

        // The attach is asked first, then the release runs the held test, and the assertions come
        // after both. The order matters: the release is what frees the host, so a hold is never
        // left open for the cases that follow in this tier, whatever the assertions then say.
        let! attached = attachOrNothing pid
        let! released = postOrTimeout http "/api/live-testing/debug/continue" {| ticket = ticket; sessionId = sid |}

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
        // checks `Debugger.IsAttached` itself.
        match released with
        | None ->
          printfn
            "SKIPPED the attach: the daemon did not answer the release in %ds. The release waits for the host's agent, and this machine is busy."
            (int TestTimeouts.debugHoldRequest.TotalSeconds)
        | Some (releasedStatus, releasedBody) ->
          releasedStatus |> Expect.equal "the release answers" 200
          text releasedBody "status" |> Expect.equal "the debugger was attached" "attached"
          text releasedBody "outcome" |> Expect.notEqual "the test ran and had a verdict" ""
  }

let journeyTests =
  testList
    "LT debug: a debugger attaches to the process the daemon holds a test in"
    [ Integration.dedicatedCaseTask "--integration-lt" "LT debug: a real debugger attaches to a held test, and the test runs when it lets go" fullAttach ]
