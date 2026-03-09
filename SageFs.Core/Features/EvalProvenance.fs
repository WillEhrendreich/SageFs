module SageFs.Features.EvalProvenance

open SageFs.Features.CellDependencyGraph
open SageFs.Features.LiveTesting

/// Whether a cell's output is fresh or stale due to upstream changes.
[<Struct>]
type Staleness =
  | Fresh
  | StaleUpstream of upstreamCellIds: CellId list

module Staleness =
  /// Human-readable description of staleness.
  let describe = function
    | Fresh -> "up-to-date"
    | StaleUpstream ids ->
      match ids.Length with
      | 1 -> sprintf "stale: 1 upstream cell changed (cell %d)" ids.[0]
      | n -> sprintf "stale: %d upstream cells changed (%s)" n (ids |> List.map string |> String.concat ", ")

/// Provenance information for a single cell.
[<Struct>]
type EvalProvenance = {
  CellId: CellId
  DependsOn: CellId list
  Staleness: Staleness
}

module EvalProvenance =
  /// Compute provenance for a cell given the dependency graph and set of recently changed cells.
  let compute (graph: CellGraph) (cellId: CellId) (changedCells: Set<CellId>) : EvalProvenance =
    let deps =
      graph.Edges
      |> List.choose (fun (producer, consumer) ->
        match consumer = cellId with
        | true -> Some producer
        | false -> None)
      |> List.distinct
    let staleUpstream =
      match changedCells.IsEmpty with
      | true -> []
      | false ->
        changedCells
        |> Set.toList
        |> List.collect (fun changed ->
          transitiveStale graph changed
          |> List.filter (fun id -> id = cellId)
          |> List.map (fun _ -> changed))
        |> List.distinct
    let staleness =
      match staleUpstream with
      | [] -> Fresh
      | ids -> StaleUpstream ids
    { CellId = cellId; DependsOn = deps; Staleness = staleness }

  /// Create a LineAnnotation for a stale cell. Returns the annotation unconditionally.
  let toAnnotation (line: int) (prov: EvalProvenance) : LineAnnotation =
    let tooltip =
      match prov.Staleness with
      | Fresh -> "cell is up-to-date"
      | StaleUpstream ids ->
        sprintf "stale: depends on changed cells %s" (ids |> List.map string |> String.concat ", ")
    { Line = line; Icon = GutterIcon.CellStale; Tooltip = tooltip }

  /// Create a LineAnnotation only if the cell is stale (None for fresh).
  let tryAnnotation (line: int) (prov: EvalProvenance) : LineAnnotation option =
    match prov.Staleness with
    | Fresh -> None
    | StaleUpstream _ -> Some (toAnnotation line prov)
