module SageFs.Tests.McpStdioBridgeE2ETests

open System
open System.Diagnostics
open System.Text
open System.Threading.Tasks
open Expecto
open Expecto.Flip

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private sageFsExe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()

/// The one real end-to-end regression test: spawn the BUILT `sagefs mcp` as
/// a real process on a fresh port with no daemon running yet — the exact
/// shape of the bug report (a client that starts before the daemon gets
/// ECONNREFUSED and never recovers) — write real JSON-RPC to its stdin, and
/// assert a well-formed MCP response comes back on stdout with the tools in
/// it. Nothing here is faked: not the process, not the daemon it spawns, not
/// the protocol bytes.
let private runBridgeSmokeTest () : Task<unit> =
  task {
    let mcpPort, _dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
    let dataDir = IO.Path.Combine(IO.Path.GetTempPath(), "sagefs-test", "mcp-stdio-e2e", Guid.NewGuid().ToString("N"))

    let psi = ProcessStartInfo()
    psi.FileName <- sageFsExe
    psi.ArgumentList.Add("mcp")
    psi.ArgumentList.Add("--mcp-port")
    psi.ArgumentList.Add(string mcpPort)
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.StandardOutputEncoding <- Encoding.UTF8
    psi.StandardInputEncoding <- Encoding.UTF8
    psi.Environment["SAGEFS_DATA_DIR"] <- dataDir

    use proc = Process.Start(psi)
    let stderrBuf = Text.StringBuilder()
    proc.ErrorDataReceived.Add(fun e -> match e.Data with null -> () | line -> stderrBuf.AppendLine(line) |> ignore)
    proc.BeginErrorReadLine()

    // The daemon this spawns needs real warmup time (build-free but still
    // ASP.NET Core startup) — generous but bounded, same order of magnitude
    // as HttpApiIntegrationTests' own daemon-readiness budget.
    let readTimeout = TimeSpan.FromSeconds(90.0)

    let readLineWithTimeout () : Task<string option> =
      task {
        let readTask = proc.StandardOutput.ReadLineAsync()
        let! completed = Task.WhenAny(readTask, Task.Delay(readTimeout))
        match Object.ReferenceEquals(completed, readTask) with
        | true -> return Option.ofObj readTask.Result
        | false -> return None
      }

    let daemonPid = ref (None: int option)
    try
      try
        do!
          proc.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"e2e-test","version":"0.0.1"}}}"""
          )
        do! proc.StandardInput.FlushAsync()

        let! initLine = readLineWithTimeout ()
        match initLine with
        | None ->
          failwith (
            sprintf
              "sagefs mcp produced no stdout line within %gs — exited=%b exitCode=%s stderr:\n%s"
              readTimeout.TotalSeconds
              proc.HasExited
              (if proc.HasExited then string proc.ExitCode else "n/a")
              (stderrBuf.ToString())
          )
        | Some line ->
          line |> Expect.stringContains "the initialize response names the server" "\"name\":\"SageFs\""
          line |> Expect.stringContains "the initialize response carries the request id back" "\"id\":1"

          // Confirm the daemon this bridge started is real, then hold its
          // pid for cleanup — probed the same way the CLI's own `status`
          // command does.
          match SageFs.DaemonState.readOnPort mcpPort with
          | Some info -> daemonPid.Value <- Some info.Pid
          | None -> ()

          do! proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""")
          do! proc.StandardInput.FlushAsync()

          let! toolsLine = readLineWithTimeout ()
          match toolsLine with
          | None -> failwith (sprintf "no tools/list response within %gs — stderr:\n%s" readTimeout.TotalSeconds (stderrBuf.ToString()))
          | Some line2 ->
            line2 |> Expect.stringContains "the tools/list response carries the request id back" "\"id\":2"
            line2 |> Expect.stringContains "the real tool catalogue came back, not an empty list" "\"send_fsharp_code\""
            line2 |> Expect.stringContains "another real tool, confirming this is the actual SageFs server" "\"create_session\""
      finally
        // Let the bridge terminate its MCP session and exit cleanly.
        try
          proc.StandardInput.Close()
        with _ -> ()
        proc.WaitForExit(15_000) |> ignore
        match proc.HasExited with
        | false -> (try proc.Kill() with _ -> ())
        | true -> ()
    finally
      // The daemon `sagefs mcp` spawned is detached by design (other
      // clients are meant to share it) — this test is the only "other
      // client", so it is responsible for taking it back down. Kill only
      // the exact pid this test observed, never by name.
      match daemonPid.Value with
      | Some pid ->
        try
          let d = Process.GetProcessById(pid)
          match d.HasExited with
          | false -> d.Kill()
          | true -> ()
        with _ -> ()
      | None -> ()
  }

[<Tests>]
let tests =
  Integration.hostCase
    "sagefs mcp: cold start with no daemon running answers initialize and tools/list on stdout"
    (fun () -> runBridgeSmokeTest () |> _.GetAwaiter() |> _.GetResult())
