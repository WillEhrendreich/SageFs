/// Clearing finished runs out of the run root. The run root is tmpfs, so a run that keeps its working
/// copy, its NuGet folder and its sandbox scratch there is RAM nobody gets back. A finished run keeps
/// only its evidence (out/: summary.json, timeline.ndjson, shots, logs); everything else is removed,
/// by the run itself when it ends and by `prune-runs` for older ones. Only a run that has a
/// summary.json is ever touched, because one without may still be running.
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
    AgeDays: float
    /// What is under out/, the part a finished run keeps.
    EvidenceBytes: int64 }

/// What pruning a run takes away.
type Reclaim =
  /// Everything except out/. The default: the evidence stays.
  | KeepEvidence
  /// The whole run directory, evidence included.
  | DeleteWhole

/// How much a prune frees.
let freedBytes (reclaim: Reclaim) (run: RunDir) : int64 =
  match reclaim with
  | KeepEvidence -> run.Bytes - run.EvidenceBytes
  | DeleteWhole -> run.Bytes

/// The directory a finished run keeps.
let evidenceDirectory = "out"

/// Files the harness staged INSIDE out/ to run the editor (not evidence of anything): the copy of the F#
/// tree-sitter parser, about 12 MB. Removed with the rest, so a pruned run is a few MB at most.
let stagingInsideEvidence = [ "ui/ts" ]

/// What marks a run as over: a lemming run writes summary.json, a tour (which has no lemming and no
/// score) writes tour.json. Without one a run may still be going and is never touched.
let finishedMarkers = [ "summary.json"; "tour.json" ]

let private newestMarker (runDir: string) : string option =
  finishedMarkers
  |> List.map (fun name -> Path.Combine(runDir, evidenceDirectory, name))
  |> List.filter File.Exists
  |> List.sortByDescending File.GetLastWriteTimeUtc
  |> List.tryHead

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
  match Directory.Exists path with
  | false -> 0L
  | true ->
    let options = EnumerationOptions(RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint)
    Directory.EnumerateFiles(path, "*", options)
    |> Seq.sumBy (fun f -> try FileInfo(f).Length with _ -> 0L)

/// What a pruned run still holds: out/, without the editor staging inside it.
let private keptBytes (runDir: string) : int64 =
  directoryBytes (Path.Combine(runDir, evidenceDirectory))
  - (stagingInsideEvidence |> List.sumBy (fun s -> directoryBytes (Path.Combine(runDir, evidenceDirectory, s))))

let private describe (now: DateTime) (path: string) : RunDir =
  let marker = newestMarker path
  let finished = marker.IsSome
  { Id = Path.GetFileName path
    Path = path
    Bytes = directoryBytes path
    Finished = finished
    AgeDays = (match marker with Some m -> (now - File.GetLastWriteTimeUtc m).TotalDays | None -> 0.0)
    EvidenceBytes = keptBytes path }

/// Removes everything in a finished run's directory except out/, and returns the bytes it freed. A
/// run with no out/summary.json is refused (it may still be running), and so is a path that is not
/// absolute and at least three segments deep, so this can never be pointed at a root.
let slim (runDir: string) : Result<int64, string> =
  let segments = runDir.Split('/', StringSplitOptions.RemoveEmptyEntries)
  match runDir.StartsWith "/", segments.Length >= 3, (newestMarker runDir).IsSome with
  | false, _, _ | _, false, _ -> Error (sprintf "'%s' is not a run directory (absolute, at least 3 segments deep)" runDir)
  | _, _, false -> Error (sprintf "%s has no out/summary.json (or out/tour.json), so it may still be running" runDir)
  | true, true, true ->
    let before = directoryBytes runDir
    let remove (entry: string) =
      match Directory.Exists entry with
      | true ->
        // A tree a lemming's build or a read-only mount left unwritable still has to go.
        match Diagnostics.Process.Start(Diagnostics.ProcessStartInfo("chmod", [ "-R"; "u+w"; entry ], UseShellExecute = false)) with
        | null -> ()
        | chmod ->
          chmod.WaitForExit()
          chmod.Dispose()
        Directory.Delete(entry, true)
      | false -> File.Delete entry
    try
      Directory.EnumerateFileSystemEntries runDir
      |> Seq.filter (fun e -> Path.GetFileName e <> evidenceDirectory)
      |> Seq.iter remove
      stagingInsideEvidence |> List.map (fun s -> Path.Combine(runDir, evidenceDirectory, s)) |> List.filter Directory.Exists |> List.iter remove
      Ok (before - directoryBytes (Path.Combine(runDir, evidenceDirectory)))
    with ex -> Error (sprintf "could not slim %s: %s" runDir ex.Message)

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
