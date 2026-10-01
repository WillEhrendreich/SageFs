// The Neovim driver for UI lemmings.
//
// A lemming gets a terminal-ish driver and nothing else: it can send keys to a Neovim
// that runs inside tmux, read the screen, wait, read :messages, and run three read-only
// shell commands in a second tmux window. It cannot run Lua, call the plugin, or reach
// the daemon through this tool. The enforcement is not a promise in a README. The
// lemming-side client (`dotnet LemDrive.dll nvim <command>`) only talks to a unix socket,
// and the server on the other end of that socket only knows the closed command set
// below. The tmux socket itself never enters the lemming's sandbox, so a lemming cannot
// ask tmux to open a window of its own.
//
// Pure parts (key notation, shell allow-list, screen header) are plain functions so they
// can be tried in the SageFs REPL. The process and socket parts sit below them.
module LemDrive.Nvim

open System
open System.Diagnostics
open System.IO
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading

// ---------------------------------------------------------------------------
// Limits. Every number the driver depends on has a name.
// ---------------------------------------------------------------------------

module Limits =
  let MaxWaitSeconds = 30
  let ScreenColumns = 140
  let ScreenRows = 40
  /// Time nvim gets to react to keys before the screen is read back.
  let KeySettleMs = 400
  /// A lone Escape followed at once by another key reads as Alt-<key> to nvim.
  let EscapeSettleMs = 120
  let TmuxCallTimeoutMs = 10_000
  let ClientTimeoutMs = 120_000
  let StartupWaitMs = 30_000
  let StartupPollMs = 250
  let ShellPollMs = 200
  let ShellWaitMs = 25_000
  let ShellScrollback = 400
  let MaxMessagePages = 8
  let CurlDefaultMaxTimeSeconds = 15
  let CurlMaxTimeSeconds = 30
  let MaxShellOutputChars = 12_000

// ---------------------------------------------------------------------------
// Key notation: the vim spelling a user already knows (<Esc> <CR> <C-w> <M-CR>).
// ---------------------------------------------------------------------------

type NamedKey =
  | Esc
  | Enter
  | Tab
  | Backspace
  | Space
  | Up
  | Down
  | Left
  | Right
  | Home
  | End
  | PageUp
  | PageDown
  | Delete
  | Insert
  | Function of int

let tmuxNamedKey (key: NamedKey) : string =
  match key with
  | Esc -> "Escape"
  | Enter -> "Enter"
  | Tab -> "Tab"
  | Backspace -> "BSpace"
  | Space -> "Space"
  | Up -> "Up"
  | Down -> "Down"
  | Left -> "Left"
  | Right -> "Right"
  | Home -> "Home"
  | End -> "End"
  | PageUp -> "PageUp"
  | PageDown -> "PageDown"
  | Delete -> "DC"
  | Insert -> "IC"
  | Function n -> sprintf "F%d" n

type BaseKey =
  | Named of NamedKey
  | Char of char

type Chord =
  { Ctrl: bool
    Alt: bool
    Shift: bool
    Base: BaseKey }

type KeyToken =
  | Text of string
  | Key of Chord

let tmuxChord (chord: Chord) : string =
  let prefix =
    [ if chord.Ctrl then "C-"
      if chord.Alt then "M-"
      if chord.Shift then "S-" ]
    |> String.concat ""
  let baseName =
    match chord.Base with
    | Named key -> tmuxNamedKey key
    | Char c -> string c
  prefix + baseName

let private functionKeyMin = 1
let private functionKeyMax = 12

let private tryNamedKey (name: string) : NamedKey option =
  match name.ToLowerInvariant() with
  | "esc" | "escape" -> Some Esc
  | "cr" | "enter" | "return" -> Some Enter
  | "tab" -> Some Tab
  | "bs" | "backspace" -> Some Backspace
  | "space" -> Some Space
  | "up" -> Some Up
  | "down" -> Some Down
  | "left" -> Some Left
  | "right" -> Some Right
  | "home" -> Some Home
  | "end" -> Some End
  | "pageup" -> Some PageUp
  | "pagedown" -> Some PageDown
  | "del" | "delete" -> Some Delete
  | "insert" -> Some Insert
  | lower when lower.Length >= 2 && lower.[0] = 'f' ->
    match Int32.TryParse(lower.Substring 1) with
    | true, n when n >= functionKeyMin && n <= functionKeyMax -> Some(Function n)
    | _ -> None
  | _ -> None

/// Names that stand for one literal character, so a key list can still type `<` or `|`.
let private tryLiteralName (name: string) : string option =
  match name.ToLowerInvariant() with
  | "lt" -> Some "<"
  | "bar" -> Some "|"
  | "bslash" -> Some "\\"
  | "leader" -> Some "\\"
  | _ -> None

let private tokenShape = Regex(@"^[A-Za-z0-9\-]+$", RegexOptions.Compiled)

