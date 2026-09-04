module SageFs.Features.CellDependencyGraph

open System.Text.RegularExpressions
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
  Regex.IsMatch(source, @"(?<![.\w])" + Regex.Escape(name) + @"(?![.\w])")

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

/// Incrementally append one newly-evaluated cell to a graph, producing the
/// SAME graph a full rebuild from the same history would (roast item 8 —
/// the feature push rebuilt the whole ≤10k-cell graph on every eval).
///
/// Full-rebuild semantics re-analyze every retained cell against the LATEST
/// knownBindings and resolve each cell's frozen Consumes through the newest
/// producer of each binding. Appending a cell that redefines a binding B
/// therefore retargets to it every prior cell that references B: consumers
/// whose Consumes lists B, AND the old producer of B (its defining source
/// occurrence of B stops being self-consumption once a newer producer
/// exists). The equivalence property tests prove the result identical to a
/// full rebuild, including redefinition chains and shadowed bindings.
let appendCell (knownBindings: Map<string, CellId>) (prior: CellGraph) (cellId: CellId) (source: string) (fsiOutput: string) : CellGraph =
  let info = analyzeCell knownBindings cellId source fsiOutput
  // Consumers of THIS cell: each consumed binding -> its producer.
  // analyzeCell already excludes self-consumption (producerCellId <> cellId).
  let ownEdges =
    info.Consumes
    |> List.choose (fun name ->
      knownBindings
      |> Map.tryFind name
      |> Option.map (fun producerId -> (producerId, cellId)))
    |> List.distinct
  // Bindings this cell produces that already had a (different) producer.
  let oldProducerOf (b: string) : CellId option =
    prior.Cells
    |> Map.toSeq
    |> Seq.tryPick (fun (_, c) -> if c.Produces |> List.contains b then Some c.Id else None)
  let redefines =
    info.Produces
    |> List.choose (fun b ->
      oldProducerOf b |> Option.map (fun oldProducer -> (b, oldProducer)))
  // Full-rebuild semantics re-analyze every retained cell against the LATEST
  // knownBindings. Two prior-cell kinds therefore gain an edge to the new
  // producer of a redefined binding B:
  //   1. cells whose analyzed Consumes contains B (they reference B), and
  //   2. cells whose SOURCE contains B but were the OLD producer of B — the
  //      analyzer's whole-identifier regex sees the defining occurrence in
  //      their source, and against the new binding map that occurrence is no
  //      longer self-consumption (producerCellId <> cellId).
  // Both are exactly the cells a full rebuild edges to the new producer.
  let priorCellConsumes (cid: CellId) (b: string) =
    prior.Cells
    |> Map.tryFind cid
    |> Option.map (fun c -> c.Consumes |> List.contains b)
    |> Option.defaultValue false
  let redefinitionTargets =
    prior.Cells
    |> Map.toSeq
    |> Seq.choose (fun (cid, c) ->
      redefines
      |> List.tryFind (fun (b, oldProducer) ->
        (priorCellConsumes cid b) || (cid = oldProducer && containsIdentifier b c.Source))
      |> Option.map (fun _ -> (cellId, cid)))
    |> Seq.toList
  // Edges that survive untouched: any prior edge not involving a redefined
  // binding's old producer as the source of a retargeted consumer edge.
  // Every prior edge whose consumer is a redefinition target is replaced by
  // redefinitionTargets; every prior edge FROM an old producer of a
  // redefined binding whose consumer references the binding is likewise
  // retargeted (its source no longer produces the binding in a full rebuild —
  // the newest producer does). Simplest correct rule: drop prior edges whose
  // consumer is a redefinition target OR whose source is a redefined binding's
  // old producer, then add the fresh edges.
  let retargetedConsumerIds = redefinitionTargets |> List.map snd |> Set.ofList
  let staleProducerIds = redefines |> List.map snd |> Set.ofList
  let survives (fromId: CellId, toId: CellId) =
    not (Set.contains toId retargetedConsumerIds)
    && not (Set.contains fromId staleProducerIds)
  // A full rebuild re-analyzes every retained cell against the LATEST
  // knownBindings, so prior cells that reference a redefined binding gain
  // that binding in their Consumes annotation. Update those cells so the
  // node annotations stay identical to a full rebuild too.
  let updatedPriorCells =
    prior.Cells
    |> Map.map (fun cid c ->
      let gained =
        redefines
        |> List.choose (fun (b, oldProducer) ->
          if (priorCellConsumes cid b) || (cid = oldProducer && containsIdentifier b c.Source) then
            Some b
          else None)
      match gained with
      | [] -> c
      | g ->
        let missing = g |> List.filter (fun b -> not (c.Consumes |> List.contains b))
        match missing with
        | [] -> c
        | m -> { c with Consumes = c.Consumes @ m })
  { Cells = updatedPriorCells |> Map.add cellId info
    Edges =
      (prior.Edges |> List.filter survives) @ redefinitionTargets @ ownEdges
      |> List.distinct }

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
