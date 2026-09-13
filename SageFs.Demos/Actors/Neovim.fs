/// The Neovim actor (demo-actors-plan.md §2.2): a REAL Neovim inside a REAL
/// kitty terminal on the cell's own Xvfb display, driven two ways — RPC
/// (`nvim --server <sock> --remote-expr <vimscript>`, nvim's own remote-
/// client CLI, never a hand-rolled msgpack codec) for setup/geometry/
/// observation, and XTest (delivered centrally by `CellAgent.runStep`,
/// exactly like every other actor) for the on-camera keystrokes — so a
/// recording shows human-driven typing, never an RPC paste.
///
/// Xvfb runs with NO window manager (`Runtime.fs`'s `innerScript` starts only
/// Xvfb — confirmed by reading it start to finish), so nothing else on the
/// display will ever report or place kitty's window. This actor talks
/// straight to libX11 for that one narrow need — enumerate the root's
/// children, find kitty's by its `--title` marker, read/force its geometry —
/// via its OWN P/Invoke declarations, deliberately NOT added to `XTest.fs`
/// (never-touch: that module is the shared XTEST *input-delivery* edge and
/// has no need for window enumeration/placement; duplicating the four calls
/// this actor needs here keeps that shared edge exactly as Island F left it).
///
/// Selector vocabulary (the opaque strings `ResolveRect`/`Observe` take, per
/// the seam's own doc: "DOM for dashboard, ext-host for VS Code, RPC for
/// nvim" — `CellAgent.fs`'s `LiveActor` doc): see `Selector` below. It is
/// deliberately shaped to be the direct wire-string image of the two
/// Neovim-specific `Domain` cases already reserved for this actor
/// (`Target.NvimCommandLine`, `Expectation.NvimBufferContains`) plus the
/// actor-agnostic `Target.WindowCenter`/`Target.EditorPosition` — see this
/// island's final report for the one open seam gap this depends on
/// (`Runtime.fs`'s `wireStepOf` does not yet emit these strings; that
/// function is on this island's never-touch list, so it is reported for
/// main-thread sign-off rather than edited here).
module SageFs.Demos.Actors.Neovim

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading.Tasks
open SageFs.Demos.Domain
open SageFs.Demos.Actors.Actor

let private killQuietly (proc: Process) : unit =
  try
    proc.Kill true
  with _ ->
    ()

/// Xlib's DEFAULT error handler does not just print an `XErrorEvent` — for
/// most error types it calls `exit()` on the WHOLE PROCESS (confirmed
/// directly: an `XSetInputFocus` `BadMatch` — the target window not yet
/// fully viewable at the exact moment focus was requested, a real, expected
/// timing race, never a genuine bug in the request itself — killed the
/// entire cell-agent mid-recording with no StepLog ever written). Mirrors
/// `Actors/App.fs`'s own identical fix for the identical class of problem:
/// installing a handler that returns instead of aborting turns a transient,
/// recoverable X protocol timing race into an ordinary failed call (a
/// non-zero return this module already treats as "try again"/"give up
/// gracefully"), never a process-ending abort.
type private XErrorHandler = delegate of nativeint * nativeint -> int

/// Minimal Xlib bindings, private to this actor. See the module doc above
/// for why these live here instead of `XTest.fs`.
module private Xlib =
  [<DllImport("libX11.so.6")>]
  extern nativeint XOpenDisplay(string display)

  [<DllImport("libX11.so.6")>]
  extern int XCloseDisplay(nativeint display)

  [<DllImport("libX11.so.6")>]
  extern nativeint XDefaultRootWindow(nativeint display)

  [<DllImport("libX11.so.6")>]
  extern int XQueryTree(nativeint display, nativeint w, nativeint& root, nativeint& parent, nativeint& children, uint32& nchildren)

  [<DllImport("libX11.so.6")>]
  extern int XFree(nativeint data)

  [<DllImport("libX11.so.6")>]
  extern int XFetchName(nativeint display, nativeint w, nativeint& name)

  [<DllImport("libX11.so.6")>]
  extern int XGetGeometry(
    nativeint display,
    nativeint drawable,
    nativeint& root,
    int& x,
    int& y,
    uint32& width,
    uint32& height,
    uint32& borderWidth,
    uint32& depth
  )

  [<DllImport("libX11.so.6")>]
  extern int XMoveResizeWindow(nativeint display, nativeint w, int x, int y, uint32 width, uint32 height)

  [<DllImport("libX11.so.6")>]
  extern int XSync(nativeint display, bool discard)

  [<DllImport("libX11.so.6")>]
  extern int XSetInputFocus(nativeint display, nativeint w, int revertTo, nativeint time)

  [<DllImport("libX11.so.6")>]
  extern int XRaiseWindow(nativeint display, nativeint w)

  [<DllImport("libX11.so.6")>]
  extern nativeint XSetErrorHandler(XErrorHandler handler)

