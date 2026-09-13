module SageFs.Vscode.DebugRects

// WHY — the pure geometry math behind `sagefs.debug.rectFor`
// (demo-actors-plan.md §2.1, roast H5): the demo recorder's VS Code actor
// needs REAL screen coordinates to drive a human-shaped X11 cursor at, and
// roast H5 forbids getting them via CDP/`--remote-debugging-port` (issue
// #133's flakiness) — geometry must come from the extension host instead.
// The extension host has no DOM access to its own renderer, so it cannot
// read an exact pixel bounding box the way a CDP `getBoundingClientRect`
// call could. What it CAN do, genuinely, without CDP: ask the OS window
// manager for its own top-level window's rect (an X11 query the extension
// host itself performs — see `Extension.fs`'s `sagefs.debug.rectFor`
// handler), and read its own real, currently-in-effect state (workbench
// layout config, the active editor's real caret position and font
// settings). This module turns those REAL facts into a rect for a named
// target — coarse where the API genuinely cannot go further (the plan's own
// accepted compromise: "return the editor window rect + a coarse caret
// cell"), but never a fabricated pixel value.
//
// Zero Fable dependency (mirrors `AppRunPure.fs`/`DaemonDiscovery.fs`) so
// `../tests/DebugRectsContractTests.fsx` runs this module's logic in <2s
// under plain `dotnet fsi` — no VS Code, no Fable, no Node required to
// prove the math. `Extension.fs`'s activation gathers the live vscode/OS
// inputs (real, not invented) and calls straight into `resolve` below.

/// A screen rect in CSS/device pixels — same shape whichever source
/// produced it (an OS window-geometry query, or one of this module's own
/// carve-outs of that outer rect).
type WindowRect = { X: float; Y: float; W: float; H: float }

/// Documented VS Code workbench chrome pixel constants (see
/// `src/vs/workbench/browser/parts/*/*.css` in the VS Code source for the
/// current stable release pinned in `package.json`'s `engines.vscode`):
/// the activity bar is a fixed 48px column, the (custom, Linux/Windows)
/// title bar is 30px, the status bar is 22px, and one open editor's tab row
/// is 35px. These are workbench layout constants, not a per-demo guess —
/// they change only across a VS Code major redesign, exactly like any other
/// versioned UI contract this codebase already pins (e.g. `DashboardId`'s
/// `data-testid` mapping).
[<Literal>]
let ActivityBarWidth = 48.0

[<Literal>]
let TitleBarHeight = 30.0

[<Literal>]
let StatusBarHeight = 22.0

[<Literal>]
let TabRowHeight = 35.0

/// VS Code has no stable, versioned "current sidebar width" pixel constant
/// (it is user-resized) and no stable config key exposing it — so unless
/// the caller measured a real width another way, this is the sidebar's
/// documented default initial width, used only when the caller cannot
/// supply a better one.
[<Literal>]
let DefaultSideBarWidth = 256.0

/// The workbench chrome facts `resolve` needs to carve a semantic sub-rect
/// out of the outer window rect. Every field is something `Extension.fs`
/// reads from a REAL vscode config value or a real fact the VS Code actor
/// itself imposed at launch (see `SageFs.Demos.Actors.VsCode`'s
/// `workbench.action.closeSidebar` call) — never invented per call.
type ChromeConfig =
  { ActivityBarVisible: bool
    SideBarVisible: bool
    SideBarOnLeft: bool
    SideBarWidth: float
    StatusBarVisible: bool }

/// A real caret position (`TextEditor.selection.active`) plus the real
/// `editor.fontSize`/`editor.lineHeight` settings in effect — `LineHeight
/// = 0.0` mirrors VS Code's own setting semantics ("derive from
/// fontSize") exactly, so callers pass the raw setting value through
/// unmodified.
type CaretConfig =
  { Line: float
    Character: float
    FontSize: float
    LineHeight: float }

/// The exhaustive target vocabulary `sagefs.debug.rectFor` accepts
/// (demo-actors-plan.md §2.1: `"caret"|"editor"|"statusBar"|"view:<id>"`),
/// parsed once here so the resolver and its contract test share one closed
/// mapping instead of each re-parsing the wire string themselves.
[<RequireQualifiedAccess>]
type RectTarget =
  | Caret
  | Editor
  | StatusBar
  | View of id: string

module RectTarget =
  let parse (s: string) : RectTarget option =
    match s with
    | "caret" -> Some RectTarget.Caret
    | "editor" -> Some RectTarget.Editor
    | "statusBar" -> Some RectTarget.StatusBar
    | s when not (System.String.IsNullOrEmpty s) && s.StartsWith "view:" -> Some(RectTarget.View(s.Substring 5))
    | _ -> None

