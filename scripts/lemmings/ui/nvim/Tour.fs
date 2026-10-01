// Tours: a typed list of steps that drives the run's Neovim into the same states every time,
// without a lemming, and takes numbered shots along the way. A design reviewer reads the shots.
//
// A tour file is plain text, one step per line. Blank lines and lines that start with # are
// ignored. The steps are a closed set:
//
//   keys <vim keys>              send keys, vim notation (the same as `nvim keys`)
//   type <text>                  type the text as it is
//   wait <seconds>               wait 0 to 30 seconds
//   shot <name>                  save OUT/shots/NNN-<name>.png (+ .txt, .json)
//   expect "<text>" [within N]   the screen must show the text within N seconds (default 10),
//                                or the tour stops there and says what the screen showed
//   resize <columns>x<rows>      resize the editor's terminal (20x8 up to 300x100)
//   shell <curl|cat|ls ...>      the same read-only shell the lemming has
//
// Every step also writes a line to OUT/timeline.ndjson, so a recording can be lined up with it.
module LemDrive.Tour

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading

module Limits =
  let DefaultExpectSeconds = 10
  let MaxExpectSeconds = 120
  let ExpectPollMs = 500
  let MinColumns = 20
  let MaxColumns = 300
  let MinRows = 8
  let MaxRows = 100
  let ResizeSettleMs = 600

type TourStep =
  | StepKeys of string
  | StepType of string
  | StepWait of seconds: int
  | StepShot of name: string
  | StepExpect of text: string * withinSeconds: int
  | StepResize of columns: int * rows: int
  | StepShell of string

/// The word a step starts with. The one place the step names are spelled.
let stepWord (step: TourStep) : string =
  match step with
  | StepKeys _ -> "keys"
  | StepType _ -> "type"
  | StepWait _ -> "wait"
  | StepShot _ -> "shot"
  | StepExpect _ -> "expect"
  | StepResize _ -> "resize"
  | StepShell _ -> "shell"

let private quoteText (text: string) : string =
  "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

/// A step as it is written in a tour file. `parseLine (formatStep s)` gives `s` back.
let formatStep (step: TourStep) : string =
  match step with
  | StepKeys keys -> "keys " + keys
  | StepType text -> "type " + text
  | StepWait seconds -> sprintf "wait %d" seconds
  | StepShot name -> "shot " + name
  | StepExpect(text, within) -> sprintf "expect %s within %d" (quoteText text) within
  | StepResize(c, r) -> sprintf "resize %dx%d" c r
  | StepShell line -> "shell " + line

type TourError =
  { Line: int
    Reason: string }

let private resizeShape = Regex(@"^(\d+)x(\d+)$", RegexOptions.Compiled)

/// `"text with \" and \\ escapes" rest` -> (text, rest).
let private takeQuoted (input: string) : Result<string * string, string> =
  if not (input.StartsWith "\"") then Result.Error "the text must be in double quotes, e.g. expect \"Connected\""
  else
    let sb = StringBuilder()
    let mutable i = 1
    let mutable closed = false
    let mutable failure: string option = None
    while not closed && failure.IsNone && i < input.Length do
      match input.[i] with
      | '\\' when i + 1 < input.Length ->
        sb.Append input.[i + 1] |> ignore
        i <- i + 2
      | '\\' -> failure <- Some "a backslash at the end of the text"
      | '"' ->
        closed <- true
        i <- i + 1
      | c ->
        sb.Append c |> ignore
        i <- i + 1
    match failure, closed with
    | Some f, _ -> Result.Error f
    | None, false -> Result.Error "the quoted text is never closed"
    | None, true -> Result.Ok(sb.ToString(), input.Substring(i).Trim())

let private parseWithin (rest: string) : Result<int, string> =
  if rest = "" then Result.Ok Limits.DefaultExpectSeconds
  else
    match rest.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) with
    | [| "within"; n |] ->
      match Int32.TryParse n with
      | true, v when v >= 1 && v <= Limits.MaxExpectSeconds -> Result.Ok v
      | _ -> Result.Error(sprintf "within takes whole seconds from 1 to %d" Limits.MaxExpectSeconds)
    | _ -> Result.Error "after the text only 'within <seconds>' is allowed"

