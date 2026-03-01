module SageFs.Features.BindingExplorer

type BindingInfo = {
  Name: string
  TypeSig: string
  CellIndex: int
  ShadowedBy: int list
  ReferencedIn: int list
}

type BindingScopeSnapshot = {
  Bindings: BindingInfo list
  ActiveBindings: Map<string, BindingInfo>
  ShadowedBindings: BindingInfo list
}

let parseBinding (fsiLine: string) : (string * string) option =
  let trimmed = fsiLine.Trim()
  if trimmed.StartsWith("val ") then
    let rest = trimmed.Substring(4)
    let colonIdx = rest.IndexOf(':')
    if colonIdx > 0 then
      let name = rest.Substring(0, colonIdx).Trim()
      let typeSig = rest.Substring(colonIdx + 1).Trim()
      Some (name, typeSig)
    else
      Some (rest, "")
  else None

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
      |> Array.map (fun (name, typeSig) ->
        { Name = name
          TypeSig = typeSig
          CellIndex = cell.CellIndex
          ShadowedBy = []
          ReferencedIn = [] })
      |> Array.toList)
  let withShadows =
    allBindings
    |> List.mapi (fun i binding ->
      let shadowedBy =
        allBindings
        |> List.filter (fun other ->
          other.Name = binding.Name
          && other.CellIndex > binding.CellIndex)
        |> List.map (fun other -> other.CellIndex)
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
