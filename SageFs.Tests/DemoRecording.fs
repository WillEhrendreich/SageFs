module SageFs.Tests.DemoRecording

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Playwright

// VscodeHelpers is stateless (takes IPage) — safe to reuse.
open SageFs.Tests.VscodeExtensionTests


/// Isolated VS Code launcher for demo recording.
/// Does NOT call killOrphans — only manages the instance it creates.
/// Uses a dedicated CDP port and user-data-dir to avoid conflicts.
module DemoVscode =
  let cdpPort = 9333
  let userDataDir = @"C:\temp\sagefs-demo-profile"
  let mutable private pw: IPlaywright option = None
  let mutable private browser: IBrowser option = None
  let mutable private codePid: int option = None

  /// Find VS Code executable (same logic as VscodeFixture).
  let private codeExePath =
    let fromEnv =
      Environment.GetEnvironmentVariable("VSCODE_PATH")
      |> Option.ofObj
      |> Option.filter IO.File.Exists
    let fromPath =
      lazy
        try
          let psi =
            ProcessStartInfo(
              FileName = "where",
              Arguments = "code",
              UseShellExecute = false,
              RedirectStandardOutput = true,
              CreateNoWindow = true)
          use p = Process.Start(psi)
          let out = p.StandardOutput.ReadToEnd().Trim()
          p.WaitForExit(5000) |> ignore
          match out.Split([|'\r'; '\n'|], StringSplitOptions.RemoveEmptyEntries)
                |> Array.tryHead with
          | Some cmdPath ->
            // 'code' resolves to a cmd shim; Code.exe is two dirs up
            let dir = Path.GetDirectoryName(Path.GetDirectoryName(cmdPath))
            let exe = Path.Combine(dir, "Code.exe")
            match File.Exists exe with
            | true -> Some exe
            | false -> None
          | None -> None
        with _ -> None
    let wellKnown =
      lazy
        let candidates = [
          @"C:\Program Files\Microsoft VS Code\Code.exe"
          Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Programs\Microsoft VS Code\Code.exe")
        ]
        candidates |> List.tryFind File.Exists
    match fromEnv with
    | Some p -> Some p
    | None ->
      match fromPath.Value with
      | Some p -> Some p
      | None -> wellKnown.Value

  let isAvailable = codeExePath.IsSome

  let private codeExe () =
    match codeExePath with
    | Some p -> p
    | None ->
      failwith
        "VS Code not found. Set VSCODE_PATH or ensure 'code' is on PATH"

  /// Pre-configure settings for a clean, cinematic demo appearance.
  let private ensureDemoSettings (projectPath: string) =
    let userDir = Path.Combine(userDataDir, "User")
    if not (Directory.Exists userDir) then
      Directory.CreateDirectory(userDir) |> ignore
    let settingsPath = Path.Combine(userDir, "settings.json")
    let settings =
      sprintf """{
  "security.workspace.trust.enabled": false,
  "workbench.startupEditor": "none",
  "update.mode": "none",
  "extensions.autoCheckUpdates": false,
  "extensions.ignoreRecommendations": true,
  "telemetry.telemetryLevel": "off",
  "workbench.colorTheme": "Default Dark Modern",
  "editor.fontSize": 15,
  "editor.minimap.enabled": false,
  "window.menuBarVisibility": "hidden",
  "breadcrumbs.enabled": false,
  "editor.renderWhitespace": "none",
  "workbench.activityBar.location": "hidden",
  "github.copilot.enable": { "*": false },
  "github.copilot.editor.enableAutoCompletions": false,
  "workbench.notifications.doNotDisturbMode": true,
  "window.titleBarStyle": "custom",
  "editor.lineNumbers": "on",
  "editor.scrollBeyondLastLine": false,
  "workbench.tips.enabled": false,
  "workbench.welcomePage.walkthroughs.openOnInstall": false,
  "sagefs.projectPath": "%s",
  "sagefs.autoStart": true,
  "git.enabled": false,
  "git.autoRepositoryDetection": false,
  "extensions.autoUpdate": false,
  "update.showReleaseNotes": false,
  "window.newWindowProfile": "Default",
  "workbench.panel.defaultLocation": "bottom",
  "window.autoDetectColorScheme": false,
  "extensions.closeExtensionDetailsOnViewChange": true,
  "workbench.editor.enablePreview": false,
  "workbench.statusBar.visible": true,
  "editor.cursorBlinking": "solid",
  "files.autoSave": "off",
  "editor.wordWrap": "off",
  "editor.tabSize": 2,
  "workbench.sideBar.location": "left",
  "workbench.activityBar.visible": false,
  "window.restoreWindows": "none",
  "window.enableMenuBarMnemonics": false,
  "remote.autoForwardPorts": false,
  "notebook.globalToolbar": false,
  "interactiveWindow.restore": false,
  "editor.codeLens": false,
  "editor.inlayHints.enabled": false,
  "workbench.panel.opensMaximized": "never",
  "editor.lightbulb.enabled": "off",
  "editor.stickyScroll.enabled": false,
  "FSharp.codeLenses.references.enabled": false,
  "FSharp.inlayHints.enabled": false,
  "FSharp.inlayHints.typeAnnotations": false,
  "FSharp.inlayHints.parameterNames": false,
  "chat.commandCenter.enabled": false,
  "workbench.commandPalette.preserveInput": false,
  "zenMode.hideLineNumbers": false,
  "workbench.secondarySideBar.visible": false,
  "terminal.integrated.defaultLocation": "view",
  "FSharp.codeLenses.signature.enabled": false,
  "FSharp.lineLens.enabled": "never",
  "FSharp.pipelineHints.enabled": false,
  "editor.autoClosingBrackets": "never",
  "editor.autoClosingQuotes": "never",
  "editor.acceptSuggestionOnCommitCharacter": false,
  "editor.quickSuggestions": { "other": false, "comments": false, "strings": false },
  "editor.suggestOnTriggerCharacters": false,
  "editor.parameterHints.enabled": false
      }""" projectPath
    File.WriteAllText(settingsPath, settings)

  /// Poll CDP /json/version until the endpoint responds.
  let private waitForCdp (timeoutMs: int) = task {
    let sw = Stopwatch.StartNew()
    use client = new HttpClient()
    client.Timeout <- TimeSpan.FromSeconds(3.0)
    let mutable ready = false
    while not ready && sw.ElapsedMilliseconds < int64 timeoutMs do
      try
        let! resp =
          client.GetStringAsync(
            sprintf "http://127.0.0.1:%d/json/version" cdpPort)
        match resp.Contains("webSocketDebuggerUrl") with
        | true -> ready <- true
        | false -> do! Task.Delay(500)
      with _ ->
        do! Task.Delay(500)
    match ready with
    | true -> ()
    | false ->
      failwithf "CDP port %d not available after %dms" cdpPort timeoutMs
  }

  /// Launch a fresh VS Code instance for demo recording.
  /// fileToOpen: absolute path to a file to open immediately (skips Quick Open).
  /// projectPath: the value for sagefs.projectPath setting (e.g., "SageFs.slnx").
  let launch
    (workspaceDir: string)
    (disableExtensions: bool)
    (fileToOpen: string option)
    (projectPath: string) = task {
    ensureDemoSettings projectPath
    // Disable noisy extensions but keep Ionide (F# syntax) and SageFs (the product).
    let extFlags =
      match disableExtensions with
      | true -> " --disable-extensions"
      | false ->
        " --disable-extension GitHub.copilot"
        + " --disable-extension GitHub.copilot-chat"
        + " --disable-extension eamodio.gitlens"
        + " --disable-extension asvetliakov.vscode-neovim"
    let fileArg =
      match fileToOpen with
      | Some path -> sprintf " \"%s\"" path
      | None -> ""
    let args =
      sprintf
        "--remote-debugging-port=%d --user-data-dir=\"%s\" --new-window --window-size=1280,720%s \"%s\"%s"
        cdpPort userDataDir extFlags workspaceDir fileArg
    let psi = ProcessStartInfo(FileName = codeExe (), Arguments = args)
    psi.UseShellExecute <- true
    let proc = Process.Start(psi)
    codePid <- Some proc.Id
    printfn "   VS Code PID %d on CDP port %d" proc.Id cdpPort

    // Wait for CDP to become available (up to 30s)
    do! waitForCdp 30_000

    // Small settle time after CDP is up before connecting
    do! Task.Delay(2000)

    let! playwright = Playwright.CreateAsync()
    pw <- Some playwright
    let! b =
      playwright.Chromium.ConnectOverCDPAsync(
        sprintf "http://127.0.0.1:%d" cdpPort)
    browser <- Some b
    return b
  }

  /// Get the main VS Code renderer page.
  let getPage () = task {
    match browser with
    | Some b when b.Contexts.Count > 0 && b.Contexts[0].Pages.Count > 0 ->
      return b.Contexts[0].Pages[0]
    | _ -> return failwith "No VS Code page — call launch first"
  }

  /// Disconnect Playwright and kill only the VS Code we started.
  let cleanup () = task {
    match browser with
    | Some b ->
      try do! b.CloseAsync() with _ -> ()
      browser <- None
    | None -> ()
    match pw with
    | Some p -> p.Dispose(); pw <- None
    | None -> ()
    match codePid with
    | Some pid ->
      try Process.GetProcessById(pid).Kill(true) with _ -> ()
      codePid <- None
    | None -> ()
  }


