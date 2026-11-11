module SageFs.Tests.CliStopExitCodeTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server.WatchdogRunner

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
    SessionCount = None; ComponentFailures = [] }

let private daemonOnPort = mkDaemonInfo 4242

let private noWedgedPid : int -> int option = fun _ -> None

let private runStopWith restartersOf readOnPort wedgedPid requestShutdown killProcess waitForExit =
  let origOut = Console.Out
  let origErr = Console.Error
  use outWriter = new StringWriter()
  use errWriter = new StringWriter()
  Console.SetOut(outWriter)
  Console.SetError(errWriter)
  try
    let code = Program.stopCommand readOnPort wedgedPid requestShutdown killProcess waitForExit restartersOf 37749
    code, outWriter.ToString(), errWriter.ToString()
  finally
    Console.SetOut(origOut)
    Console.SetError(origErr)

let private noRestarters : int -> SageFs.Server.WatchdogRunner.Restarter list = fun _ -> []

let private runStopWedged readOnPort wedgedPid requestShutdown killProcess waitForExit =
  runStopWith noRestarters readOnPort wedgedPid requestShutdown killProcess waitForExit

let private runStop readOnPort requestShutdown killProcess waitForExit =
  runStopWedged readOnPort noWedgedPid requestShutdown killProcess waitForExit

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

    test "WHY — Program.stopCommand — a wedged daemon (no HTTP answer, live local pid) is killed directly rather than reported as no daemon running" {
      let code, stdout, stderr =
        runStopWedged
          (fun _ -> None)
          (fun _ -> Some 9001)
          (fun _ -> failtest "a wedged daemon never answers HTTP, so a graceful shutdown request must not be attempted")
          (fun pid -> pid |> Expect.equal "kills the recorded pid, not the daemon-info's" 9001; Program.StopKilled)
          (fun _ -> failtest "a direct kill has nothing to wait for")
      Expect.equal "the wedged daemon is stopped" 0 code
      stdout |> Expect.stringContains "stdout says it stopped" "Daemon stopped (PID 9001)"
      stderr |> Expect.stringContains "stderr explains it was wedged" "wedged"
    }

    test "WHY — Program.stopCommand — a wedged daemon whose pid is already gone by kill time is reported as a stale PID, not a success" {
      let code, stdout, stderr =
        runStopWedged
          (fun _ -> None)
          (fun _ -> Some 9001)
          (fun _ -> failtest "no HTTP answer means no graceful shutdown attempt")
          (fun _ -> Program.StopProcessGone "process 9001 has already exited")
          (fun _ -> failtest "a direct kill has nothing to wait for")
      Expect.isTrue "a stale wedged pid must exit non-zero" (code <> 0)
      stdout |> Expect.stringContains "stdout keeps the stale PID message" "Daemon was not running (stale PID 9001)"
      stderr |> Expect.stringContains "stderr keeps the error detail" "Stop daemon error for PID 9001"
    }

    test "WHY — Program.stopCommand — a daemon a service will restart is not reported as stopped for good, and the command that stops it is named" {
      let code, stdout, _ =
        runStopWith
          (fun pid -> pid |> Expect.equal "asks about the daemon's pid" 4242; [ Restarter.SystemdUnit "sagefs.service" ])
          (fun _ -> Some daemonOnPort) noWedgedPid (fun _ -> true) (fun _ -> failtest "graceful") (fun _ -> Program.StopWait.Exited)
      code |> Expect.equal "a stop that will not stick has its own exit code" Program.stopWillRestartExitCode
      stdout |> Expect.stringContains "says it will come back" "will come back"
      stdout |> Expect.stringContains "names the command that stops it" "systemctl --user stop sagefs"
    }

    test "WHY — Program.stopCommand — a live sagefs mcp bridge is named, since it starts a new daemon on its next request" {
      let code, stdout, _ =
        runStopWith
          (fun _ -> [ Restarter.Bridge 777 ])
          (fun _ -> Some daemonOnPort) noWedgedPid (fun _ -> true) (fun _ -> failtest "graceful") (fun _ -> Program.StopWait.Exited)
      code |> Expect.equal "will restart" Program.stopWillRestartExitCode
      stdout |> Expect.stringContains "names the bridge pid" "777"
      stdout |> Expect.stringContains "says what it is" "sagefs mcp"
    }
  ]

[<Tests>]
let forceKillTreeTests =
  testList "Program force-kill" [
    test "WHY — a forced stop and sweep --kill end the daemon's whole process tree, so its FSI host workers are not orphaned" {
      // The kill sites are IO edges no test can drive without a real daemon, so the source is the subject here.
      let source = File.ReadAllText(RepoPaths.repoPath [| "SageFs"; "Program.fs" |])
      (source.Contains "proc.Kill()") |> Expect.isFalse "a Kill() without entireProcessTree leaves the FSI hosts running"
      source |> Expect.stringContains "the tree kill is used" "Kill(entireProcessTree = true)"
    }
  ]