/// One line of a tour file. `None` for a blank line or a comment.
let parseLine (line: string) : Result<TourStep option, string> =
  let trimmed = line.Trim()
  if trimmed = "" || trimmed.StartsWith "#" then Result.Ok None
  else
    let word, rest =
      match trimmed.IndexOf ' ' with
      | -1 -> trimmed, ""
      | i -> trimmed.Substring(0, i), trimmed.Substring(i + 1).TrimStart()
    let some step = Result.Ok(Some step)
    match word.ToLowerInvariant() with
    | "keys" ->
      if rest = "" then Result.Error "keys needs the keys to send" else some (StepKeys rest)
    | "type" ->
      if rest = "" then Result.Error "type needs the text to type" else some (StepType rest)
    | "wait" ->
      match Int32.TryParse rest with
      | true, v when v >= 0 && v <= Nvim.Limits.MaxWaitSeconds -> some (StepWait v)
      | _ -> Result.Error(sprintf "wait takes whole seconds from 0 to %d" Nvim.Limits.MaxWaitSeconds)
    | "shot" ->
      Nvim.parseShotName rest |> Result.bind (fun n -> some (StepShot n))
    | "expect" ->
      takeQuoted rest
      |> Result.bind (fun (text, after) ->
        if text = "" then Result.Error "expect needs some text"
        else parseWithin after |> Result.bind (fun within -> some (StepExpect(text, within))))
    | "resize" ->
      let m = resizeShape.Match rest
      if not m.Success then Result.Error "resize takes <columns>x<rows>, e.g. resize 80x24"
      else
        let c = int m.Groups.[1].Value
        let r = int m.Groups.[2].Value
        if c < Limits.MinColumns || c > Limits.MaxColumns || r < Limits.MinRows || r > Limits.MaxRows then
          Result.Error(sprintf "resize wants %dx%d up to %dx%d" Limits.MinColumns Limits.MinRows Limits.MaxColumns Limits.MaxRows)
        else some (StepResize(c, r))
    | "shell" ->
      if rest = "" then Result.Error "shell needs a command: curl, cat or ls" else some (StepShell rest)
    | other ->
      Result.Error(sprintf "unknown step '%s'. The steps are keys, type, wait, shot, expect, resize, shell" other)

/// A whole tour file. Every bad line is reported with its number, not just the first.
let parse (text: string) : Result<TourStep list, TourError list> =
  let results =
    text.Split('\n')
    |> Array.mapi (fun i raw -> i + 1, parseLine (raw.TrimEnd('\r')))
    |> Array.toList
  let errors =
    results
    |> List.choose (fun (n, r) ->
      match r with
      | Result.Error reason -> Some { Line = n; Reason = reason }
      | Result.Ok _ -> None)
  match errors with
  | [] -> Result.Ok(results |> List.choose (fun (_, r) -> match r with Result.Ok(Some s) -> Some s | _ -> None))
  | errs -> Result.Error errs

// ---------------------------------------------------------------------------
// Running a tour.
// ---------------------------------------------------------------------------

type StepOutcome =
  { index: int
    step: string
    ok: bool
    detail: string
    startMs: int64
    endMs: int64 }

type TourReport =
  { tour: string
    passed: bool
    steps: StepOutcome list }

let private nowMs () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

let private firstLine (text: string) =
  match text.Split('\n') with
  | [||] -> ""
  | lines -> lines.[0]

let private expectOnScreen (ctx: Nvim.ExecContext) (text: string) (within: int) : Result<string, string> =
  let deadline = DateTime.UtcNow.AddSeconds(float within)
  let rec poll () =
    match Nvim.snapshot ctx.Tmux with
    | Result.Error e -> Result.Error e
    | Result.Ok snap ->
      let screen = String.concat "\n" snap.Rows
      if screen.Contains text then Result.Ok(sprintf "saw \"%s\"" text)
      elif DateTime.UtcNow > deadline then
        Result.Error(sprintf "\"%s\" never appeared within %d s. The screen showed:\n%s" text within (Nvim.formatScreen snap))
      else
        Thread.Sleep Limits.ExpectPollMs
        poll ()
  poll ()

let private runStep (ctx: Nvim.ExecContext) (step: TourStep) : Result<string, string> =
  match step with
  | StepKeys keys -> Nvim.execute ctx (Nvim.Keys keys) |> Result.map firstLine
  | StepType text -> Nvim.execute ctx (Nvim.NvimType text) |> Result.map firstLine
  | StepWait seconds -> Nvim.execute ctx (Nvim.NvimWait seconds) |> Result.map firstLine
  | StepShot name ->
    Nvim.takeShot ctx.Tmux (Nvim.shotsDirOf ctx.OutDir) name
    |> Result.map (fun (meta, _) -> sprintf "%s (%dx%d terminal, %dx%d px)" meta.png meta.columns meta.rows meta.imageWidth meta.imageHeight)
  | StepExpect(text, within) -> expectOnScreen ctx text within
  | StepResize(c, r) ->
    Nvim.resizeEditor ctx.Tmux c r
    |> Result.map (fun () ->
      Thread.Sleep Limits.ResizeSettleMs
      sprintf "terminal is now %dx%d" c r)
  | StepShell line -> Nvim.execute ctx (Nvim.NvimShell line)

/// Runs the steps in order and stops at the first one that fails.
let runSteps (ctx: Nvim.ExecContext) (name: string) (steps: TourStep list) : TourReport =
  let outcomes = ResizeArray<StepOutcome>()
  let mutable failed = false
  steps
  |> List.iteri (fun i step ->
    if not failed then
      let start = nowMs ()
      let result = runStep ctx step
      let stop = nowMs ()
      let ok, detail =
        match result with
        | Result.Ok d -> true, d
        | Result.Error e -> false, e
      if not ok then failed <- true
      Nvim.appendTimeline ctx.OutDir { startMs = start; endMs = stop; source = "tour"; command = stepWord step; args = (formatStep step).Substring((stepWord step).Length).Trim(); ok = ok }
      outcomes.Add { index = i + 1; step = formatStep step; ok = ok; detail = detail; startMs = start; endMs = stop })
  { tour = name; passed = not failed; steps = List.ofSeq outcomes }

