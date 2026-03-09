namespace SageFs.Features

open SageFs.Features.CellDependencyGraph

/// Status of a single step in a ripple re-evaluation plan.
type RippleStatus =
  | Pending
  | Evaluating
  | Succeeded of output: string
  | Failed of error: string
  | Skipped of reason: string

/// A single step in the ripple plan.
type RippleStep = {
  CellId: CellId
  Code: string
  Status: RippleStatus
}

/// An ordered plan for cascade re-evaluation.
type RipplePlan = {
  ChangedCells: Set<CellId>
  Steps: RippleStep list
}

module EvalRipple =

  /// Topological sort of cell IDs using DFS post-order over dependency edges.
  let private toposort (graph: CellGraph) (cellIds: CellId list) : CellId list =
    let depAdj =
      graph.Edges
      |> List.fold (fun (acc: Map<CellId, CellId list>) (producer, consumer) ->
        let existing = acc |> Map.tryFind consumer |> Option.defaultValue []
        acc |> Map.add consumer (producer :: existing)) Map.empty
    let cellSet = set cellIds
    let mutable visited = Set.empty
    let mutable result = []
    let rec visit (id: CellId) =
      match visited |> Set.contains id with
      | true -> ()
      | false ->
        visited <- visited |> Set.add id
        let deps =
          depAdj
          |> Map.tryFind id
          |> Option.defaultValue []
          |> List.filter (fun d -> cellSet |> Set.contains d)
        deps |> List.iter visit
        result <- id :: result
    cellIds |> List.iter visit
    result |> List.rev

  /// Plan a ripple re-evaluation from a set of changed cells.
  let planRipple (graph: CellGraph) (changedCells: Set<CellId>) : RipplePlan =
    let allStale =
      changedCells
      |> Set.toList
      |> List.collect (fun cid -> CellDependencyGraph.transitiveStale graph cid)
      |> List.distinct
      |> List.filter (fun cid -> changedCells |> Set.contains cid |> not)
    let sorted = toposort graph allStale
    let steps =
      sorted
      |> List.choose (fun cid ->
        graph.Cells
        |> Map.tryFind cid
        |> Option.map (fun cell ->
          { CellId = cid; Code = cell.Source; Status = Pending }))
    { ChangedCells = changedCells; Steps = steps }

  /// Advance a step by recording its evaluation result.
  /// If the step failed, downstream dependents are marked as Skipped.
  let advanceStep (cellId: CellId) (result: Result<string, string>) (plan: RipplePlan) : RipplePlan =
    let failedSet =
      match result with
      | Error _ -> set [cellId]
      | Ok _ -> Set.empty
    let updatedSteps =
      plan.Steps
      |> List.map (fun step ->
        match step.CellId = cellId with
        | true ->
          match result with
          | Ok output -> { step with Status = Succeeded output }
          | Error err -> { step with Status = Failed err }
        | false ->
          match step.Status with
          | Pending when failedSet |> Set.contains cellId |> not -> step
          | Pending ->
            { step with Status = Skipped (sprintf "upstream %d failed" cellId) }
          | _ -> step)
    { plan with Steps = updatedSteps }

  /// Render a single step as a human-readable string.
  let renderStep (step: RippleStep) : string =
    let icon =
      match step.Status with
      | Pending -> "⏳"
      | Evaluating -> "🔄"
      | Succeeded _ -> "✅"
      | Failed _ -> "❌"
      | Skipped _ -> "⏭"
    sprintf "%s [%d] %s" icon step.CellId step.Code

  /// Summary of a ripple plan execution.
  let summary (plan: RipplePlan) : string =
    let count status =
      plan.Steps |> List.filter (fun s -> match s.Status, status with
                                          | Pending, Pending -> true
                                          | Succeeded _, Succeeded "" -> true
                                          | Failed _, Failed "" -> true
                                          | Skipped _, Skipped "" -> true
                                          | Evaluating, Evaluating -> true
                                          | _ -> false) |> List.length
    let parts =
      [ count (Succeeded ""), "succeeded"
        count (Failed ""), "failed"
        count (Skipped ""), "skipped"
        count Pending, "pending" ]
      |> List.filter (fun (n, _) -> n > 0)
      |> List.map (fun (n, label) -> sprintf "%d %s" n label)
    match parts with
    | [] -> "no steps"
    | _ -> parts |> String.concat ", "