/// Frame-by-frame screen capture using Playwright screenshots.
/// Produces PNG sequences that ffmpeg composes into animated GIFs.
module DemoCapture =
  let defaultFrameDir = @"C:\temp\sagefs-demo-frames"
  let defaultOutputDir = @"C:\Code\Repos\SageFs\docs\media"

  let prepareFrameDir dir =
    if Directory.Exists dir then
      Directory.Delete(dir, true)
    Directory.CreateDirectory(dir) |> ignore

  /// Start continuous screenshot capture at the given fps.
  /// Returns (backgroundTask, cancellationSource, frameCountRef).
  let startCapture (page: IPage) (frameDir: string) (fps: int) =
    prepareFrameDir frameDir
    let intervalMs = max 50 (1000 / fps)
    let cts = new CancellationTokenSource()
    let frameCount = ref 0
    let captureTask = task {
      while not cts.IsCancellationRequested do
        try
          let n = Interlocked.Increment(frameCount) - 1
          let path = Path.Combine(frameDir, sprintf "frame-%04d.png" n)
          let! _ = page.ScreenshotAsync(PageScreenshotOptions(Path = path))
          do! Task.Delay(intervalMs, cts.Token)
        with
        | :? OperationCanceledException -> ()
        | ex ->
          eprintfn "  [capture] %s" ex.Message
          do! Task.Delay(intervalMs)
    }
    (captureTask, cts, frameCount)

  /// Compose PNG frames into an animated GIF via two-pass ffmpeg palette encoding.
  let composeGif (frameDir: string) (outputPath: string) (fps: int) =
    let paletteFile =
      Path.Combine(Path.GetTempPath(), "sagefs-demo-palette.png")
    let framePattern = Path.Combine(frameDir, "frame-%04d.png")

    let outDir = Path.GetDirectoryName(outputPath)
    if not (Directory.Exists outDir) then
      Directory.CreateDirectory(outDir) |> ignore

    let run exe args =
      let psi =
        ProcessStartInfo(
          FileName = exe,
          Arguments = args,
          UseShellExecute = false,
          RedirectStandardError = true,
          CreateNoWindow = true)
      use p = Process.Start(psi)
      let _err = p.StandardError.ReadToEnd()
      p.WaitForExit(60_000) |> ignore
      p.ExitCode

    // Pass 1: generate palette
    let filters =
      sprintf "fps=%d,scale=1280:-1:flags=lanczos" fps
    run "ffmpeg"
      (sprintf
        """-y -framerate %d -i "%s" -vf "%s,palettegen" "%s" """
        fps framePattern filters paletteFile)
    |> ignore

    // Pass 2: encode with palette
    run "ffmpeg"
      (sprintf
        """-y -framerate %d -i "%s" -i "%s" -filter_complex "%s[x];[x][1:v]paletteuse" "%s" """
        fps framePattern paletteFile filters outputPath)
    |> ignore

    if File.Exists paletteFile then
      File.Delete(paletteFile)

    match File.Exists outputPath with
    | true ->
      let sizeMb =
        float (FileInfo(outputPath).Length) / (1024.0 * 1024.0)
      printfn "  ✅ GIF: %s (%.1f MB)" outputPath sizeMb
      true
    | false ->
      eprintfn "  ❌ GIF was not created"
      false