/// Kept alive for the whole process — see `Actors/App.fs`'s identical
/// pattern/doc for why a delegate passed to native code must not be
/// eligible for GC.
let mutable private errorHandler: XErrorHandler = Unchecked.defaultof<_>

/// Installs the non-aborting error handler exactly once per process
/// (idempotent — a second `XSetErrorHandler` call is harmless).
let private installErrorHandler =
  lazy
    (errorHandler <- XErrorHandler(fun _ _ -> 0)
     Xlib.XSetErrorHandler errorHandler |> ignore)

let private childrenOf (display: nativeint) (w: nativeint) : nativeint list =
  let mutable root = 0n
  let mutable parent = 0n
  let mutable childrenPtr = 0n
  let mutable count = 0u
  Xlib.XQueryTree(display, w, &root, &parent, &childrenPtr, &count) |> ignore

  if childrenPtr = 0n then
    []
  else
    try
      [ for i in 0 .. int count - 1 -> Marshal.ReadIntPtr(childrenPtr, i * IntPtr.Size) ]
    finally
      Xlib.XFree childrenPtr |> ignore

let private nameOf (display: nativeint) (w: nativeint) : string option =
  let mutable namePtr = 0n

  if Xlib.XFetchName(display, w, &namePtr) <> 0 && namePtr <> 0n then
    let name = Marshal.PtrToStringAnsi namePtr
    Xlib.XFree namePtr |> ignore
    Some name
  else
    None

/// Depth-first search from `root` for a window whose `WM_NAME` is exactly
/// `title` — one level finds kitty's own top-level window under a WM-less
/// Xvfb (it is a direct child of root), the recursion is only a safety net
/// in case kitty ever reparents itself.
let rec private findWindowByTitle (display: nativeint) (root: nativeint) (title: string) : nativeint option =
  childrenOf display root
  |> List.tryPick (fun w ->
    match nameOf display w with
    | Some n when n = title -> Some w
    | _ -> findWindowByTitle display w title)

let private geometryOf (display: nativeint) (w: nativeint) : ScreenRect =
  let mutable root = 0n
  let mutable x = 0
  let mutable y = 0
  let mutable width = 0u
  let mutable height = 0u
  let mutable borderWidth = 0u
  let mutable depth = 0u
  Xlib.XGetGeometry(display, w, &root, &x, &y, &width, &height, &borderWidth, &depth) |> ignore
  { X = x; Y = y; W = int width; H = int height }

/// A live Neovim-inside-kitty actor. `WindowRect`/`Columns`/`Rows` are
/// measured ONCE at launch (after the forced resize below settles) — kitty's
/// grid does not change again for the life of a cell, so re-measuring per
/// selector would just be the same live facts read twice.
type Handle =
  { KittyProcess: Process
    XDisplay: nativeint
    WindowId: nativeint
    NvimPath: string
    NvimSocket: string
    NvimEnv: (string * string) list
    WindowRect: ScreenRect
    Columns: int
    Rows: int }

