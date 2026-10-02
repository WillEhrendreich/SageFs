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
///   expect-text --within 60 11/11       ... or give it longer, up to 300 s
///   expect-session --within 240         wait until this run's own session on the daemon is Ready
///   set-workflow HotReload              the harness puts this run's session in that workflow
///                                       (Interactive, LiveTesting or HotReload)
///   replace DemoEnv/DemoEnv.fs "Some value" "Some (value + 1)"
///                                       change a workspace file on disk, exactly one match
///   other-session                       start a second session in a folder OUTSIDE the workspace
///                                       (<run>/other, a copy of the DemoEnv fixture): another agent
///   replace-other DemoEnv.Tests/DemoEnvTests.fs "a" "b"
///                                       change a file in that folder, exactly one match
///   expect-absent --for 20 ShimSubject  fail if the window shows that text during those seconds
module LemDrive.Tour

open System
open LemDrive.VscCommand

/// How long an expect-text waits when the step gives no time.
[<Literal>]
let DefaultExpectSeconds = 20

/// The longest an expect-text may be given.
[<Literal>]
let MostExpectSeconds = 300

/// How long an expect-absent watches when the step gives no time.
[<Literal>]
let DefaultAbsentSeconds = 15

/// The longest an expect-absent may watch.
[<Literal>]
let MostAbsentSeconds = 120

type Step =
  | Run of VscCommand
  | ExpectText of text: string * withinSeconds: int
  | ExpectSession of withinSeconds: int
  | SetWorkflow of workflow: string
  | Replace of path: string * find: string * replacement: string
  /// A second session in a folder outside the workspace, standing in for another agent on the daemon.
  | OtherSession
  | ReplaceOther of path: string * find: string * replacement: string
  /// The window must NOT show this text for the whole time.
  | ExpectAbsent of text: string * forSeconds: int

/// A step and the line of the file it came from.
type Placed = { Line: int; Step: Step }

type Tour = { Steps: Placed list }

/// The verbs a tour file understands. `command` is the palette; the rest are the
/// driver's own verbs, plus the two a tour adds.
let tourVerbs : string list =
  [ "command"; "key"; "click"; "type"; "open"; "wait"; "shot"; "resize"; "expect-text"; "expect-session"; "set-workflow"; "replace"
    "other-session"; "replace-other"; "expect-absent" ]

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

/// The workflow names the daemon's workflow route accepts.
let workflows : string list = [ "Interactive"; "LiveTesting"; "HotReload" ]

/// `expect-session [--within N]`: this run's own session on the shared daemon is Ready. A text match
/// on the Sessions view cannot tell this run's session from another agent's, the daemon can.
let private parseExpectSession (rest: string) : Result<Step, string> =
  match rest.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray with
  | [] -> Ok(ExpectSession DefaultExpectSeconds)
  | [ "--within"; n ] ->
    match Int32.TryParse n with
    | true, s when s >= 1 && s <= MostExpectSeconds -> Ok(ExpectSession s)
    | true, _ -> Result.Error(sprintf "--within is 1 to %d seconds" MostExpectSeconds)
    | false, _ -> Result.Error(sprintf "--within needs a whole number of seconds, not '%s'" n)
  | _ -> Result.Error "expect-session takes only --within <seconds>"

/// `expect-absent [--for N] <text>`.
let private parseExpectAbsent (rest: string) : Result<Step, string> =
  let words = rest.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
  let build (seconds: int) (text: string) =
    match text.Trim() with
    | "" -> Result.Error "expect-absent needs the text that must not show"
    | t -> Ok(ExpectAbsent(t, seconds))
  match words with
  | "--for" :: n :: _ ->
    match Int32.TryParse n with
    | true, s when s >= 1 && s <= MostAbsentSeconds ->
      let afterFlag = rest.Substring(rest.IndexOf "--for" + "--for".Length).TrimStart()
      build s (afterFlag.Substring(n.Length))
    | true, _ -> Result.Error(sprintf "--for is 1 to %d seconds" MostAbsentSeconds)
    | false, _ -> Result.Error(sprintf "--for needs a whole number of seconds, not '%s'" n)
  | _ -> build DefaultAbsentSeconds rest

