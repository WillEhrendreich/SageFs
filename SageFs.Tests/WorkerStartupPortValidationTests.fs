/// The port a worker prints is where the daemon will send every request for the life of the session. The worker binds
/// 127.0.0.1 itself, so a port line naming any other host is not a worker we started: `WorkerStartup.await` must refuse it
/// (a spawn failure, never a ready) instead of handing it to the session as the place to send eval requests.
module SageFs.Tests.WorkerStartupPortValidationTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs

/// What the start told the caller, as the one thing that happened first.
[<RequireQualifiedAccess>]
type private StartOutcome =
  | Ready of baseUrl: string
  | SpawnFailed of reason: string
  | NothingYet

/// Start a fake worker whose whole stdout is `printed`, then which stays alive, and report the first thing the start said.
let private firstOutcome (printed: string) : Task<StartOutcome> =
  task {
    let script = Path.Combine(Path.GetTempPath(), sprintf "fake-port-worker-%s.sh" (Guid.NewGuid().ToString "N"))
    File.WriteAllText(script, sprintf "echo %s\nsleep %d\n" printed (int TestTimeouts.readyBudget.TotalSeconds))
    // Run it THROUGH /bin/sh rather than exec'ing the file. Executing a file this test just wrote races with every other
    // test in the parallel suite that forks: a child can inherit the still-open write handle and the exec then fails with
    // ETXTBSY ("Text file busy"), which errored this case in 10 ms in the first full run of the merged tree.
    let psi = ProcessStartInfo("/bin/sh")
    psi.ArgumentList.Add script
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    let proc = Process.Start psi
    try
      let first = TaskCompletionSource<StartOutcome>()
      let events : WorkerStartupEvents =
        { OnProgress = ignore
          OnReady = fun _ url -> first.TrySetResult(StartOutcome.Ready url) |> ignore
          OnSpawnFailed = fun _ reason -> first.TrySetResult(StartOutcome.SpawnFailed reason) |> ignore
          OnStartTimedOut = fun _ _ _ -> first.TrySetResult(StartOutcome.NothingYet) |> ignore
          OnAppOutput = fun _ _ -> () }
      let budget : StartBudget =
        { Attempt = 1
          Inactivity = TestTimeouts.patience
          Absolute = TestTimeouts.readyBudget
          Earlier = [] }
      WorkerStartup.await StartLedger.closed proc CancellationToken.None budget events
      let! finished = Task.WhenAny(first.Task, Task.Delay TestTimeouts.patience)
      match obj.ReferenceEquals(finished, first.Task) with
      | true -> return first.Task.Result
      | false -> return StartOutcome.NothingYet
    finally
      try if not proc.HasExited then proc.Kill(true) with _ -> ()
      try proc.Dispose() with _ -> ()
      try File.Delete script with _ -> ()
  }

[<Tests>]
let tests =
  testList "Worker startup: the port line is validated" [
    testTask "a port line naming a host other than loopback is a spawn failure, never ready" {
      match OperatingSystem.IsWindows() with
      | true -> skiptest "the fake worker is a shell script"
      | false ->
        let! outcome = firstOutcome "WORKER_PORT=http://evil.example.com:9999"
        match outcome with
        | StartOutcome.SpawnFailed reason ->
          reason |> Expect.stringContains "the reason says the host is not loopback" "loopback"
          reason |> Expect.stringContains "and names the URL it refused" "evil.example.com"
        | StartOutcome.Ready url -> failtestf "a non-loopback port line was accepted as ready: %s" url
        | StartOutcome.NothingYet -> failtest "the start said nothing about a non-loopback port line"
    }

    testTask "a loopback port line is still ready" {
      match OperatingSystem.IsWindows() with
      | true -> skiptest "the fake worker is a shell script"
      | false ->
        let! outcome = firstOutcome "WORKER_PORT=http://127.0.0.1:54321"
        match outcome with
        | StartOutcome.Ready url -> url |> Expect.equal "the URL is handed over unchanged" "http://127.0.0.1:54321"
        | StartOutcome.SpawnFailed reason -> failtestf "a loopback port line was refused: %s" reason
        | StartOutcome.NothingYet -> failtest "the start said nothing about a loopback port line"
    }
  ]
