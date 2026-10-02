/// Key chords as a lemming writes them ("ctrl+shift+p", "alt+enter") and as
/// Playwright wants them ("Control+Shift+P", "Alt+Enter"). Pure: no browser, no
/// IO, so it is proven in the REPL before the driver ever uses it.
module LemDrive.Chord

open System

type Modifier =
  | Ctrl
  | Shift
  | Alt
  | Meta

type NamedKey =
  | Enter
  | Escape
  | Tab
  | Backspace
  | Delete
  | Space
  | Up
  | Down
  | Left
  | Right
  | Home
  | End
  | PageUp
  | PageDown
  | Insert

type Key =
  | Named of NamedKey
  | Character of char
  | Function of int

type Chord = { Modifiers: Modifier list; Key: Key }

/// The first and last function key a keyboard has.
[<Literal>]
let FirstFunctionKey = 1

[<Literal>]
let LastFunctionKey = 12

let private modifierWords =
  [ "ctrl", Ctrl; "control", Ctrl; "shift", Shift; "alt", Alt; "option", Alt
    "meta", Meta; "cmd", Meta; "super", Meta; "win", Meta ]

let private namedKeyWords =
  [ "enter", Enter; "return", Enter; "esc", Escape; "escape", Escape; "tab", Tab
    "backspace", Backspace; "delete", Delete; "del", Delete; "space", Space
    "up", Up; "down", Down; "left", Left; "right", Right
    "arrowup", Up; "arrowdown", Down; "arrowleft", Left; "arrowright", Right
    "home", Home; "end", End; "pageup", PageUp; "pagedown", PageDown; "insert", Insert ]

/// The wire name of a modifier, in the one order chords are written in.
let modifierName (m: Modifier) : string =
  match m with
  | Ctrl -> "Control"
  | Shift -> "Shift"
  | Alt -> "Alt"
  | Meta -> "Meta"

let private modifierOrder (m: Modifier) : int =
  match m with
  | Ctrl -> 0
  | Alt -> 1
  | Shift -> 2
  | Meta -> 3

let namedKeyName (k: NamedKey) : string =
  match k with
  | Enter -> "Enter"
  | Escape -> "Escape"
  | Tab -> "Tab"
  | Backspace -> "Backspace"
  | Delete -> "Delete"
  | Space -> "Space"
  | Up -> "ArrowUp"
  | Down -> "ArrowDown"
  | Left -> "ArrowLeft"
  | Right -> "ArrowRight"
  | Home -> "Home"
  | End -> "End"
  | PageUp -> "PageUp"
  | PageDown -> "PageDown"
  | Insert -> "Insert"

let keyName (k: Key) : string =
  match k with
  | Named n -> namedKeyName n
  | Character c -> string c
  | Function n -> sprintf "F%d" n

/// The Playwright spelling of a chord: modifiers first, then the key.
let toPlaywright (c: Chord) : string =
  c.Modifiers
  |> List.sortBy modifierOrder
  |> List.map modifierName
  |> fun mods -> String.Join("+", mods @ [ keyName c.Key ])

/// A chord reads as the lemming wrote it, for transcripts.
let describe (c: Chord) : string = toPlaywright c

/// True for the chords that would accept a quick-input row: Enter on its own.
let isAcceptKey (c: Chord) : bool =
  match c.Key with
  | Named Enter -> true
  | _ -> false

let private parseKey (word: string) : Result<Key, string> =
  let lower = word.ToLowerInvariant()
  match namedKeyWords |> List.tryFind (fun (w, _) -> w = lower) with
  | Some(_, named) -> Ok(Named named)
  | None ->
    match lower.Length, lower.StartsWith "f" with
    | 1, _ -> Ok(Character lower[0])
    | _, true ->
      match Int32.TryParse(lower.Substring 1) with
      | true, n when n >= FirstFunctionKey && n <= LastFunctionKey -> Ok(Function n)
      | _ -> Result.Error(sprintf "unknown key '%s'" word)
    | _ -> Result.Error(sprintf "unknown key '%s'" word)

let private parseModifier (word: string) : Result<Modifier, string> =
  match modifierWords |> List.tryFind (fun (w, _) -> w = word.ToLowerInvariant()) with
  | Some(_, m) -> Ok m
  | None -> Result.Error(sprintf "unknown modifier '%s' (use ctrl, shift, alt or meta)" word)

/// Splits "ctrl+shift+p" into its words. A trailing '+' is the plus key itself
/// ("ctrl++"), not an empty word.
let private words (text: string) : string list =
  match text.EndsWith "+" && text.Length > 1 with
  | true ->
    let head = text.Substring(0, text.Length - 1).TrimEnd('+')
    (match head with
     | "" -> []
     | h -> h.Split('+') |> List.ofArray) @ [ "+" ]
  | false -> text.Split('+') |> List.ofArray

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

/// Parses one chord: "alt+enter", "ctrl+shift+p", "f5", "escape".
let parse (text: string) : Result<Chord, string> =
  match text.Trim() with
  | "" -> Result.Error "an empty chord"
  | trimmed ->
    match words trimmed |> List.rev with
    | [] -> Result.Error "an empty chord"
    | keyWord :: revModifiers ->
      match parseKey keyWord, revModifiers |> List.rev |> List.map parseModifier |> sequenceResult with
      | Result.Error e, _ -> Result.Error e
      | _, Result.Error e -> Result.Error e
      | Ok key, Ok mods -> Ok { Modifiers = mods |> List.distinct; Key = key }

/// Parses a space-separated sequence of chords ("ctrl+k ctrl+s") in order.
let parseSequence (text: string) : Result<Chord list, string> =
  match text.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray with
  | [] -> Result.Error "an empty chord"
  | parts -> parts |> List.map parse |> sequenceResult
