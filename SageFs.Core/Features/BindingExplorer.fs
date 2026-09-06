module SageFs.Features.BindingExplorer

open System.Text.RegularExpressions

type BindingInfo = {
  Name: string
  TypeSig: string
  Value: string option
  CellIndex: int
  ShadowedBy: int list
  ReferencedIn: int list
}

type BindingScopeSnapshot = {
  Bindings: BindingInfo list
  ActiveBindings: Map<string, BindingInfo>
  ShadowedBindings: BindingInfo list
}

let parseBinding (fsiLine: string) : (string * string * string option) option =
  let trimmed = fsiLine.Trim()
  match trimmed.StartsWith("val ") with
  | false -> None
  | true ->
    let rest = trimmed.Substring(4)
    let colonIdx = rest.IndexOf(':')
    match colonIdx > 0 with
    | false -> Some (rest, "", None)
    | true ->
      let name = rest.Substring(0, colonIdx).Trim()
      let afterColon = rest.Substring(colonIdx + 1).Trim()
      // Use first = after colon, not last — values can contain =
      let eqIdx = afterColon.IndexOf('=')
      match eqIdx > 0 with
      | false -> Some (name, afterColon, None)
      | true ->
        let typeSig = afterColon.Substring(0, eqIdx).Trim()
        let value = afterColon.Substring(eqIdx + 1).Trim()
        let valueOpt =
          match value with
          | "" -> None
          | v -> Some v
        Some (name, typeSig, valueOpt)

type CellInput = {
  CellIndex: int
  FsiOutput: string
  Source: string
}

let buildScopeSnapshot (cells: CellInput list) : BindingScopeSnapshot =
  let allBindings =
    cells
    |> List.collect (fun cell ->
      cell.FsiOutput.Split('\n')
      |> Array.choose parseBinding
      |> Array.map (fun (name, typeSig, valueOpt) ->
        { Name = name
          TypeSig = typeSig
          Value = valueOpt
          CellIndex = cell.CellIndex
          ShadowedBy = []
          ReferencedIn = [] })
      |> Array.toList)
  // W5(R8): Index by name to reduce shadow computation from O(n²) to O(n).
  // Old: nested List.filter per binding. New: one Map lookup per binding.
  let byName = allBindings |> List.groupBy (fun b -> b.Name) |> Map.ofList
  let withShadows =
    allBindings
    |> List.map (fun binding ->
      let shadowedBy =
        byName |> Map.tryFind binding.Name |> Option.defaultValue []
        |> List.choose (fun other ->
          if other.CellIndex > binding.CellIndex then Some other.CellIndex else None)
      { binding with ShadowedBy = shadowedBy })
  let withRefs =
    withShadows
    |> List.map (fun binding ->
      // W13(R10): Pre-compile regex ONCE per binding, outside the cells inner loop.
      // Regex.IsMatch(string, string) uses a 15-slot static LRU cache; with >15 bindings
      // the cache thrashes causing recompilation per match. Compile once here instead.
      let re = Regex(@"\b" + Regex.Escape(binding.Name) + @"\b")
      let refs =
        cells
        |> List.choose (fun cell ->
          if cell.CellIndex <> binding.CellIndex && re.IsMatch(cell.Source) then
            Some cell.CellIndex
          else None)
      { binding with ReferencedIn = refs })
  let active =
    withRefs
    |> List.filter (fun b -> b.ShadowedBy |> List.isEmpty)
    |> List.map (fun b -> (b.Name, b))
    |> Map.ofList
  let shadowed = withRefs |> List.filter (fun b -> not (b.ShadowedBy |> List.isEmpty))
  { Bindings = withRefs; ActiveBindings = active; ShadowedBindings = shadowed }

/// Cells (excluding index `selfCellIndex`) whose source contains `name` as a
/// whole word. Shared by buildScopeSnapshot and appendCell so the incremental
/// merge uses identical word-boundary semantics.
let private cellsReferencingName (name: string) (selfCellIndex: int) (cells: CellInput list) : int list =
  let re = Regex(@"\b" + Regex.Escape(name) + @"\b")
  cells
  |> List.choose (fun cell ->
    if cell.CellIndex <> selfCellIndex && re.IsMatch(cell.Source) then
      Some cell.CellIndex
    else None)

