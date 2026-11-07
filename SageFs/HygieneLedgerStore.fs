/// The hygiene ledger on disk: one JSON object per line in `<data dir>/hygiene-ledger.jsonl`, appended and never rewritten. The
/// decisions about what the lines mean are `HygieneLedger` (pure, Core); this is the file.
///
/// Append-only on purpose. A restore point is only worth having if nothing the daemon does later can lose it, so there is no
/// rewrite, no trim and no rotation here: the file grows with what happens (a line when a worktree first appears, one when its
/// activity changes, one per removal), which is a few lines a day.
module SageFs.HygieneLedgerStore

open System
open System.IO
open SageFs.HygieneLedger

let fileName = "hygiene-ledger.jsonl"

let private gate = obj ()

let path (dataDir: string) : string = Path.Combine(dataDir, fileName)

/// Every line that can be read, in the order written. A missing file is an empty ledger; a line that cannot be read is skipped.
let read (dataDir: string) : LedgerLine list =
  try
    match File.Exists(path dataDir) with
    | false -> []
    | true -> lock gate (fun () -> File.ReadAllLines(path dataDir)) |> Ledger.decodeAll
  with
  | :? IOException
  | :? UnauthorizedAccessException -> []

/// Append lines. Best effort for the caller that only wants a record (`append`), and `tryAppend` for one that must know.
let tryAppend (dataDir: string) (lines: LedgerLine list) : Result<unit, string> =
  match lines with
  | [] -> Result.Ok()
  | _ ->
    try
      lock gate (fun () ->
        Directory.CreateDirectory dataDir |> ignore
        File.AppendAllLines(path dataDir, lines |> List.map Ledger.encode))
      Result.Ok()
    with
    | :? IOException as ex -> Result.Error ex.Message
    | :? UnauthorizedAccessException as ex -> Result.Error ex.Message

let append (dataDir: string) (lines: LedgerLine list) : unit = tryAppend dataDir lines |> ignore

let records (dataDir: string) : Map<string, WorktreeRecord> = read dataDir |> Ledger.fold

/// When each worktree the ledger knows (and that has not been removed) was last in use, for the scan.
let lastActive (dataDir: string) (now: DateTime) : (string * DateTime) list =
  records dataDir
  |> Map.toList
  |> List.filter (fun (_, r) ->
    match r.Presence with
    | Presence.OnDisk -> true
    | Presence.RemovedByHygiene -> false)
  |> List.map (fun (p, r) -> p, Ledger.lastActive now r)
