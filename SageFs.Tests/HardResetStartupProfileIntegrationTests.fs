module SageFs.Tests.HardResetStartupProfileIntegrationTests

/// #179 through a real daemon: a session whose `.SageFs/init.fsx` binds a fixed port survives hard resets in a row.
/// Before the fix the replacement host loaded the profile while the old host still held the port, so the bind lost
/// every time and the session came back Ready without its app. Each start of the profile appends its process id to a
/// log after the bind succeeded, so a lost bind leaves no line.
///
/// Spawns its own daemon (own port, own data dir) like CohortMcpToolsIntegrationTests, so it runs under
/// --integration-host and not in the default tier.

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private resets = 10

let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

let private call (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, CancellationToken.None)
    return textOf result
  }

/// The profile: bind the app's port, then record which process did. A failed bind throws before the record is written.
let private profileSource (port: int) (log: string) : string =
  String.concat "\n" [
    sprintf "let appListener = System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, %d)" port
    "appListener.Start()"
    sprintf "System.IO.File.AppendAllText(@\"%s\", sprintf \"%%d\\n\" System.Environment.ProcessId)" log ]

let private startedPids (log: string) : int list =
  match File.Exists log with
  | false -> []
  | true -> File.ReadAllLines log |> Array.choose (fun l -> match Int32.TryParse l with | true, pid -> Some pid | false, _ -> None) |> List.ofArray

let private listening (port: int) : Task<bool> =
  task {
    use client = new TcpClient()
    try
      do! client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TestTimeouts.httpProbe)
      return true
    with _ -> return false
  }

/// Waits until the session reports Ready, a minute per call and up to the bound for a worker coming back.
let private untilReady (client: McpClient) (sessionId: string) : Task<unit> =
  task {
    let deadline = DateTime.UtcNow + TestTimeouts.sessionBackAfterRestart
    let mutable ready = false
    while not ready do
      let! status = call client "get_session_status" [ "session_id", box sessionId; "wait_seconds", box TestTimeouts.sessionStatusLongWaitSeconds ]
      ready <- status.Contains "\"lifecycle\":\"Ready\""
      match ready, DateTime.UtcNow > deadline with
      | false, true -> failtestf "session %s did not come back Ready in %O: %s" sessionId TestTimeouts.sessionBackAfterRestart status
      | _ -> ()
  }

[<Tests>]
let tests =
  Integration.hostList "Hard reset of a session with a startup profile" [

    testTask "WHY - a profile that binds a fixed port binds it after every hard reset, because the old host is gone before the new one loads it" {
      let! proc, port = SageFs.Tests.CohortMcpToolsIntegrationTests.startIsolatedDaemonWith []
      let appPort, _ = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
      let folder = Path.Combine(Path.GetTempPath(), "sagefs-test-startup-profile", Guid.NewGuid().ToString "N")
      Directory.CreateDirectory(Path.Combine(folder, ".SageFs")) |> ignore
      let log = Path.Combine(folder, "started.log")
      File.WriteAllText(Path.Combine(folder, ".SageFs", "init.fsx"), profileSource appPort log)
      try
        use! client = SageFs.Tests.CohortMcpToolsIntegrationTests.connect port
        let! created = call client "create_bare_session" [ "working_directory", box folder ]
        let sessionId =
          let m = Regex.Match(created, "[0-9a-f]{8}")
          match m.Success with
          | true -> m.Value
          | false -> failtestf "create_bare_session did not name a session: %s" created
        do! untilReady client sessionId
        startedPids log |> List.length |> Expect.equal "the profile bound the port on the first start" 1
        for reset in 1 .. resets do
          let! answer = call client "hard_reset_fsi_session" [ "session_id", box sessionId ]
          do! untilReady client sessionId
          let pids = startedPids log
          pids |> List.length |> Expect.equal (sprintf "reset %d (%s): the profile bound the port again (log %A)" reset answer pids) (reset + 1)
          pids |> List.distinct |> List.length |> Expect.equal (sprintf "reset %d: every start was a new host" reset) (reset + 1)
          let! up = listening appPort
          up |> Expect.isTrue (sprintf "reset %d: the new host is listening on the app's port" reset)
      finally
        SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon proc
        try Directory.Delete(folder, true) with _ -> ()
    }
  ]
