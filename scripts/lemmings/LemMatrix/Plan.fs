/// A matrix plan: which fixture x task x model combinations to run, and how many times each.
module LemMatrix.Plan

open System
open System.IO
open LemScore

/// One line of a plan file: `<fixture> <task> <model> [reps]`. `#` starts a comment.
type Line =
  { Fixture: string
    Task: string
    Model: string
    Reps: int }

/// One run to dispatch: a line's repetition with its own unique run id.
type Cell =
  { Fixture: string
    Task: string
    Model: string
    Rep: int
    RunId: string }

/// A repetition count above this is almost certainly a typo, and every run spends free quota.
let maxRepsPerLine = 20

let parseLine (lineNumber: int) (text: string) : Result<Line option, string> =
  let body =
    match text.IndexOf '#' with
    | -1 -> text
    | cut -> text.Substring(0, cut)
  match body.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries) with
  | [||] -> Ok None
  | [| fixture; task; model |] -> Ok (Some { Fixture = fixture; Task = task; Model = model; Reps = 1 })
  | [| fixture; task; model; reps |] ->
    match Int32.TryParse reps with
    | true, n when n >= 1 && n <= maxRepsPerLine -> Ok (Some { Fixture = fixture; Task = task; Model = model; Reps = n })
    | _ -> Error (sprintf "line %d: reps must be a whole number from 1 to %d, not '%s'" lineNumber maxRepsPerLine reps)
  | _ -> Error (sprintf "line %d: expected '<fixture> <task> <model> [reps]'" lineNumber)

let parse (text: string) : Result<Line list, string> =
  text.Split('\n')
  |> Array.mapi (fun i l -> parseLine (i + 1) (l.TrimEnd '\r'))
  |> Array.toList
  |> List.fold
       (fun acc r ->
         match acc, r with
         | Error e, _ -> Error e
         | Ok xs, Ok (Some x) -> Ok (xs @ [ x ])
         | Ok xs, Ok None -> Ok xs
         | Ok _, Error e -> Error e)
       (Ok [])
  |> Result.bind (fun lines ->
    match lines with
    | [] -> Error "the plan has no runs in it"
    | _ -> Ok lines)

/// The task files a plan names must exist; naming one that does not is a plan error, not a
/// run that starts and fails.
let checkTasks (tasksDir: string) (lines: Line list) : Result<unit, string> =
  lines
  |> List.tryFind (fun l -> not (File.Exists(Path.Combine(tasksDir, l.Task + ".md"))))
  |> function
    | Some l -> Error (sprintf "no task '%s' (looked for %s)" l.Task (Path.Combine(tasksDir, l.Task + ".md")))
    | None -> Ok ()

/// Expands lines into cells with run ids that are unique across the whole plan and against
/// what is already under `root`, so two repetitions can never share a run directory.
let expand (root: string) (lines: Line list) : Cell list =
  let step (issued: Set<string>, cells: Cell list) (line: Line) =
    let ids = Catalog.runIdsAvoiding root issued line.Model line.Task line.Reps
    let made =
      ids |> List.mapi (fun i id -> { Fixture = line.Fixture; Task = line.Task; Model = line.Model; Rep = i + 1; RunId = id })
    Set.union issued (Set.ofList ids), cells @ made
  lines |> List.fold step (Set.empty, []) |> snd

let models (lines: Line list) : string list =
  lines |> List.map _.Model |> List.distinct
