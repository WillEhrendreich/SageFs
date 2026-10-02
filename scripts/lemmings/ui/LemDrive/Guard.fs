/// The few VS Code commands a lemming must not run, because the daemon they
/// reach is the one Will is watching and every lemming shares. The extension's
/// "SageFs: Stop Daemon" asks the daemon over HTTP to shut down, and "Switch
/// Project" stops it and starts another. A free model exploring the palette
/// would find them. This is the sandbox-side half of the guard; the prompt never
/// mentions them. Pure, so the matching is proven in the REPL.
module LemDrive.Guard

open System
open System.Text

/// A command that controls the daemon itself rather than a session.
type DaemonCommand =
  | StopDaemon
  | RestartDaemon
  | StartDaemon
  | SwitchProject

/// Every guarded command, so a new case cannot be forgotten by the matcher.
let all : DaemonCommand list = [ StopDaemon; RestartDaemon; StartDaemon; SwitchProject ]

/// The title the command palette shows, exactly as sagefs-vscode/package.json
/// declares it.
let title (c: DaemonCommand) : string =
  match c with
  | StopDaemon -> "SageFs: Stop Daemon"
  | RestartDaemon -> "SageFs: Restart Daemon"
  | StartDaemon -> "SageFs: Start Daemon"
  | SwitchProject -> "SageFs: Switch Project"

/// The command id behind the title.
let commandId (c: DaemonCommand) : string =
  match c with
  | StopDaemon -> "sagefs.stop"
  | RestartDaemon -> "sagefs.restart"
  | StartDaemon -> "sagefs.start"
  | SwitchProject -> "sagefs.switchProject"

/// Lower case, with runs of whitespace collapsed, so a row that renders as
/// "SageFs: Stop Daemon   recently used" still matches.
let private normalize (text: string) : string =
  String.Join(' ', text.ToLowerInvariant().Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries))

/// The guarded command a piece of text names, if any.
let matches (text: string) : DaemonCommand option =
  let normalized = normalize text
  // An id must match as a whole word: "sagefs.stop" is guarded, "sagefs.stopSession" is not.
  let namesId (c: DaemonCommand) =
    RegularExpressions.Regex.IsMatch(
      normalized,
      @"(?<![\w.])" + RegularExpressions.Regex.Escape((commandId c).ToLowerInvariant()) + @"(?![\w.])")
  all |> List.tryFind (fun c -> normalized.Contains(normalize (title c)) || namesId c)

/// Refuses text (a palette row, a click target, typed input) that names a
/// guarded command, and says why in words a lemming can act on.
let checkDaemon (text: string) : Result<unit, string> =
  match matches text with
  | None -> Ok()
  | Some c ->
    Result.Error(
      sprintf
        "'%s' controls the shared SageFs daemon, which other people are using, so the driver will not run it. Work with your own session instead."
        (title c))

// ---- ways out of the editor into a shell ------------------------------------------------
//
// The driver has no verb that runs script, but the palette, a click or a key can open a
// terminal or the developer tools in the window, and that window's sandbox has an open
// network and the run directory writable. A shell there reaches the shared daemon by plain
// HTTP. This list is the editor-side half of keeping the lemming inside SageFs: the profile
// also points every terminal profile at /usr/bin/false and the window's sandbox mounts the
// run's evidence read-only (VscRun.windowArgs), so this is one layer of three, and none of them is
// a boundary against a model that goes looking for the CDP port. It is a denylist of named
// commands, not an allowlist, so a new VS Code command with a new name is not covered.

/// A command, panel or key that gives the window a shell or a script console.
type Hatch =
  | NewTerminal
  | ToggleTerminal
  | NativeTerminal
  | DeveloperTools
  | ProcessExplorer
  | RunTask
  | StartDebugging

module Hatch =
  let all : Hatch list =
    [ NewTerminal; ToggleTerminal; NativeTerminal; DeveloperTools; ProcessExplorer; RunTask; StartDebugging ]