/// Helpers specific to demo recording — polling, dismissing, verifying.
module DemoHelpers =
  let private delay ms = Task.Delay(ms: int)

  /// Dismiss all notifications by clicking "Clear All" or pressing Escape.
  /// Repeats to catch staggered notifications.
  let dismissAllNotifications (page: IPage) = task {
    for _ in 1..3 do
      try
        // Try clicking the "Clear All Notifications" button if visible
        let clearBtn =
          page.Locator(
            ".notifications-center-header .codicon-notifications-clear-all")
        let! isVis = clearBtn.IsVisibleAsync()
        match isVis with
        | true -> do! clearBtn.ClickAsync()
        | false -> ()
      with _ -> ()
      do! page.Keyboard.PressAsync("Escape")
      do! delay 300
  }

  /// Wait for the SageFs status bar item to appear (extension activated).
  let waitForSageFsStatus (page: IPage) (timeoutMs: int) = task {
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable found = false
    while not found && sw.ElapsedMilliseconds < int64 timeoutMs do
      let! statusText = VscodeHelpers.getStatusBarText page
      found <-
        statusText.Contains("SageFs") || statusText.Contains("sagefs")
      match found with
      | true ->
        printfn "   ✅ SageFs status bar appeared after %.1fs"
          sw.Elapsed.TotalSeconds
      | false -> do! delay 1000
    return found
  }

  /// Wait for an inline result decoration to appear (any line containing "→").
  /// SageFs renders results as CSS decorations with "// → value" text.
  /// Returns true if found within timeout.
  let waitForInlineResult (page: IPage) (timeoutMs: int) = task {
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable found = false
    // SageFs eval results render as CSS ::after pseudo-elements with text like
    // "// → 2  (1.2ms)". The timing pattern "(Xms)" is unique to actual eval
    // results and never appears in source file comments (which just say "// → 2").
    // We also check for decoration overlay elements with eval-related content.
    let js = """(() => {
      // Pattern: "(digits.digitsms)" — only in eval result decorations
      var timingRe = /\(\d+(\.\d+)?ms\)/;
      // Check view-line textContent (decorations may inject text nodes)
      var lines = document.querySelectorAll('.view-line');
      for (var i = 0; i < lines.length; i++) {
        var text = lines[i].textContent || '';
        if (timingRe.test(text)) return true;
      }
      // Check overlay decorations (VS Code injects spans with after content)
      var overlays = document.querySelectorAll('.view-overlays span');
      for (var i = 0; i < overlays.length; i++) {
        var after = window.getComputedStyle(overlays[i], '::after');
        var content = after.getPropertyValue('content');
        if (content && timingRe.test(content)) return true;
      }
      // Check for decoration containers with eval result text
      var decos = document.querySelectorAll('[class*="sagefs"], [class*="inline-decoration"]');
      for (var i = 0; i < decos.length; i++) {
        var text = decos[i].textContent || '';
        if (timingRe.test(text)) return true;
      }
      return false;
    })()"""
    while not found && sw.ElapsedMilliseconds < int64 timeoutMs do
      try
        let! result = page.EvaluateAsync<bool>(js)
        found <- result
      with _ -> ()
      match found with
      | true ->
        printfn "   ✅ Inline result appeared after %.1fs"
          sw.Elapsed.TotalSeconds
      | false -> do! delay 500
    return found
  }

  /// Navigate to a specific line using Ctrl+G (Go to Line dialog).
  /// Clicks on the editor area first to ensure focus isn't in terminal/panel.
  let goToLine (page: IPage) (line: int) = task {
    // Click on the editor area to ensure it has focus (not terminal/panel)
    do! page.Mouse.ClickAsync(400.0f, 300.0f)
    do! delay 300
    do! page.Keyboard.PressAsync("Control+g")
    do! delay 800
    do! page.Keyboard.TypeAsync(string line)
    do! delay 300
    do! page.Keyboard.PressAsync("Enter")
    do! delay 800
  }