/// `<path> "<find>" "<replacement>"`, shared by replace and replace-other.
let private parseReplacement (verb: string) (rest: string) (build: string -> string -> string -> Step) : Result<Step, string> =
  let words = rest.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
  match words with
  | [] -> Result.Error(sprintf "%s needs a path, the text to find and the text to put there" verb)
  | path :: _ ->
    let afterPath = rest.Substring(path.Length)
    match safeRelative path, quotedPieces afterPath with
    | Result.Error e, _ -> Result.Error e
    | _, Result.Error e -> Result.Error(sprintf "%s: %s (write the find and replace texts in double quotes)" verb e)
    | Ok p, Ok [ find; replacement ] when find <> "" -> Ok(build p find replacement)
    | Ok _, Ok [ ""; _ ] -> Result.Error(sprintf "%s: the text to find is empty" verb)
    | Ok _, Ok pieces -> Result.Error(sprintf "%s needs exactly two quoted texts, found %d" verb (List.length pieces))

/// The text after the verb, as the file wrote it.
let private afterVerb (line: string) (verb: string) : string = line.Substring(verb.Length).Trim()

/// Parses one line (already trimmed, not blank, not a comment) into a step.
let parseLine (line: string) : Result<Step, string> =
  let firstWord = line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)[0]
  let verb = firstWord.ToLowerInvariant()
  let rest = afterVerb line verb
  let words = rest.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
  match verb with
  | "command" ->
    // A tour wants the command it names, not the first fuzzy match: the title must be exact.
    match VscCommand.parse [ "palette"; rest ] with
    | Ok(Palette t) -> Ok(Run(PaletteExact t))
    | Ok other -> Ok(Run other)
    | Result.Error e -> Result.Error e
  | "type" -> VscCommand.parse [ "type"; rest ] |> Result.map Run
  | "click" -> VscCommand.parse [ "click"; rest ] |> Result.map Run
  | "open" -> VscCommand.parse [ "open"; rest ] |> Result.map Run
  | "key" | "wait" | "shot" | "resize" -> VscCommand.parse (verb :: words) |> Result.map Run
  | "expect-text" -> parseExpect rest
  | "expect-session" -> parseExpectSession rest
  | "set-workflow" ->
    match words with
    | [ w ] when workflows |> List.contains w -> Ok(SetWorkflow w)
    | _ -> Result.Error(sprintf "set-workflow takes one of: %s" (String.Join(", ", workflows)))
  | "replace" -> parseReplacement "replace" rest (fun p f r -> Replace(p, f, r))
  | "replace-other" -> parseReplacement "replace-other" rest (fun p f r -> ReplaceOther(p, f, r))
  | "other-session" ->
    match words with
    | [] -> Ok OtherSession
    | _ -> Result.Error "other-session takes no arguments"
  | "expect-absent" -> parseExpectAbsent rest
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

/// The placeholder a step's text may carry for this run's own session id, which only the daemon
/// knows once the session exists (the Switch Session picker lists sessions by id).
[<Literal>]
let SessionPlaceholder = "{session}"

let private mentionsSession (text: string) : bool = text.Contains SessionPlaceholder

/// True when running the step needs this run's session id.
let usesSession (s: Step) : bool =
  match s with
  | Run(Palette t) | Run(PaletteExact t) | Run(Click t) | Run(Type t) -> mentionsSession t
  | ExpectText(t, _) -> mentionsSession t
  | ExpectAbsent(t, _) -> mentionsSession t
  | Run _ | ExpectSession _ | SetWorkflow _ | Replace _ | OtherSession | ReplaceOther _ -> false

/// The step with the placeholder replaced by the session id.
let withSession (id: string) (s: Step) : Step =
  let fill (t: string) = t.Replace(SessionPlaceholder, id)
  match s with
  | Run(Palette t) -> Run(Palette(fill t))
  | Run(PaletteExact t) -> Run(PaletteExact(fill t))
  | Run(Click t) -> Run(Click(fill t))
  | Run(Type t) -> Run(Type(fill t))
  | ExpectText(t, n) -> ExpectText(fill t, n)
  | ExpectAbsent(t, n) -> ExpectAbsent(fill t, n)
  | other -> other

/// One line saying what a step does, for the tour's log.
let describe (s: Step) : string =
  match s with
  | Run c ->
    match c with
    | Palette t -> sprintf "command (first match): %s" t
    | PaletteExact t -> sprintf "command: %s" t
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
  | ExpectSession s -> sprintf "expect-session (within %d s): this run's session is Ready" s
  | SetWorkflow w -> sprintf "set-workflow: %s (the harness asks the daemon; the extension's own Switch Workflow cannot, see TOURS.md)" w
  | Replace(p, f, r) -> sprintf "replace in %s: \"%s\" -> \"%s\"" p f r
  | OtherSession -> "other-session: a second session in a folder outside the workspace (another agent)"
  | ReplaceOther(p, f, r) -> sprintf "replace in the other session's %s: \"%s\" -> \"%s\"" p f r
  | ExpectAbsent(t, s) -> sprintf "expect-absent (for %d s): %s" s t
