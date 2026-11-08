/// P13, run by real processes: the conductor's identity survives a daemon restart.
///
/// A real daemon (own port pair, own SAGEFS_DATA_DIR, owned by this test process) and two real `sagefs mcp` bridges, each
/// a client of its own. The first bridge joins a cohort and is its conductor. The daemon is killed and started again on the
/// same port with the same data directory (an upgrade), the bridges re-initialize by themselves (the daemon's answer is a
/// new MCP session), and the first bridge is still the same member: still the conductor, and its next claim is accepted.
/// Before the client key the member id was a fingerprint of the session id, so the restart made the conductor a stranger
/// (finding C3, observed 2026-11-07: `mcp:m-ed00…` became `mcp:m-ac28…`).
///
/// The bridges are driven the way a client drives them, over stdio: lines in, lines out, stderr drained on its own thread.
module SageFs.Tests.BridgeIdentityIntegrationTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private sageFsExe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()

let private repoRoot = RepoPaths.repoPathFull [||]

let private memberIdPattern = Regex(@"mcp:m-[0-9a-f]{16}")

let private killProcess (proc: Process) =
  try
    if not proc.HasExited then
      proc.Kill(entireProcessTree = true)
      proc.WaitForExit TestTimeouts.patience |> ignore
  with _ -> ()
  proc.Dispose()

/// A daemon on `port` with `dataDir`, owned by this test process, answering /health. The same port and data directory
/// start it again after a kill, which is what an upgrade does.
let private startDaemon (port: int) (dataDir: string) : Task<Process> =
  task {
    let psi = ProcessStartInfo()
    psi.FileName <- sageFsExe
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.WorkingDirectory <- repoRoot
    psi.ArgumentList.Add "--mcp-port"
    psi.ArgumentList.Add(string port)
    let self = Process.GetCurrentProcess()
    psi.ArgumentList.Add "--owner-pid"
    psi.ArgumentList.Add(string self.Id)
    psi.ArgumentList.Add "--owner-start"
    psi.ArgumentList.Add(string (self.StartTime.ToUniversalTime().Ticks))
    psi.ArgumentList.Add "--ttl"
    psi.ArgumentList.Add "10m"
    SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
    psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
    let proc = Process.Start psi
    use client = new Net.Http.HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" port), Timeout = TestTimeouts.httpProbe)
    let deadline = DateTime.UtcNow.Add Timeouts.integrationDaemonReady
    let mutable ready = false
    while not ready && DateTime.UtcNow < deadline do
      do! Task.Delay Timeouts.cohortLandingPoll
      try
        let! response = client.GetAsync "/health"
        ready <- int response.StatusCode > 0
      with _ -> ()
    match ready with
    | true -> return proc
    | false ->
      killProcess proc
      return failwithf "the daemon did not answer /health on port %d within %O" port Timeouts.integrationDaemonReady
  }

/// One `sagefs mcp` process, a client of its own. Its stdout is read by a task of its own into a channel, so a test waits
/// for the line it wants (a reply with an id, a notification) with a deadline and nothing is lost between two reads.
type private Bridge(port: int, dataDir: string) =
  let lines = Channel.CreateUnbounded<string>()
  let stderrText = StringBuilder()
  let mutable nextId = 100
  let proc =
    let psi = ProcessStartInfo()
    psi.FileName <- sageFsExe
    psi.ArgumentList.Add "mcp"
    psi.ArgumentList.Add "--mcp-port"
    psi.ArgumentList.Add(string port)
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.StandardOutputEncoding <- Encoding.UTF8
    psi.StandardInputEncoding <- Encoding.UTF8
    SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
    psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
    let started = Process.Start psi
    started.ErrorDataReceived.Add(fun e -> match e.Data with null -> () | text -> lock stderrText (fun () -> stderrText.AppendLine text |> ignore))
    started.BeginErrorReadLine()
    started
  do
    let rec pump () =
      async {
        let! line = proc.StandardOutput.ReadLineAsync() |> Async.AwaitTask
        match line with
        | null -> lines.Writer.TryComplete() |> ignore
        | text ->
          lines.Writer.TryWrite text |> ignore
          return! pump ()
      }
    Started.andForget (pump ())

  member _.Stderr = lock stderrText (fun () -> stderrText.ToString())

  member _.Send(line: string) : Task =
    task {
      do! proc.StandardInput.WriteLineAsync line
      do! proc.StandardInput.FlushAsync()
    }

  /// The next line that satisfies `wanted`, or a failure naming `what` with the bridge's stderr.
  member this.WaitFor (what: string) (wanted: string -> bool) : Task<string> =
    task {
      use cts = new CancellationTokenSource(TestTimeouts.daemonFirstOutput)
      let mutable found = None
      try
        while found.IsNone do
          let! more = lines.Reader.WaitToReadAsync cts.Token
          match more with
          | false -> failwithf "the bridge's stdout ended while waiting for %s. stderr:\n%s" what this.Stderr
          | true ->
            let mutable line = null
            while found.IsNone && lines.Reader.TryRead(&line) do
              if wanted line then found <- Some line
      with :? OperationCanceledException ->
        failwithf "no %s from the bridge within %O. stderr:\n%s" what TestTimeouts.daemonFirstOutput this.Stderr
      return found.Value
    }

  /// A JSON-RPC request; the reply with its id.
  member this.Request (methodName: string) (paramsJson: string) : Task<JsonDocument> =
    task {
      let id = Interlocked.Increment(&nextId)
      do! this.Send(sprintf """{"jsonrpc":"2.0","id":%d,"method":"%s","params":%s}""" id methodName paramsJson)
      let hasId (line: string) =
        try
          use doc = JsonDocument.Parse line
          let mutable value = Unchecked.defaultof<JsonElement>
          doc.RootElement.TryGetProperty("id", &value) && value.ValueKind = JsonValueKind.Number && value.GetInt32() = id
        with _ -> false
      let! line = this.WaitFor (sprintf "the reply to %s" methodName) hasId
      return JsonDocument.Parse line
    }

  member this.Initialize() : Task =
    task {
      use! _ =
        this.Request "initialize" """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"bridge-identity-test","version":"0.0.1"}}"""
      do! this.Send """{"jsonrpc":"2.0","method":"notifications/initialized"}"""
    }

  /// A tool call; the text of its answer.
  member this.Call (tool: string) (arguments: (string * string) list) : Task<string> =
    task {
      let argsJson = JsonSerializer.Serialize(dict arguments)
      use! reply = this.Request "tools/call" (sprintf """{"name":"%s","arguments":%s}""" tool argsJson)
      let result = reply.RootElement.GetProperty "result"
      return result.GetProperty("content").[0].GetProperty("text").GetString()
    }

  member _.Stop() =
    try proc.StandardInput.Close() with _ -> ()
    try proc.WaitForExit TestTimeouts.childExit |> ignore with _ -> ()
    killProcess proc

  interface IAsyncDisposable with
    member this.DisposeAsync() = ValueTask(Task.Run(fun () -> this.Stop()))