/// Pre-start and verify a SageFs daemon for demo recording.
/// Ensures the daemon is healthy with correct apiVersion before VS Code launches.
module DemoDaemon =
  let private daemonPort = 37749
  let private httpClient = new HttpClient(Timeout = TimeSpan.FromSeconds(5.0))

  let mutable private daemonPid: int option = None

  /// Poll the daemon's /api/system/status until apiVersion=1 or timeout.
  let private waitForReady (timeoutMs: int) = task {
    let sw = Stopwatch.StartNew()
    let mutable ready = false
    while not ready && sw.ElapsedMilliseconds < int64 timeoutMs do
      try
        let! resp =
          httpClient.GetStringAsync(sprintf "http://127.0.0.1:%d/api/system/status" daemonPort)
        match resp.Contains("\"apiVersion\":1") with
        | true ->
          printfn "   ✅ Daemon ready (apiVersion=1) after %.1fs" sw.Elapsed.TotalSeconds
          ready <- true
        | false ->
          printfn "   ⏳ Daemon responded but apiVersion≠1: %s" (resp.Substring(0, min 120 resp.Length))
          do! Task.Delay(1000)
      with _ ->
        do! Task.Delay(1000)
    return ready
  }

  /// Ensure a session exists for the workspace (avoids extension "Create session?" popup).
  let private ensureSession (workspaceDir: string) (projects: string) = task {
    try
      let! sessionsJson =
        httpClient.GetStringAsync(sprintf "http://127.0.0.1:%d/api/sessions" daemonPort)
      match sessionsJson.Contains("\"id\"") with
      | true ->
        printfn "   ✅ Session already exists"
        return true
      | false ->
        printfn "   📦 Creating session for %s..." projects
        let payload =
          sprintf """{"projects":"%s","workingDirectory":"%s"}"""
            projects (workspaceDir.Replace("\\", "\\\\"))
        let content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        let! resp =
          httpClient.PostAsync(
            sprintf "http://127.0.0.1:%d/api/sessions/create" daemonPort, content)
        let! body = resp.Content.ReadAsStringAsync()
        match resp.IsSuccessStatusCode with
        | true ->
          printfn "   ✅ Session created"
          // Wait for session to warm up
          do! Task.Delay(15_000)
          return true
        | false ->
          printfn "   ⚠️ Session creation response: %d %s" (int resp.StatusCode) body
          return false
    with ex ->
      printfn "   ⚠️ Session check failed: %s" ex.Message
      return false
  }

  /// Start a fresh SageFs daemon for the demo workspace and wait for it to be ready.
  /// If a daemon is already running and healthy, reuses it.
  /// After daemon is ready, ensures a session exists to avoid extension popups.
  let start (workspaceDir: string) (projects: string) = task {
    // Check if a daemon is already running and healthy
    let! alreadyReady = waitForReady 3_000
    match alreadyReady with
    | true ->
      printfn "   ✅ Existing daemon is healthy — reusing it"
    | false ->
      // Kill any unhealthy/stale daemon
      for p in Process.GetProcessesByName("SageFs") do
        try p.Kill(true) with _ -> ()
      do! Task.Delay(2000)

      printfn "   🚀 Starting SageFs daemon..."
      // Start SageFs via Start-Process to ensure it gets a real console window
      // (PrettyPrompt requires a TTY — UseShellExecute alone can return null)
      let startArgs =
        sprintf "-NoProfile -Command \"Start-Process -FilePath sagefs -WorkingDirectory '%s' -PassThru | Select-Object -ExpandProperty Id\"" workspaceDir
      let psi =
        ProcessStartInfo(
          FileName = "powershell.exe",
          Arguments = startArgs,
          WorkingDirectory = workspaceDir,
          UseShellExecute = false,
          RedirectStandardOutput = true,
          CreateNoWindow = true)
      let psProc = Process.Start(psi)
      let! pidStr = psProc.StandardOutput.ReadToEndAsync()
      do! psProc.WaitForExitAsync()
      match System.Int32.TryParse(pidStr.Trim()) with
      | true, pid ->
        daemonPid <- Some pid
        printfn "   PID: %d" pid
      | false, _ ->
        printfn "   ⚠️ Could not parse daemon PID from: %s" (pidStr.Trim())
        // Fall back to finding the process
        do! Task.Delay(3000)
        match Process.GetProcessesByName("SageFs") |> Array.tryHead with
        | Some p ->
          daemonPid <- Some p.Id
          printfn "   PID (discovered): %d" p.Id
        | None ->
          return failwith "SageFs daemon did not start"

      // Wait up to 60s for the daemon to be ready with correct apiVersion
      let! ready = waitForReady 60_000
      match ready with
      | true -> ()
      | false ->
        return failwith "SageFs daemon did not become ready within 60s"

    // Ensure a session exists so the extension doesn't prompt to create one
    let! _ = ensureSession workspaceDir projects
    return ()
  }

  /// Stop the daemon we started.
  let stop () =
    match daemonPid with
    | Some pid ->
      try
        let proc = Process.GetProcessById(pid)
        proc.Kill(true)
        printfn "   🛑 Stopped daemon (PID %d)" pid
      with _ -> ()
      daemonPid <- None
    | None -> ()

  /// Build a project so the test DLL exists for live testing.
  let buildProject (workspaceDir: string) (project: string) = task {
    printfn "   🔨 Building %s..." project
    let psi =
      ProcessStartInfo(
        FileName = "dotnet",
        Arguments = sprintf "build \"%s\" --no-restore -v:q" project,
        WorkingDirectory = workspaceDir,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true)
    let p = Process.Start(psi)
    let! _out = p.StandardOutput.ReadToEndAsync()
    let! _err = p.StandardError.ReadToEndAsync()
    do! p.WaitForExitAsync()
    match p.ExitCode with
    | 0 -> printfn "   ✅ Build succeeded"
    | code -> printfn "   ⚠️ Build exit code: %d" code
    return p.ExitCode = 0
  }

  /// Enable live testing via the command palette in VS Code.
  /// This is more reliable than REST because it goes through the extension.
  let enableLiveTestingViaVscode (page: IPage) = task {
    printfn "   🧪 Enabling live testing via command palette..."
    do! page.Keyboard.PressAsync("Control+Shift+p")
    do! Task.Delay(500)
    do! page.Keyboard.TypeAsync("SageFs: Enable Live Testing")
    do! Task.Delay(800)
    do! page.Keyboard.PressAsync("Enter")
    do! Task.Delay(500)
    // Dismiss any notification that appeared
    do! page.Keyboard.PressAsync("Escape")
    printfn "   ✅ Live testing enable command sent"
  }

  /// Wait for the VS Code status bar to show test results.
  /// Polls the status bar text for a test count pattern.
  let waitForTestStatusBar (page: IPage) (pattern: string) (timeoutMs: int) = task {
    let sw = Stopwatch.StartNew()
    let mutable found = false
    while not found && sw.ElapsedMilliseconds < int64 timeoutMs do
      let! statusText = VscodeHelpers.getStatusBarText page
      found <- statusText.Contains(pattern)
      match found with
      | true ->
        printfn "   ✅ Status bar contains '%s' after %.1fs"
          pattern sw.Elapsed.TotalSeconds
      | false -> do! Task.Delay(1000)
    match found with
    | true -> ()
    | false ->
      printfn "   ⚠️ Status bar pattern '%s' not found after %dms" pattern timeoutMs
    return found
  }

  /// Restore files modified during the demo using git checkout.
  let restoreFiles (workspaceDir: string) (files: string list) =
    let args = "checkout -- " + (files |> String.concat " ")
    printfn "   🔄 Restoring files: git %s" args
    let psi =
      ProcessStartInfo(
        FileName = "git",
        Arguments = args,
        WorkingDirectory = workspaceDir,
        UseShellExecute = false,
        CreateNoWindow = true)
    use p = Process.Start(psi)
    p.WaitForExit(5000) |> ignore
    match p.ExitCode with
    | 0 -> printfn "   ✅ Files restored"
    | code -> printfn "   ⚠️ git checkout exit code: %d" code


