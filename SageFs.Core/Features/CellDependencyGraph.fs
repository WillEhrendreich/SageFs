module SageFs.Features.CellDependencyGraph

type CellId = int

type CellInfo = {
  Id: CellId
  Source: string
  Produces: string list
  Consumes: string list
}

type CellGraph = {
  Cells: Map<CellId, CellInfo>
  Edges: (CellId * CellId) list
}

let analyzeCell (knownBindings: Map<string, CellId>) (cellId: CellId) (source: string) (fsiOutput: string) : CellInfo =
  let produces =
    fsiOutput.Split('\n')
    |> Array.choose (fun line ->
      let trimmed = line.Trim()
      if trimmed.StartsWith("val ") then
        let nameEnd = trimmed.IndexOfAny([| ':'; ' ' |], 4)
        if nameEnd > 4 then Some (trimmed.Substring(4, nameEnd - 4))
        else None
      else None)
    |> Array.toList
  let consumes =
    knownBindings
    |> Map.toList
    |> List.choose (fun (name, producerCellId) ->
      if producerCellId <> cellId && source.Contains(name) then Some name
      else None)
  { Id = cellId; Source = source; Produces = produces; Consumes = consumes }

let buildGraph (cells: CellInfo list) : CellGraph =
  let bindingToCell =
    cells
    |> List.collect (fun c -> c.Produces |> List.map (fun b -> (b, c.Id)))
    |> Map.ofList
  let edges =
    cells
    |> List.collect (fun consumer ->
      consumer.Consumes
      |> List.choose (fun binding ->
        bindingToCell
        |> Map.tryFind binding
        |> Option.map (fun producerId -> (producerId, consumer.Id))))
    |> List.distinct
  { Cells = cells |> List.map (fun c -> (c.Id, c)) |> Map.ofList
    Edges = edges }

let transitiveStale (graph: CellGraph) (changedCellId: CellId) : CellId list =
  let adjacency =
    graph.Edges
    |> List.groupBy fst
    |> List.map (fun (k, vs) -> (k, vs |> List.map snd))
    |> Map.ofList
  let rec bfs visited queue =
    match queue with
    | [] -> visited |> Set.toList
    | current :: rest ->
      if Set.contains current visited then
        bfs visited rest
      else
        let neighbors =
          adjacency
          |> Map.tryFind current
          |> Option.defaultValue []
          |> List.filter (fun n -> not (Set.contains n visited))
        bfs (Set.add current visited) (rest @ neighbors)
  let directDependents =
    adjacency |> Map.tryFind changedCellId |> Option.defaultValue []
  bfs Set.empty directDependents
  |> List.filter (fun id -> id <> changedCellId)
