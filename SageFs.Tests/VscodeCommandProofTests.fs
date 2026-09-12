/// Proves that real SageFs VS Code commands reach the real daemon and change
/// its real state — replacing the CDP/keystroke-driven "DoD journeys" that
/// used to live in VscodeExtensionTests.fs (HR-VSC-E2E, LT-VSC-E2E), which
/// never passed once since being wired into CI (command-palette title
/// matching, activity-bar CSS selectors, and a --remote-debugging-port
/// connection that went unreachable on two different Windows runner images
/// — see issue #133).
///
/// The mechanism here is VS Code's own official extension-test API,
/// @vscode/test-electron: it loads a real extension host and lets a test
/// script call `vscode.commands.executeCommand` directly — the same stable
/// API the command palette itself dispatches through, minus the palette,
/// minus any DOM, minus any debug port. The whole class of automation
/// flakiness the old journeys hit cannot occur here by construction.
///
/// Every line of test LOGIC is F#, compiled to JS by Fable
/// (sagefs-vscode/test-electron/) — Node.js is a RUNTIME here (the same
/// relationship the shipped extension already has to Node), never a
/// test-authoring language. See sagefs-vscode/test-electron/Bindings.fs's
/// header for the rule this file's own tests are structured around.
///
/// This is a normal [Integration] Host suite — it owns its own daemon on
/// reserved ports, same as every other real-daemon test in this project —
/// runnable via --integration-host with no separate CLI flag or CI job.
module SageFs.Tests.VscodeCommandProofTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private repoRoot =
  Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private vscodeExtensionDir =
  Path.Combine(repoRoot, "sagefs-vscode")

let private launcherPath =
  Path.Combine(vscodeExtensionDir, "test-electron-dist", "launcher.cjs")

let private suitePath =
  Path.Combine(vscodeExtensionDir, "test-electron-dist", "suite.cjs")

let private extensionEntryPath =
  Path.Combine(vscodeExtensionDir, "dist", "Extension.js")

let private sampleProject =
  Path.Combine(
    repoRoot, "samples", "from-csharp", "SageFs.Samples.FromCSharp",
    "SageFs.Samples.FromCSharp.fsproj")

let private sampleDir = Path.GetDirectoryName(sampleProject)

let private nodeExe () =
  match OperatingSystem.IsWindows() with
  | true -> "node.exe"
  | false -> "node"

let private pickFreePort () =
  use l = new TcpListener(IPAddress.Loopback, 0)
  l.Start()
  (l.LocalEndpoint :?> IPEndPoint).Port

let rec private findPortPair attempts =
  let mcp = pickFreePort ()
  let dash = mcp + 1
  try
    use probe = new TcpListener(IPAddress.Loopback, dash)
    probe.Start()
    mcp
  with
  | :? SocketException when attempts > 0 -> findPortPair (attempts - 1)
  | :? SocketException -> failwith "could not find a free port pair"

/// Everything a proof-suite run needs, torn down together in `finally`.
type private Fixture = {
  DaemonProcess: Process
  DataDir: string
  UserDataDir: string
  McpPort: int
  DashboardPort: int
  SessionId: string
}

