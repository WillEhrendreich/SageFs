/// The closed set of things asked of VS Code. There is no verb that runs arbitrary
/// script: every command is one of these, parsed here, and run by a function that
/// matches on it exhaustively. Pure, so parsing is proven in the REPL and in the tests.
///
/// A lemming gets eight of them as vsc-* tools (snapshot, click, key, type, palette,
/// open, wait, shot). `resize` and `tour` are for the harness and the design review:
/// they are never wrapped as lemming tools.
module LemDrive.VscCommand

open System
open LemDrive.Chord

/// The longest a single wait may last, in seconds.
[<Literal>]
let MaxWaitSeconds = 30

/// The smallest and largest window a resize may ask for, in pixels.
[<Literal>]
let MinWindowPixels = 400

[<Literal>]
let MaxWindowPixels = 4000

/// A part of the workbench a screenshot can be cropped to.
type Region =
  | ActivityBar
  | SideBar
  | Editor
  | Panel
  | StatusBar

module Region =
  let all = [ ActivityBar; SideBar; Editor; Panel; StatusBar ]

  /// The word used on the command line and in file names.
  let name (r: Region) : string =
    match r with
    | ActivityBar -> "activitybar"
    | SideBar -> "sidebar"
    | Editor -> "editor"
    | Panel -> "panel"
    | StatusBar -> "statusbar"

  let tryParse (text: string) : Result<Region, string> =
    match all |> List.tryFind (fun r -> name r = text.ToLowerInvariant()) with
    | Some r -> Ok r
    | None -> Result.Error(sprintf "unknown region '%s' (use %s)" text (String.Join(", ", all |> List.map name)))

/// The name a shot gets when none is given.
[<Literal>]
let DefaultShotName = "shot"

type VscCommand =
  | Snapshot
  | Click of target: string
  | Key of sequence: Chord list
  | Type of text: string
  | Palette of command: string
  | Open of relativePath: string
  | Wait of seconds: int
  | Shot of name: string * regions: Region list
  | Resize of width: int * height: int
  | Tour of file: string

/// Every command, so the usage text and the README list cannot miss one.
let verbs : string list =
  [ "snapshot"; "click"; "key"; "type"; "palette"; "open"; "wait"; "shot"; "resize"; "tour" ]

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
  | Shot _ -> "shot"
  | Resize _ -> "resize"
  | Tour _ -> "tour"

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
  | "shot" -> "shot [<name>] [--region <part>]...  save the window as a PNG, with a text sidecar; parts: activitybar sidebar editor panel statusbar"
  | "resize" -> sprintf "resize <width> <height>  set the window size, %d to %d pixels each way" MinWindowPixels MaxWindowPixels
  | "tour" -> "tour <tour-file>         run a tour file step by step (see TOURS.md)"
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

let private sequenceResult (items: Result<'a, string> list) : Result<'a list, string> =
  items
  |> List.fold
    (fun acc item ->
      match acc, item with
      | Result.Error e, _ -> Result.Error e
      | _, Result.Error e -> Result.Error e
      | Ok xs, Ok x -> Ok(x :: xs))
    (Ok [])
  |> Result.map List.rev

/// "name --region sidebar --region editor" into a name and its regions.
let private parseShot (args: string list) : Result<VscCommand, string> =
  let rec go (name: string option) (regions: Region list) (rest: string list) : Result<VscCommand, string> =
    match rest with
    | [] -> Ok(Shot(defaultArg name DefaultShotName, List.rev regions))
    | "--region" :: part :: tail ->
      match Region.tryParse part with
      | Result.Error e -> Result.Error e
      | Ok r -> go name (r :: regions) tail
    | [ "--region" ] -> Result.Error "--region needs a part name"
    | word :: tail when not (word.StartsWith "--") && name.IsNone -> go (Some word) regions tail
    | word :: _ -> Result.Error(sprintf "shot does not understand '%s'.\n%s" word (usageLine "shot"))
  go None [] args

let private pixels (what: string) (text: string) : Result<int, string> =
  match Int32.TryParse text with
  | true, n when n >= MinWindowPixels && n <= MaxWindowPixels -> Ok n
  | true, _ -> Result.Error(sprintf "%s must be %d to %d pixels" what MinWindowPixels MaxWindowPixels)
  | false, _ -> Result.Error(sprintf "%s must be a whole number of pixels, not '%s'" what text)

/// Parses the words after "vsc" into a command.
let parse (args: string list) : Result<VscCommand, string> =
  match args with
  | [] -> Result.Error usage
  | verbName :: rest ->
    match verbName.ToLowerInvariant() with
    | "snapshot" -> Ok Snapshot
    | "shot" -> parseShot rest
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
    | "resize" ->
      match rest with
      | [ w; h ] ->
        match sequenceResult [ pixels "width" w; pixels "height" h ] with
        | Ok [ w'; h' ] -> Ok(Resize(w', h'))
        | Ok _ -> Result.Error(usageLine "resize")
        | Result.Error e -> Result.Error e
      | _ -> Result.Error(sprintf "resize needs a width and a height.\n%s" (usageLine "resize"))
    | "tour" -> needsText "tour" rest |> Result.map Tour
    | unknown -> Result.Error(sprintf "unknown command '%s'.\n%s" unknown usage)