[<Tests>]
let uninstallPlanTests =
  let unitFile = "/h/.config/systemd/user/sagefs.service"
  let dropIn = "/h/.config/systemd/user/sagefs.service.d"
  let everything : Program.UninstallPlan.Leftover list =
    [ Program.UninstallPlan.Leftover.Data(Program.UninstallPlan.DataKind.State, "/h/.SageFs")
      Program.UninstallPlan.Leftover.AgentSkill "/h/.claude/skills/sagefs"
      Program.UninstallPlan.Leftover.Daemon 5
      Program.UninstallPlan.Leftover.SystemdUnit [ unitFile; dropIn ] ]
  let none : Program.UninstallPlan.Options = { Stop = false; RemoveService = false; PurgeData = false; Yes = false }
  let performed (steps: Program.UninstallPlan.Step list) =
    steps |> List.choose (fun s -> match s.Run with Program.UninstallPlan.Run.Perform e -> Some e | Program.UninstallPlan.Run.PrintOnly -> None)
  testList "Program.UninstallPlan.plan" [
    test "WHY — by default it only prints: every step is a command for the person to run, the tool uninstall last" {
      let steps = Program.UninstallPlan.plan none Program.UninstallPlan.Shell.Posix everything
      performed steps |> Expect.isEmpty "nothing is done without a flag"
      (List.last steps).Command |> Expect.equal "the tool goes last" "dotnet tool uninstall --global SageFs"
      steps |> List.length |> Expect.equal "one step per leftover and the tool" 5
    }
    test "WHY — the service is removed before the daemon is stopped, since the service would start it again" {
      let steps = Program.UninstallPlan.plan { none with Stop = true; RemoveService = true } Program.UninstallPlan.Shell.Posix everything
      performed steps
      |> Expect.equal "service, then daemon" [ Program.UninstallPlan.Effect.RemoveSystemdUnit [ unitFile; dropIn ]; Program.UninstallPlan.Effect.StopDaemon 5 ]
      steps.Head.Command |> Expect.stringContains "the unit's own stop" "systemctl --user disable --now sagefs"
    }
    test "WHY — data is deleted only with --purge-data AND --yes" {
      Program.UninstallPlan.plan { none with PurgeData = true } Program.UninstallPlan.Shell.Posix everything
      |> performed |> Expect.isEmpty "--purge-data alone deletes nothing"
      Program.UninstallPlan.plan { none with PurgeData = true; Yes = true } Program.UninstallPlan.Shell.Posix everything
      |> performed
      |> Expect.equal "both flags delete the data and the skill" [ Program.UninstallPlan.Effect.DeletePath "/h/.claude/skills/sagefs"; Program.UninstallPlan.Effect.DeletePath "/h/.SageFs" ]
    }
    test "a machine with nothing left still gets the tool uninstall line" {
      Program.UninstallPlan.plan none Program.UninstallPlan.Shell.Posix []
      |> List.map _.Command
      |> Expect.equal "only the tool" [ "dotnet tool uninstall --global SageFs" ]
    }
    test "the flags parse, and an unknown one is refused" {
      Program.UninstallPlan.parseArgs [ "--stop"; "--remove-service" ]
      |> Expect.equal "two flags" (Ok { none with Stop = true; RemoveService = true })
      match Program.UninstallPlan.parseArgs [ "--purge" ] with
      | Error why -> why |> Expect.stringContains "names the flag" "--purge"
      | Ok _ -> failtest "an unknown flag must be refused"
    }
    test "uninstall is a verb" {
      Program.CliCommand.parse [| "uninstall"; "--stop" |] |> Expect.equal "routed" (Program.CliCommand.Uninstall [ "--stop" ])
    }
  ]

[<Tests>]
let watchdogEndTests =
  testList "WatchdogRunner.WatchdogEnd" [
    test "WHY — a watchdog that gives up ends its loop: `return ()` inside a task's while loop did not, so it logged the give-up forever with no daemon" {
      SageFs.Server.WatchdogRunner.endAfter (SageFs.Watchdog.Action.GiveUp "five crashes in a minute")
      |> Expect.equal "the give-up ends it" (Some(WatchdogEnd.GaveUp "five crashes in a minute"))
      [ SageFs.Watchdog.Action.Wait; SageFs.Watchdog.Action.StartDaemon; SageFs.Watchdog.Action.RestartDaemon(TimeSpan.Zero) ]
      |> List.map SageFs.Server.WatchdogRunner.endAfter
      |> Expect.equal "everything else keeps watching" [ None; None; None ]
    }
    test "WHY — a give-up exits non-zero, so systemd's Restart=on-failure and launchd's KeepAlive see a failure" {
      WatchdogEnd.exitCode (WatchdogEnd.GaveUp "x") |> Expect.equal "the named code" SageFs.Server.WatchdogRunner.watchdogGaveUpExitCode
      (SageFs.Server.WatchdogRunner.watchdogGaveUpExitCode <> 0) |> Expect.isTrue "non-zero"
      WatchdogEnd.exitCode WatchdogEnd.Cancelled |> Expect.equal "a stop asked for is success" 0
    }
  ]

