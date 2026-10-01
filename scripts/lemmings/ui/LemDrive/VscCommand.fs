/// The closed set of things a lemming can ask of VS Code. There is no verb that
/// runs arbitrary script: every command is one of these, parsed here, and run by
/// a function that matches on it exhaustively. Pure, so parsing is proven in the
/// REPL.
module LemDrive.VscCommand

open System
open LemDrive.Chord

/// The longest a single wait may last, in seconds.
[<Literal>]
let MaxWaitSeconds = 30

type VscCommand =
  | Snapshot
  | Click of target: string
  | Key of sequence: Chord list
  | Type of text: string
  | Palette of command: string
  | Open of relativePath: string
  | Wait of seconds: int
  | Shot

/// Every command, so the usage text and the README list cannot miss one.
let verbs : string list =
  [ "snapshot"; "click"; "key"; "type"; "palette"; "open"; "wait"; "shot" ]

/// The verb a command is invoked by.
let verb (c: VscCommand) : string =
  match c with
  | Snapshot -> "snapshot"
  | Click _ -> "click"
  | Key _ -> "key"
  | Type _ -> "type"
  | Palette _ -> "palette"
  | Open _ -> "open"
  | Wait _ -> "wait"
  | Shot -> "shot"

/// One line of help per verb.
let usageLine (verbName: string) : string =
  match verbName with
  | "snapshot" -> "snapshot                what the window shows right now, as text"
  | "click" -> "click <text-or-label>    click the control, row or tab with that text or aria-label"
  | "key" -> "key <chord> [<chord>...]  press keys, e.g. ctrl+shift+p, alt+enter, escape"
  | "type" -> "type <text>              type text into whatever has focus"
  | "palette" -> "palette <command text>  open the command palette, type, run the first match"
  | "open" -> "open <relative path>     open a file from the workspace with quick open"
  | "wait" -> sprintf "wait <seconds>           wait 1 to %d seconds" MaxWaitSeconds
  | "shot" -> "shot                    save a screenshot and print the PNG path"
  | other -> other

let usage : string =
  String.Join("\n", "usage: vsc <command>" :: (verbs |> List.map (fun v -> "  " + usageLine v)))

let private joinArgs (args: string list) : string = String.Join(' ', args).Trim()

let private needsText (verbName: string) (args: string list) : Result<string, string> =
  match joinArgs args with
  | "" -> Result.Error(sprintf "%s needs some text.\n%s" verbName (usageLine verbName))
  | text -> Ok text

/// A path a lemming may open: relative, inside the workspace, no ".." segments.
let private relativePath (text: string) : Result<string, string> =
  let segments = text.Replace('\\', '/').Split('/')
  match IO.Path.IsPathRooted text, segments |> Array.contains ".." with
  | true, _ -> Result.Error "open takes a path relative to the workspace, not an absolute one"
  | _, true -> Result.Error "open stays inside the workspace: no '..' in the path"
  | false, false -> Ok text

/// Parses the words after "vsc" into a command.
let parse (args: string list) : Result<VscCommand, string> =
  match args with
  | [] -> Result.Error usage
  | verbName :: rest ->
    match verbName.ToLowerInvariant() with
    | "snapshot" -> Ok Snapshot
    | "shot" -> Ok Shot
    | "click" -> needsText "click" rest |> Result.map Click
    | "type" ->
      // Typing keeps its own spacing: join with single spaces, do not trim the inside.
      match rest with
      | [] -> Result.Error(sprintf "type needs some text.\n%s" (usageLine "type"))
      | _ -> Ok(Type(String.Join(' ', rest)))
    | "palette" -> needsText "palette" rest |> Result.map Palette
    | "open" -> needsText "open" rest |> Result.bind relativePath |> Result.map Open
    | "key" ->
      match rest with
      | [] -> Result.Error(sprintf "key needs a chord.\n%s" (usageLine "key"))
      | _ -> parseSequence (joinArgs rest) |> Result.map Key
    | "wait" ->
      match rest with
      | [ n ] ->
        match Int32.TryParse n with
        | true, s when s >= 1 && s <= MaxWaitSeconds -> Ok(Wait s)
        | true, _ -> Result.Error(sprintf "wait is 1 to %d seconds" MaxWaitSeconds)
        | false, _ -> Result.Error(sprintf "wait needs a whole number of seconds.\n%s" (usageLine "wait"))
      | _ -> Result.Error(sprintf "wait needs one number.\n%s" (usageLine "wait"))
    | unknown -> Result.Error(sprintf "unknown command '%s'.\n%s" unknown usage)
