namespace SageFs.Features.DomainModelViz

open System

/// Represents a single case in a discriminated union
type DUCaseInfo = {
  Name: string
  Fields: (string * string) list
}

/// Represents a transition between DU cases (function: CaseA -> CaseB)
type StateTransition = {
  FromState: string
  ToState: string
  FunctionName: string
  IsErrorBranch: bool
}

/// Complete state machine extracted from types + functions
type StateMachineModel = {
  TypeName: string
  Cases: DUCaseInfo list
  Transitions: StateTransition list
}

module StateMachineModel =
  let empty name = { TypeName = name; Cases = []; Transitions = [] }

  let addCase (case: DUCaseInfo) (m: StateMachineModel) =
    { m with Cases = m.Cases @ [case] }

  let addTransition (t: StateTransition) (m: StateMachineModel) =
    { m with Transitions = m.Transitions @ [t] }


/// Extracts DU case structure from runtime types via reflection
module DUExtractor =
  open Microsoft.FSharp.Reflection

  /// Format a Type as a readable string
  let rec formatTypeName (t: Type) : string =
    match t.IsGenericType with
    | true ->
      let genDef = t.GetGenericTypeDefinition()
      let genArgs = t.GetGenericArguments() |> Array.map formatTypeName
      let baseName =
        match genDef.Name.Contains('`') with
        | true -> genDef.Name.Substring(0, genDef.Name.IndexOf('`'))
        | false -> genDef.Name
      sprintf "%s<%s>" baseName (String.concat ", " genArgs)
    | false ->
      match t.Name with
      | "String" -> "string"
      | "Int32" -> "int"
      | "Int64" -> "int64"
      | "Boolean" -> "bool"
      | "Double" -> "float"
      | "Single" -> "float32"
      | "Decimal" -> "decimal"
      | "DateTime" -> "DateTime"
      | "Unit" -> "unit"
      | name -> name

  /// Extract DU case info from a System.Type
  let extractCases (t: Type) : DUCaseInfo list =
    match FSharpType.IsUnion(t) with
    | false -> []
    | true ->
      FSharpType.GetUnionCases(t)
      |> Array.toList
      |> List.map (fun uc ->
        let fields =
          uc.GetFields()
          |> Array.toList
          |> List.map (fun pi ->
            let fieldName = pi.Name
            let fieldType = formatTypeName pi.PropertyType
            (fieldName, fieldType))
        { Name = uc.Name; Fields = fields })

  /// Detect transitions from function signatures.
  /// A transition is: CaseA -> CaseB (or CaseA -> Result<CaseB, Error>)
  let extractTransitionsFromSignature
    (duCaseNames: Set<string>)
    (funcName: string)
    (inputType: string)
    (outputType: string) : StateTransition list =

    let normalizeTypeName (s: string) =
      match s.Contains(".") with
      | true -> s.Substring(s.LastIndexOf('.') + 1)
      | false -> s

    let inputNorm = normalizeTypeName inputType

    match outputType.StartsWith("Result<") || outputType.StartsWith("FSharpResult") with
    | true ->
      let inner =
        outputType
          .Replace("Result<", "")
          .Replace("FSharpResult`2<", "")
          .TrimEnd('>')
      let parts = inner.Split(',') |> Array.map (fun s -> s.Trim() |> normalizeTypeName)
      match parts.Length >= 1 with
      | true ->
        let okType = parts.[0]
        match Set.contains inputNorm duCaseNames && Set.contains okType duCaseNames with
        | true ->
          let okT = { FromState = inputNorm; ToState = okType; FunctionName = funcName; IsErrorBranch = false }
          match parts.Length >= 2 with
          | true ->
            let errType = parts.[1]
            match Set.contains errType duCaseNames with
            | true ->
              let errT = { FromState = inputNorm; ToState = errType; FunctionName = funcName; IsErrorBranch = true }
              [okT; errT]
            | false -> [okT]
          | false -> [okT]
        | false -> []
      | false -> []
    | false ->
      let outputNorm = normalizeTypeName outputType
      match Set.contains inputNorm duCaseNames && Set.contains outputNorm duCaseNames with
      | true -> [{ FromState = inputNorm; ToState = outputNorm; FunctionName = funcName; IsErrorBranch = false }]
      | false -> []

  /// Build a StateMachineModel from a runtime DU type and a list
  /// of known function signatures (funcName, inputTypeName, outputTypeName)
  let buildModel (duType: Type) (functions: (string * string * string) list) : StateMachineModel =
    let cases = extractCases duType
    let caseNames = cases |> List.map (fun c -> c.Name) |> Set.ofList
    let transitions =
      functions
      |> List.collect (fun (fn, inp, outp) ->
        extractTransitionsFromSignature caseNames fn inp outp)
    { TypeName = duType.Name; Cases = cases; Transitions = transitions }


