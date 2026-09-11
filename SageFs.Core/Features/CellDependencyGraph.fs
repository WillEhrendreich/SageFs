module SageFs.Features.CellDependencyGraph

open System.Collections.Generic

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

/// Returns true if `name` appears as a whole identifier in `source`
/// (not as a substring of a longer word/qualified name).
let private containsIdentifier (name: string) (source: string) =
  IdentifierScan.occursAsFreeIdentifier name source

/// Names bound by the `val` lines of FSI output, in output order. The name
/// runs from after `val ` to the first ':' or space.
let producedNames (fsiOutput: string) : string list =
  fsiOutput.Split('\n')
  |> Array.choose (fun line ->
    let trimmed = line.Trim()
    match trimmed.StartsWith("val ") with
    | true ->
      let nameEnd = trimmed.IndexOfAny([| ':'; ' ' |], 4)
      match nameEnd > 4 with
      | true -> Some (trimmed.Substring(4, nameEnd - 4))
      | false -> None
    | false -> None)
  |> Array.toList

let analyzeCell (knownBindings: Map<string, CellId>) (cellId: CellId) (source: string) (fsiOutput: string) : CellInfo =
  let produces = producedNames fsiOutput
  let consumes =
    knownBindings
    |> Map.toList
    |> List.choose (fun (name, producerCellId) ->
      if producerCellId <> cellId && containsIdentifier name source then Some name
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
  let queue = Queue<CellId>()
  adjacency
  |> Map.tryFind changedCellId
  |> Option.defaultValue []
  |> List.iter queue.Enqueue
  let visited = System.Collections.Generic.HashSet<CellId>()
  while queue.Count > 0 do
    let current = queue.Dequeue()
    if visited.Add(current) then
      adjacency
      |> Map.tryFind current
      |> Option.defaultValue []
      |> List.iter (fun n -> if not (visited.Contains n) then queue.Enqueue n)
  visited |> Seq.filter (fun id -> id <> changedCellId) |> Seq.toList
