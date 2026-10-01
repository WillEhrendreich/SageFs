/// A tour is a plain text file of steps, run in order against the run's VS Code and
/// daemon, writing numbered shots. This module is only the file format: it parses a
/// tour into typed steps (and refuses a bad file with line numbers) and does nothing
/// else, so every example tour is proven to parse before a window is ever opened.
///
/// The format, one step per line (full description in TOURS.md):
///
///   # a comment, and blank lines, are ignored
///   command SageFs: Create Session      run a command from the palette (first match)
///   key alt+enter                       press a chord, or several in order
///   click Sessions                      click what has that text or label
///   type let x = 1                      type text where the keyboard focus is
///   open DemoEnv/DemoEnv.fs             open a workspace file with quick open
///   wait 5                              wait that many seconds (1 to 30)
///   shot session-ready                  a numbered shot of the window, with its text sidecar
///   shot editor-only --region editor    ... plus a crop of one part (repeat --region)
///   resize 1024 700                     set the window size
///   expect-text SageFs: ready           wait until the window shows that text (default 20 s)
///   expect-text --within 60 11/11       ... or give it longer, up to 120 s
///   replace DemoEnv/DemoEnv.fs "Some value" "Some (value + 1)"
///                                       change a workspace file on disk, exactly one match
module LemDrive.Tour

open System
open LemDrive.VscCommand

/// How long an expect-text waits when the step gives no time.
[<Literal>]
let DefaultExpectSeconds = 20

/// The longest an expect-text may be given.
[<Literal>]
let MostExpectSeconds = 120

type Step =
  | Run of VscCommand
  | ExpectText of text: string * withinSeconds: int
  | Replace of path: string * find: string * replacement: string

/// A step and the line of the file it came from.
type Placed = { Line: int; Step: Step }

type Tour = { Steps: Placed list }

/// The verbs a tour file understands. `command` is the palette; the rest are the
/// driver's own verbs, plus the two a tour adds.
let tourVerbs : string list =
  [ "command"; "key"; "click"; "type"; "open"; "wait"; "shot"; "resize"; "expect-text"; "replace" ]

/// Splits `"a b" "c \"d\""` into its quoted pieces. Escapes: \" \\ \n \t.
let quotedPieces (text: string) : Result<string list, string> =
  let pieces = ResizeArray<string>()
  let current = Text.StringBuilder()
  let mutable inside = false
  let mutable escaped = false
  let mutable failure : string option = None
  let mutable seenClose = true
  for ch in text do
    match inside, escaped with
    | true, true ->
      (match ch with
       | 'n' -> current.Append '\n' |> ignore
       | 't' -> current.Append '\t' |> ignore
       | other -> current.Append other |> ignore)
      escaped <- false
    | true, false ->
      (match ch with
       | '\\' -> escaped <- true
       | '"' ->
         pieces.Add(current.ToString())
         current.Clear() |> ignore
         inside <- false
         seenClose <- true
       | other -> current.Append other |> ignore)
    | false, _ ->
      (match ch with
       | '"' when seenClose ->
         inside <- true
         seenClose <- false
       | ' ' | '\t' -> ()
       | other -> failure <- failure |> Option.orElse (Some(sprintf "expected a quoted string, found '%c'" other)))
  match failure, inside with
  | Some f, _ -> Result.Error f
  | None, true -> Result.Error "a quoted string is not closed"
  | None, false -> Ok(List.ofSeq pieces)

let private safeRelative (path: string) : Result<string, string> =
  let segments = path.Replace('\\', '/').Split('/')
  match IO.Path.IsPathRooted path, segments |> Array.contains ".." with
  | true, _ -> Result.Error "a tour changes files inside the workspace only: the path must be relative"
  | _, true -> Result.Error "a tour changes files inside the workspace only: no '..' in the path"
  | false, false -> Ok path

