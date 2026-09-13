/// The VS Code actor (demo-actors-plan.md §2.1, roast H5 — "the riskiest
/// actor"). Drives a real VS Code instance via its extension host —
/// `executeCommand`/the `sagefs.debug.rectFor` loopback control channel
/// (`sagefs-vscode/src/Extension.fs`/`DebugRects.fs`) — never CDP, never
/// `--remote-debugging-port` (issue #133's flakiness, forbidden outright by
/// H5). Mirrors `Actors/Dashboard.fs`'s shape exactly: `launch` owns
/// spawning the real process (a deliberate, documented choice — see the
/// module doc on `launch` below), `resolveRect`/`observe`/`close` are moved
/// onto the shared `LiveActor` record the same way Dashboard's are.
///
/// SPIKE PROOF (demo-actors-plan.md §4/§7's "spike the one unknown first"):
/// this actor's launch + control-channel design was proven live against the
/// REAL cached VS Code build (`sagefs-vscode/.vscode-test/vscode-linux-x64-
/// 1.137.0/code`) on an isolated Xvfb display, following
/// `feedback_electron_gui_isolation.md`'s verified recipe exactly (`env -i`
/// with an explicit HOME/XDG_*/DISPLAY allowlist, `--ozone-platform=x11`, a
/// SHORT `--user-data-dir` — a long one throws VS Code's own `listen EINVAL`
/// on its IPC socket, confirmed directly, hence `Runtime.VsCode.fs`'s note
/// on cell path length): the extension activated (via the real
/// `workspaceContains:**/*.fsproj` trigger, opening a workspace that
/// contains one), registered `sagefs.debug.rectFor`, and started its
/// loopback HTTP listener — confirmed reachable from a separate OS process
/// (`curl http://127.0.0.1:<port>/rectFor?target=editor` → a well-formed
/// JSON response) with zero window ever appearing on the real desktop
/// (`hyprctl clients` before/after). The one thing NOT provable on this
/// specific dev box: a non-null rect — `xdotool` (the window-geometry query
/// `Extension.fs`'s handler shells out to) is not installed here and has no
/// passwordless sudo path to install (memory: ask the user rather than
/// work around a missing system package), so `resolveOwnWindowRect`
/// legitimately, honestly returned `None` → the command legitimately
/// returned `null` — never a crash, never a fabricated rect. The demo cell
/// (`Sandbox.fs`'s bwrap image) RO-binds its own toolchain and must include
/// `xdotool` for a real recording to resolve non-null rects; that RO-bind
/// is `Runtime.VsCode.fs`'s `cellBinds` concern once wired.
module SageFs.Demos.Actors.VsCode

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text.Json
open SageFs.Demos.Domain
open SageFs.Demos.Actors.Actor

/// A running VS Code instance this actor drives: the process handle and an
/// `HttpClient` pointed at the extension's own loopback control channel
/// (`sagefs.debug.rectFor`'s HTTP listener, port `SAGEFS_DEBUG_RECTS_PORT`).
type Handle =
  { Process: Process
    ControlBaseUrl: string
    Http: HttpClient }

/// The daemon's own MCP HTTP port inside a demo cell (`Runtime.fs`'s
/// `McpPort` — re-declared here rather than referenced across the
/// never-touch seam boundary, exactly like `CellAgent.fs`'s own
/// `CellDisplay = ":99"` re-declaration of a fixed, documented cell
/// constant). Used by `observe`'s `"daemon:sessionReady"` signal.
[<Literal>]
let DaemonMcpPort = 47749

/// VS Code's own IPC socket path has roughly a 107-char ceiling (a raw
/// AF_UNIX path-length limit) — a `--user-data-dir` nested deep inside a
/// cell's own scratch tree throws `Error: listen EINVAL` at startup with no
/// window ever appearing (confirmed directly against this exact VS Code
/// build during the spike). Callers must pass a SHORT `userDataDir` (e.g.
/// `/home/demo/vsc` inside the cell, not a long nested path) — this
/// function only asserts the constraint so a violation fails loud at spawn
/// time with an actionable message instead of a baffling silent hang.
let private checkUserDataDirLength (userDataDir: string) : Result<unit, string> =
  if userDataDir.Length > 60 then
    Error(
      sprintf
        "VS Code actor: --user-data-dir '%s' (%d chars) is too long — VS Code's own IPC socket has ~107 char ceiling and throws 'listen EINVAL' with no window ever appearing (confirmed directly). Use a short scratch path (e.g. /home/demo/vsc)."
        userDataDir
        userDataDir.Length
    )
  else
    Ok()