let private isToolsListChanged (line: string) = line.Contains "notifications/tools/list_changed"

let private idsIn (text: string) : Set<string> = memberIdPattern.Matches text |> Seq.map (fun m -> m.Value) |> Set.ofSeq

let private conductorLine (status: string) : string =
  status.Split('\n') |> Array.tryFind (fun line -> line.Contains "onductor" && memberIdPattern.IsMatch line) |> Option.defaultValue ""

[<Tests>]
let tests =
  Integration.hostList "A conductor behind a stdio bridge survives a daemon restart (real daemon, real bridges)" [

    testTask "the same bridge is the same member, and still the conductor, after the daemon is killed and started again" {
      let port, _dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
      let dataDir = Path.Combine(Path.GetTempPath(), "sagefs-test-bridge-identity", Guid.NewGuid().ToString "N")
      Directory.CreateDirectory dataDir |> ignore
      let! first = startDaemon port dataDir
      let mutable daemon = Some first
      use! (conductor: Bridge) = Task.FromResult(new Bridge(port, dataDir))
      use! (other: Bridge) = Task.FromResult(new Bridge(port, dataDir))
      try
        do! conductor.Initialize()
        do! other.Initialize()
        let join (bridge: Bridge) (name: string) =
          bridge.Call "join_cohort" [ "agentName", name; "role", "Implementer"; "working_directory", repoRoot ]
        let! _ = join conductor "conductor"
        let! _ = join other "other"
        let status (bridge: Bridge) = bridge.Call "get_cohort_status" [ "working_directory", repoRoot ]
        let! before = status conductor
        let membersBefore = idsIn before
        membersBefore |> Set.count |> Expect.equal "two bridges are two members" 2
        let seatedBefore = conductorLine before
        seatedBefore |> Expect.isNotEmpty (sprintf "the status names a conductor:\n%s" before)

        // An upgrade: the daemon stops and starts again on the same port with the same ledger.
        killProcess first
        daemon <- None
        let! restarted = startDaemon port dataDir
        daemon <- Some restarted

        // Each bridge notices the daemon is back, re-initializes by itself and tells its client the lists may have changed.
        let! _ = conductor.WaitFor "the conductor bridge's reconnect notice" isToolsListChanged
        let! _ = other.WaitFor "the other bridge's reconnect notice" isToolsListChanged

        let! after = status conductor
        idsIn after |> Expect.equal (sprintf "no stranger appeared and nobody was lost:\n%s" after) membersBefore
        conductorLine after |> Expect.equal "the same member is still the conductor" seatedBefore

        // The call needs a seat: it is accepted only if the daemon knows this bridge as the member who joined.
        let! (claim: string) = conductor.Call "acquire_claim" [ "agentName", "conductor"; "scope", "file:SageFs.Core/MemberTable.fs"; "purpose", "identity survives"; "working_directory", repoRoot ]
        claim |> Expect.stringContains "the conductor's claim after the restart was accepted, not refused for want of a seat" "Acquired claim"

        // The second bridge is its own member before and after.
        let! otherAfter = status other
        idsIn otherAfter |> Expect.equal "the second client is still the second member" membersBefore      finally
        match daemon with
        | Some proc -> killProcess proc
        | None -> ()
    }
  ]