/// Demo scenarios that drive VS Code via Playwright CDP.
/// Each returns a Task that runs the scenario against the given page.
/// The file is opened via VS Code launch arguments — no Quick Open needed.
module DemoScenarios =

  let private delay ms = Task.Delay(ms: int)

  /// Hero demo: show SageFs evaluating F# code in VS Code.
  ///
  /// Pre-conditions (handled by recordDemo):
  ///   - SageFs daemon is running and healthy (apiVersion=1)
  ///   - Session is pre-created and warm
  ///   - VS Code launched with getting-started.fsx already open
  ///   - Extension is activated and connected to daemon
  ///   - Sidebar and panels are closed (clean editor view)
  ///
  /// The demo shows:
  ///   Scene 1: File is open, SageFs status bar visible — "this is VS Code with SageFs"
  ///   Scene 2: Evaluate "1 + 1" — result appears inline: "// → 2  (Xms)"
  ///   Scene 3: Evaluate pipeline — result appears inline with pipeline sum
  ///   Scene 4: Hold — viewer absorbs what just happened
  ///
  /// No Quick Open, no command palette, no file navigation.
  /// Just code → eval → result. The product speaks for itself.
  let heroDemo (page: IPage) = task {
    // ── Scene 1: Establish context (file already open from launch args) ──
    // Ensure no dialogs or popups are stealing focus.
    do! page.Keyboard.PressAsync("Escape")
    do! delay 500

    // Scroll to top so the viewer sees the file header and SageFs branding.
    do! page.Keyboard.PressAsync("Control+Home")
    do! delay 5000  // hold: viewer reads the file header and notices the status bar

    // ── Scene 2: Evaluate "1 + 1" (line 11) ──
    // Navigate to line 11 using Ctrl+G. SageFs auto-detects the cell boundary.
    do! DemoHelpers.goToLine page 11
    do! delay 2000  // hold: viewer sees cursor on "1 + 1"

    // Evaluate with Alt+Enter — the first money shot.
    do! page.Keyboard.PressAsync("Alt+Enter")

    // Wait for eval result. Try DOM detection first (looks for timing pattern),
    // fall back to fixed delay if detection doesn't trigger.
    let! gotResult = DemoHelpers.waitForInlineResult page 8_000
    match gotResult with
    | true -> ()
    | false -> printfn "   ⚠️ No inline result detected — using fixed delay"
    // Either way, hold long enough for the viewer to see the result.
    do! delay 5000

    // ── Scene 2.5: Define "double" so the pipeline works correctly ──
    // Without this, the pipeline uses F#'s built-in `double` (float conversion)
    // instead of our `let double x = x * 2`, giving 30.0 instead of 60.
    do! DemoHelpers.goToLine page 25
    do! delay 1500  // brief hold: viewer sees the function definition
    do! page.Keyboard.PressAsync("Alt+Enter")
    do! delay 3000  // wait for eval result

    // ── Scene 3: Evaluate the pipeline (lines 33-37) ──
    do! DemoHelpers.goToLine page 33
    do! delay 2000  // hold: viewer reads the pipeline code

    // Select the full pipeline block (lines 33-36) so it's visually obvious.
    // SageFs evaluates the cell containing the cursor, but selection helps the GIF viewer.
    do! page.Keyboard.PressAsync("Alt+Enter")
    let! gotPipeline = DemoHelpers.waitForInlineResult page 8_000
    match gotPipeline with
    | true -> ()
    | false -> printfn "   ⚠️ No pipeline result detected — using fixed delay"
    do! delay 6000  // hold: viewer reads pipeline result

    // ── Scene 4: Breathe ──
    // Hold on the final state. Viewer sees: code + inline results + status bar.
    do! delay 5000
  }

  /// Extension demo: show SageFs status bar, eval, command palette.
  /// Requires SageFs extension installed and daemon running.
  let extensionDemo (page: IPage) = task {
    // File opened via launch args, wait for extension
    do! delay 3000

    // Navigate to "1 + 1" (line 11) — cursor placement is enough
    do! DemoHelpers.goToLine page 11
    do! delay 1000

    // Execute with SageFs (Alt+Enter)
    do! page.Keyboard.PressAsync("Alt+Enter")
    let! _ = DemoHelpers.waitForInlineResult page 15_000
    do! delay 3000

    // Navigate to pipeline example and execute
    do! DemoHelpers.goToLine page 33
    do! delay 1000

    // Evaluate the pipeline cell
    do! page.Keyboard.PressAsync("Alt+Enter")
    let! _ = DemoHelpers.waitForInlineResult page 15_000
    do! delay 3000

    // Show SageFs commands in command palette
    do! page.Keyboard.PressAsync("Control+Shift+p")
    do! delay 1000
    do! page.Keyboard.TypeAsync("SageFs")
    do! delay 3000  // hold: viewer reads the available commands

    // Close and end
    do! VscodeHelpers.dismiss page
    do! delay 2000
  }

  /// Hot-reload hero demo: showcase SageFs's live testing cycle.
  ///
  /// Pre-conditions (handled by recordDemo + setupBeforeCapture):
  ///   - SageFs daemon is running with a session for hotreloaddemo/Tests/Tests.fsproj
  ///   - Test project is built (DLL exists for live testing)
  ///   - Live testing enabled — status bar shows "27/27 passed"
  ///   - VS Code opened with Pong.fs
  ///   - Sidebar and panels closed
  ///
  /// The demo shows:
  ///   Scene 1: Pong.fs code with all tests green — "this is a real project"
  ///   Scene 2: REPL eval ballColor() — inline result shows (255, 255, 255)
  ///   Scene 3: Edit ballColor to red → save → test fails (RED)
  ///   Scene 4: Open test file → fix assertion → save → all green (GREEN)
  ///   Scene 5: Breathe — viewer absorbs the red-green cycle
  let hotReloadHeroDemo (page: IPage) = task {
    // ── Scene 1: Establish context (Pong.fs open, tests green) ──
    do! page.Keyboard.PressAsync("Escape")
    do! delay 500

    // Scroll to top — viewer sees the module header and pure functions
    do! page.Keyboard.PressAsync("Control+Home")
    do! delay 4000  // hold: viewer reads the code and notices "X/Y passed"

    // ── Scene 2: REPL eval — show ballColor returns white ──
    do! DemoHelpers.goToLine page 19
    do! delay 1500  // hold: viewer sees "let ballColor () = (255, 255, 255)"

    // Evaluate with Alt+Enter — inline result appears
    do! page.Keyboard.PressAsync("Alt+Enter")
    let! gotResult = DemoHelpers.waitForInlineResult page 8_000
    match gotResult with
    | true -> ()
    | false -> printfn "   ⚠️ No inline result detected — using fixed delay"
    do! delay 4000  // hold: viewer sees the eval result

    // ── Scene 3: Break a test — change ballColor to red ──
    // Select the whole line and retype it. Home+Shift+End selects line content.
    do! DemoHelpers.goToLine page 19
    do! delay 500
    do! page.Keyboard.PressAsync("Home")
    do! page.Keyboard.PressAsync("Shift+End")
    do! delay 500  // hold: viewer sees the line selected

    // Type the new value — viewer watches the edit happen
    do! page.Keyboard.TypeAsync("let ballColor () = (255, 0, 0)")
    do! delay 500

    // Save — triggers file watcher → live testing re-run
    do! page.Keyboard.PressAsync("Control+s")
    do! delay 500
    printfn "   💾 Saved ballColor change — waiting for test failure..."

    // Wait for status bar to show failure (test detects changed value)
    let! sawFail =
      DemoDaemon.waitForTestStatusBar page "failed" 15_000
    match sawFail with
    | true -> ()
    | false -> printfn "   ⚠️ Test failure not detected in status bar"
    do! delay 3000  // hold: viewer sees red — THE DRAMATIC MOMENT

    // ── Scene 4: Fix the test — make assertion match new color ──
    // Quick Open to navigate to PongTests.fs
    do! page.Keyboard.PressAsync("Control+p")
    do! delay 500
    do! page.Keyboard.TypeAsync("PongTests")
    do! delay 800
    do! page.Keyboard.PressAsync("Enter")
    do! delay 2000  // wait for file to open and settle

    // Go to line 37 — the failing assertion
    do! DemoHelpers.goToLine page 37
    do! delay 500

    // Select the whole line and retype with corrected assertion
    do! page.Keyboard.PressAsync("Home")
    do! page.Keyboard.PressAsync("Shift+End")
    do! delay 500  // hold: viewer sees the assertion line selected

    // Type the fixed assertion — matches the new red color
    do! page.Keyboard.TypeAsync(
      "    ballColor () |> Expect.equal \"red\" (255, 0, 0)")
    do! delay 500

    // Save — triggers re-run, all tests should pass now
    do! page.Keyboard.PressAsync("Control+s")
    do! delay 500
    printfn "   💾 Saved test fix — waiting for all tests green..."

    // Wait for status bar to show all passing
    let! sawPass =
      DemoDaemon.waitForTestStatusBar page "passed" 15_000
    match sawPass with
    | true -> ()
    | false -> printfn "   ⚠️ All-pass not detected in status bar"
    do! delay 3000  // hold: viewer sees green — RESOLUTION

    // ── Scene 5: Breathe ──
    // Hold on the final all-green state. The viewer has seen the complete
    // red-green cycle: edit code → test fails → fix test → all green.
    do! delay 4000
  }




