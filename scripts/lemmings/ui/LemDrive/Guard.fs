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
let check (text: string) : Result<unit, string> =
  match matches text with
  | None -> Ok()
  | Some c ->
    Result.Error(
      sprintf
        "'%s' controls the shared SageFs daemon, which other people are using, so the driver will not run it. Work with your own session instead."
        (title c))