/// Every spelling of a hatch the editor shows or accepts: palette titles, button labels and
/// command ids.
let hatchNames (h: Hatch) : string list =
  match h with
  | NewTerminal ->
    [ "Terminal: Create New Terminal"; "Terminal: Create New Terminal in Editor Area"
      "Terminal: Create New Terminal in Active Workspace"; "New Terminal (Ctrl+Shift+`)"
      "workbench.action.terminal.new"; "workbench.action.createTerminalEditor" ]
  | ToggleTerminal ->
    [ "View: Toggle Terminal"; "Terminal (Ctrl+`)"; "workbench.action.terminal.toggleTerminal"
      "workbench.action.terminal.focus" ]
  | NativeTerminal -> [ "Open New External Terminal"; "workbench.action.terminal.openNativeConsole" ]
  | DeveloperTools ->
    [ "Developer: Toggle Developer Tools"; "Toggle Developer Tools"; "workbench.action.toggleDevTools" ]
  | ProcessExplorer -> [ "Developer: Open Process Explorer"; "workbench.action.openProcessExplorer" ]
  | RunTask ->
    [ "Tasks: Run Task"; "Tasks: Run Build Task"; "Tasks: Run Test Task"
      "workbench.action.tasks.runTask"; "workbench.action.tasks.build" ]
  | StartDebugging -> [ "Debug: Start Debugging"; "Debug: Start Without Debugging"; "workbench.action.debug.start" ]

let private hatchNamed (normalized: string) (h: Hatch) : bool =
  hatchNames h
  |> List.exists (fun name ->
    match name.Contains '.' && not (name.Contains ' ') with
    | true ->
      // An id matches as a whole word, like the daemon commands above.
      RegularExpressions.Regex.IsMatch(
        normalized,
        @"(?<![\w.])" + RegularExpressions.Regex.Escape(name.ToLowerInvariant()) + @"(?![\w.])")
    | false -> normalized.Contains(normalize name))

/// The hatch a piece of text names, if any.
let matchesHatch (text: string) : Hatch option =
  let normalized = normalize text
  Hatch.all |> List.tryFind (hatchNamed normalized)

let private hatchRefusal =
  "this window is for working with SageFs. A terminal, a task or the developer tools would let you run things outside it, so the driver will not open them. Use the editor and the SageFs views."

/// Refuses text (a palette row, a click target, typed input) that names a daemon-control
/// command or a shell or developer-tool hatch, and says why.
let check (text: string) : Result<unit, string> =
  match checkDaemon text with
  | Result.Error e -> Result.Error e
  | Ok() ->
    match matchesHatch text with
    | Some _ -> Result.Error(sprintf "'%s': %s" (text.Trim()) hatchRefusal)
    | None -> Ok()

/// The chords that open a terminal or the developer tools directly.
let private hatchChords : Chord.Chord list =
  let key c mods : Chord.Chord = { Modifiers = mods; Key = Chord.Character c }
  [ key '`' [ Chord.Ctrl ]
    key '`' [ Chord.Ctrl; Chord.Shift ]
    key 'i' [ Chord.Ctrl; Chord.Shift ]
    key 'c' [ Chord.Ctrl; Chord.Shift ] ]

/// Refuses a chord that would open a terminal or the developer tools.
let checkChord (chord: Chord.Chord) : Result<unit, string> =
  let normalized (c: Chord.Chord) =
    { c with
        Modifiers = c.Modifiers |> List.sortBy (sprintf "%A")
        Key = (match c.Key with | Chord.Character ch -> Chord.Character(Char.ToLowerInvariant ch) | k -> k) }
  match hatchChords |> List.exists (fun h -> normalized h = normalized chord) with
  | true -> Result.Error(sprintf "%s: %s" (Chord.describe chord) hatchRefusal)
  | false -> Ok()