let recordDemo
  (scenarioName: string)
  (scenario: IPage -> Task<unit>)
  (withExtensions: bool)
  (captureFps: int)
  (playbackFps: int)
  (fileToOpen: string option)
  (workspaceDir: string)
  (projects: string)
  (projectPath: string)
  (setupBeforeCapture: (IPage -> Task<unit>) option)
  (cleanupAfter: (unit -> unit) option) =
  task {
    let frameDir =
      Path.Combine(DemoCapture.defaultFrameDir, scenarioName)
    let outputPath =
      Path.Combine(DemoCapture.defaultOutputDir, sprintf "%s.gif" scenarioName)

    printfn "🎬 Recording: %s" scenarioName
    printfn "   Frames → %s" frameDir
    printfn "   Output → %s" outputPath
    printfn "   Workspace → %s" workspaceDir
    printfn "   Projects → %s" projects
    printfn "   Capture: %d fps, Playback: %d fps" captureFps playbackFps

    try
      // Ensure SageFs daemon is running and healthy BEFORE launching VS Code.
      // Also pre-creates a session so the extension doesn't popup a dialog.
      do! DemoDaemon.start workspaceDir projects

      // Launch isolated VS Code with file already open (no Quick Open needed).
      let! _browser =
        DemoVscode.launch
          workspaceDir
          (not withExtensions)
          fileToOpen
          projectPath
      let! page = DemoVscode.getPage()

      // Wait for VS Code to settle and SageFs extension to activate.
      printfn "   ⏳ Waiting for VS Code and SageFs extension..."
      do! Task.Delay(10_000)

      // Wait for SageFs to appear in the status bar
      let! sageFsReady = DemoHelpers.waitForSageFsStatus page 30_000
      match sageFsReady with
      | true -> printfn "   ✅ Extension connected"
      | false -> printfn "   ⚠️ SageFs status bar not found — continuing anyway"

      // ── Clean up the UI before recording ──
      do! page.Keyboard.PressAsync("Control+Shift+p")
      do! Task.Delay(500)
      do! page.Keyboard.TypeAsync("View: Close Primary Side Bar")
      do! Task.Delay(500)
      do! page.Keyboard.PressAsync("Enter")
      do! Task.Delay(500)

      do! page.Keyboard.PressAsync("Control+Shift+p")
      do! Task.Delay(500)
      do! page.Keyboard.TypeAsync("View: Close Secondary Side Bar")
      do! Task.Delay(500)
      do! page.Keyboard.PressAsync("Enter")
      do! Task.Delay(500)

      do! page.Keyboard.PressAsync("Control+Shift+p")
      do! Task.Delay(500)
      do! page.Keyboard.TypeAsync("View: Close Panel")
      do! Task.Delay(500)
      do! page.Keyboard.PressAsync("Enter")
      do! Task.Delay(500)

      // Dismiss any startup notifications
      do! DemoHelpers.dismissAllNotifications page
      do! Task.Delay(2000)
      do! DemoHelpers.dismissAllNotifications page
      do! Task.Delay(1000)
      do! page.Keyboard.PressAsync("Escape")
      do! Task.Delay(500)

      // Run pre-capture setup (e.g., enable live testing, wait for tests)
      match setupBeforeCapture with
      | Some setup ->
        printfn "   🔧 Running pre-capture setup..."
        let! page = DemoVscode.getPage()
        do! setup page
        printfn "   ✅ Pre-capture setup complete"
      | None -> ()

      // Dismiss anything the setup may have triggered
      do! DemoHelpers.dismissAllNotifications page
      do! Task.Delay(1000)
      do! page.Keyboard.PressAsync("Escape")
      do! Task.Delay(500)

      // Refresh page reference
      let! page = DemoVscode.getPage()

      // Start background screenshot capture
      let (captureTask, cts, frameCount) =
        DemoCapture.startCapture page frameDir captureFps

      try
        do! scenario page
      finally
        cts.Cancel()

      do! Task.Delay(500)
      let frames = frameCount.Value
      printfn "   Captured %d frames" frames
      let estimatedDuration =
        match playbackFps with
        | 0 -> 0.0
        | fps -> float frames / float fps
      printfn "   Estimated GIF duration: %.1fs at %dfps" estimatedDuration playbackFps

      match frames > 0 with
      | true ->
        let ok = DemoCapture.composeGif frameDir outputPath playbackFps
        match ok with
        | true -> printfn "   ✅ Demo '%s' done!" scenarioName
        | false -> printfn "   ❌ GIF composition failed"
      | false ->
        printfn "   ❌ No frames captured"
    finally
      // Always cleanup the VS Code we launched; leave daemon for reuse
      DemoVscode.cleanup().GetAwaiter().GetResult()
      // Run post-recording cleanup (e.g., restore edited files)
      match cleanupAfter with
      | Some cleanup ->
        try cleanup ()
        with ex -> printfn "   ⚠️ Cleanup failed: %s" ex.Message
      | None -> ()
  }


