/// Pure squarified treemap layout (Bruls, Huizing, van Wijk 2000) plus the
/// coverage hierarchy (solution -> project -> file -> symbol) the dashboard's
/// coverage treemap panel packs into it.
///
/// No IO, no Falco, no daemon state: callers (the dashboard's read model)
/// supply already-computed facts and this module only arranges/aggregates
/// them. `squarify` is a direct generic port of the algorithm already proven
/// in production at `Features.LiveTesting.TestTreemap.layout` (per-test
/// duration treemap) — the same shape, generalized over any weighted id.
module SageFs.Features.Treemap

open System

/// A positioned rectangle in layout space; callers scale X/Y/W/H to pixels.
type Rect = { X: float; Y: float; W: float; H: float }

module Rect =
  let area (r: Rect) = r.W * r.H

/// Whether adding the next item to the current strip keeps the worst aspect
/// ratio no worse than before (squarified treemap's greedy strip rule).
let private rowKeep (bestWorst: float) (candidateWorst: float) : bool =
  bestWorst = Double.MaxValue || candidateWorst <= bestWorst

/// Squarified treemap layout: packs weighted items into `bounds` with
/// near-square aspect ratios. A non-positive weight is floored to a small
/// epsilon so every item still gets a (tiny) rectangle instead of
/// disappearing. Pure: no shared mutable state escapes the function.
let squarify (items: ('id * float) list) (bounds: Rect) : ('id * Rect) list =
  match items, bounds.W > 0.0 && bounds.H > 0.0 with
  | [], _ | _, false -> []
  | _, true ->
    let epsilon = 0.001
    let entries =
      items
      |> List.map (fun (id, w) -> id, max w epsilon)
      |> List.sortByDescending snd
      |> List.toArray
    let totalWeight = entries |> Array.sumBy snd
    let totalArea = bounds.W * bounds.H
    let areas = entries |> Array.map (fun (id, w) -> id, w / totalWeight * totalArea)
    let result = ResizeArray<'id * Rect>()
    let mutable x0 = bounds.X
    let mutable y0 = bounds.Y
    let mutable w0 = bounds.W
    let mutable h0 = bounds.H
    let mutable idx = 0
    while idx < areas.Length do
      let isVertical = w0 >= h0
      let side = if isVertical then h0 else w0
      let mutable stripArea = 0.0
      let mutable bestWorst = Double.MaxValue
      let mutable stripEnd = idx
      let mutable improving = true
      while stripEnd < areas.Length && improving do
        stripArea <- stripArea + snd areas.[stripEnd]
        let stripLen = stripArea / side
        let mutable worst = 0.0
        for j in idx .. stripEnd do
          let cellLen = snd areas.[j] / stripLen
          let aspect = max (cellLen / stripLen) (stripLen / cellLen)
          worst <- max worst aspect
        match rowKeep bestWorst worst with
        | true ->
          bestWorst <- worst
          stripEnd <- stripEnd + 1
        | false ->
          stripArea <- stripArea - snd areas.[stripEnd]
          improving <- false
      let stripEnd = if stripEnd = idx then idx + 1 else stripEnd
      let stripLen = stripArea / side
      let mutable offset = 0.0
      for j in idx .. stripEnd - 1 do
        let (id, a) = areas.[j]
        let cellLen = a / stripLen
        match isVertical with
        | true -> result.Add(id, { X = x0; Y = y0 + offset; W = stripLen; H = cellLen })
        | false -> result.Add(id, { X = x0 + offset; Y = y0; W = cellLen; H = stripLen })
        offset <- offset + cellLen
      match isVertical with
      | true -> x0 <- x0 + stripLen; w0 <- w0 - stripLen
      | false -> y0 <- y0 + stripLen; h0 <- h0 - stripLen
      idx <- stripEnd
    result |> List.ofSeq

// --- Coverage hierarchy (solution -> project -> file -> symbol) ---

/// Color-legend status for one treemap region.
/// Covered = green (a passing test proves this), Failed = red (a failing
/// test touches it), Uncovered = grey (the gap — nothing exercises it),
/// Running = amber (in flight, no verdict yet).
[<RequireQualifiedAccess>]
type CoverageStatus =
  | Covered
  | Failed
  | Uncovered
  | Running

[<RequireQualifiedAccess>]
type CoverageNodeKind =
  | Solution
  | Project
  | File
  | Symbol

/// One node in the coverage hierarchy. `Id` is globally unique within the
/// tree and doubles as the drill-down key the dashboard's signal stores.
type CoverageTreemapNode = {
  Id: string
  Name: string
  Kind: CoverageNodeKind
  /// Layout weight (area basis) — probe count for files/projects/solution,
  /// a flat 1.0 per symbol so symbols pack evenly within their file.
  Weight: float
  ProbeCount: int
  CoveredCount: int
  Status: CoverageStatus
  Children: CoverageTreemapNode list
}

/// Raw per-symbol facts the caller (dashboard read model) has already
/// computed from the session's test dependency graph.
type SymbolCoverageFact = {
  SymbolName: string
  Line: int
  TestCount: int
  PassingCount: int
  FailingCount: int
}

/// Raw per-file facts the caller has already computed from the session's
/// merged instrumentation map + coverage bitmaps.
type FileCoverageFact = {
  FilePath: string
  ProjectName: string
  ProbeCount: int
  CoveredCount: int
  HasFailingTest: bool
  Symbols: SymbolCoverageFact list
}

module CoverageTreemapNode =

  let private statusFromCounts (probeCount: int) (coveredCount: int) (hasFailing: bool) : CoverageStatus =
    match hasFailing with
    | true -> CoverageStatus.Failed
    | false ->
      match probeCount, coveredCount with
      | 0, _ -> CoverageStatus.Uncovered
      | _, c when c <= 0 -> CoverageStatus.Uncovered
      | _ -> CoverageStatus.Covered

  let private statusRank = function
    | CoverageStatus.Failed -> 0
    | CoverageStatus.Running -> 1
    | CoverageStatus.Uncovered -> 2
    | CoverageStatus.Covered -> 3

  /// Aggregate a parent's status from its children: red if any child failed,
  /// amber if any (non-failed) child is still running, grey only if every
  /// child is uncovered, green otherwise (real, passing coverage exists
  /// somewhere under this node).
  let rollupStatus (children: CoverageTreemapNode list) : CoverageStatus =
    match children with
    | [] -> CoverageStatus.Uncovered
    | _ ->
      children
      |> List.map (fun c -> c.Status)
      |> List.minBy statusRank
      |> function
        | CoverageStatus.Uncovered when children |> List.forall (fun c -> c.Status = CoverageStatus.Uncovered) ->
          CoverageStatus.Uncovered
        | CoverageStatus.Uncovered -> CoverageStatus.Covered
        | worst -> worst

  let private symbolNode (filePath: string) (f: SymbolCoverageFact) : CoverageTreemapNode =
    let status =
      match f.FailingCount > 0, f.PassingCount > 0, f.TestCount > 0 with
      | true, _, _ -> CoverageStatus.Failed
      | _, true, _ -> CoverageStatus.Covered
      | _, _, true -> CoverageStatus.Running
      | _ -> CoverageStatus.Uncovered
    { Id = sprintf "%s::%s" filePath f.SymbolName
      Name = f.SymbolName
      Kind = CoverageNodeKind.Symbol
      Weight = 1.0
      ProbeCount = f.TestCount
      CoveredCount = f.PassingCount
      Status = status
      Children = [] }

  let private fileNode (f: FileCoverageFact) : CoverageTreemapNode =
    let symbolChildren = f.Symbols |> List.map (symbolNode f.FilePath)
    let ownStatus = statusFromCounts f.ProbeCount f.CoveredCount f.HasFailingTest
    let status =
      match symbolChildren with
      | [] -> ownStatus
      | _ ->
        [ ownStatus; rollupStatus symbolChildren ]
        |> List.minBy statusRank
    { Id = f.FilePath
      Name = IO.Path.GetFileName f.FilePath
      Kind = CoverageNodeKind.File
      Weight = float (max f.ProbeCount 1)
      ProbeCount = f.ProbeCount
      CoveredCount = f.CoveredCount
      Status = status
      Children = symbolChildren }

  let private projectNode (projectName: string) (files: FileCoverageFact list) : CoverageTreemapNode =
    let fileChildren =
      files |> List.map fileNode |> List.sortByDescending (fun n -> n.Weight)
    { Id = projectName
      Name = projectName
      Kind = CoverageNodeKind.Project
      Weight = fileChildren |> List.sumBy (fun n -> n.Weight)
      ProbeCount = files |> List.sumBy (fun f -> f.ProbeCount)
      CoveredCount = files |> List.sumBy (fun f -> f.CoveredCount)
      Status = rollupStatus fileChildren
      Children = fileChildren }

  /// Build the whole solution -> project -> file -> symbol tree from flat
  /// per-file facts. Pure — the caller does all the IO/state lookups.
  let build (solutionName: string) (facts: FileCoverageFact list) : CoverageTreemapNode =
    let projectChildren =
      facts
      |> List.groupBy (fun f -> f.ProjectName)
      |> List.map (fun (name, files) -> projectNode name files)
      |> List.sortByDescending (fun n -> n.Weight)
    { Id = solutionName
      Name = solutionName
      Kind = CoverageNodeKind.Solution
      Weight = projectChildren |> List.sumBy (fun n -> n.Weight)
      ProbeCount = facts |> List.sumBy (fun f -> f.ProbeCount)
      CoveredCount = facts |> List.sumBy (fun f -> f.CoveredCount)
      Status = rollupStatus projectChildren
      Children = projectChildren }

  /// Find a node anywhere in the tree by its unique `Id` (drill-down lookup).
  let rec tryFind (id: string) (node: CoverageTreemapNode) : CoverageTreemapNode option =
    match node.Id = id with
    | true -> Some node
    | false -> node.Children |> List.tryPick (tryFind id)

  /// Squarify one node's immediate children into `bounds` — the drill-down
  /// UI only ever lays out the level currently being viewed.
  let layoutChildren (bounds: Rect) (node: CoverageTreemapNode) : (CoverageTreemapNode * Rect) list =
    match node.Children with
    | [] -> []
    | children -> squarify (children |> List.map (fun c -> c, c.Weight)) bounds

  /// Resolve a project name for a file path from the session's known
  /// project directories (longest-prefix match), falling back to "External"
  /// for files outside every known project (e.g. generated/obj sources).
  let projectNameForFile (projectDirs: (string * string) list) (filePath: string) : string =
    let norm (p: string) = p.Replace('\\', '/').TrimEnd('/')
    let target = norm filePath
    projectDirs
    |> List.map (fun (name, dir) -> name, norm dir)
    |> List.filter (fun (_, dir) -> dir.Length > 0 && target.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
    |> List.sortByDescending (fun (_, dir) -> dir.Length)
    |> List.tryHead
    |> Option.map fst
    |> Option.defaultValue "External"
