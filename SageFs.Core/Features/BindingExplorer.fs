module SageFs.Features.BindingExplorer

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
      let refs =
        cells
        |> List.choose (fun cell ->
          if cell.CellIndex <> binding.CellIndex && cell.Source.Contains(binding.Name) then
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