/// Incrementally append ONE new cell to an existing scope snapshot.
///
/// Per-eval cost driver (roast §6/queue-7): recordEval used to rebuild the
/// whole scope from up to 10,000 retained cells on EVERY eval — re-parsing
/// every retained result and recomputing every cross-reference, O(n) per
/// eval, O(n²) total. Only the newest cell can change the scope, in exactly
/// these ways (mirroring buildScopeSnapshot's semantics precisely):
///   - prior bindings same-named as a new binding gain the new cell index in
///     ShadowedBy (the rebuild shadows EVERY prior same-named binding);
///   - prior bindings whose name the new source references gain the new cell
///     index in ReferencedIn;
///   - a NEW binding's ReferencedIn includes every prior cell whose source
///     mentions the name — the rebuild's reference scan is bidirectional, so
///     an OLDER cell that defined or used the same name is recorded as a
///     reference on the new binding (e.g. redefining `let x = ...` makes the
///     new binding referenced-by the older defining cell);
///   - the new binding is never referenced by cells after it (none exist).
/// Every other binding is untouched. `priorCells` are the cells that produced
/// `prior`; ORDER IS IRRELEVANT — this merge only ever scans them for word
/// matches, never indexes positionally. ReferencedIn/ShadowedBy lists are
/// sorted by cell index so the incremental result is byte-identical to a full
/// rebuild (which scans chronologically) regardless of the storage order
/// recordEval keeps (roast queue item 4: recordEval stores them newest-first
/// for an O(1) cons and passes them here as-is).
let appendCell (cell: CellInput) (priorCells: CellInput list) (prior: BindingScopeSnapshot) : BindingScopeSnapshot =
  let newBindings =
    cell.FsiOutput.Split('\n')
    |> Array.choose parseBinding
    |> Array.map (fun (name, typeSig, valueOpt) ->
      { Name = name
        TypeSig = typeSig
        Value = valueOpt
        CellIndex = cell.CellIndex
        ShadowedBy = []
        ReferencedIn = cellsReferencingName name cell.CellIndex priorCells |> List.sort })
    |> Array.toList
  let newNames = newBindings |> List.map (fun b -> b.Name) |> Set.ofList
  let newShadowIdx = newBindings |> List.map (fun b -> b.CellIndex)
  let mergedPrior =
    prior.Bindings
    |> List.map (fun b ->
      let shadowed =
        match newNames.Contains b.Name with
        | true -> (b.ShadowedBy @ newShadowIdx) |> List.sort
        | false -> b.ShadowedBy
      // Does the new source reference this prior binding's name as a word?
      let referenced =
        match Regex(@"\b" + Regex.Escape(b.Name) + @"\b").IsMatch(cell.Source) with
        | true ->
          if b.ReferencedIn |> List.contains cell.CellIndex |> not then
            (b.ReferencedIn @ [ cell.CellIndex ]) |> List.sort
          else b.ReferencedIn
        | false -> b.ReferencedIn
      { b with ShadowedBy = shadowed; ReferencedIn = referenced })
  let merged = mergedPrior @ newBindings
  // Active = empty ShadowedBy; within one cell, the last same-named binding
  // wins (mirrors the Map.ofList last-wins behavior of buildScopeSnapshot).
  let lastOfNameInNewCell =
    newBindings
    |> List.groupBy (fun b -> b.Name)
    |> Map.ofList
    |> Map.map (fun _ group -> List.last group)
  let activeMap =
    merged
    |> List.filter (fun b ->
      match b.ShadowedBy |> List.isEmpty with
      | true ->
        match lastOfNameInNewCell |> Map.tryFind b.Name with
        | Some last -> last.CellIndex = b.CellIndex
        | None -> true
      | false -> false)
    |> List.map (fun b -> (b.Name, b))
    |> Map.ofList
  let shadowed = merged |> List.filter (fun b -> not (b.ShadowedBy |> List.isEmpty))
  { Bindings = merged; ActiveBindings = activeMap; ShadowedBindings = shadowed }

/// Compute a binding scope snapshot from raw FSI output text (no per-cell source attribution).
/// Returns None when the output contains no parseable val bindings.
/// Used by the dashboard so bindings are visible even when no MCP SSE client is connected.
let fromRawOutput (rawFsiOutput: string) : BindingScopeSnapshot option =
  match System.String.IsNullOrWhiteSpace rawFsiOutput with
  | true -> None
  | false ->
    let cellInputs = [ { CellIndex = 0; FsiOutput = rawFsiOutput; Source = "" } ]
    let snapshot = buildScopeSnapshot cellInputs
    match Map.isEmpty snapshot.ActiveBindings with
    | true -> None
    | false -> Some snapshot
