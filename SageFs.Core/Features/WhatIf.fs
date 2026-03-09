namespace SageFs.Features

open SageFs.Features.CellDependencyGraph

/// A hypothetical binding override for "what if" exploration.
type WhatIfOverride = {
  BindingName: string
  OriginalValue: string
  OverrideValue: string
  TypeSig: string
}

/// Plan for a what-if scenario: which cells would be affected.
type WhatIfPlan = {
  Override: WhatIfOverride
  AffectedCells: CellId list
  RippleSteps: RippleStep list
}

/// Result of executing a what-if scenario.
type WhatIfDiffResult = {
  Override: WhatIfOverride
  OriginalOutputs: (CellId * string) list
  NewOutputs: (CellId * string) list
}

module WhatIf =

  /// Create a what-if override.
  let createOverride (name: string) (original: string) (newValue: string) (typeSig: string) : WhatIfOverride =
    { BindingName = name; OriginalValue = original; OverrideValue = newValue; TypeSig = typeSig }

  /// Format the override as a let binding for FSI evaluation.
  let formatOverrideAsLet (ov: WhatIfOverride) : string =
    sprintf "let %s = %s" ov.BindingName ov.OverrideValue

  /// Format an override for display.
  let formatOverride (ov: WhatIfOverride) : string =
    sprintf "%s: %s → %s" ov.BindingName ov.OriginalValue ov.OverrideValue

  /// Find the cell that produces a binding.
  let private findProducerCell (graph: CellGraph) (bindingName: string) : CellId option =
    graph.Cells
    |> Map.tryPick (fun id cell ->
      match cell.Produces |> List.contains bindingName with
      | true -> Some id
      | false -> None)

  /// Plan a what-if scenario: identify affected cells and build a ripple plan.
  let planWhatIf (graph: CellGraph) (override': WhatIfOverride) : WhatIfPlan =
    match findProducerCell graph override'.BindingName with
    | None ->
      { Override = override'; AffectedCells = []; RippleSteps = [] }
    | Some producerId ->
      let ripple = EvalRipple.planRipple graph (set [producerId])
      { Override = override'
        AffectedCells = ripple.Steps |> List.map (fun s -> s.CellId)
        RippleSteps = ripple.Steps }

  /// Format a what-if diff result.
  let formatDiff (diff: WhatIfDiffResult) : string =
    let pairs =
      List.zip diff.OriginalOutputs diff.NewOutputs
      |> List.map (fun ((cid, orig), (_, new')) ->
        match orig = new' with
        | true -> sprintf "  [%d] unchanged: %s" cid orig
        | false -> sprintf "  [%d] %s → %s" cid orig new')
    let header = sprintf "What if %s?" (formatOverride diff.Override)
    match pairs with
    | [] -> sprintf "%s\n  no affected cells" header
    | _ -> sprintf "%s\n%s" header (pairs |> String.concat "\n")

  /// Format a plan summary before execution.
  let formatPlanSummary (plan: WhatIfPlan) : string =
    let count = plan.AffectedCells |> List.length
    sprintf "What if %s? %d cell(s) would re-evaluate" (formatOverride plan.Override) count