/// Fails loud (mirrors the `--integration-vsc` `Environment.Exit 1`
/// precedent — never a silent skip) when the resolved VS Code binary does
/// not exist, so a missing/undownloaded build surfaces as a clear message
/// instead of a `Win32Exception` deep inside `Process.Start`.
let checkPrerequisites (codeBin: string) : Result<unit, string> =
  if File.Exists codeBin then
    Ok()
  else
    Error(
      sprintf
        "VS Code actor: no executable at '%s'. Set SAGEFS_TE_VSCODE_PATH or run `npx @vscode/test-electron` under sagefs-vscode/ to download one — never silently skip the VS Code scenarios."
        codeBin
    )

/// Best-effort, non-fatal window placement via a real X11 query — searches
/// by window name (the extension host runs in a separate Node process from
/// the Electron window it draws, so there is no PID to search by from
/// here) and moves/resizes it to `rect`. Swallows every failure (missing
/// `xdotool`, no window yet) exactly like `resolveOwnWindowRect` in
/// `Extension.fs` does for the SAME reason: a demo cell that lacks
/// `xdotool` still records something (at the window's default position)
/// rather than crashing the whole cell-agent over a placement nicety. Never
/// CDP `Browser.setWindowBounds` (roast H5).
let private tryPlaceWindow (rect: Rect) : Async<unit> =
  async {
    try
      let search = ProcessStartInfo("xdotool", RedirectStandardOutput = true, UseShellExecute = false)
      for a in [ "search"; "--name"; "Visual Studio Code" ] do
        search.ArgumentList.Add a
      use proc = Process.Start search
      let! out = proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
      do! proc.WaitForExitAsync() |> Async.AwaitTask

      match out.Split('\n') |> Array.tryHead |> Option.map (fun s -> s.Trim()) with
      | None
      | Some "" -> ()
      | Some winId ->
        let move = ProcessStartInfo("xdotool", UseShellExecute = false)
        for a in [ "windowmove"; winId; string rect.X; string rect.Y ] do
          move.ArgumentList.Add a
        use p1 = Process.Start move
        do! p1.WaitForExitAsync() |> Async.AwaitTask

        let resize = ProcessStartInfo("xdotool", UseShellExecute = false)
        for a in [ "windowsize"; winId; string rect.W; string rect.H ] do
          resize.ArgumentList.Add a
        use p2 = Process.Start resize
        do! p2.WaitForExitAsync() |> Async.AwaitTask
    with _ ->
      ()
  }