// ---------------------------------------------------------------------------
// Integration tests — run with --all --filter-test-case "Demo recording"
// Skipped automatically when VS Code is not installed.
// ---------------------------------------------------------------------------

[<Tests>]
let demoRecordingTests =
  testList "[Integration] Demo recording" [
    match DemoVscode.isAvailable with
    | true ->
      testCase "hero — F# code showcase" (fun () ->
        let fsx =
          Path.Combine(
            @"C:\Code\Repos\SageFs", "samples", "getting-started.fsx")
        let t =
          recordDemo
            "sagefs-hero"
            DemoScenarios.heroDemo
            true   // extensions for syntax highlighting + SageFs
            16     // capture: 16 fps
            4      // playback: 4 fps — slow enough to watch
            (Some fsx)
            @"C:\Code\Repos\SageFs"
            "SageFs.slnx"
            "SageFs.slnx"
            None   // no pre-capture setup
            None   // no cleanup
        t.GetAwaiter().GetResult())

      testCase "extension — SageFs in action" (fun () ->
        let fsx =
          Path.Combine(
            @"C:\Code\Repos\SageFs", "samples", "getting-started.fsx")
        let t =
          recordDemo
            "sagefs-extension"
            DemoScenarios.extensionDemo
            true   // needs SageFs extension
            10     // capture fps
            3      // playback fps
            (Some fsx)
            @"C:\Code\Repos\SageFs"
            "SageFs.slnx"
            "SageFs.slnx"
            None   // no pre-capture setup
            None   // no cleanup
        t.GetAwaiter().GetResult())

      testCase "hot-reload hero — live testing red-green cycle" (fun () ->
        let hotreloadDir = @"C:\Code\Repos\hotreloaddemo"
        let testProj = @"Tests\Tests.fsproj"
        let pongFile = Path.Combine(hotreloadDir, "Pong.fs")

        // Pre-condition: build the test project so DLL exists
        let buildTask = DemoDaemon.buildProject hotreloadDir testProj
        let buildOk = buildTask.GetAwaiter().GetResult()
        match buildOk with
        | false -> failtest "Test project build failed — cannot record demo"
        | true -> ()

        // Setup before capture: enable live testing, wait for all green
        let setupLiveTesting (page: IPage) = task {
          // Enable live testing via command palette
          do! DemoDaemon.enableLiveTestingViaVscode page

          // Wait for test discovery + initial run (27 tests, ~10-20s)
          printfn "   ⏳ Waiting for live testing to discover and run tests..."
          do! Task.Delay(20_000)

          // Verify tests appear in status bar
          let! _ = DemoDaemon.waitForTestStatusBar page "passed" 30_000

          // Navigate back to Pong.fs for the demo
          do! page.Keyboard.PressAsync("Control+p")
          do! Task.Delay(500)
          do! page.Keyboard.TypeAsync("Pong.fs")
          do! Task.Delay(500)
          do! page.Keyboard.PressAsync("Enter")
          do! Task.Delay(2000)
        }

        let cleanup () =
          DemoDaemon.restoreFiles hotreloadDir
            [ "Pong.fs"; @"Tests\PongTests.fs" ]

        let t =
          recordDemo
            "sagefs-hero"
            DemoScenarios.hotReloadHeroDemo
            true   // extensions (SageFs + Ionide for syntax)
            16     // capture: 16 fps
            4      // playback: 4 fps — ~25s GIF
            (Some pongFile)
            hotreloadDir
            testProj
            @"Tests/Tests.fsproj"
            (Some setupLiveTesting)
            (Some cleanup)

        try
          t.GetAwaiter().GetResult()
        with ex ->
          // Always restore files even if recording fails
          cleanup ()
          reraise ())
    | false ->
      // Kept pending: VS Code demo recording is machine-dependent — needs VS Code + the
      // recorder driving it. These placeholder cases only exist so the suite is visible;
      // real recording runs in the `true` branch above.
      ptestCase "Demo recording: hero (VS Code not found)" ignore
      ptestCase "Demo recording: extension (VS Code not found)" ignore
      ptestCase "Demo recording: hot-reload hero (VS Code not found)" ignore
  ]
