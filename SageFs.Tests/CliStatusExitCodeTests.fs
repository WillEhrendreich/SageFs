module SageFs.Tests.CliStatusExitCodeTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs

// The `sagefs status` decision logic is exercised through Program.statusCommand
// with the daemon lookup and the live-session-count fetch injected, mirroring
// CliStopExitCodeTests.fs's treatment of `stopCommand`. Nothing here starts,
// probes, or stops a real daemon, and nothing here makes a real HTTP call.
//
// This is the pure half of DaemonIntegrationTests.fs's real-process
// "SageFs status returns 1 when no daemon running" claim: the DECISION (no
// daemon found -> print "No daemon running", exit 1) lives here; the real
// process/argument-parsing WIRE claim is left to the one retained CLI smoke.

let private mkDaemonInfo pid =
  { Pid = pid
    Port = 37749
    DashboardPort = 37750
    StartedAt = DateTime.UtcNow
    WorkingDirectory = Path.Combine(Path.GetTempPath(), "sagefs-status-tests")
    Version = "test"
    ApiVersion = None
    SessionCount = None; ComponentFailures = [] }

let private daemonOnPort = mkDaemonInfo 4242

let private noWedgedPid : int -> int option = fun _ -> None

let private runStatusAt mcpPort readOnPort wedgedPid fetchSessionCount =
  let origOut = Console.Out
  use outWriter = new StringWriter()
  Console.SetOut(outWriter)
  try
    let code = Program.statusCommand readOnPort wedgedPid fetchSessionCount mcpPort
    code, outWriter.ToString()
  finally
    Console.SetOut(origOut)

let private runStatusWedged readOnPort wedgedPid fetchSessionCount =
  runStatusAt 37749 readOnPort wedgedPid fetchSessionCount

/// The real probe against a port nothing listens on. The probe asks the
/// dashboard port (mcpPort + 1), so that is the port the OS hands back free.
let private runStatusOnPort readOnPort mcpPort =
  runStatusAt mcpPort readOnPort noWedgedPid (fun _ -> failtest "no daemon, so the session count must never be fetched")

let private freePort () =
  use listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
  listener.Start()
  (listener.LocalEndpoint :?> System.Net.IPEndPoint).Port

let private runStatus readOnPort fetchSessionCount =
  runStatusWedged readOnPort noWedgedPid fetchSessionCount

[<Tests>]
let cliStatusExitCodeTests =
  testSequenced <| testList "Cli.status exit codes" [
    test "no daemon running exits non-zero and keeps the message" {
      let code, stdout =
        runStatus (fun _ -> None) (fun _ -> failtest "no daemon, so the session count must never be fetched")
      Expect.isTrue "no-daemon status must exit non-zero" (code <> 0)
      stdout |> Expect.stringContains "stdout keeps the no-daemon message" "No daemon running"
    }

    test "no daemon running says what to do next: start it, or register it with an agent, and where the docs are" {
      let code, stdout =
        runStatus (fun _ -> None) (fun _ -> failtest "no daemon, so the session count must never be fetched")
      Expect.equal "still exit 1" 1 code
      stdout |> Expect.stringContains "says how to start it" "run `sagefs`"
      stdout |> Expect.stringContains "says how to register it with an agent" "claude mcp add sagefs -- sagefs mcp"
      stdout |> Expect.stringContains "says how to make that registration global" "-s user"
      stdout |> Expect.stringContains "points at the tool docs" "docs/mcp-tools.md"
    }

    test "a refused connection is the expected answer to is-it-running, so the real probe logs no warning and status still exits 1" {
      let port = freePort ()
      let mcpPort = port - 1
      let warnings = System.Collections.Concurrent.ConcurrentQueue<string>()
      let origWarn = Utils.Log.logWarn
      Utils.Log.logWarn <- fun s -> warnings.Enqueue s
      try
        let code, stdout = runStatusOnPort DaemonState.readOnPort mcpPort
        Expect.equal "no daemon exits 1" 1 code
        stdout |> Expect.stringContains "still says no daemon" "No daemon running"
        warnings
        |> Seq.filter (fun w -> w.Contains(string port))
        |> List.ofSeq
        |> Expect.isEmpty "a refused connection must not warn"
      finally
        Utils.Log.logWarn <- origWarn
    }

    test "WHY — Program.statusCommand — a running daemon reports its info and exits 0 whether or not the session count could be fetched" {
      let code, stdout = runStatus (fun _ -> Some daemonOnPort) (fun _ -> None)
      Expect.equal "a running daemon is success" 0 code
      stdout |> Expect.stringContains "reports it is running" "SageFs daemon running"
      stdout |> Expect.stringContains "reports the pid" "4242"
      (stdout.Contains "Sessions:")
      |> Expect.isFalse "an unfetchable session count is omitted, not reported as zero"
    }

    test "a running daemon whose session count IS fetched reports it" {
      let code, stdout = runStatus (fun _ -> Some daemonOnPort) (fun _ -> Some 3)
      Expect.equal "still success" 0 code
      stdout |> Expect.stringContains "reports the live count" "Sessions:   3 active"
    }

    test "WHY — Program.statusCommand — a wedged daemon (no HTTP answer, live local pid) is reported distinctly from no daemon running, with its pid as the recovery handle" {
      let code, stdout =
        runStatusWedged (fun _ -> None) (fun _ -> Some 9001) (fun _ -> failtest "a wedged daemon's session count is never fetched over the HTTP that never answers")
      Expect.isTrue "a wedged daemon is not success" (code <> 0)
      stdout |> Expect.stringContains "says it is wedged" "wedged"
      stdout |> Expect.stringContains "names the pid to recover" "9001"
      (stdout.Contains "No daemon running")
      |> Expect.isFalse "a wedged daemon must never read the same as no daemon at all"
    }
  ]

// `CliCommand.parse` routes "status" and "stop" to distinct pure decisions
// (statusCommand / stopCommand above); this is the pure half of the routing
// itself, closing the gap the real-process CLI smoke used to be the only
// proof of. Purely a function of args — no daemon, no process.
[<Tests>]
let cliCommandRoutingTests =
  testList "Program.CliCommand.parse routes daemon subcommands" [
    test "status routes to Status" {
      Program.CliCommand.parse [| "status" |] |> Expect.equal "status" Program.CliCommand.Status
    }
    test "stop routes to Stop" {
      Program.CliCommand.parse [| "stop" |] |> Expect.equal "stop" Program.CliCommand.Stop
    }
    test "--mcp-port after status is not mistaken for a new subcommand" {
      Program.CliCommand.parse [| "status"; "--mcp-port"; "39990" |]
      |> Expect.equal "still Status" Program.CliCommand.Status
    }
  ]