/// Launches VS Code with the sagefs extension loaded via
/// `--extensionDevelopmentPath` (no install step, no marketplace, no CDP)
/// following `feedback_electron_gui_isolation.md`'s verified recipe: an
/// explicit env ALLOWLIST (never the inherited ambient environment —
/// `psi.Environment.Clear()` below is the .NET equivalent of `env -i`),
/// `--ozone-platform=x11`, and `SAGEFS_DEBUG_RECTS_PORT` so the extension's
/// loopback control channel binds to a port this actor already knows.
/// `launch` — not a bash `innerScript` prologue — owns spawning, exactly
/// like `Actors/Dashboard.fs`'s `launch` owns spawning Chromium: the two
/// editor/browser actors should be symmetric, and this shape is the one
/// actually proven live (see the module doc above), so `Runtime.VsCode.fs`
/// contributes cell BINDS (the resolved VS Code build + this extension
/// directory, RO) rather than a bash-level launch fragment.
let launch
  (codeBin: string)
  (extensionDevPath: string)
  (userDataDir: string)
  (extensionsDir: string)
  (workspaceDir: string)
  (rect: Rect)
  (display: string)
  (controlPort: int)
  : Async<Result<Handle, string>> =
  async {
    match checkPrerequisites codeBin, checkUserDataDirLength userDataDir with
    | Error e, _
    | _, Error e -> return Error e
    | Ok(), Ok() ->

    let psi = ProcessStartInfo(codeBin, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
    psi.Environment.Clear()
    psi.Environment.["HOME"] <- userDataDir
    psi.Environment.["XDG_CONFIG_HOME"] <- Path.Combine(userDataDir, "xdg-config")
    psi.Environment.["XDG_CACHE_HOME"] <- Path.Combine(userDataDir, "xdg-cache")
    psi.Environment.["XDG_DATA_HOME"] <- Path.Combine(userDataDir, "xdg-data")
    psi.Environment.["XDG_STATE_HOME"] <- Path.Combine(userDataDir, "xdg-state")
    psi.Environment.["DISPLAY"] <- display
    psi.Environment.["PATH"] <- (Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue "/usr/bin")
    psi.Environment.["SAGEFS_DEBUG_RECTS_PORT"] <- string controlPort

    for arg in
      [ sprintf "--user-data-dir=%s" userDataDir
        sprintf "--extensions-dir=%s" extensionsDir
        sprintf "--extensionDevelopmentPath=%s" extensionDevPath
        "--disable-workspace-trust"
        "--ozone-platform=x11"
        "--no-sandbox"
        // Best-effort — VS Code does not document these as a stable CLI
        // contract the way `--user-data-dir` is; `tryPlaceWindow` below is
        // the mechanism actually verified to move the window (§2.1: "window
        // placement via X11 ... on :99, never CDP").
        sprintf "--window-position=%d,%d" rect.X rect.Y
        sprintf "--window-size=%d,%d" rect.W rect.H
        "--new-window"
        workspaceDir ] do
      psi.ArgumentList.Add arg

    let proc = Process.Start psi
    // Drain both streams (never leave a redirected pipe undrained — a
    // known deadlock class for a long-lived child, per this repo's own E2E
    // harness lesson) without keeping the output around; a demo cell's own
    // `daemon.log`-style capture is `Runtime.VsCode.fs`'s concern once
    // wired, not this actor's.
    proc.OutputDataReceived.Add(fun _ -> ())
    proc.ErrorDataReceived.Add(fun _ -> ())
    proc.BeginOutputReadLine()
    proc.BeginErrorReadLine()

    // Give the extension host a moment to activate and start its loopback
    // listener before attempting to place the window — a real recording
    // gates on `ResolveRect`/`Observe` succeeding rather than a fixed
    // sleep, but the window has to exist before `xdotool` can find it.
    do! Async.Sleep 3000
    do! tryPlaceWindow rect

    let http = new HttpClient(Timeout = TimeSpan.FromSeconds 5.0)
    return Ok { Process = proc; ControlBaseUrl = sprintf "http://127.0.0.1:%d" controlPort; Http = http }
  }

/// GETs `url`, returning `None` on any failure OR a genuine JSON `null`
/// body (`sagefs.debug.rectFor`'s own honest-absence contract) — never
/// throws past this function, mirroring `Dashboard.fs`'s `resolveRect`
/// swallowing its own Playwright timeouts into `None`.
let private tryGetJson (http: HttpClient) (url: string) : Async<JsonElement option> =
  async {
    try
      let! resp = http.GetAsync url |> Async.AwaitTask

      if not resp.IsSuccessStatusCode then
        return None
      else
        let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        use doc = JsonDocument.Parse body

        if doc.RootElement.ValueKind = JsonValueKind.Null then
          return None
        else
          return Some(doc.RootElement.Clone())
    with _ ->
      return None
  }

/// Resolves a target ("caret"|"editor"|"statusBar"|"view:<id>") to a
/// `ScreenRect` through the extension host's own `sagefs.debug.rectFor`
/// loopback endpoint — never CDP, never a guess.
let resolveRect (handle: Handle) (target: string) : Async<ScreenRect option> =
  async {
    let url = sprintf "%s/rectFor?target=%s" handle.ControlBaseUrl (Uri.EscapeDataString target)
    let! json = tryGetJson handle.Http url

    return
      json
      |> Option.bind (fun el ->
        try
          let getf (name: string) = el.GetProperty(name).GetDouble()
          Some { X = int (getf "x"); Y = int (getf "y"); W = int (getf "w"); H = int (getf "h") }
        with _ ->
          None)
  }

/// Observes an expectation through real ext-host/daemon state — never the
/// DOM (there is none to read without CDP). Two selector conventions this
/// actor understands: `"daemon:sessionReady"` polls the daemon's own real
/// `/health` endpoint for `status = "Ready"` (the same source of truth the
/// extension's own status bar polls — SageFsClient.fs); `"rect:<target>"`
/// polls `resolveRect` until it resolves non-`None` (a panel/view becoming
/// visible). Any other selector is an honest `false` — a signal this actor
/// does not yet know how to observe, never a fabricated pass.
let observe (handle: Handle) (daemonBaseUrl: string) (selector: string) (timeoutMs: float) : Async<bool> =
  let deadline = DateTime.UtcNow.AddMilliseconds timeoutMs

  let checkOnce () =
    async {
      if selector = "daemon:sessionReady" then
        let! json = tryGetJson handle.Http (sprintf "%s/health" daemonBaseUrl)

        return
          json
          |> Option.exists (fun el ->
            match el.TryGetProperty "status" with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() = "Ready"
            | _ -> false)
      elif selector.StartsWith "rect:" then
        let! rect = resolveRect handle (selector.Substring 5)
        return rect.IsSome
      else
        return false
    }

  let rec poll () =
    async {
      let! ok = checkOnce ()

      if ok then
        return true
      elif DateTime.UtcNow > deadline then
        return false
      else
        do! Async.Sleep 500
        return! poll ()
    }

  poll ()

/// Runs a non-input client command. The only token this actor understands
/// today is `"create-session-api:<absolute-working-dir>"`
/// (`Domain.fs`'s `ClientCommand.CreateSession`, `Runtime.fs`'s wire
/// mapping): the extension's own session-creation paths are ALL
/// interactive (`createSessionCmd`'s quick-pick, and the auto-discover
/// flow's `Window.showInformationMessage` confirm dialog,
/// `sagefs-vscode/src/Extension.fs`) — synthetic input has no reliable way
/// to answer either, the exact same picker problem class the Neovim
/// actor's pinned-commit fix solves on ITS side. Rather than fake a click
/// on a dialog that may not even be visible yet, this calls the cell
/// daemon's own `/api/sessions/create` HTTP API directly — the same real
/// endpoint the extension itself would call, just invoked over the actor's
/// existing HTTP channel instead of through the (missing) non-interactive
/// UI command. Posting BEFORE the extension's own 2-second auto-discover
/// delay fires means `Client.listSessions` already sees a session, so that
/// flow's own `[||] -> prompt` branch never triggers — no dialog ever
/// appears to race. `projects = []` (never a project literal): the daemon
/// auto-discovers the lone `.fsproj` in `workingDirectory`, exactly
/// `hello-dashboard`'s own real-project doctrine (`Scenarios.fs`).
let command (handle: Handle) (daemonBaseUrl: string) (token: string) : Async<unit> =
  async {
    if token.StartsWith "create-session-api:" then
      let workingDir = token.Substring "create-session-api:".Length
      let payload = JsonSerializer.Serialize {| projects = ([||]: string[]); workingDirectory = workingDir |}
      use content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")

      try
        let! resp = handle.Http.PostAsync(sprintf "%s/api/sessions/create" daemonBaseUrl, content) |> Async.AwaitTask
        resp.Dispose()
      with _ ->
        // A real, honest failure here surfaces the same way every other
        // missing/failed observation does — through the step's own
        // `Expect` never resolving, never a swallowed exception pretending
        // the session exists.
        ()
  }

/// Terminates the VS Code process tree and disposes the control-channel
/// client. `killTree = true` so the electron zygote/gpu/renderer/extension-
/// host children this actor's own spike observed (confirmed via `ps aux`
/// during the live proof) do not orphan.
let close (handle: Handle) : Async<unit> =
  async {
    try
      if not handle.Process.HasExited then
        handle.Process.Kill(entireProcessTree = true)
    with _ ->
      ()

    handle.Http.Dispose()
  }

/// Wraps this actor behind the cell-agent's actor-dispatch seam (Island F,
/// demo-actors-plan.md §1.2) — the same shape `Actors/Dashboard.fs`'s
/// `toLiveActor` exposes. `Command` is genuinely implemented (see its own
/// doc above) — the only token it knows is `"create-session-api:..."`;
/// everything else is still a no-op, exactly `Dashboard.fs`'s own doctrine
/// for an unrecognized command.
let toLiveActor (handle: Handle) (daemonBaseUrl: string) : LiveActor =
  { Id = ActorId.VsCode
    ResolveRect = resolveRect handle
    Observe = observe handle daemonBaseUrl
    Command = command handle daemonBaseUrl
    Close = fun () -> close handle }
