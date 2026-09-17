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

// --- Self-provisioning: node/npm detection, `npm ci`/compile, staleness ---
//
// A developer running `--integration-host` fresh (no prior `npm run compile`
// in sagefs-vscode) used to hit a bare Expecto.AssertException — "launcher.cjs
// must exist — run npm run compile:test-electron" — which reads exactly like
// a product failure. This suite now builds what it needs itself, and only
// falls back to an explicit pending test (never an ambiguous error, never a
// silent no-op pass) when the tooling to do that isn't even on PATH.

let private pathDirs () : string[] =
  Environment.GetEnvironmentVariable("PATH")
  |> Option.ofObj
  |> Option.defaultValue ""
  |> fun s -> s.Split(Path.PathSeparator)

/// First PATH entry containing any of `candidateNames`, as a plain existence
/// scan — no subprocess spawn needed just to answer "is this on PATH".
let private findOnPath (candidateNames: string list) : string option =
  pathDirs ()
  |> Array.collect (fun dir ->
    candidateNames |> List.map (fun name -> Path.Combine(dir, name)) |> Array.ofList)
  |> Array.tryFind File.Exists

let private nodeAvailable =
  findOnPath (match OperatingSystem.IsWindows() with
              | true -> [ "node.exe" ]
              | false -> [ "node" ])
  |> Option.isSome

let private npmAvailable =
  findOnPath (match OperatingSystem.IsWindows() with
              | true -> [ "npm.cmd"; "npm" ]
              | false -> [ "npm" ])
  |> Option.isSome

let private xvfbRunPath = findOnPath [ "xvfb-run" ]

/// npm is a shell shim (npm.cmd) on Windows: invoking "npm" directly with
/// UseShellExecute=false fails to resolve it there. Route every npm call
/// through cmd.exe /c on Windows and npm directly everywhere else.
let private npmInvocation (args: string list) : string * string list =
  match OperatingSystem.IsWindows() with
  | true -> "cmd.exe", "/c" :: "npm" :: args
  | false -> "npm", args

/// Runs `fileName args` in `workDir`, capturing combined stdout+stderr.
/// Kills the process tree on timeout instead of hanging the suite forever —
/// a wedged build must surface as a failure with whatever output it produced,
/// never a silent stall.
let private runCaptured
  (fileName: string) (args: string list) (workDir: string) (timeout: TimeSpan)
  : int * string =
  let psi = ProcessStartInfo()
  psi.FileName <- fileName
  for a in args do psi.ArgumentList.Add(a)
  psi.WorkingDirectory <- workDir
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  let output = StringBuilder()
  use proc = new Process(StartInfo = psi)
  proc.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then output.AppendLine(e.Data) |> ignore)
  proc.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then output.AppendLine(e.Data) |> ignore)
  proc.Start() |> ignore
  proc.BeginOutputReadLine()
  proc.BeginErrorReadLine()
  match proc.WaitForExit(int timeout.TotalMilliseconds) with
  | true -> proc.ExitCode, output.ToString()
  | false ->
    (try proc.Kill(entireProcessTree = true) with _ -> ())
    -1, output.ToString() + sprintf "\n[TIMED OUT after %O]" timeout

let private describeCommand (fileName: string) (args: string list) =
  sprintf "%s %s" fileName (String.concat " " args)

/// Runs a provisioning command, failing with the exact command, its working
/// directory, and its full captured output on a non-zero exit — never a bare
/// "must exist" with no clue what was tried or why it didn't work.
let private runOrFail
  (label: string) (fileName: string) (args: string list) (workDir: string) (timeout: TimeSpan)
  : unit =
  let exitCode, output = runCaptured fileName args workDir timeout
  match exitCode with
  | 0 -> ()
  | _ ->
    failwithf
      "%s failed (exit %d) — command: %s (cwd=%s)\n%s"
      label exitCode (describeCommand fileName args) workDir output

let rec private allFilesUnder (path: string) : string seq =
  match Directory.Exists path, File.Exists path with
  | true, _ -> Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
  | false, true -> Seq.singleton path
  | false, false -> Seq.empty

/// True when `artifact` is missing, or older than any file under `sources`
/// (directories walked recursively, plain files compared directly) — so an
/// edit to the extension's F# source or package.json triggers a rebuild, and
/// an already-fresh artifact from a prior run is reused as-is.
let private isStale (artifact: string) (sources: string list) : bool =
  match File.Exists artifact with
  | false -> true
  | true ->
    let artifactTime = File.GetLastWriteTimeUtc artifact
    sources
    |> Seq.collect allFilesUnder
    |> Seq.exists (fun f -> File.GetLastWriteTimeUtc f > artifactTime)