let private parseExpect (rest: string) : Result<Step, string> =
  let words = rest.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
  let build (seconds: int) (text: string) =
    match text.Trim() with
    | "" -> Result.Error "expect-text needs the text to wait for"
    | t -> Ok(ExpectText(t, seconds))
  match words with
  | "--within" :: n :: _ ->
    match Int32.TryParse n with
    | true, s when s >= 1 && s <= MostExpectSeconds ->
      // Everything after the number is the text, spacing kept.
      let afterFlag = rest.Substring(rest.IndexOf "--within" + "--within".Length).TrimStart()
      build s (afterFlag.Substring(n.Length))
    | true, _ -> Result.Error(sprintf "--within is 1 to %d seconds" MostExpectSeconds)
    | false, _ -> Result.Error(sprintf "--within needs a whole number of seconds, not '%s'" n)
  | _ -> build DefaultExpectSeconds rest

/// The text after the verb, as the file wrote it.
let private afterVerb (line: string) (verb: string) : string = line.Substring(verb.Length).Trim()

/// Parses one line (already trimmed, not blank, not a comment) into a step.
let parseLine (line: string) : Result<Step, string> =
  let firstWord = line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)[0]
  let verb = firstWord.ToLowerInvariant()
  let rest = afterVerb line verb
  let words = rest.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
  match verb with
  | "command" -> VscCommand.parse [ "palette"; rest ] |> Result.map Run
  | "type" -> VscCommand.parse [ "type"; rest ] |> Result.map Run
  | "click" -> VscCommand.parse [ "click"; rest ] |> Result.map Run
  | "open" -> VscCommand.parse [ "open"; rest ] |> Result.map Run
  | "key" | "wait" | "shot" | "resize" -> VscCommand.parse (verb :: words) |> Result.map Run
  | "expect-text" -> parseExpect rest
  | "replace" ->
    match words with
    | [] -> Result.Error "replace needs a path, the text to find and the text to put there"
    | path :: _ ->
      let afterPath = rest.Substring(path.Length)
      match safeRelative path, quotedPieces afterPath with
      | Result.Error e, _ -> Result.Error e
      | _, Result.Error e -> Result.Error(sprintf "replace: %s (write the find and replace texts in double quotes)" e)
      | Ok p, Ok [ find; replacement ] when find <> "" -> Ok(Replace(p, find, replacement))
      | Ok _, Ok [ "" ; _ ] -> Result.Error "replace: the text to find is empty"
      | Ok _, Ok pieces -> Result.Error(sprintf "replace needs exactly two quoted texts, found %d" (List.length pieces))
  | other -> Result.Error(sprintf "unknown step '%s' (known: %s)" other (String.Join(", ", tourVerbs)))

/// Parses a whole tour. Every bad line is reported with its number, not just the first.
let parse (text: string) : Result<Tour, string list> =
  let parsed =
    text.Replace("\r\n", "\n").Split('\n')
    |> Array.mapi (fun i raw -> i + 1, raw.Trim())
    |> Array.filter (fun (_, l) -> l <> "" && not (l.StartsWith "#"))
    |> Array.map (fun (n, l) ->
      match parseLine l with
      | Ok step -> Ok { Line = n; Step = step }
      | Result.Error e -> Result.Error(sprintf "line %d: %s" n e))
    |> List.ofArray
  let errors = parsed |> List.choose (function Result.Error e -> Some e | Ok _ -> None)
  match errors, parsed with
  | [], [] -> Result.Error [ "the tour has no steps" ]
  | [], _ -> Ok { Steps = parsed |> List.choose (function Ok p -> Some p | Result.Error _ -> None) }
  | errs, _ -> Result.Error errs

/// One line saying what a step does, for the tour's log.
let describe (s: Step) : string =
  match s with
  | Run c ->
    match c with
    | Palette t -> sprintf "command: %s" t
    | Key chords -> sprintf "key: %s" (String.Join(" ", chords |> List.map Chord.describe))
    | Click t -> sprintf "click: %s" t
    | Type t -> sprintf "type: %s" t
    | Open p -> sprintf "open: %s" p
    | Wait n -> sprintf "wait: %d s" n
    | Shot(name, regions) ->
      sprintf "shot: %s%s" name (match regions with | [] -> "" | rs -> " + " + String.Join(", ", rs |> List.map Region.name))
    | Resize(w, h) -> sprintf "resize: %dx%d" w h
    | Snapshot -> "snapshot"
    | Tour f -> sprintf "tour: %s" f
  | ExpectText(t, s) -> sprintf "expect-text (within %d s): %s" s t
  | Replace(p, f, r) -> sprintf "replace in %s: \"%s\" -> \"%s\"" p f r
