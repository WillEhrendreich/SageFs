module SageFs.Features.NotebookExport

type CellMetadata = {
  Index: int
  Label: string option
  Deps: int list
  Bindings: string list
}

module CellMarker =
  let format (meta: CellMetadata) : string =
    let parts = [
      sprintf "@sagefs-cell[%d]" meta.Index
      match meta.Label with Some l -> sprintf "label:%s" l | None -> ()
      match meta.Deps with [] -> () | ds -> ds |> List.map string |> String.concat "," |> sprintf "deps:%s"
      match meta.Bindings with [] -> () | bs -> bs |> String.concat "," |> sprintf "bindings:%s"
    ]
    sprintf "(* %s *)" (parts |> String.concat " ")

  let parse (line: string) : CellMetadata option =
    let trimmed = line.Trim()
    if not (trimmed.StartsWith("(* @sagefs-cell[")) then None
    else
      let content =
        trimmed
          .Replace("(* ", "")
          .Replace(" *)", "")
          .Trim()
      let bracketStart = content.IndexOf('[')
      let bracketEnd = content.IndexOf(']')
      if bracketStart < 0 || bracketEnd < 0 then None
      else
        let indexStr = content.Substring(bracketStart + 1, bracketEnd - bracketStart - 1)
        match System.Int32.TryParse(indexStr) with
        | false, _ -> None
        | true, idx ->
          let rest = content.Substring(bracketEnd + 1).Trim()
          let kvPairs =
            rest.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun kv ->
              let colonIdx = kv.IndexOf(':')
              if colonIdx > 0 then
                (kv.Substring(0, colonIdx), kv.Substring(colonIdx + 1))
              else (kv, ""))
            |> Map.ofArray
          let label = kvPairs |> Map.tryFind "label"
          let deps =
            kvPairs
            |> Map.tryFind "deps"
            |> Option.map (fun d -> d.Split(',') |> Array.choose (fun s -> match System.Int32.TryParse(s) with true, v -> Some v | _ -> None) |> Array.toList)
            |> Option.defaultValue []
          let bindings =
            kvPairs
            |> Map.tryFind "bindings"
            |> Option.map (fun b -> b.Split(',') |> Array.toList)
            |> Option.defaultValue []
          Some { Index = idx; Label = label; Deps = deps; Bindings = bindings }

type NotebookCell = {
  Metadata: CellMetadata
  Code: string
  Output: string option
}

type NotebookHeader = {
  Project: string
  CellCount: int
  Timestamp: string
}

let exportNotebook (header: NotebookHeader) (cells: NotebookCell list) : string =
  let headerComment =
    sprintf "(* @sagefs-notebook project:%s cells:%d timestamp:%s *)" header.Project header.CellCount header.Timestamp
  let cellBlocks =
    cells
    |> List.map (fun cell ->
      let marker = CellMarker.format cell.Metadata
      let output =
        match cell.Output with
        | Some o -> sprintf "\n(* Output:\n%s\n*)" o
        | None -> ""
      sprintf "%s\n%s%s" marker cell.Code output)
  headerComment :: cellBlocks |> String.concat "\n\n"

let importNotebook (fsx: string) : NotebookCell list =
  let lines = fsx.Split('\n') |> Array.toList
  let rec parseCells
    (acc: NotebookCell list)
    (currentMeta: CellMetadata option)
    (currentCode: string list)
    (currentOutput: string option)
    (remainingLines: string list) =
    match remainingLines with
    | [] ->
      match currentMeta with
      | Some meta ->
        let cell = { Metadata = meta; Code = currentCode |> List.rev |> String.concat "\n"; Output = currentOutput }
        (cell :: acc) |> List.rev
      | None -> acc |> List.rev
    | line :: rest ->
      match CellMarker.parse line with
      | Some meta ->
        let acc =
          match currentMeta with
          | Some prevMeta ->
            let cell = { Metadata = prevMeta; Code = currentCode |> List.rev |> String.concat "\n"; Output = currentOutput }
            cell :: acc
          | None -> acc
        parseCells acc (Some meta) [] None rest
      | None ->
        let trimmed = line.Trim()
        if trimmed.StartsWith("(* Output:") then
          let rec collectOutput (outLines: string list) (remaining: string list) =
            match remaining with
            | [] -> (outLines |> List.rev |> String.concat "\n", [])
            | l :: r when l.Trim() = "*)" -> (outLines |> List.rev |> String.concat "\n", r)
            | l :: r -> collectOutput (l :: outLines) r
          let (outputStr, remaining) = collectOutput [] rest
          parseCells acc currentMeta currentCode (Some outputStr) remaining
        elif trimmed.StartsWith("(* @sagefs-notebook") then
          parseCells acc currentMeta currentCode currentOutput rest
        else
          parseCells acc currentMeta (line :: currentCode) currentOutput rest
  parseCells [] None [] None lines