// ---------------------------------------------------------------------------
// Command line: `nvim tour <file> <editor options>`, `nvim tour-check <file>`,
// `nvim render <ansi.txt> <out.png> --columns C --rows R`.
// ---------------------------------------------------------------------------

let private optionValue (args: string list) (name: string) : string option =
  args |> List.pairwise |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

let private readTour (path: string) : Result<TourStep list, string> =
  if not (File.Exists path) then Result.Error(sprintf "no tour file at %s" path)
  else
    match parse (File.ReadAllText path) with
    | Result.Ok steps when steps.IsEmpty -> Result.Error(sprintf "%s has no steps" path)
    | Result.Ok steps -> Result.Ok steps
    | Result.Error errs ->
      errs
      |> List.map (fun e -> sprintf "%s:%d: %s" path e.Line e.Reason)
      |> String.concat "\n"
      |> Result.Error

/// `tour-check <file>`: parses the tour and says what it would do.
let check (args: string list) : int =
  match args with
  | file :: _ ->
    match readTour file with
    | Result.Ok steps ->
      printfn "%s: %d steps, %d shots" file steps.Length (steps |> List.filter (function StepShot _ -> true | _ -> false) |> List.length)
      0
    | Result.Error e ->
      eprintfn "%s" e
      1
  | [] ->
    eprintfn "usage: nvim tour-check <tour-file>"
    2

/// `tour <file> --tmux-dir D --label L --out O --workspace W --nvim B --init I --open F`:
/// opens the editor in the tmux server the harness started, runs the tour, writes
/// OUT/tour-report.json, exits 0 only if every step passed.
let run (args: string list) : int =
  match args with
  | file :: rest ->
    let get name = optionValue rest name
    match readTour file, get "--tmux-dir", get "--label", get "--out", get "--workspace", get "--nvim", get "--init", get "--open" with
    | Result.Ok steps, Some dir, Some label, Some out, Some ws, Some nvim, Some init, Some openFile ->
      let cfg : Nvim.ServeConfig =
        { Tmux = { Dir = dir; Label = label; Session = "lem" }
          SocketPath = ""
          OutDir = out
          Workspace = ws
          NvimBin = nvim
          InitLua = init
          OpenFile = openFile
          ReadyFile = "" }
      match Nvim.startEditor cfg with
      | Result.Error reason ->
        eprintfn "the editor did not start: %s" reason
        2
      | Result.Ok _ ->
        let ctx : Nvim.ExecContext = { Tmux = cfg.Tmux; Workspace = ws; OutDir = out }
        let report = runSteps ctx (Path.GetFileNameWithoutExtension file) steps
        Directory.CreateDirectory out |> ignore
        File.WriteAllText(Path.Combine(out, "tour-report.json"), JsonSerializer.Serialize(report, JsonSerializerOptions(WriteIndented = true)))
        for s in report.steps do
          printfn "%s %02d %s  %s" (if s.ok then "ok  " else "FAIL") s.index s.step (firstLine s.detail)
        Shot.shutdown ()
        if report.passed then 0 else 1
    | Result.Error e, _, _, _, _, _, _, _ ->
      eprintfn "%s" e
      2
    | _ ->
      eprintfn "usage: nvim tour <tour-file> --tmux-dir D --label L --out O --workspace W --nvim B --init I --open F"
      2
  | [] ->
    eprintfn "usage: nvim tour <tour-file> ..."
    2

/// `render <ansi.txt> <out.png> [--columns C] [--rows R]`: draws a saved shot again, e.g. after
/// the look changed. Columns and rows default to the shot's own .json when it sits beside it.
let render (args: string list) : int =
  match args with
  | ansiFile :: png :: rest when File.Exists ansiFile ->
    let text = File.ReadAllText ansiFile
    let grid = Ansi.parse text
    let rows = grid.Length - (if grid.Length > 0 && grid |> List.last |> List.isEmpty then 1 else 0)
    let columns =
      match optionValue rest "--columns" with
      | Some c -> int c
      | None -> grid |> List.map List.length |> List.fold max Nvim.Limits.ScreenColumns
    let rows = match optionValue rest "--rows" with | Some r -> int r | None -> rows
    match Shot.renderPng (Ansi.toHtml grid) columns rows png with
    | Result.Ok r ->
      printfn "%s: %dx%d px" r.Png r.ImageWidth r.ImageHeight
      Shot.shutdown ()
      0
    | Result.Error e ->
      eprintfn "%s" e
      1
  | _ ->
    eprintfn "usage: nvim render <ansi.txt> <out.png> [--columns C] [--rows R]"
    2