/// How much of the outer window rect the activity bar + sidebar occupy on
/// each side — factored out because both `editorContentRect` and the
/// `view:sidebar` case in `resolve` need the identical carve-out.
let private horizontalChrome (chrome: ChromeConfig) : float * float =
  let activityBar = if chrome.ActivityBarVisible then ActivityBarWidth else 0.0
  let sideBar = if chrome.SideBarVisible then chrome.SideBarWidth else 0.0

  if chrome.SideBarOnLeft then
    activityBar + sideBar, 0.0
  else
    activityBar, sideBar

/// The chrome-adjusted content rect for the editor pane: the outer window
/// rect minus the title bar and one tab row on top, minus the status bar on
/// the bottom (if visible), minus the activity bar and sidebar on whichever
/// side they're docked (if visible). Coarse by construction (module doc),
/// but every subtracted band is a documented constant or a real config
/// flag — never a guess at what is INSIDE the editor.
let editorContentRect (window: WindowRect) (chrome: ChromeConfig) : WindowRect =
  let left, right = horizontalChrome chrome
  let top = TitleBarHeight + TabRowHeight
  let bottom = if chrome.StatusBarVisible then StatusBarHeight else 0.0

  { X = window.X + left
    Y = window.Y + top
    W = max 0.0 (window.W - left - right)
    H = max 0.0 (window.H - top - bottom) }

/// The status bar's own rect — `None` when the user has turned it off,
/// since there is then genuinely nothing on screen to click (honest
/// absence, never a fabricated rect for a hidden element).
let statusBarRect (window: WindowRect) (chrome: ChromeConfig) : WindowRect option =
  if not chrome.StatusBarVisible then
    None
  else
    Some
      { X = window.X
        Y = window.Y + window.H - StatusBarHeight
        W = window.W
        H = StatusBarHeight }

/// The sidebar's own rect — `None` when it is hidden.
let sideBarRect (window: WindowRect) (chrome: ChromeConfig) : WindowRect option =
  if not chrome.SideBarVisible then
    None
  else
    let activityBar = if chrome.ActivityBarVisible then ActivityBarWidth else 0.0
    let top = TitleBarHeight
    let bottom = if chrome.StatusBarVisible then StatusBarHeight else 0.0
    let x = if chrome.SideBarOnLeft then window.X + activityBar else window.X + window.W - chrome.SideBarWidth

    Some
      { X = x
        Y = window.Y + top
        W = chrome.SideBarWidth
        H = max 0.0 (window.H - top - bottom) }

/// A coarse caret CELL rect inside the editor content area, projected from
/// a REAL caret line/character and REAL font settings through VS Code's own
/// documented default metrics when the user has not pinned exact values
/// (`editor.lineHeight` unset ⇒ VS Code itself uses `1.35 × fontSize`; the
/// bundled default monospace font's advance width is `~0.6 × fontSize`).
/// This is the plan's own accepted compromise (§2.1): never an exact DOM
/// pixel, but never invented either — every input is something the
/// extension host genuinely read.
let caretRect (editorContent: WindowRect) (caret: CaretConfig) : WindowRect =
  let lineHeight = if caret.LineHeight > 0.0 then caret.LineHeight else caret.FontSize * 1.35
  let charWidth = caret.FontSize * 0.6

  { X = editorContent.X + caret.Character * charWidth
    Y = editorContent.Y + caret.Line * lineHeight
    W = max 1.0 charWidth
    H = max 1.0 lineHeight }

/// The one function `Extension.fs`'s `sagefs.debug.rectFor` handler calls
/// once it has gathered the live window/chrome/caret facts. Returns `None`
/// — honest absence, never a fabricated rect — when the target is
/// unrecognized, hidden, or (for `Caret`) there is no active editor.
let resolve (window: WindowRect) (chrome: ChromeConfig) (caret: CaretConfig option) (target: RectTarget) : WindowRect option =
  match target with
  | RectTarget.Editor -> Some(editorContentRect window chrome)
  | RectTarget.StatusBar -> statusBarRect window chrome
  | RectTarget.Caret -> caret |> Option.map (fun c -> caretRect (editorContentRect window chrome) c)
  | RectTarget.View "sidebar" -> sideBarRect window chrome
  // Every other named view (e.g. a specific TreeView id) is a real gap this
  // coarse, window-manager-level model cannot resolve without more
  // ext-host plumbing — honest `None`, never a guessed rect.
  | RectTarget.View _ -> None