let private startDaemonWithWebLiveSession () : Fixture =
  let mcpPort = findPortPair 5
  let dashboardPort = mcpPort + 1
  let dataDir =
    Path.Combine(Path.GetTempPath(), "sagefs-vsc-proof", Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory(dataDir) |> ignore

  let psi = ProcessStartInfo()
  psi.FileName <- SageFs.Tests.TestInfrastructure.SageFsBinary.path ()
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- repoRoot
  psi.ArgumentList.Add("--mcp-port")
  psi.ArgumentList.Add(string mcpPort)
  psi.ArgumentList.Add("--no-resume")
  psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
  psi.Environment["SAGEFS_HOT_RELOAD"] <- "true"
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  let daemon = Process.Start(psi)
  daemon.BeginOutputReadLine()
  daemon.BeginErrorReadLine()

  use client = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" mcpPort))
  client.Timeout <- TimeSpan.FromSeconds(5.0)

  let healthDeadline = DateTime.UtcNow.AddSeconds(60.0)
  let mutable healthy = false
  while not healthy && DateTime.UtcNow < healthDeadline do
    try
      use _resp = client.GetAsync("/health").GetAwaiter().GetResult()
      healthy <- true
    with _ -> Threading.Thread.Sleep(250)
  match healthy with
  | false -> failwithf "daemon did not become healthy on port %d" mcpPort
  | true -> ()

  let payload =
    JsonSerializer.Serialize(
      {| projects = [| sampleProject |]
         workingDirectory = sampleDir
         workflow = "WebLive" |})
  use content = new StringContent(payload, Encoding.UTF8, "application/json")
  let createStatus = client.PostAsync("/api/sessions/create", content).GetAwaiter().GetResult()
  match createStatus.IsSuccessStatusCode with
  | false -> failwithf "session create failed: %d" (int createStatus.StatusCode)
  | true -> ()

  let readyDeadline = DateTime.UtcNow.AddSeconds(300.0)
  let mutable ready = false
  let mutable faulted = false
  let mutable sessionId = ""
  while not ready && not faulted && DateTime.UtcNow < readyDeadline do
    try
      let body = client.GetStringAsync("/api/sessions").GetAwaiter().GetResult()
      use doc = JsonDocument.Parse(body)
      let sessions = doc.RootElement.GetProperty("sessions").EnumerateArray() |> Seq.toList
      faulted <- sessions |> List.exists (fun s -> s.GetProperty("status").GetString() = "Faulted")
      match sessions |> List.tryFind (fun s -> s.GetProperty("status").GetString() = "Ready") with
      | Some s ->
        ready <- true
        sessionId <- s.GetProperty("id").GetString()
      | None -> ()
    with _ -> Threading.Thread.Sleep(1000)
  match faulted, ready with
  | true, _ -> failwith "session Faulted during warmup"
  | _, false -> failwith "session never reached Ready within 300s"
  | _, true -> ()

  let userDataDir =
    Path.Combine(Path.GetTempPath(), "sagefs-vsc-proof", Guid.NewGuid().ToString("N") + "-userdata")
  Directory.CreateDirectory(Path.Combine(userDataDir, "User")) |> ignore
  let settings =
    JsonSerializer.Serialize(
      {| ``sagefs.mcpPort`` = mcpPort
         ``sagefs.dashboardPort`` = dashboardPort
         ``security.workspace.trust.enabled`` = false
         ``telemetry.telemetryLevel`` = "off"
         ``update.mode`` = "none"
         ``extensions.autoCheckUpdates`` = false |})
  File.WriteAllText(Path.Combine(userDataDir, "User", "settings.json"), settings)

  { DaemonProcess = daemon
    DataDir = dataDir
    UserDataDir = userDataDir
    McpPort = mcpPort
    DashboardPort = dashboardPort
    SessionId = sessionId }

let private stopFixture (f: Fixture) =
  try
    if not f.DaemonProcess.HasExited then f.DaemonProcess.Kill(entireProcessTree = true)
  with _ -> ()
  try f.DaemonProcess.WaitForExit(5000) |> ignore with _ -> ()
  f.DaemonProcess.Dispose()
  try Directory.Delete(f.DataDir, true) with _ -> ()
  try Directory.Delete(f.UserDataDir, true) with _ -> ()

let private runProofSuite (f: Fixture) : Task<int * string> = task {
  let psi = ProcessStartInfo()
  psi.FileName <- nodeExe ()
  psi.ArgumentList.Add(launcherPath)
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  // @vscode/test-electron caches its downloaded VS Code copy in a
  // .vscode-test/ folder relative to the launcher's own CWD — without this,
  // that CWD is whatever directory `dotnet run`/`dotnet test` happened to be
  // invoked from (the repo root in CI), leaking a ~1GB cache there instead
  // of into sagefs-vscode/.vscode-test/, which is already gitignored.
  psi.WorkingDirectory <- vscodeExtensionDir
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  psi.Environment["SAGEFS_MCP_PORT"] <- string f.McpPort
  psi.Environment["SAGEFS_DASHBOARD_PORT"] <- string f.DashboardPort
  psi.Environment["SAGEFS_TE_EXT_DEV_PATH"] <- vscodeExtensionDir
  psi.Environment["SAGEFS_TE_TESTS_PATH"] <- suitePath
  psi.Environment["SAGEFS_TE_WORKSPACE"] <- sampleDir
  psi.Environment["SAGEFS_TE_USER_DATA_DIR"] <- f.UserDataDir
  psi.Environment["SAGEFS_SESSION_ID"] <- f.SessionId
  match VscodeExtensionTests.VscodeFixture.codeExePath with
  | Some codePath -> psi.Environment["SAGEFS_TE_VSCODE_PATH"] <- codePath
  | None -> ()

  let output = Text.StringBuilder()
  use proc = new Process(StartInfo = psi)
  proc.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then output.AppendLine(e.Data) |> ignore)
  proc.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then output.AppendLine(e.Data) |> ignore)
  proc.Start() |> ignore
  proc.BeginOutputReadLine()
  proc.BeginErrorReadLine()
  do! proc.WaitForExitAsync()
  return proc.ExitCode, output.ToString()
}

[<Tests>]
let vscodeCommandProofTests =
  Integration.hostList "VS Code command proof" [
    testTask "WHY — the live-testing and hot-reload commands, invoked as real VS Code commands via the real extension API, actually change the real daemon's real state — proving the extension-to-daemon wiring without any UI automation" {
      // Unlike the old CDP fixture (VscodeExtensionTests.VscodeFixture), this
      // mechanism does not require a pre-existing local VS Code: if
      // VSCODE_PATH names one it is reused (see codeExePath below), and
      // otherwise @vscode/test-electron downloads and caches its own copy —
      // proven working locally against a fresh download.
      for path, hint in
        [ launcherPath, "run `npm run compile:test-electron` in sagefs-vscode"
          suitePath, "run `npm run compile:test-electron` in sagefs-vscode"
          extensionEntryPath, "run `npm run compile` in sagefs-vscode" ] do
        File.Exists path
        |> Expect.isTrue (sprintf "%s must exist — %s" path hint)

      let fixture = startDaemonWithWebLiveSession ()
      try
        let! exitCode, output = runProofSuite fixture
        match exitCode with
        | 0 -> ()
        | _ ->
          failwithf
            "VS Code command proof suite failed (exit %d):\n%s"
            exitCode output
      finally
        stopFixture fixture
    }
  ]
