/// Clearing finished runs out of the run root. Every run leaves its own copy of the SageFs build
/// (about 380 MB) and nothing else ever removes it, so the root fills up. Only a run that has a
/// summary.json is ever removed, because one without may still be running.
module LemScore.Prune

open System
open System.IO

type RunDir =
  { Id: string
    Path: string
    Bytes: int64
    /// out/summary.json exists, so the run is over.
    Finished: bool
    /// Days since summary.json was written (0 when the run is not finished).
    AgeDays: float }

type Plan =
  { Delete: RunDir list
    Skipped: (string * string) list }

let humanBytes (bytes: int64) : string =
  match float bytes with
  | b when b >= 1073741824.0 -> sprintf "%.1f GB" (b / 1073741824.0)
  | b when b >= 1048576.0 -> sprintf "%.0f MB" (b / 1048576.0)
  | b -> sprintf "%.0f KB" (b / 1024.0)

/// A run id is one path segment: no separators, no dot-prefix (those are the matrix logs and
/// the build lock), so naming one can never reach outside the run root.
let validRunId (id: string) : bool =
  id <> "" && not (id.StartsWith ".") && not (id.Contains "/") && not (id.Contains "\\")

/// Pure selection. `ids` names runs; `olderThanDays` selects every finished run at least that old.
/// With neither, nothing is selected and the call is refused: pruning is never "everything" by default.
let select (runs: RunDir list) (ids: string list) (olderThanDays: float option) : Result<Plan, string> =
  match ids, olderThanDays with
  | [], None -> Error "name the runs to delete, or give --older-than-days N (nothing is deleted by default)"
  | _ ->
    let named = ids |> List.map (fun id -> id, runs |> List.tryFind (fun r -> r.Id = id))
    let missing =
      named |> List.choose (fun (id, r) ->
        match validRunId id, r with
        | false, _ -> Some (id, "not a run id")
        | true, None -> Some (id, "no such run under the root")
        | true, Some _ -> None)
    let byName = named |> List.choose snd
    let byAge =
      match olderThanDays with
      | Some days -> runs |> List.filter (fun r -> r.Finished && r.AgeDays >= days)
      | None -> []
    let chosen = (byName @ byAge) |> List.distinctBy _.Id
    let running = chosen |> List.filter (fun r -> not r.Finished) |> List.map (fun r -> r.Id, "no out/summary.json, so it may still be running")
    Ok { Delete = chosen |> List.filter _.Finished
         Skipped = missing @ running }

let private directoryBytes (path: string) : int64 =
  let options = EnumerationOptions(RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint)
  Directory.EnumerateFiles(path, "*", options)
  |> Seq.sumBy (fun f -> try FileInfo(f).Length with _ -> 0L)

let private describe (now: DateTime) (path: string) : RunDir =
  let summary = Path.Combine(path, "out", "summary.json")
  let finished = File.Exists summary
  { Id = Path.GetFileName path
    Path = path
    Bytes = directoryBytes path
    Finished = finished
    AgeDays = if finished then (now - File.GetLastWriteTimeUtc summary).TotalDays else 0.0 }

/// Reads the run root and plans. The root must be absolute and at least two segments deep
/// (/tmp/lem), so `--root /` or `--root /tmp` is refused.
let plan (root: string) (now: DateTime) (ids: string list) (olderThanDays: float option) : Result<Plan, string> =
  let segments = root.Split('/', StringSplitOptions.RemoveEmptyEntries)
  match root.StartsWith "/", segments.Length >= 2, Directory.Exists root with
  | false, _, _ | _, false, _ -> Error (sprintf "'%s' is not a run root: it must be absolute and at least 2 segments deep, like /tmp/lem" root)
  | _, _, false -> Error (sprintf "%s does not exist" root)
  | true, true, true ->
    // Sizing every run is the slow part, so only the runs this call could touch are read.
    let wanted = Set.ofList ids
    let candidates =
      Directory.GetDirectories root
      |> Array.filter (fun d -> let n = Path.GetFileName d in not (n.StartsWith ".") && (olderThanDays.IsSome || wanted.Contains n))
      |> Array.map (describe now)
      |> List.ofArray
    select candidates ids olderThanDays