/// The hard ceiling on any single `nvim --server ... --remote-expr` round
/// trip. A healthy server answers in milliseconds; empirically confirmed on
/// this box (see this island's final report) that it can instead hang
/// FOREVER — never returning, never erroring — when the target server is
/// stuck on its own "Press ENTER or type command to continue" modal prompt
/// (e.g. from an uncaught Lua error in `-u init.lua`), which blocks the RPC
/// channel entirely until a keystroke dismisses it. Without this cap, one
/// stuck prompt wedges every caller (`pollNvimReady`'s own readiness loop
/// included) indefinitely — the exact opposite of "fail loud". `Task.WhenAny`
/// against a `Task.Delay` is the timeout; on expiry the hung client process
/// is killed so it cannot leak.
[<Literal>]
let private RemoteExprTimeoutMs = 5000

let private remoteExprIn (nvimPath: string) (socket: string) (env: (string * string) list) (expr: string) : Async<Result<string, string>> =
  async {
    let psi = ProcessStartInfo(nvimPath, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    psi.ArgumentList.Add "--server"
    psi.ArgumentList.Add socket
    psi.ArgumentList.Add "--remote-expr"
    psi.ArgumentList.Add expr
    psi.EnvironmentVariables.Clear()

    for k, v in env do
      psi.EnvironmentVariables.[k] <- v

    use proc = new Process(StartInfo = psi)

    try
      proc.Start() |> ignore
      let stdoutTask = proc.StandardOutput.ReadToEndAsync()
      let stderrTask = proc.StandardError.ReadToEndAsync()
      let exitTask = proc.WaitForExitAsync()
      let! _ = Task.WhenAny(exitTask, Task.Delay RemoteExprTimeoutMs) |> Async.AwaitTask

      if not proc.HasExited then
        killQuietly proc
        return Error(sprintf "nvim --remote-expr timed out after %dms (server likely stuck on a modal prompt)" RemoteExprTimeoutMs)
      else
        let! stdout = stdoutTask |> Async.AwaitTask
        let! stderr = stderrTask |> Async.AwaitTask

        if proc.ExitCode = 0 then
          return Ok(stdout.TrimEnd('\n', '\r'))
        else
          return Error(if String.IsNullOrWhiteSpace stderr then sprintf "nvim --remote-expr exited %d" proc.ExitCode else stderr.Trim())
    with ex ->
      killQuietly proc
      return Error ex.Message
  }

let private remoteExpr (handle: Handle) (expr: string) : Async<Result<string, string>> =
  remoteExprIn handle.NvimPath handle.NvimSocket handle.NvimEnv expr

/// Runs ex-command(s) via VimL's own `execute()` — the same mechanism a
/// human typing `:SageFsEval<CR>` triggers, just invoked over RPC instead of
/// a keystroke (used only by `Command`, never by the on-camera `Type`
/// action, which always goes through XTest — see the module doc).
let private execCommand (handle: Handle) (exCommand: string) : Async<Result<string, string>> =
  let escaped = exCommand.Replace("'", "''")
  remoteExpr handle (sprintf "execute('%s')" escaped)

let rec private pollUntil (deadline: DateTime) (delayMs: int) (attempt: unit -> Async<bool>) : Async<bool> =
  async {
    let! ok = attempt ()

    if ok then
      return true
    elif DateTime.UtcNow > deadline then
      return false
    else
      do! Async.Sleep delayMs
      return! pollUntil deadline delayMs attempt
  }

let private pollForWindow (displayName: string) (title: string) (timeoutMs: int) : Async<Result<nativeint * nativeint, string>> =
  async {
    match Xlib.XOpenDisplay displayName with
    | d when d = 0n -> return Error(sprintf "could not open X display %s (is Xvfb up?)" displayName)
    | display ->
      let root = Xlib.XDefaultRootWindow display
      let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

      let rec loop () =
        async {
          match findWindowByTitle display root title with
          | Some w -> return Ok(display, w)
          | None ->
            if DateTime.UtcNow > deadline then
              Xlib.XCloseDisplay display |> ignore
              return Error(sprintf "kitty window titled '%s' never appeared on %s within %dms" title displayName timeoutMs)
            else
              do! Async.Sleep 200
              return! loop ()
        }

      return! loop ()
  }

/// `--remote-send` never blocks on the target's own response the way
/// `--remote-expr` can (confirmed directly: sending keys to a server stuck
/// on a modal prompt returns immediately) — used here ONLY to dismiss a
/// startup "Press ENTER or type command to continue" prompt (e.g. from an
/// uncaught Lua error in `-u init.lua`) before the NEXT readiness ping,
/// never during a real scenario step (where blindly sending Enter/Escape
/// could corrupt genuine in-progress editor state). Errors are swallowed —
/// this is best-effort self-healing before a retry, not a step whose
/// success matters on its own.
let private dismissPrompt (nvimPath: string) (socket: string) (env: (string * string) list) : Async<unit> =
  async {
    let psi = ProcessStartInfo(nvimPath, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    psi.ArgumentList.Add "--server"
    psi.ArgumentList.Add socket
    psi.ArgumentList.Add "--remote-send"
    psi.ArgumentList.Add "<CR><Esc>"
    psi.EnvironmentVariables.Clear()

    for k, v in env do
      psi.EnvironmentVariables.[k] <- v

    try
      use proc = new Process(StartInfo = psi)
      proc.Start() |> ignore
      let! _ = Task.WhenAny(proc.WaitForExitAsync(), Task.Delay 1000) |> Async.AwaitTask
      if not proc.HasExited then killQuietly proc
    with _ ->
      ()
  }

let private pollNvimReady (nvimPath: string) (socket: string) (env: (string * string) list) (timeoutMs: int) : Async<bool> =
  let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
  let mutable firstAttempt = true

  pollUntil deadline 300 (fun () ->
    async {
      if not firstAttempt then
        do! dismissPrompt nvimPath socket env

      firstAttempt <- false

      match! remoteExprIn nvimPath socket env "1" with
      | Ok _ -> return true
      | Error _ -> return false
    })

let private queryGridSize (nvimPath: string) (socket: string) (env: (string * string) list) : Async<int * int> =
  async {
    let parseOr fallback (text: string) =
      match Int32.TryParse(text.Trim()) with
      | true, v -> v
      | false, _ -> fallback

    let! colsResult = remoteExprIn nvimPath socket env "&columns"
    let! rowsResult = remoteExprIn nvimPath socket env "&lines"

    let cols =
      match colsResult with
      | Ok text -> parseOr 80 text
      | Error _ -> 80

    let rows =
      match rowsResult with
      | Ok text -> parseOr 24 text
      | Error _ -> 24

    return cols, rows
  }

let private luaStringLiteral (s: string) : string = "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'"

/// Launches kitty running `nvim --listen <socket>` on `display`, with a
/// generated, isolated `-u <init.lua>` (never the real user's `init.lua` —
/// `-u` replaces it outright) that prepends `pluginRuntimePath` (the pinned,
/// clean sagefs.nvim checkout `Runtime.Neovim.fs` resolves — never the live
/// `~/Work/sagefs.nvim` working tree, per demo-actors-plan.md §7.5) onto the
/// runtimepath and calls `require('sagefs').setup` pointed at the cell's own
/// daemon ports, then forces the window to `rect` via `XMoveResizeWindow`
/// (kitty has no `-geometry`/window-position flag of its own, and there is
/// no WM to place it) and measures the real, live grid size nvim itself
/// reports (`&columns`/`&lines`) — never a guessed cell size.
///
/// Environment isolation follows the verified `feedback_electron_gui_
/// isolation.md` recipe: a cleared environment, an explicit `DISPLAY` only
/// (no `WAYLAND_DISPLAY`, so kitty can never probe for a Wayland compositor
/// instead), and private `XDG_*`/`HOME` scratch dirs under `workDir` — the
/// same discipline the plan's own §2.1 VS Code note cites for Electron,
/// applied here because kitty auto-detects Wayland exactly the same way.
let launch
  (kittyPath: string)
  (nvimPath: string)
  (pluginRuntimePath: string option)
  (display: Display)
  (rect: Rect)
  (workDir: string)
  (openFilePath: string option)
  (mcpPort: int)
  (dashboardPort: int)
  // The REAL project directory nvim should treat as its own `getcwd()` —
  // distinct from `workDir` above (a private scratch dir for THIS actor's
  // own generated init.lua/socket, never the project). Without this, kitty
  // (and the nvim it execs) inherit the cell-agent .NET process's own
  // ambient working directory, so a plugin command that globs from
  // `vim.fn.getcwd()` (`sagefs.nvim`'s own `discover_and_create`) silently
  // scans the WRONG tree — confirmed directly against a real recording: a
  // still frame showed nvim's cwd resolved to the whole repo checkout (32
  // `.fsproj` files listed), not the one real sample project this scenario
  // opened. `None` preserves the exact pre-fix behavior (ambient cwd) for
  // any caller that genuinely has no project directory to anchor to.
  (projectDir: string option)
  : Async<Handle> =
  async {
    if not (File.Exists kittyPath) then
      failwithf "kitty not found at %s — install it (pacman -S kitty / apt install kitty) or pass its resolved path" kittyPath

    if not (File.Exists nvimPath) then
      failwithf "nvim not found at %s — install it (pacman -S neovim / apt install neovim) or pass its resolved path" nvimPath

    let (Display displayName) = display
    Directory.CreateDirectory workDir |> ignore
    let marker = sprintf "sagefs-demo-nvim-%s" (Guid.NewGuid().ToString("N").Substring(0, 8))
    let nvimSocket = Path.Combine(workDir, "nvim.sock")
    let initLuaPath = Path.Combine(workDir, "init.lua")

    // `require('sagefs').setup` only when a plugin runtimepath was actually
    // given — calling it with no plugin on the runtimepath throws inside
    // `-u init.lua`, which lands nvim on its own "Press ENTER or type
    // command to continue" prompt and wedges every subsequent RPC call
    // until a keystroke dismisses it (confirmed directly: reproduced this
    // exact hang on this box while building this actor, see this island's
    // final report — `pollNvimReady`'s `dismissPrompt` recovery is the
    // defense-in-depth half of the fix; not calling `setup` blind is the
    // other, cheaper half).
    let initLua =
      [ match pluginRuntimePath with
        | Some dir -> sprintf "vim.opt.rtp:prepend(%s)" (luaStringLiteral dir)
        | None -> ()
        "vim.opt.mouse = 'a'" // a real click must be able to move the real cursor (§9's on-camera doctrine — no synthetic teleport of *editor* state, only of the pointer).
        match pluginRuntimePath with
        | Some _ -> sprintf "require('sagefs').setup({ port = %d, dashboard_port = %d })" mcpPort dashboardPort
        | None -> () ]
      |> String.concat "\n"

    File.WriteAllText(initLuaPath, initLua)

    let nvimArgs = [ "-u"; initLuaPath; "--listen"; nvimSocket ] @ (openFilePath |> Option.toList)
    let kittyArgs = [ "--title"; marker; nvimPath ] @ nvimArgs

    let psi = ProcessStartInfo(kittyPath, UseShellExecute = false)
    projectDir |> Option.iter (fun dir -> psi.WorkingDirectory <- dir)

    for a in kittyArgs do
      psi.ArgumentList.Add a

    psi.EnvironmentVariables.Clear()
    psi.EnvironmentVariables.["DISPLAY"] <- displayName
    psi.EnvironmentVariables.["HOME"] <- workDir
    psi.EnvironmentVariables.["XDG_CONFIG_HOME"] <- Path.Combine(workDir, ".config")
    psi.EnvironmentVariables.["XDG_CACHE_HOME"] <- Path.Combine(workDir, ".cache")
    psi.EnvironmentVariables.["XDG_DATA_HOME"] <- Path.Combine(workDir, ".local", "share")
    psi.EnvironmentVariables.["XDG_STATE_HOME"] <- Path.Combine(workDir, ".local", "state")

    match Environment.GetEnvironmentVariable "PATH" with
    | null -> ()
    | path -> psi.EnvironmentVariables.["PATH"] <- path
    // WAYLAND_DISPLAY deliberately absent (see the doc comment above).

    let kitty = new Process(StartInfo = psi)
    kitty.Start() |> ignore

    match! pollForWindow displayName marker 8000 with
    | Error message ->
      killQuietly kitty
      return failwith message
    | Ok(xdisplay, windowId) ->

    installErrorHandler.Force()
    Xlib.XMoveResizeWindow(xdisplay, windowId, rect.X, rect.Y, uint32 rect.W, uint32 rect.H) |> ignore
    Xlib.XSync(xdisplay, false) |> ignore
    do! Async.Sleep 300 // let kitty reflow its grid after the forced resize before measuring it.

    // With NO window manager on this cell's Xvfb (module doc above), nothing
    // ever calls `XSetInputFocus` on kitty's own window the way a real WM's
    // click-to-focus policy would — confirmed directly against a real
    // recording: every subsequent XTEST-delivered keystroke silently went
    // nowhere (the buffer stayed byte-for-byte unchanged, cursor pinned at
    // 1,1), because X11 keyboard events are delivered to whichever window
    // currently holds the input focus, and Xvfb's own default focus (the
    // root window) is never automatically handed to a newly mapped client
    // without a WM to do it. Done AFTER the resize settles (the window must
    // be viewable, not just mapped, or `XSetInputFocus` itself raises
    // `BadMatch` — a real, expected timing race the installed error handler
    // above now survives instead of aborting the whole process on).
    // `RevertToParent` (2) means focus falls back to this window's parent
    // (the root) if it is ever destroyed — the same safe default a WM would
    // pick.
    Xlib.XRaiseWindow(xdisplay, windowId) |> ignore
    Xlib.XSetInputFocus(xdisplay, windowId, 2, 0n) |> ignore
    Xlib.XSync(xdisplay, false) |> ignore

    let nvimEnv = [ "DISPLAY", displayName ]
    let! ready = pollNvimReady nvimPath nvimSocket nvimEnv 15000

    if not ready then
      killQuietly kitty
      Xlib.XCloseDisplay xdisplay |> ignore
      return failwithf "nvim never became ready on socket %s within 15s" nvimSocket

    let windowRect = geometryOf xdisplay windowId
    let! columns, rows = queryGridSize nvimPath nvimSocket nvimEnv

    return
      { KittyProcess = kitty
        XDisplay = xdisplay
        WindowId = windowId
        NvimPath = nvimPath
        NvimSocket = nvimSocket
        NvimEnv = nvimEnv
        WindowRect = windowRect
        Columns = columns
        Rows = rows }
  }

let private cellRect (handle: Handle) (row1: int) (col1: int) : ScreenRect =
  let cellW = max 1 (handle.WindowRect.W / max 1 handle.Columns)
  let cellH = max 1 (handle.WindowRect.H / max 1 handle.Rows)

  { X = handle.WindowRect.X + (max 0 (col1 - 1)) * cellW
    Y = handle.WindowRect.Y + (max 0 (row1 - 1)) * cellH
    W = cellW
    H = cellH }

/// The whole bottom row (the cell agent clicks/observes the row, not one
/// cell in it — a command-line target is meaningful as a strip, not a
/// point).
let private fullWidthRow (handle: Handle) (row1: int) : ScreenRect =
  let r = cellRect handle row1 1
  { r with W = handle.WindowRect.W; X = handle.WindowRect.X }

/// Resolves a `Selector` (this actor's own opaque wire vocabulary — see the
/// module doc) to a screen rect this actor actually measured live — never a
/// guess. `caret`/`position:<line>:<col>` go through nvim's own `screenpos()`
/// — the SAME function nvim uses internally to place its own cursor, so a
/// coordinate it returns is exactly where nvim itself believes that buffer
/// position currently renders (empty result ⇒ not currently visible ⇒
/// `None`, never a fabricated point).
let resolveRect (handle: Handle) (selector: string) : Async<ScreenRect option> =
  async {
    let screenpos (line: int) (col: int) : Async<ScreenRect option> =
      async {
        match! remoteExpr handle (sprintf "json_encode(screenpos(0, %d, %d))" line col) with
        | Error _ -> return None
        | Ok json ->
          try
            let doc = JsonDocument.Parse json
            let root = doc.RootElement
            let row = root.GetProperty("row").GetInt32()
            let col = root.GetProperty("col").GetInt32()

            if row <= 0 || col <= 0 then
              return None // screenpos() reports 0/0 when the position is not currently visible.
            else
              return Some(cellRect handle row col)
          with _ ->
            return None
      }

    match selector with
    | "window-center" -> return Some handle.WindowRect
    | "commandline" -> return Some(fullWidthRow handle handle.Rows)
    | "statusline" -> return Some(fullWidthRow handle (max 1 (handle.Rows - 1)))
    | "caret" ->
      match! remoteExpr handle "line('.')" with
      | Error _ -> return None
      | Ok lineText ->
        match! remoteExpr handle "col('.')" with
        | Error _ -> return None
        | Ok colText ->
          match Int32.TryParse lineText, Int32.TryParse colText with
          | (true, line), (true, col) -> return! screenpos line col
          | _ -> return None
    | s when s.StartsWith "position:" ->
      match s.Substring("position:".Length).Split(':') with
      | [| lineText; colText |] ->
        match Int32.TryParse lineText, Int32.TryParse colText with
        | (true, line), (true, col) -> return! screenpos line col
        | _ -> return None
      | _ -> return None
    | _ -> return None
  }

/// The Lua one-liner behind `buffer-contains:` — buffer TEXT alone is not
/// what a viewer sees for an eval result: `sagefs.nvim` renders results as
/// extmark `virt_text`/`virt_lines` (confirmed by reading
/// `sagefs.nvim/lua/sagefs/render.lua`: every result/codelens/annotation is
/// `nvim_buf_set_extmark` with `virt_text`/`virt_lines`, never a line
/// inserted into the real buffer), which `getline()` cannot see at all. An
/// observation that only checked `getline()` would never see a real eval
/// result land — silently "passing" for the wrong reason on a step that
/// timed out. This concatenates real buffer lines AND every visible
/// extmark's virtual text/lines, so "the result appeared" means what a
/// camera pointed at the window would actually show.
// The literal Lua source text this produces is wrapped in a VimL DOUBLE-
// quoted string (the `luaeval("...")` argument) — and VimL's own
// double-quote parsing interprets `\n` as an escape and collapses it to a
// real newline BEFORE Lua ever sees the text, which breaks the single-quoted
// Lua string literal `'<newline-here>'` (confirmed directly: this produced
// `E5107: ... unfinished string`). Four backslashes in this F# source ⇒ two
// literal backslashes in the .NET string ⇒ VimL's escape pass collapses
// them to ONE literal backslash+n, which is what Lua's own single-quote
// parser needs to see to produce an actual newline at the RIGHT layer.
let private visibleTextExpr =
  "luaeval(\"(function() local out={} for _,l in ipairs(vim.api.nvim_buf_get_lines(0,0,-1,false)) do table.insert(out,l) end \
for _,m in ipairs(vim.api.nvim_buf_get_extmarks(0,-1,0,-1,{details=true})) do local d=m[4] \
if d.virt_text then local p={} for _,vt in ipairs(d.virt_text) do table.insert(p,vt[1]) end table.insert(out,table.concat(p)) end \
if d.virt_lines then for _,vl in ipairs(d.virt_lines) do local p={} for _,seg in ipairs(vl) do table.insert(p,seg[1]) end table.insert(out,table.concat(p)) end end \
end return table.concat(out,'\\\\n') end)()\")"

let private isTruthy (text: string) : bool =
  match text.Trim().ToLowerInvariant() with
  | "" | "0" | "false" | "v:false" -> false
  | _ -> true

/// Observes a `Selector` through nvim's own RPC — the actor's semantic
/// surface, exactly like the Dashboard actor's DOM `WaitForSelectorAsync`
/// (§4.7 layer 1: proof the action actually worked, never a pixel guess).
/// Polls every 300ms until `timeoutMs` elapses; an unrecognized selector
/// returns `false` rather than a silent, fabricated pass (the job's own
/// "never fake a step" rule).
let observe (handle: Handle) (selector: string) (timeoutMs: float) : Async<bool> =
  let deadline = DateTime.UtcNow.AddMilliseconds timeoutMs

  let attempt () : Async<bool> =
    async {
      match selector with
      | s when s.StartsWith "buffer-contains:" ->
        let text = s.Substring("buffer-contains:".Length)

        match! remoteExpr handle visibleTextExpr with
        | Ok content -> return content.Contains text
        | Error _ -> return false
      | s when s.StartsWith "mode:" ->
        let wanted = s.Substring("mode:".Length)

        match! remoteExpr handle "mode()" with
        | Ok actual -> return actual.Trim('\'', '"') = wanted
        | Error _ -> return false
      | "session-ready" ->
        match! remoteExpr handle "luaeval(\"require('sagefs').active_session ~= nil\")" with
        | Ok result -> return isTruthy result
        | Error _ -> return false
      // `Expectation.EditorSaved` (seam-integration threading, `Runtime.fs`'s
      // `expectationWire`): a real `:w` flips the buffer's own `&modified`
      // option to 0 the instant it lands — genuine proof of a save through
      // the SAME RPC channel every other observation here uses, never a
      // guess that the typed `:w<CR>` "must have worked."
      | "saved" ->
        match! remoteExpr handle "&modified" with
        | Ok result -> return result.Trim() = "0"
        | Error _ -> return false
      | _ -> return false
    }

  pollUntil deadline 300 attempt

/// Runs a non-input client command through the plugin's own `:SageFs*`
/// commands (`sagefs.nvim/lua/sagefs/commands.lua`, `app_run.lua` — real
/// user commands, not invented ones: `SageFsRunApp`/`SageFsStopApp`/
/// `SageFsCreateSession`/`SageFsEvalLine` are all registered there at the
/// pinned commit this actor's `Runtime.Neovim.fs` resolves). No `WireStep`
/// encodes a client command yet (Island F builds no actor logic there
/// either — see `Actors/Dashboard.fs`'s identical `Command = fun _ -> ...`
/// no-op doc), so this is genuinely implemented and ready, not yet called by
/// anything.
let command (handle: Handle) (token: string) : Async<unit> =
  async {
    let run cmd =
      async {
        let! _ = execCommand handle cmd
        ()
      }

    match token with
    | "run-app" -> do! run "SageFsRunApp"
    | "stop-app" -> do! run "SageFsStopApp"
    | "save-all" -> do! run "wall"
    | "create-session" -> do! run "SageFsCreateSession"
    | "eval-line" -> do! run "SageFsEvalLine"
    | t when t.StartsWith "open-file:" -> do! run (sprintf "edit %s" (t.Substring "open-file:".Length))
    | _ -> ()
  }

/// Best-effort graceful quit (`:qa!` over RPC) before the hard kill — kitty's
/// death takes nvim with it regardless (nvim is kitty's own foreground
/// child), but asking first lets a genuinely idle nvim exit its own way.
let close (handle: Handle) : Async<unit> =
  async {
    let! _ = execCommand handle "qa!"
    do! Async.Sleep 100

    killQuietly handle.KittyProcess

    Xlib.XCloseDisplay handle.XDisplay |> ignore
  }

/// Wraps this actor behind the cell-agent's actor-dispatch seam (Island F,
/// demo-actors-plan.md §1.2/§1.3) — the same shape `Actors/Dashboard.fs`
/// exposes. Not yet reachable from `CellAgent.assembleActors` (that `match`
/// only ever builds `"dashboard"` today — see this island's final report for
/// why extending it is out of this island's own never-touch scope).
let toLiveActor (handle: Handle) : LiveActor =
  { Id = ActorId.Neovim
    ResolveRect = resolveRect handle
    Observe = observe handle
    Command = command handle
    Close = fun () -> close handle }