/// Self-provisions the Fable build artifacts this proof suite needs. Only
/// ever called from inside the test body (never at discovery/list-build
/// time), and only reached when `nodeAvailable && npmAvailable` — see the
/// pending fallback at the bottom of this file — so npm is known to be on
/// PATH here.
let private ensureFableArtifacts () : unit =
  match Directory.Exists(Path.Combine(vscodeExtensionDir, "node_modules")) with
  | true -> ()
  | false ->
    let fileName, args = npmInvocation [ "ci" ]
    runOrFail "npm ci" fileName args vscodeExtensionDir (TimeSpan.FromMinutes 5.0)

  let packageJson = Path.Combine(vscodeExtensionDir, "package.json")

  match isStale extensionEntryPath [ Path.Combine(vscodeExtensionDir, "src"); packageJson ] with
  | false -> ()
  | true ->
    let fileName, args = npmInvocation [ "run"; "compile" ]
    runOrFail "npm run compile" fileName args vscodeExtensionDir (TimeSpan.FromMinutes 10.0)

  let testElectronSrc = Path.Combine(vscodeExtensionDir, "test-electron")
  match isStale launcherPath [ testElectronSrc; packageJson ]
        || isStale suitePath [ testElectronSrc; packageJson ] with
  | false -> ()
  | true ->
    let fileName, args = npmInvocation [ "run"; "compile:test-electron" ]
    runOrFail "npm run compile:test-electron" fileName args vscodeExtensionDir (TimeSpan.FromMinutes 10.0)

  for path, hint in
    [ launcherPath, "npm run compile:test-electron"
      suitePath, "npm run compile:test-electron"
      extensionEntryPath, "npm run compile" ] do
    File.Exists path
    |> Expect.isTrue
      (sprintf
        "%s is still missing after self-provisioning ran `%s` in %s — see the captured build output above for the real failure"
        path hint vscodeExtensionDir)

// --- Headless display handling (Linux) ---
//
// This suite launches a real VS Code/Electron window. On a bare Linux CI
// runner (no display at all) Electron cannot start without a virtual one.
// On a Linux DESKTOP session it is worse than that: Electron's ozone
// auto-detect prefers Wayland whenever XDG_SESSION_TYPE=wayland or
// WAYLAND_DISPLAY is set, and will reach for the REAL compositor even though
// DISPLAY also happens to be set (XWayland's compatibility socket) — a real
// window would flash onto the developer's real desktop mid test-run.
// Reproduced empirically while building this fix: with only WAYLAND_DISPLAY
// cleared, VS Code still crashed with "Failed to connect to Wayland display"
// / SIGTRAP; clearing XDG_SESSION_TYPE too made it fall back to X11 and run
// cleanly under Xvfb. So "needs isolation" is not simply "DISPLAY is unset" —
// it is "no usable display" OR "a Wayland session is present". Either way
// the fix is the same: run under `xvfb-run` and strip the Wayland session
// hints from the child's environment so Electron's ozone auto-detect can
// only find the X11 display Xvfb provides.
let private needsVirtualDisplay () : bool =
  OperatingSystem.IsLinux()
  && (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "DISPLAY")
      || not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "WAYLAND_DISPLAY"))
      || Environment.GetEnvironmentVariable "XDG_SESSION_TYPE" = "wayland")

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
         workflow = "HotReload" |})
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
  let useVirtualDisplay = needsVirtualDisplay ()
  match useVirtualDisplay, xvfbRunPath with
  | true, Some xvfb ->
    psi.FileName <- xvfb
    psi.ArgumentList.Add("-a")
    psi.ArgumentList.Add(nodeExe ())
    psi.ArgumentList.Add(launcherPath)
  | true, None ->
    failwith
      "This session needs a virtual display to launch VS Code/Electron safely \
       (no usable display, or a Wayland session Electron would otherwise reach \
       for directly) but `xvfb-run` is not on PATH — install it \
       (e.g. `apt install xvfb` / `pacman -S xorg-server-xvfb`) so this proof \
       can run headless without touching a real desktop."
  | false, _ ->
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
  match useVirtualDisplay with
  | true ->
    // Xvfb supplies its own DISPLAY to this child; remove the Wayland
    // session hints so Electron's ozone auto-detect can't reach past it for
    // the real compositor (see needsVirtualDisplay's comment above).
    psi.Environment.Remove("WAYLAND_DISPLAY") |> ignore
    psi.Environment.Remove("XDG_SESSION_TYPE") |> ignore
  | false -> ()

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

let private proofTestName =
  "WHY — the live-testing and hot-reload commands, invoked as real VS Code commands via the real extension API, actually change the real daemon's real state — proving the extension-to-daemon wiring without any UI automation"

/// Node and/or npm are what this suite uses to self-provision and run its
/// own Fable build artifacts (see ensureFableArtifacts above); without them
/// there is nothing this suite can do locally. Rather than an ambiguous
/// error mid-run, a node-less environment gets a single clean pending test
/// naming exactly what's missing and exactly what to run once it's fixed —
/// so the REST of --integration-host stays green, while CI (which always
/// installs Node) still runs this as a real gate.
let private missingToolsReason =
  match nodeAvailable, npmAvailable with
  | true, true -> None
  | _ ->
    let missing =
      [ if not nodeAvailable then "node"
        if not npmAvailable then "npm" ]
      |> String.concat " and "
    Some (
      sprintf
        "%s not on PATH — this environment cannot build or run the VS Code command proof. Install it, then run `npm ci` followed by `npm run compile` and `npm run compile:test-electron` in sagefs-vscode (or just re-run --integration-host, which self-provisions all three)."
        missing)

[<Tests>]
let vscodeCommandProofTests =
  let proofTest =
    match missingToolsReason with
    | Some reason -> ptestCase (sprintf "%s [PENDING: %s]" proofTestName reason) ignore
    | None ->
      testTask proofTestName {
        // Unlike the old CDP fixture (VscodeExtensionTests.VscodeFixture), this
        // mechanism does not require a pre-existing local VS Code: if
        // VSCODE_PATH names one it is reused (see codeExePath below), and
        // otherwise @vscode/test-electron downloads and caches its own copy —
        // proven working locally against a fresh download.
        ensureFableArtifacts ()

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
  Integration.hostList "VS Code command proof" [ proofTest ]