/// Renders a StateMachineModel as ASCII art using box-drawing characters
module StateMachineRenderer =

  type NodePosition = { X: int; Y: int; Width: int; Height: int }

  /// Compute a simple top-down layered layout.
  /// Entry states (no inbound transitions) go at top, terminal states at bottom.
  let computeLayout (model: StateMachineModel) : Map<string, NodePosition> =
    let allStates = model.Cases |> List.map (fun c -> c.Name)
    let hasInbound = model.Transitions |> List.map (fun t -> t.ToState) |> Set.ofList
    let hasOutbound = model.Transitions |> List.map (fun t -> t.FromState) |> Set.ofList

    let entryStates = allStates |> List.filter (fun s -> Set.contains s hasInbound |> not)
    let terminalStates = allStates |> List.filter (fun s -> Set.contains s hasOutbound |> not)
    let middleStates = allStates |> List.filter (fun s ->
      (Set.contains s hasInbound) && (Set.contains s hasOutbound))

    let layers = [
      match entryStates with [] -> () | es -> yield es
      match middleStates with [] -> () | ms -> yield ms
      match terminalStates with [] -> () | ts -> yield ts
    ]

    let nodeWidth s = max 10 (String.length s + 4)
    let nodeHeight = 3
    let hGap = 4
    let vGap = 2

    let mutable positions = Map.empty
    let mutable y = 0
    for layer in layers do
      let totalWidth = layer |> List.sumBy (fun s -> nodeWidth s + hGap)
      let mutable x = max 0 ((60 - totalWidth) / 2)
      for state in layer do
        let w = nodeWidth state
        positions <- Map.add state { X = x; Y = y; Width = w; Height = nodeHeight } positions
        x <- x + w + hGap
      y <- y + nodeHeight + vGap
    positions

  /// Render a single state node as box-drawing characters
  let renderNode (name: string) (pos: NodePosition) : string list =
    let w = pos.Width
    let top = sprintf "┌%s┐" (String.replicate (w - 2) "─")
    let bot = sprintf "└%s┘" (String.replicate (w - 2) "─")
    let padded (s: string) =
      let pad = w - 2 - s.Length
      let left = pad / 2
      let right = pad - left
      sprintf "│%s%s%s│" (String.replicate left " ") s (String.replicate right " ")
    [top; padded name; bot]

  /// Render the complete diagram as a multi-line string
  let render (model: StateMachineModel) : string =
    let positions = computeLayout model

    let maxY = positions |> Map.values |> Seq.map (fun p -> p.Y + p.Height) |> Seq.fold max 0
    let maxX = positions |> Map.values |> Seq.map (fun p -> p.X + p.Width) |> Seq.fold max 0
    let gridH = maxY + 10
    let gridW = maxX + 20
    let grid = Array2D.create gridH gridW ' '

    let writeStr row col (s: string) =
      for i in 0 .. s.Length - 1 do
        match col + i < gridW && row < gridH && row >= 0 && col + i >= 0 with
        | true -> grid.[row, col + i] <- s.[i]
        | false -> ()

    // Draw nodes
    for case in model.Cases do
      match Map.tryFind case.Name positions with
      | Some pos ->
        let lines = renderNode case.Name pos
        lines |> List.iteri (fun i line -> writeStr (pos.Y + i) pos.X line)
      | None -> ()

    // Draw transitions as vertical arrows between nodes
    for t in model.Transitions do
      match Map.tryFind t.FromState positions, Map.tryFind t.ToState positions with
      | Some fromPos, Some toPos ->
        let fromCenterX = fromPos.X + fromPos.Width / 2
        let fromBottom = fromPos.Y + fromPos.Height
        let toTop = toPos.Y

        let arrowX =
          match t.IsErrorBranch with
          | true -> fromCenterX + 2
          | false -> fromCenterX
        let label =
          match t.IsErrorBranch with
          | true -> sprintf "✗ %s" t.FunctionName
          | false -> t.FunctionName

        for row in fromBottom .. toTop - 1 do
          match row < gridH && arrowX < gridW && arrowX >= 0 with
          | true -> grid.[row, arrowX] <- '│'
          | false -> ()

        match toTop - 1 < gridH && arrowX < gridW && arrowX >= 0 with
        | true -> grid.[toTop - 1, arrowX] <- '▼'
        | false -> ()

        let labelRow = fromBottom + (toTop - fromBottom) / 2
        match labelRow < gridH with
        | true -> writeStr labelRow (arrowX + 2) label
        | false -> ()
      | _ -> ()

    [| for row in 0 .. gridH - 1 do
         yield String(grid.[row, *]).TrimEnd() |]
    |> Array.filter (fun s -> s.Length > 0)
    |> String.concat "\n"

  /// Render as JSON-serializable structure for SSE/MCP consumption
  let renderAsData (model: StateMachineModel) : {| TypeName: string; States: {| Name: string; Fields: (string * string) list; IsEntry: bool; IsTerminal: bool |} array; Transitions: {| From: string; To: string; Function: string; IsError: bool |} array; AsciiDiagram: string |} =
    let hasInbound = model.Transitions |> List.map (fun t -> t.ToState) |> Set.ofList
    let hasOutbound = model.Transitions |> List.map (fun t -> t.FromState) |> Set.ofList
    {| TypeName = model.TypeName
       States =
         model.Cases
         |> List.map (fun c ->
           {| Name = c.Name
              Fields = c.Fields
              IsEntry = Set.contains c.Name hasInbound |> not
              IsTerminal = Set.contains c.Name hasOutbound |> not |})
         |> List.toArray
       Transitions =
         model.Transitions
         |> List.map (fun t ->
           {| From = t.FromState; To = t.ToState; Function = t.FunctionName; IsError = t.IsErrorBranch |})
         |> List.toArray
       AsciiDiagram = render model |}


