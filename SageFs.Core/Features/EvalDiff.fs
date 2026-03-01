module SageFs.Features.EvalDiff

type DiffLine =
  | Unchanged of string
  | Added of string
  | Removed of string
  | Modified of old: string * current: string

type DiffSummary = {
  Lines: DiffLine list
  AddedCount: int
  RemovedCount: int
  ModifiedCount: int
  UnchangedCount: int
}

let splitLines (s: string) =
  if System.String.IsNullOrEmpty(s) then []
  else s.Split('\n') |> Array.toList

let diffLines (oldOutput: string option) (newOutput: string option) : DiffLine list =
  match oldOutput, newOutput with
  | None, None -> []
  | None, Some n ->
    splitLines n |> List.map Added
  | Some o, None ->
    splitLines o |> List.map Removed
  | Some o, Some n when o = n ->
    splitLines o |> List.map Unchanged
  | Some o, Some n ->
    let oldLines = splitLines o |> Array.ofList
    let newLines = splitLines n |> Array.ofList
    let maxLen = max oldLines.Length newLines.Length
    [ for i in 0 .. maxLen - 1 do
        let oldLine = if i < oldLines.Length then Some oldLines.[i] else None
        let newLine = if i < newLines.Length then Some newLines.[i] else None
        match oldLine, newLine with
        | Some ol, Some nl when ol = nl -> Unchanged ol
        | Some ol, Some nl -> Modified(ol, nl)
        | None, Some nl -> Added nl
        | Some ol, None -> Removed ol
        | None, None -> () ]

let summarize (lines: DiffLine list) : DiffSummary =
  { Lines = lines
    AddedCount = lines |> List.sumBy (function Added _ -> 1 | _ -> 0)
    RemovedCount = lines |> List.sumBy (function Removed _ -> 1 | _ -> 0)
    ModifiedCount = lines |> List.sumBy (function Modified _ -> 1 | _ -> 0)
    UnchangedCount = lines |> List.sumBy (function Unchanged _ -> 1 | _ -> 0) }

let formatDiffLine (line: DiffLine) : string =
  match line with
  | Unchanged s -> sprintf "  %s" s
  | Added s -> sprintf "+ %s" s
  | Removed s -> sprintf "- %s" s
  | Modified(old, cur) -> sprintf "~ %s → %s" old cur

let formatSummary (summary: DiffSummary) : string =
  let parts =
    [ if summary.AddedCount > 0 then sprintf "%d added" summary.AddedCount
      if summary.RemovedCount > 0 then sprintf "%d removed" summary.RemovedCount
      if summary.ModifiedCount > 0 then sprintf "%d modified" summary.ModifiedCount ]
  match parts with
  | [] -> "no changes"
  | _ -> parts |> String.concat ", " |> sprintf "Δ %s"

/// For property testing: applying a diff to old output should produce new output
let applyDiff (oldLines: string list) (diff: DiffLine list) : string list =
  diff |> List.collect (function
    | Unchanged s -> [s]
    | Added s -> [s]
    | Removed _ -> []
    | Modified(_, cur) -> [cur])