[<Tests>]
let supervisionTests =
  testList "WatchdogRunner.Supervision" [
    test "the systemd unit is read off the cgroup path, and the user manager's own unit is not taken for it" {
      Supervision.unitOfCgroup "0::/user.slice/user-1000.slice/user@1000.service/app.slice/sagefs.service\n"
      |> Expect.equal "the unit" (Some "sagefs.service")
      Supervision.unitOfCgroup "0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-graphical.slice/app-x.scope\n"
      |> Expect.equal "a terminal's scope is no unit" None
    }
    test "a bridge is `sagefs mcp`, from the shim or through dotnet, and nothing else" {
      Supervision.isBridgeArgv [ "sagefs"; "mcp" ] |> Expect.isTrue "the shim"
      Supervision.isBridgeArgv [ "/home/u/.dotnet/tools/sagefs"; "mcp" ] |> Expect.isTrue "the shim by path"
      Supervision.isBridgeArgv [ "dotnet"; "/x/tools/net11.0/any/SageFs.dll"; "mcp" ] |> Expect.isTrue "through the muxer"
      Supervision.isBridgeArgv [ "sagefs"; "--no-resume" ] |> Expect.isFalse "a daemon"
      Supervision.isBridgeArgv [ "sagefs"; "call"; "mcp" ] |> Expect.isFalse "mcp as a later word"
      Supervision.isBridgeArgv [ "node"; "mcp" ] |> Expect.isFalse "another program"
    }
    test "a daemon in a unit's cgroup is the unit's; one under a plain watchdog is the watchdog's; bridges are listed" {
      let daemon : Supervision.ProcessFacts = { Pid = 10; Parent = Supervision.ParentProcess.Pid 9; Argv = [ "sagefs" ]; Cgroup = "0::/user.slice/user@1000.service/app.slice/sagefs.service" }
      let watchdog : Supervision.ProcessFacts = { Pid = 9; Parent = Supervision.ParentProcess.Pid 1; Argv = [ "sagefs"; "--supervised" ]; Cgroup = daemon.Cgroup }
      let bridge : Supervision.ProcessFacts = { Pid = 20; Parent = Supervision.ParentProcess.Pid 5; Argv = [ "sagefs"; "mcp" ]; Cgroup = "" }
      let home : Supervision.Ports = { Stopped = 37749; Home = 37749 }
      Supervision.restarters home daemon (Some watchdog) [ bridge ]
      |> Expect.equal "unit and bridge" [ Restarter.SystemdUnit "sagefs.service"; Restarter.Bridge 20 ]
      let plain = { daemon with Cgroup = "0::/user.slice/app.slice/app-x.scope" }
      Supervision.restarters home plain (Some { watchdog with Cgroup = plain.Cgroup }) []
      |> Expect.equal "the watchdog" [ Restarter.Watchdog 9 ]
      Supervision.restarters home plain (Some { watchdog with Argv = [ "bash" ] }) []
      |> Expect.equal "a shell parent restarts nothing" []
    }
    test "WHY — a daemon in some other service's cgroup with no watchdog is restarted by nobody, so a CI runner's unit is not named" {
      let runnerCgroup = "0::/system.slice/actions.runner.sagefs.homepc.service"
      let daemon : Supervision.ProcessFacts = { Pid = 10; Parent = Supervision.ParentProcess.Pid 9; Argv = [ "sagefs"; "--mcp-port"; "41000"; "--ttl"; "20m" ]; Cgroup = runnerCgroup }
      let testHost : Supervision.ProcessFacts = { Pid = 9; Parent = Supervision.ParentProcess.Pid 1; Argv = [ "dotnet"; "SageFs.Tests.dll" ]; Cgroup = runnerCgroup }
      Supervision.restarters { Stopped = 41000; Home = 37749 } daemon (Some testHost) []
      |> Expect.equal "nobody" []
    }
    test "WHY — a bridge restarts only the daemon on its own port, so stopping a test daemon on another port names none" {
      let daemon : Supervision.ProcessFacts = { Pid = 10; Parent = Supervision.ParentProcess.Pid 1; Argv = [ "sagefs"; "--mcp-port"; "41000"; "--ttl"; "20m" ]; Cgroup = "" }
      let homeBridge : Supervision.ProcessFacts = { Pid = 20; Parent = Supervision.ParentProcess.Pid 5; Argv = [ "sagefs"; "mcp" ]; Cgroup = "" }
      let ownBridge : Supervision.ProcessFacts = { homeBridge with Pid = 21; Argv = [ "sagefs"; "mcp"; "--mcp-port"; "41000" ] }
      Supervision.restarters { Stopped = 41000; Home = 37749 } daemon None [ homeBridge; ownBridge ]
      |> Expect.equal "only the bridge on 41000" [ Restarter.Bridge 21 ]
    }
  ]