let private parseChord (inner: string) : Result<KeyToken, string> =
  let parts = inner.Split('-', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
  match List.rev parts with
  | [] -> Result.Error(sprintf "empty key name <%s>" inner)
  | baseName :: revModifiers ->
    let modifiers = List.rev revModifiers |> List.map (fun m -> m.ToUpperInvariant())
    let unknown = modifiers |> List.filter (fun m -> m <> "C" && m <> "M" && m <> "A" && m <> "S")
    match unknown with
    | bad :: _ -> Result.Error(sprintf "unknown modifier '%s' in <%s> (use C, M or A, S)" bad inner)
    | [] ->
      let baseKey =
        match tryNamedKey baseName with
        | Some named -> Result.Ok(Named named)
        | None when baseName.Length = 1 -> Result.Ok(Char baseName.[0])
        | None -> Result.Error(sprintf "unknown key name <%s>. To type a literal < write <lt>" inner)
      baseKey
      |> Result.map (fun b ->
        Key
          { Ctrl = List.contains "C" modifiers
            Alt = List.contains "M" modifiers || List.contains "A" modifiers
            Shift = List.contains "S" modifiers
            Base = b })

/// Splits "ihello<Esc>:w<CR>" into text and keys. An unknown <name> is an error with a
/// reason, never typed silently, because a silent literal is how an Escape gets lost.
let parseKeys (input: string) : Result<KeyToken list, string> =
  let tokens = ResizeArray<KeyToken>()
  let text = StringBuilder()
  let flush () =
    if text.Length > 0 then
      tokens.Add(Text(text.ToString()))
      text.Clear() |> ignore
  let mutable error: string option = None
  let mutable i = 0
  while error.IsNone && i < input.Length do
    let c = input.[i]
    if c = '<' then
      let close = input.IndexOf('>', i + 1)
      let inner = if close > i then input.Substring(i + 1, close - i - 1) else ""
      if close > i && inner.Length > 0 && tokenShape.IsMatch inner then
        match tryLiteralName inner with
        | Some literal ->
          text.Append literal |> ignore
          i <- close + 1
        | None ->
          match parseChord inner with
          | Result.Ok token ->
            flush ()
            tokens.Add token
            i <- close + 1
          | Result.Error reason -> error <- Some reason
      else
        text.Append c |> ignore
        i <- i + 1
    else
      text.Append c |> ignore
      i <- i + 1
  match error with
  | Some reason -> Result.Error reason
  | None ->
    flush ()
    Result.Ok(List.ofSeq tokens)

// ---------------------------------------------------------------------------
// The shell window: curl, cat and ls, and nothing that can chain or redirect.
// ---------------------------------------------------------------------------

type CurlFlag =
  | Silent
  | ShowErrors
  | IncludeHeaders
  | HeadOnly
  | Verbose

let curlFlagText (flag: CurlFlag) : string =
  match flag with
  | Silent -> "-s"
  | ShowErrors -> "-S"
  | IncludeHeaders -> "-i"
  | HeadOnly -> "-I"
  | Verbose -> "-v"

type LsFlag =
  | Long
  | All
  | OnePerLine
  | Recursive

let lsFlagText (flag: LsFlag) : string =
  match flag with
  | Long -> "-l"
  | All -> "-a"
  | OnePerLine -> "-1"
  | Recursive -> "-R"

type ShellCommand =
  | Curl of flags: CurlFlag list * maxTimeSeconds: int * url: string
  | Cat of paths: string list
  | Ls of flags: LsFlag list * paths: string list

let shellCommandName (cmd: ShellCommand) : string =
  match cmd with
  | Curl _ -> "curl"
  | Cat _ -> "cat"
  | Ls _ -> "ls"

let shellCommandLine (cmd: ShellCommand) : string =
  match cmd with
  | Curl(flags, maxTime, url) ->
    [ "curl"
      yield! flags |> List.map curlFlagText
      "--max-time"
      string maxTime
      url ]
    |> String.concat " "
  | Cat paths -> String.concat " " ("cat" :: paths)
  | Ls(flags, paths) ->
    [ "ls"
      yield! flags |> List.map lsFlagText
      yield! paths ]
    |> String.concat " "

let private shellWord = Regex(@"^[A-Za-z0-9_./:?=%+@,~#\-]+$", RegexOptions.Compiled)
let private localUrl = Regex(@"^https?://(localhost|127\.0\.0\.1)(:\d{1,5})?(/[A-Za-z0-9_./:?=%+@,~#\-]*)?$", RegexOptions.Compiled)

let private pathAllowed (workspace: string) (path: string) : Result<string, string> =
  let hasDotDot = path.Split('/') |> Array.contains ".."
  match hasDotDot, path.StartsWith "/" with
  | true, _ -> Result.Error(sprintf "path '%s' contains '..'" path)
  | false, true when not (path = workspace || path.StartsWith(workspace.TrimEnd('/') + "/")) ->
    Result.Error(sprintf "path '%s' is outside the project directory %s" path workspace)
  | false, _ -> Result.Ok path

let private collect (items: Result<'a, string> list) : Result<'a list, string> =
  items
  |> List.fold
    (fun acc item ->
      match acc, item with
      | Result.Error e, _ -> Result.Error e
      | _, Result.Error e -> Result.Error e
      | Result.Ok xs, Result.Ok x -> Result.Ok(x :: xs))
    (Result.Ok [])
  |> Result.map List.rev

let private parseFlagLetters (allowed: (char * 'f) list) (word: string) : Result<'f list, string> =
  word.Substring 1
  |> Seq.toList
  |> List.map (fun c ->
    match List.tryFind (fun (letter, _) -> letter = c) allowed with
    | Some(_, flag) -> Result.Ok flag
    | None -> Result.Error(sprintf "flag -%c is not allowed here" c))
  |> collect

let private curlLetters =
  [ 's', Silent
    'S', ShowErrors
    'i', IncludeHeaders
    'I', HeadOnly
    'v', Verbose ]

let private lsLetters =
  [ 'l', Long
    'a', All
    '1', OnePerLine
    'R', Recursive ]

let parseShellCommand (workspace: string) (line: string) : Result<ShellCommand, string> =
  let words = line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList
  let badWord = words |> List.tryFind (fun w -> not (shellWord.IsMatch w))
  match words, badWord with
  | [], _ -> Result.Error "empty command. Allowed: curl, cat, ls"
  | _, Some bad ->
    Result.Error(sprintf "'%s' has a character that is not allowed. No quotes, ; & | < > $ ` or redirection: the shell window runs one plain curl, cat or ls" bad)
  | "curl" :: args, None ->
    let rec go flags maxTime url rest =
      match rest with
      | [] -> Result.Ok(flags, maxTime, url)
      | ("-m" | "--max-time") :: n :: tail ->
        match Int32.TryParse n with
        | true, v when v >= 1 && v <= Limits.CurlMaxTimeSeconds -> go flags v url tail
        | _ -> Result.Error(sprintf "--max-time wants a number from 1 to %d" Limits.CurlMaxTimeSeconds)
      | w :: tail when w.StartsWith "-" && w.Length > 1 ->
        match parseFlagLetters curlLetters w with
        | Result.Ok fs -> go (flags @ fs) maxTime url tail
        | Result.Error e -> Result.Error e
      | w :: tail ->
        match url with
        | Some _ -> Result.Error "curl takes one URL"
        | None when localUrl.IsMatch w -> go flags maxTime (Some w) tail
        | None -> Result.Error(sprintf "'%s' is not a localhost URL. curl may only reach http://localhost:<port>/..." w)
    match go [] Limits.CurlDefaultMaxTimeSeconds None args with
    | Result.Error e -> Result.Error e
    | Result.Ok(_, _, None) -> Result.Error "curl needs a URL"
    | Result.Ok(flags, maxTime, Some url) -> Result.Ok(Curl(flags, maxTime, url))
  | "cat" :: args, None ->
    match args with
    | [] -> Result.Error "cat needs at least one file"
    | _ when args |> List.exists (fun a -> a.StartsWith "-") -> Result.Error "cat takes no flags"
    | _ -> args |> List.map (pathAllowed workspace) |> collect |> Result.map Cat
  | "ls" :: args, None ->
    let flagWords, pathWords = args |> List.partition (fun a -> a.StartsWith "-")
    let flags = flagWords |> List.map (parseFlagLetters lsLetters) |> collect |> Result.map List.concat
    let paths = pathWords |> List.map (pathAllowed workspace) |> collect
    match flags, paths with
    | Result.Error e, _ -> Result.Error e
    | _, Result.Error e -> Result.Error e
    | Result.Ok fs, Result.Ok ps -> Result.Ok(Ls(fs, ps))
  | first :: _, None -> Result.Error(sprintf "'%s' is not available here. Allowed: curl, cat, ls" first)

// ---------------------------------------------------------------------------
// The closed command set.
// ---------------------------------------------------------------------------

type DriveCommand =
  | Keys of string
  | NvimType of string
  | NvimScreen
  | NvimWait of seconds: int
  | NvimMessages
  | NvimShell of string
  | NvimShot of name: string

/// The one place command names are spelled. The client sends this name on the wire and
/// the server parses it back with `parseCommand`.
let commandName (cmd: DriveCommand) : string =
  match cmd with
  | Keys _ -> "keys"
  | NvimType _ -> "nvim-type"
  | NvimScreen -> "nvim-screen"
  | NvimWait _ -> "nvim-wait"
  | NvimMessages -> "nvim-messages"
  | NvimShell _ -> "nvim-shell"
  | NvimShot _ -> "nvim-shot"

let commandArgument (cmd: DriveCommand) : string =
  match cmd with
  | Keys keys -> keys
  | NvimType text -> text
  | NvimScreen -> ""
  | NvimWait seconds -> string seconds
  | NvimMessages -> ""
  | NvimShell line -> line
  | NvimShot name -> name

let commandUsage =
  [ "keys <keys>           send keys, vim notation: ihello<Esc>  :w<CR>  <C-w>l  <M-CR>  (<lt> for a literal <)"
    "nvim-type <text>      type the text as-is, character by character (a newline is an Enter)"
    "nvim-screen           show the editor screen, with cursor row/col, mode and the status line"
    "nvim-wait <seconds>   wait up to 30 seconds, then show the screen"
    "nvim-messages         show :messages"
    "nvim-shell <cmd>      run one curl (localhost only), cat or ls in a second window"
    "nvim-shot <name>      save a picture of the editor (a PNG with its colours, signs and text) for the review" ]

/// A shot name becomes part of a file name, so it is short and plain.
let private shotNameShape = Regex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$", RegexOptions.Compiled)

let parseShotName (name: string) : Result<string, string> =
  if shotNameShape.IsMatch name then Result.Ok name
  else Result.Error "a shot name is 1 to 40 letters, digits, - or _ and starts with a letter or digit"

/// Aliases are accepted because a lemming that types `nvim screen` meant `nvim-screen`.
let parseCommand (name: string) (rest: string list) : Result<DriveCommand, string> =
  match name.ToLowerInvariant() with
  | "keys" ->
    match rest with
    | [] -> Result.Error "keys needs the keys to send, e.g. keys \":SageFsEval<CR>\""
    | _ -> Result.Ok(Keys(String.concat "" rest))
  | "nvim-type" | "type" ->
    match rest with
    | [] -> Result.Error "nvim-type needs the text to type"
    | _ -> Result.Ok(NvimType(String.concat " " rest))
  | "nvim-screen" | "screen" -> Result.Ok NvimScreen
  | "nvim-wait" | "wait" ->
    match rest with
    | [ n ] ->
      match Int32.TryParse n with
      | true, v when v >= 0 -> Result.Ok(NvimWait(min v Limits.MaxWaitSeconds))
      | _ -> Result.Error "nvim-wait takes a whole number of seconds (at most 30)"
    | _ -> Result.Error "nvim-wait takes one number of seconds (at most 30)"
  | "nvim-messages" | "messages" -> Result.Ok NvimMessages
  | "nvim-shell" | "shell" ->
    match rest with
    | [] -> Result.Error "nvim-shell needs a command: curl, cat or ls"
    | _ -> Result.Ok(NvimShell(String.concat " " rest))
  | "nvim-shot" | "shot" ->
    match rest with
    | [] -> Result.Ok(NvimShot "shot")
    | [ name ] -> parseShotName name |> Result.map NvimShot
    | _ -> Result.Error "nvim-shot takes one name, e.g. nvim-shot after-eval"
  | other ->
    Result.Error(sprintf "unknown command '%s'. The commands are:\n%s" other (String.concat "\n" commandUsage))

// ---------------------------------------------------------------------------
// What the screen shows.
// ---------------------------------------------------------------------------

type Mode =
  | Normal
  | InsertMode
  | Replace
  | Visual
  | VisualLine
  | VisualBlock
  | CommandLine
  | HitEnter
  | MorePrompt
  | InputPrompt
  | Exited of status: int

let modeText (mode: Mode) : string =
  match mode with
  | InputPrompt -> "INPUT PROMPT (type the number or answer and press <CR>; q or <Esc> cancels)"
  | Normal -> "NORMAL"
  | InsertMode -> "INSERT"
  | Replace -> "REPLACE"
  | Visual -> "VISUAL"
  | VisualLine -> "VISUAL LINE"
  | VisualBlock -> "VISUAL BLOCK"
  | CommandLine -> "COMMAND-LINE"
  | HitEnter -> "HIT-ENTER PROMPT (press <CR> or <Esc>)"
  | MorePrompt -> "MORE PROMPT (press <Space> for more, q to quit)"
  | Exited status -> sprintf "EXITED (nvim ended with status %d)" status

/// `cursorRow` is the terminal cursor's row, counted from 1. In command-line mode the cursor sits on
/// the last row, which is how it is told from a command that has already run and is only still
/// printed there.
let detectMode (rows: string list) (cursorRow: int) : Mode =
  let nonEmpty = rows |> List.filter (fun r -> r.Trim().Length > 0)
  let last = match List.tryLast rows with Some r -> r | None -> ""
  let cursorOnLastRow = cursorRow >= rows.Length
  let any (needle: string) = rows |> List.exists (fun r -> r.Contains needle)
  match () with
  | _ when any "Press ENTER or type command to continue" -> HitEnter
  | _ when any "-- More --" -> MorePrompt
  | _ when any "Type number and <Enter>" -> InputPrompt
  | _ when last.Contains "-- INSERT --" || last.Contains "-- (insert)" -> InsertMode
  | _ when last.Contains "-- VISUAL LINE --" -> VisualLine
  | _ when last.Contains "-- VISUAL BLOCK --" -> VisualBlock
  | _ when last.Contains "-- VISUAL --" -> Visual
  | _ when last.Contains "-- REPLACE --" -> Replace
  | _ when cursorOnLastRow && (last.StartsWith ":" || last.StartsWith "/" || last.StartsWith "?") -> CommandLine
  | _ when nonEmpty.IsEmpty -> Normal
  | _ -> Normal

type ScreenSnapshot =
  { Rows: string list
    CursorRow: int
    CursorCol: int
    Mode: Mode }

/// The status line is the second row from the bottom (laststatus=2 in the lemming's init).
let statusLine (rows: string list) : string =
  match List.rev rows with
  | _ :: status :: _ -> status.Trim()
  | _ -> ""

let formatScreen (snap: ScreenSnapshot) : string =
  let header =
    sprintf "[nvim] mode=%s | cursor on screen row %d, column %d | status line: %s"
      (modeText snap.Mode) snap.CursorRow snap.CursorCol (statusLine snap.Rows)
  String.concat "\n" (header :: String('-', 60) :: snap.Rows)

let private trimTrailingBlankRows (rows: string list) : string list =
  rows |> List.rev |> List.skipWhile (fun r -> r.Trim().Length = 0) |> List.rev

// ---------------------------------------------------------------------------
// Processes and tmux.
// ---------------------------------------------------------------------------

type ProcessOutcome =
  { ExitCode: int
    Output: string }

let runProcess (exe: string) (args: string list) (env: (string * string) list) (timeoutMs: int) : Result<ProcessOutcome, string> =
  let psi = ProcessStartInfo(exe)
  for a in args do
    psi.ArgumentList.Add a
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  psi.UseShellExecute <- false
  psi.Environment.Remove "TMUX" |> ignore
  for (k, v) in env do
    psi.Environment.[k] <- v
  try
    match Process.Start psi with
    | null -> Result.Error(sprintf "could not start %s" exe)
    | proc ->
      use proc = proc
      let stdout = proc.StandardOutput.ReadToEndAsync()
      let stderr = proc.StandardError.ReadToEndAsync()
      if proc.WaitForExit timeoutMs then
        proc.WaitForExit()
        Result.Ok { ExitCode = proc.ExitCode; Output = stdout.Result + stderr.Result }
      else
        (try proc.Kill true with _ -> ())
        Result.Error(sprintf "%s timed out after %d ms" exe timeoutMs)
  with ex ->
    Result.Error(sprintf "could not run %s: %s" exe ex.Message)

type TmuxTarget =
  { Dir: string
    Label: string
    Session: string }

let private nvimWindow (t: TmuxTarget) = sprintf "%s:nvim" t.Session
let private shellWindow (t: TmuxTarget) = sprintf "%s:shell" t.Session

let tmux (t: TmuxTarget) (args: string list) : Result<string, string> =
  match runProcess "tmux" ([ "-L"; t.Label ] @ args) [ ("TMUX_TMPDIR", t.Dir) ] Limits.TmuxCallTimeoutMs with
  | Result.Error e -> Result.Error e
  | Result.Ok o when o.ExitCode <> 0 -> Result.Error(sprintf "tmux %s failed: %s" (String.concat " " args) (o.Output.Trim()))
  | Result.Ok o -> Result.Ok o.Output

let private bindResult (f: 'a -> Result<'b, string>) (r: Result<'a, string>) = Result.bind f r

let private sendTokens (t: TmuxTarget) (window: string) (tokens: KeyToken list) : Result<unit, string> =
  let sendOne (token: KeyToken) : Result<unit, string> =
    match token with
    | Key chord ->
      let sent = tmux t [ "send-keys"; "-t"; window; tmuxChord chord ] |> Result.map ignore
      match chord.Base, chord.Ctrl, chord.Alt, chord.Shift with
      | Named Esc, false, false, false ->
        Thread.Sleep Limits.EscapeSettleMs
        sent
      | _ -> sent
    | Text text ->
      // A newline in typed text is an Enter key, because a raw LF reads as Ctrl-J.
      let lines = text.Split('\n')
      lines
      |> Array.mapi (fun i line ->
        let typed =
          if line.Length = 0 then Result.Ok()
          else tmux t [ "send-keys"; "-t"; window; "-l"; "--"; line ] |> Result.map ignore
        let enter =
          if i < lines.Length - 1 then tmux t [ "send-keys"; "-t"; window; "Enter" ] |> Result.map ignore
          else Result.Ok()
        typed |> bindResult (fun () -> enter))
      |> Array.fold (fun acc r -> acc |> bindResult (fun () -> r)) (Result.Ok())
  tokens |> List.fold (fun acc token -> acc |> bindResult (fun () -> sendOne token)) (Result.Ok())

let private paneFacts (t: TmuxTarget) (window: string) : Result<int * int * bool * int, string> =
  tmux t [ "display-message"; "-p"; "-t"; window; "#{cursor_y} #{cursor_x} #{pane_dead} #{pane_dead_status}" ]
  |> Result.bind (fun text ->
    match text.Trim().Split(' ') with
    | [| y; x; dead; status |] ->
      let parsed = Int32.TryParse y, Int32.TryParse x
      match parsed with
      | (true, yv), (true, xv) ->
        let statusValue = match Int32.TryParse status with | true, s -> s | _ -> 0
        Result.Ok(yv + 1, xv + 1, (dead = "1"), statusValue)
      | _ -> Result.Error(sprintf "unexpected tmux pane facts: %s" text)
    | [| y; x; dead |] ->
      match Int32.TryParse y, Int32.TryParse x with
      | (true, yv), (true, xv) -> Result.Ok(yv + 1, xv + 1, (dead = "1"), 0)
      | _ -> Result.Error(sprintf "unexpected tmux pane facts: %s" text)
    | _ -> Result.Error(sprintf "unexpected tmux pane facts: %s" text))

let private capture (t: TmuxTarget) (window: string) (scrollback: int option) : Result<string list, string> =
  let range =
    match scrollback with
    | Some n -> [ "-S"; sprintf "-%d" n ]
    | None -> []
  tmux t ([ "capture-pane"; "-p"; "-J"; "-t"; window ] @ range)
  |> Result.map (fun text ->
    text.Split('\n') |> Array.toList |> List.map (fun r -> r.TrimEnd()) |> fun rows ->
      // capture-pane ends with a newline: drop the empty tail element it creates
      match List.rev rows with
      | "" :: rest -> List.rev rest
      | _ -> rows)

let snapshot (t: TmuxTarget) : Result<ScreenSnapshot, string> =
  match paneFacts t (nvimWindow t), capture t (nvimWindow t) None with
  | Result.Error e, _ -> Result.Error e
  | _, Result.Error e -> Result.Error e
  | Result.Ok(row, col, dead, status), Result.Ok rows ->
    let mode = if dead then Exited status else detectMode rows row
    Result.Ok { Rows = rows; CursorRow = row; CursorCol = col; Mode = mode }

let screenText (t: TmuxTarget) : Result<string, string> =
  snapshot t |> Result.map formatScreen

// ---------------------------------------------------------------------------
// Pictures: the editor with its colours, signs and virtual text, for the design review.
// ---------------------------------------------------------------------------

/// The pane's size in columns and rows, as tmux reports it.
let paneSize (t: TmuxTarget) : Result<int * int, string> =
  tmux t [ "display-message"; "-p"; "-t"; nvimWindow t; "#{pane_width} #{pane_height}" ]
  |> Result.bind (fun text ->
    match text.Trim().Split(' ') with
    | [| w; h |] ->
      match Int32.TryParse w, Int32.TryParse h with
      | (true, wv), (true, hv) -> Result.Ok(wv, hv)
      | _ -> Result.Error(sprintf "unexpected tmux pane size: %s" text)
    | _ -> Result.Error(sprintf "unexpected tmux pane size: %s" text))

/// The pane with its SGR colours, as `tmux capture-pane -e` writes it. No -J: a picture shows
/// the pane row for row, so a long message that wraps stays wrapped.
let captureAnsi (t: TmuxTarget) : Result<string, string> =
  tmux t [ "capture-pane"; "-e"; "-p"; "-t"; nvimWindow t ]

/// Resizes the editor's terminal (a tour step: the narrow 80x24 and the wide 200x50 views).
let resizeEditor (t: TmuxTarget) (columns: int) (rows: int) : Result<unit, string> =
  tmux t [ "resize-window"; "-t"; nvimWindow t; "-x"; string columns; "-y"; string rows ] |> Result.map ignore

type ShotMeta =
  { name: string
    number: int
    columns: int
    rows: int
    cellWidthPx: float
    cellHeightPx: int
    imageWidth: int
    imageHeight: int
    fontFamily: string
    fontPx: int
    mode: string
    statusLine: string
    capturedAt: string
    png: string
    ansi: string }

let private shotFilePattern = Regex(@"^(\d{3,})-", RegexOptions.Compiled)

/// Shots are numbered in the order they are taken, across lemming calls and tour steps alike.
let nextShotNumber (shotsDir: string) : int =
  if not (Directory.Exists shotsDir) then 1
  else
    Directory.GetFiles shotsDir
    |> Array.choose (fun f ->
      let m = shotFilePattern.Match(Path.GetFileName f)
      if m.Success then Some(int m.Groups.[1].Value) else None)
    |> Array.fold max 0
    |> (+) 1

/// Writes OUT/shots/NNN-name.png, .txt (the raw ANSI) and .json (what it was drawn with).
let takeShot (t: TmuxTarget) (shotsDir: string) (name: string) : Result<ShotMeta * ScreenSnapshot, string> =
  match parseShotName name with
  | Result.Error e -> Result.Error e
  | Result.Ok name ->
    match snapshot t, captureAnsi t, paneSize t with
    | Result.Error e, _, _
    | _, Result.Error e, _
    | _, _, Result.Error e -> Result.Error e
    | Result.Ok snap, Result.Ok ansi, Result.Ok(columns, rows) ->
      Directory.CreateDirectory shotsDir |> ignore
      let number = nextShotNumber shotsDir
      let stem = sprintf "%03d-%s" number name
      let txt = Path.Combine(shotsDir, stem + ".txt")
      let png = Path.Combine(shotsDir, stem + ".png")
      File.WriteAllText(txt, ansi)
      match NvimShot.renderPng (Ansi.render ansi) columns rows png with
      | Result.Error e -> Result.Error e
      | Result.Ok r ->
        let meta =
          { name = name
            number = number
            columns = columns
            rows = rows
            cellWidthPx = r.CellWidthPx
            cellHeightPx = r.CellHeightPx
            imageWidth = r.ImageWidth
            imageHeight = r.ImageHeight
            fontFamily = r.FontFamily
            fontPx = r.FontPx
            mode = modeText snap.Mode
            statusLine = statusLine snap.Rows
            capturedAt = DateTime.UtcNow.ToString("o")
            png = stem + ".png"
            ansi = stem + ".txt" }
        File.WriteAllText(Path.Combine(shotsDir, stem + ".json"), JsonSerializer.Serialize(meta, JsonSerializerOptions(WriteIndented = true)))
        Result.Ok(meta, snap)

// ---------------------------------------------------------------------------
// Doing a command.
// ---------------------------------------------------------------------------

let private endsWithExitMarker = Regex(@"^\[exit (\d+)\]$", RegexOptions.Compiled ||| RegexOptions.Multiline)

let private runShell (t: TmuxTarget) (workspace: string) (line: string) : Result<string, string> =
  match parseShellCommand workspace line with
  | Result.Error reason -> Result.Error reason
  | Result.Ok cmd ->
    let window = shellWindow t
    let commandLine = shellCommandLine cmd
    let typed = sprintf "clear; %s; echo \"[exit $?]\"" commandLine
    tmux t [ "clear-history"; "-t"; window ]
    |> bindResult (fun _ -> tmux t [ "send-keys"; "-t"; window; "-l"; "--"; typed ])
    |> bindResult (fun _ -> tmux t [ "send-keys"; "-t"; window; "Enter" ])
    |> bindResult (fun _ ->
      let deadline = DateTime.UtcNow.AddMilliseconds(float Limits.ShellWaitMs)
      let rec poll () =
        match capture t window (Some Limits.ShellScrollback) with
        | Result.Error e -> Result.Error e
        | Result.Ok rows ->
          // Only what comes after the echoed command line is this command's output.
          let afterCommand =
            match rows |> List.tryFindIndexBack (fun r -> r.Contains "echo \"[exit") with
            | Some i -> rows |> List.skip (i + 1)
            | None -> rows
          let output = afterCommand |> List.takeWhile (fun r -> not (endsWithExitMarker.IsMatch r))
          let marker = afterCommand |> List.tryFind (fun r -> endsWithExitMarker.IsMatch r)
          let body = String.concat "\n" output
          match marker with
          | Some m ->
            let clipped =
              if body.Length > Limits.MaxShellOutputChars then body.Substring(0, Limits.MaxShellOutputChars) + "\n[output clipped]"
              else body
            Result.Ok(sprintf "$ %s\n%s\n%s" commandLine clipped m)
          | None when DateTime.UtcNow > deadline ->
            Result.Ok(sprintf "$ %s\n%s\n[still running after %d s; the output so far is above]" commandLine body (Limits.ShellWaitMs / 1000))
          | None ->
            Thread.Sleep Limits.ShellPollMs
            poll ()
      poll ())

let private settle () = Thread.Sleep Limits.KeySettleMs

let private readMessages (t: TmuxTarget) : Result<string, string> =
  let escape = parseKeys "<Esc>"
  let open' = parseKeys ":messages<CR>"
  match escape, open' with
  | Result.Ok esc, Result.Ok cmd ->
    sendTokens t (nvimWindow t) esc
    |> bindResult (fun () -> sendTokens t (nvimWindow t) cmd)
    |> bindResult (fun () ->
      settle ()
      let pages = ResizeArray<string>()
      let rec loop n =
        match snapshot t with
        | Result.Error e -> Result.Error e
        | Result.Ok snap ->
          pages.Add(String.concat "\n" (trimTrailingBlankRows snap.Rows))
          match snap.Mode with
          | MorePrompt when n < Limits.MaxMessagePages ->
            tmux t [ "send-keys"; "-t"; nvimWindow t; "Space" ]
            |> bindResult (fun _ ->
              settle ()
              loop (n + 1))
          | _ -> Result.Ok()
      loop 1
      |> bindResult (fun () ->
        // Leave nvim where :messages found it: dismiss the prompt with Escape.
        sendTokens t (nvimWindow t) esc
        |> Result.map (fun () ->
          settle ()
          String.concat "\n----- next page -----\n" pages)))
  | Result.Error e, _ | _, Result.Error e -> Result.Error e

/// Where a command runs and where its files go.
type ExecContext =
  { Tmux: TmuxTarget
    Workspace: string
    OutDir: string }

let shotsDirOf (outDir: string) = Path.Combine(outDir, "shots")

let execute (ctx: ExecContext) (cmd: DriveCommand) : Result<string, string> =
  let t = ctx.Tmux
  let workspace = ctx.Workspace
  let window = nvimWindow t
  match cmd with
  | NvimShot name ->
    takeShot t (shotsDirOf ctx.OutDir) name
    |> Result.map (fun (meta, snap) ->
      sprintf "[shot %03d-%s saved: a %dx%d PNG of the editor in colour. The design review reads it, you cannot open it, so here is the screen as text]\n%s"
        meta.number meta.name meta.imageWidth meta.imageHeight (formatScreen snap))
  | Keys keys ->
    parseKeys keys
    |> bindResult (sendTokens t window)
    |> bindResult (fun () ->
      settle ()
      screenText t)
  | NvimType text ->
    sendTokens t window [ Text text ]
    |> bindResult (fun () ->
      settle ()
      screenText t)
  | NvimScreen -> screenText t
  | NvimWait seconds ->
    Thread.Sleep(seconds * 1000)
    screenText t
  | NvimMessages -> readMessages t
  | NvimShell line -> runShell t workspace line

// ---------------------------------------------------------------------------
// The wire between the lemming's client and the server.
// ---------------------------------------------------------------------------

type Request = { cmd: string; arg: string }
type Response = { ok: bool; text: string }

let private jsonOptions = JsonSerializerOptions()

/// What the lemming-side client does: one connection, one request, one answer.
let client (socketPath: string) (cmd: DriveCommand) : Result<string, string> =
  try
    use sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
    sock.ReceiveTimeout <- Limits.ClientTimeoutMs
    sock.SendTimeout <- Limits.ClientTimeoutMs
    sock.Connect(UnixDomainSocketEndPoint socketPath)
    use stream = new NetworkStream(sock)
    let body = JsonSerializer.Serialize({ cmd = commandName cmd; arg = commandArgument cmd }, jsonOptions) + "\n"
    let bytes = Encoding.UTF8.GetBytes body
    stream.Write(bytes, 0, bytes.Length)
    sock.Shutdown SocketShutdown.Send
    use reader = new StreamReader(stream, Encoding.UTF8)
    let reply = reader.ReadToEnd()
    match JsonSerializer.Deserialize<Response>(reply, jsonOptions) with
    | null -> Result.Error "the editor driver sent an empty answer"
    | parsed -> if parsed.ok then Result.Ok parsed.text else Result.Error parsed.text
  with ex ->
    Result.Error(sprintf "could not reach the editor driver: %s" ex.Message)

type ServeConfig =
  { Tmux: TmuxTarget
    SocketPath: string
    OutDir: string
    Workspace: string
    NvimBin: string
    InitLua: string
    OpenFile: string
    ReadyFile: string }

let private requestToCommand (req: Request) : Result<DriveCommand, string> =
  parseCommand req.cmd [ req.arg ]

let private screensDir (cfg: ServeConfig) = Path.Combine(cfg.OutDir, "screens")
let private callsLog (cfg: ServeConfig) = Path.Combine(cfg.OutDir, "ui-calls.jsonl")

let private writeScreenFile (cfg: ServeConfig) (n: int) (header: string) (text: string) : string =
  Directory.CreateDirectory(screensDir cfg) |> ignore
  let name = sprintf "%03d.txt" n
  File.WriteAllText(Path.Combine(screensDir cfg, name), header + "\n" + text + "\n")
  name

let private nowIso () = DateTime.UtcNow.ToString("o")
let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

/// One line of OUT/timeline.ndjson per driver command: the "actions" clock for the temporal
/// coupling analysis. Times are epoch milliseconds. A tour step writes the same record.
type TimelineEntry =
  { startMs: int64
    endMs: int64
    editor: string
    /// "lemming" for a call through the socket, "tour" for a tour step.
    source: string
    command: string
    args: string list
    /// "ok" or "failed", the same words the VS Code driver writes.
    outcome: string }

let timelineEntry (source: string) (command: string) (arg: string) (ok: bool) (startMs: int64) (endMs: int64) : TimelineEntry =
  { startMs = startMs
    endMs = endMs
    editor = "nvim"
    source = source
    command = command
    args = (if arg = "" then [] else [ arg ])
    outcome = (if ok then "ok" else "failed") }

let timelinePath (outDir: string) = Path.Combine(outDir, "timeline.ndjson")

let private timelineLock = obj ()

let appendTimeline (outDir: string) (entry: TimelineEntry) : unit =
  lock timelineLock (fun () ->
    Directory.CreateDirectory outDir |> ignore
    File.AppendAllText(timelinePath outDir, JsonSerializer.Serialize(entry, jsonOptions) + "\n"))

let private handle (cfg: ServeConfig) (counter: int ref) (req: Request) : Response =
  counter.Value <- counter.Value + 1
  let n = counter.Value
  let started = Stopwatch.StartNew()
  let startMs = nowMs ()
  let parsed = requestToCommand req
  let ctx: ExecContext = { Tmux = cfg.Tmux; Workspace = cfg.Workspace; OutDir = cfg.OutDir }
  let result =
    match parsed with
    | Result.Error reason -> Result.Error reason
    | Result.Ok cmd -> execute ctx cmd
  let ok, text =
    match result with
    | Result.Ok t -> true, t
    | Result.Error e -> false, "error: " + e
  let header = sprintf "# %03d %s %s | %s | ok=%b | %d ms" n req.cmd (JsonSerializer.Serialize req.arg) (nowIso ()) ok started.ElapsedMilliseconds
  let file = writeScreenFile cfg n header text
  let line =
    JsonSerializer.Serialize(
      {| n = n
         cmd = req.cmd
         arg = req.arg
         ok = ok
         ms = started.ElapsedMilliseconds
         at = nowIso ()
         screen = "screens/" + file |},
      jsonOptions
    )
  File.AppendAllText(callsLog cfg, line + "\n")
  appendTimeline cfg.OutDir (timelineEntry "lemming" req.cmd req.arg ok startMs (nowMs ()))
  { ok = ok; text = text }

let private quote (s: string) = "'" + s.Replace("'", "'\\''") + "'"

/// Opens nvim in window 1 and a plain shell in window 2 of the tmux server that the
/// harness already started inside its own sandbox.
let launch (cfg: ServeConfig) : Result<unit, string> =
  let t = cfg.Tmux
  let nvimCommand = sprintf "%s -n -u %s %s" (quote cfg.NvimBin) (quote cfg.InitLua) (quote cfg.OpenFile)
  let rec waitServer (deadline: DateTime) =
    match tmux t [ "list-sessions" ] with
    | Result.Ok _ -> Result.Ok()
    | Result.Error _ when DateTime.UtcNow < deadline ->
      // With no session yet the server answers "no sessions"; before it binds, "error connecting".
      Thread.Sleep Limits.StartupPollMs
      waitServer deadline
    | Result.Error e -> Result.Error(sprintf "tmux server did not come up: %s" e)
  let deadline = DateTime.UtcNow.AddMilliseconds(float Limits.StartupWaitMs)
  waitServer deadline
  |> bindResult (fun () -> tmux t [ "set-option"; "-g"; "remain-on-exit"; "on" ])
  |> bindResult (fun _ -> tmux t [ "set-option"; "-g"; "escape-time"; "0" ])
  |> bindResult (fun _ -> tmux t [ "set-option"; "-g"; "extended-keys"; "on" ])
  |> bindResult (fun _ ->
    tmux t
      [ "new-session"; "-d"; "-s"; t.Session; "-n"; "nvim"
        "-x"; string Limits.ScreenColumns; "-y"; string Limits.ScreenRows
        "-c"; cfg.Workspace; nvimCommand ])
  |> bindResult (fun _ ->
    tmux t
      [ "new-window"; "-d"; "-t"; t.Session; "-n"; "shell"; "-c"; cfg.Workspace
        "env PS1='$ ' bash --norc --noprofile" ])
  |> Result.map ignore

let private waitForEditor (cfg: ServeConfig) : Result<string, string> =
  let deadline = DateTime.UtcNow.AddMilliseconds(float Limits.StartupWaitMs)
  let rec poll () =
    match snapshot cfg.Tmux with
    | Result.Error e when DateTime.UtcNow > deadline -> Result.Error e
    | Result.Ok snap when (match snap.Mode with Exited _ -> true | _ -> false) ->
      Result.Error(sprintf "nvim exited at startup:\n%s" (String.concat "\n" snap.Rows))
    | Result.Ok snap when snap.Rows |> List.exists (fun r -> r.Contains "SageFs") -> Result.Ok(statusLine snap.Rows)
    | _ when DateTime.UtcNow > deadline -> Result.Error "nvim came up but the SageFs status text never appeared in the status line"
    | _ ->
      Thread.Sleep Limits.StartupPollMs
      poll ()
  poll ()

/// Opens the editor and waits until the plugin has put its status text on screen.
/// Returns the status line it saw.
let startEditor (cfg: ServeConfig) : Result<string, string> =
  launch cfg |> bindResult (fun () -> waitForEditor cfg)

/// Runs until it is told to stop. SIGTERM stops it; the call log is flushed per call.
let serve (cfg: ServeConfig) : int =
  let writeReady (status: string) = File.WriteAllText(cfg.ReadyFile, status + "\n")
  match startEditor cfg with
  | Result.Error reason ->
    writeReady ("failed: " + reason)
    eprintfn "driver could not start: %s" reason
    2
  | Result.Ok status ->
    if File.Exists cfg.SocketPath then File.Delete cfg.SocketPath
    use listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
    listener.Bind(UnixDomainSocketEndPoint cfg.SocketPath)
    listener.Listen 8
    use stop = new ManualResetEventSlim(false)
    use _sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, fun ctx ->
      ctx.Cancel <- true
      stop.Set())
    use _sigint = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, fun ctx ->
      ctx.Cancel <- true
      stop.Set())
    writeReady ("ready: " + status)
    let counter = ref 0
    let serveOne () =
      use conn = listener.Accept()
      conn.ReceiveTimeout <- Limits.ClientTimeoutMs
      use stream = new NetworkStream(conn)
      use reader = new StreamReader(stream, Encoding.UTF8)
      let reply =
        try
          let raw = reader.ReadToEnd()
          match JsonSerializer.Deserialize<Request>(raw, jsonOptions) with
          | null -> { ok = false; text = "error: empty request" }
          | req -> handle cfg counter req
        with ex ->
          { ok = false; text = "error: " + ex.Message }
      let bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, jsonOptions))
      stream.Write(bytes, 0, bytes.Length)
    let acceptLoop =
      Thread(
        (fun () ->
          while not stop.IsSet do
            try serveOne () with _ -> ()),
        IsBackground = true
      )
    acceptLoop.Start()
    stop.Wait()
    // The last picture of the run, for the review. Best effort: the editor may be gone.
    (match takeShot cfg.Tmux (shotsDirOf cfg.OutDir) "final" with
     | Result.Ok _ -> ()
     | Result.Error e -> eprintfn "final shot not taken: %s" e)
    NvimShot.shutdown ()
    0

// ---------------------------------------------------------------------------
// Command line.
// ---------------------------------------------------------------------------

let private optionValue (args: string list) (name: string) : string option =
  args
  |> List.pairwise
  |> List.tryFind (fun (k, _) -> k = name)
  |> Option.map snd

let private requiredOption (args: string list) (name: string) : Result<string, string> =
  match optionValue args name with
  | Some v -> Result.Ok v
  | None -> Result.Error(sprintf "missing %s" name)

let private parseServeConfig (args: string list) : Result<ServeConfig, string> =
  let get = requiredOption args
  match get "--tmux-dir", get "--label", get "--socket", get "--out", get "--workspace", get "--nvim", get "--init", get "--open", get "--ready" with
  | Result.Ok dir, Result.Ok label, Result.Ok sock, Result.Ok out, Result.Ok ws, Result.Ok nvim, Result.Ok init, Result.Ok openFile, Result.Ok ready ->
    Result.Ok
      { Tmux = { Dir = dir; Label = label; Session = "lem" }
        SocketPath = sock
        OutDir = out
        Workspace = ws
        NvimBin = nvim
        InitLua = init
        OpenFile = openFile
        ReadyFile = ready }
  | results ->
    let (a, b, c, d, e, f, g, h, i) = results
    [ a; b; c; d; e; f; g; h; i ]
    |> List.choose (function Result.Error e -> Some e | Result.Ok _ -> None)
    |> String.concat ", "
    |> Result.Error

let driverSocketVariable = "LEM_DRIVE_SOCKET"

let private runClient (name: string) (rest: string list) : int =
  match parseCommand name rest with
  | Result.Error reason ->
    eprintfn "%s" reason
    2
  | Result.Ok cmd ->
    match Environment.GetEnvironmentVariable driverSocketVariable with
    | null | "" ->
      eprintfn "%s is not set: this tool only works from inside the lemming sandbox" driverSocketVariable
      2
    | socket ->
      match client socket cmd with
      | Result.Ok text ->
        printfn "%s" text
        0
      | Result.Error reason ->
        printfn "%s" reason
        1

/// `dotnet LemDrive.dll nvim <command> ...`. Returns the process exit code.
let run (args: string list) : int =
  match args with
  | "serve" :: rest ->
    match parseServeConfig rest with
    | Result.Ok cfg -> serve cfg
    | Result.Error reason ->
      eprintfn "nvim serve: %s" reason
      2
  | name :: rest -> runClient name rest
  | [] ->
    eprintfn "usage: dotnet LemDrive.dll nvim <command>\n%s" (String.concat "\n" commandUsage)
    2
