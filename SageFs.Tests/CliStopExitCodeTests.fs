module SageFs.Tests.CliStopExitCodeTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

// The `sagefs stop` decision logic is exercised through Program.stopCommand with
// every daemon interaction injected, so automation can distinguish a successful
// stop (exit 0) from a no-op: stale-PID and no-daemon cases must exit NON-zero.
// Nothing here starts, probes, or stops a real daemon.

let private mkDaemonInfo pid =
  { Pid = pid
    Port = 37749
    DashboardPort = 37750
    StartedAt = DateTime.UtcNow
    WorkingDirectory = Path.Combine(Path.GetTempPath(), "sagefs-stop-tests")
    Version = "test"
    ApiVersion = None
    SessionCount = None }

let private daemonOnPort = mkDaemonInfo 4242

let private runStop readOnPort requestShutdown killProcess waitForExit =
  let origOut = Console.Out
  let origErr = Console.Error
  use outWriter = new StringWriter()
  use errWriter = new StringWriter()
  Console.SetOut(outWriter)
  Console.SetError(errWriter)
  try
    let code = Program.stopCommand readOnPort requestShutdown killProcess waitForExit 37749
    code, outWriter.ToString(), errWriter.ToString()
  finally
    Console.SetOut(origOut)
    Console.SetError(origErr)

[<Tests>]
let cliStopExitCodeTests =
  testSequenced <| testList "Cli.stop exit codes" [
    test "no daemon running exits non-zero and keeps the message" {
      let code, stdout, _ =
        runStop (fun _ -> None) (fun _ -> failtest "no daemon, so requestShutdown must not be called") (fun _ -> failtest "no daemon, so kill must not be called") (fun _ -> failtest "no daemon, so nothing to wait for")
      Expect.isTrue "no-daemon stop must exit non-zero" (code <> 0)
      stdout |> Expect.stringContains "stdout keeps the no-daemon message" "No daemon running"
    }

    test "WHY — Program.stopCommand — a graceful stop is reported once the daemon has exited because printing success while it keeps running misleads scripts" {
      let code, stdout, _ =
        runStop (fun _ -> Some daemonOnPort) (fun _ -> true) (fun _ -> failtest "graceful shutdown must not fall back to kill") (fun _ -> Program.StopWait.Exited)
      Expect.equal "successful graceful stop exits 0" 0 code
      stdout |> Expect.stringContains "stdout says it stopped" "Daemon stopped (PID 4242)"
    }

    test "WHY — Program.stopCommand — a daemon that ignores the shutdown request is killed because stop must end it" {
      let code, stdout, stderr =
        runStop (fun _ -> Some daemonOnPort) (fun _ -> true) (fun _ -> Program.StopKilled) (fun _ -> Program.StopWait.StillRunning)
      Expect.equal "the fallback kill still stops it" 0 code
      stdout |> Expect.stringContains "stdout says it stopped" "Daemon stopped (PID 4242)"
      stderr |> Expect.stringContains "stderr says why it was killed" "did not exit"
    }

    test "fallback kill success exits zero" {
      let code, stdout, _ =
        runStop (fun _ -> Some daemonOnPort) (fun _ -> false) (fun _ -> Program.StopKilled) (fun _ -> failtest "a refused request has nothing to wait for")
      Expect.equal "successful fallback kill exits 0" 0 code
      stdout |> Expect.stringContains "stdout announces the kill" "Daemon stopped (PID 4242)"
    }

    test "stale PID (process gone at kill time) exits non-zero and keeps the message" {
      let code, stdout, stderr =
        runStop (fun _ -> Some daemonOnPort) (fun _ -> false) (fun _ -> Program.StopProcessGone "process 4242 has already exited") (fun _ -> failtest "a refused request has nothing to wait for")
      Expect.isTrue "stale PID stop must exit non-zero" (code <> 0)
      stdout |> Expect.stringContains "stdout keeps the stale PID message" "Daemon was not running (stale PID 4242)"
      stderr |> Expect.stringContains "stderr keeps the error detail" "Stop daemon error for PID 4242"
    }

    test "no daemon never consults shutdown or kill paths" {
      let code, _, _ = runStop (fun _ -> None) (fun _ -> true) (fun _ -> Program.StopKilled) (fun _ -> Program.StopWait.Exited)
      Expect.isTrue "no-daemon stop must exit non-zero" (code <> 0)
    }
  ]
