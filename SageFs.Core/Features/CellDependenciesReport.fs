module SageFs.Features.CellDependenciesReport

open SageFs.Features.CellDependencyGraph

/// Whether a cell needs re-evaluation.
[<RequireQualifiedAccess>]
type CellFreshness =
  | Fresh
  | StaleFrom of causes: CellId list

module CellFreshness =
  let isStale = function
    | CellFreshness.Fresh -> false
    | CellFreshness.StaleFrom _ -> true
  let causes = function
    | CellFreshness.Fresh -> []
    | CellFreshness.StaleFrom cs -> cs

/// A single cell annotated with dependency and staleness information.
type CellNode = {
  /// Cell identifier.
  Id: CellId
  /// Bindings this cell produces.
  Produces: string list
  /// Bindings this cell consumes from other cells.
  Consumes: string list
  /// IDs of cells that depend on this cell (downstream consumers).
  DownstreamIds: CellId list
  /// IDs of cells this cell depends on (upstream producers).
  UpstreamIds: CellId list
  /// Whether this cell is fresh or stale (and which upstream cells caused it).
  Staleness: CellFreshness
}

/// Summary of the cell dependency graph with full staleness annotation.
type CellDependencyReport = {
  /// All nodes in the graph.
  Nodes: CellNode list
  /// Total number of cells.
  TotalCells: int
  /// Number of currently stale cells.
  TotalStale: int
  /// IDs of all stale cells.
  StaleCellIds: CellId list
  /// Total number of dependency edges.
  TotalEdges: int
  /// Human-readable summary.
  Summary: string
}

module CellDependenciesReport =

  let private buildNode (graph: CellGraph) (staleCells: Set<CellId>) (id: CellId) (info: CellInfo) : CellNode =
    let downstreamIds =
      graph.Edges |> List.choose (fun (prod, cons) -> if prod = id then Some cons else None)
    let upstreamIds =
      graph.Edges |> List.choose (fun (prod, cons) -> if cons = id then Some prod else None)
    let staleness =
      match staleCells.Contains id with
      | true -> CellFreshness.StaleFrom (upstreamIds |> List.filter staleCells.Contains)
      | false -> CellFreshness.Fresh
    { Id = id
      Produces = info.Produces
      Consumes = info.Consumes
      DownstreamIds = downstreamIds
      UpstreamIds = upstreamIds
      Staleness = staleness }

  /// Human-readable one-line summary of the report.
  let summarize (r: CellDependencyReport) =
    let staleMsg =
      match r.TotalStale with
      | 0 -> "all fresh ✅"
      | n -> $"{n}/{r.TotalCells} stale ⚠️"
    $"📊 {r.TotalCells} cell(s), {r.TotalEdges} edge(s) — {staleMsg}"

  /// Compose a CellDependencyReport from a CellGraph and a set of recently-changed cells.
  /// All cells transitively downstream of any changed cell are marked stale.
  let compose (graph: CellGraph) (changedCells: Set<CellId>) : CellDependencyReport =
    let transitiveStaleSet =
      changedCells
      |> Set.toList
      |> List.collect (transitiveStale graph)
      |> Set.ofList
    let allStale = Set.union changedCells transitiveStaleSet
    let nodes =
      graph.Cells
      |> Map.toList
      |> List.map (fun (id, info) -> buildNode graph allStale id info)
      |> List.sortBy (fun n -> n.Id)
    let staleIds =
      nodes
      |> List.choose (fun n ->
        match CellFreshness.isStale n.Staleness with
        | true -> Some n.Id
        | false -> None)
    let r = {
      Nodes = nodes
      TotalCells = nodes.Length
      TotalStale = staleIds.Length
      StaleCellIds = staleIds
      TotalEdges = graph.Edges.Length
      Summary = ""
    }
    { r with Summary = summarize r }