/// Health status of a transition in the domain model
type TransitionHealth =
  | Passing
  | Failing
  | Stale
  | Untested
  | NotImplemented

/// A transition annotated with health status from the test/coverage system
type AnnotatedTransition = {
  FromState: string
  ToState: string
  FunctionName: string option
  IsErrorBranch: bool
  Health: TransitionHealth
}

/// Gap detection and health annotation for state machine models
module GapDetection =

  /// All possible transitions: cartesian product of case names (N×N)
  let computeAllPossibleTransitions (caseNames: string list) : (string * string) list =
    [ for from in caseNames do
        for to' in caseNames do
          yield (from, to') ]

  /// Find gaps: possible transitions with no implementing function
  let detectGaps (model: StateMachineModel) : (string * string) list =
    let caseNames = model.Cases |> List.map (fun c -> c.Name)
    let allPossible = computeAllPossibleTransitions caseNames
    let implemented =
      model.Transitions
      |> List.map (fun t -> (t.FromState, t.ToState))
      |> Set.ofList
    allPossible
    |> List.filter (fun pair -> Set.contains pair implemented |> not)

  /// Annotate all possible transitions with health status
  let annotateWithHealth
    (model: StateMachineModel)
    (coverageMap: Map<string, TransitionHealth>)
    : AnnotatedTransition list =
    let caseNames = model.Cases |> List.map (fun c -> c.Name)
    let allPossible = computeAllPossibleTransitions caseNames
    let transitionLookup =
      model.Transitions
      |> List.map (fun t -> ((t.FromState, t.ToState), t))
      |> Map.ofList
    allPossible
    |> List.map (fun (from, to') ->
      match Map.tryFind (from, to') transitionLookup with
      | None ->
        { FromState = from; ToState = to'; FunctionName = None
          IsErrorBranch = false; Health = NotImplemented }
      | Some t ->
        let health =
          match Map.tryFind t.FunctionName coverageMap with
          | Some h -> h
          | None -> Untested
        { FromState = from; ToState = to'; FunctionName = Some t.FunctionName
          IsErrorBranch = t.IsErrorBranch; Health = health })
