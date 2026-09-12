namespace SageFs.Features

open System
open System.Collections
open System.Reflection
open Microsoft.FSharp.Reflection

/// Live value tree — a reflection-walked, bounded tree of the actual bound
/// values in the FSI session, for the dashboard's debugger-style watch window.
/// Unlike BindingExplorer (which parses FSI's printed `val x : T = v` text),
/// this walks the real runtime objects via FSharpValue/FSharpType, so nested
/// records, lists, maps, unions, tuples and class properties are expanded.
module LiveValueTree =

  /// How a node's value should be rendered / expanded.
  [<RequireQualifiedAccess>]
  type NodeKind =
    | Leaf
    | Record
    | List
    | Map
    | Option
    | Union
    | Tuple
    | Array
    | Class
    | Closure
    | Cycle
    | Truncated

  /// One node in the expanded value tree.
  type LiveValueNode = {
    Label: string
    TypeName: string
    Preview: string
    Kind: NodeKind
    Children: LiveValueNode list
    /// True when the children are a best-effort guess (e.g. closure captures).
    BestEffort: bool
    Depth: int
  }

  /// A single top-level binding with its expanded value tree.
  type LiveBindingValue = {
    Name: string
    TypeSignature: string
    Root: LiveValueNode
  }

  /// Point-in-time snapshot of all active bindings in one session.
  type LiveValueSnapshot = {
    SessionId: string
    Generation: int64
    Bindings: LiveBindingValue list
    Truncated: bool
    CapturedAt: DateTimeOffset
  }

  // ── Limits ────────────────────────────────────────────────────────

  let [<Literal>] MaxDepth = 6
  let [<Literal>] MaxChildren = 50
  let [<Literal>] MaxStringLen = 500
  /// Top-level bindings walked per snapshot — caps the per-eval reflection
  /// cost even when a session binds thousands of values (roast queue 3).
  let [<Literal>] MaxBindings = 200
  /// Total node budget for one snapshot build. Depth × children bounds alone
  /// allow an exponential 50^depth expansion on hostile object graphs; this
  /// ref-based budget cuts the walk at a hard node count regardless of shape.
  let [<Literal>] MaxNodes = 10_000

  // ── Preview builders ──────────────────────────────────────────────

  let private truncateString (s: string) =
    match s.Length > MaxStringLen with
    | false -> s
    | true -> s.Substring(0, MaxStringLen) + "…"

  let private truncateList (items: string list) =
    match items.Length > MaxChildren with
    | false -> String.concat "; " items
    | true -> String.concat "; " (items |> List.truncate MaxChildren) + "; …"

  /// Compact one-line preview for a scalar/leaf value.
  let private scalarPreview (value: obj) =
    match value with
    | null -> "null"
    | :? string as s -> sprintf "\"%s\"" (truncateString s)
    | :? char as c -> sprintf "'%c'" c
    | :? bool as b -> if b then "true" else "false"
    | :? float as f -> sprintf "%g" f
    | :? DateTime as dt -> dt.ToString("o")
    | _ -> truncateString (string value)

  /// Label for a collection key — unquoted so map keys read like `a` not `"a"`.
  let private keyLabel (value: obj) =
    match value with
    | null -> "null"
    | :? string as s -> s
    | _ -> truncateString (string value)

  // ── Per-type shapes ───────────────────────────────────────────────
  //
  // How a value is walked depends only on its runtime type: whether it is a
  // record/union/tuple/function, its record fields, union cases, readable
  // properties and closure captures. Working that out is the expensive
  // reflection, so it runs once per type and every later value of the type
  // reuses the readers FSharp.Core precomputes. The cache is keyed weakly:
  // FSI-defined types live in collectible load contexts a reset unloads, and a
  // strong cache would pin every session's assemblies for the process lifetime.

  type private UnionCaseShape = {
    CaseName: string
    FieldNames: string[]
    ReadFields: obj -> obj[]
  }

  [<RequireQualifiedAccess>]
  type private TypeShape =
    /// string, char, bool, float, DateTime, primitives and enums.
    | Scalar
    /// An F# function value: its closure class's fields are the captures.
    | Closure of captures: (string * FieldInfo)[]
    | Dictionary
    /// F# Map, enumerated as KeyValuePair entries.
    | FSharpMap of key: PropertyInfo * value: PropertyInfo
    | Sequence of kind: NodeKind
    | Record of fieldNames: string[] * readFields: (obj -> obj[])
    | Union of readTag: (obj -> int) * cases: UnionCaseShape[]
    | Tuple of readFields: (obj -> obj[])
    /// Any other type: its readable, non-indexed public instance properties.
    | Class of properties: PropertyInfo[]

  /// Compiler-decorated capture fields (`<captured>v__`) are labelled by the
  /// captured name.
  let private captureLabel (rawName: string) =
    if rawName.StartsWith("<", StringComparison.Ordinal) then
      let endIdx = rawName.IndexOf('>')
      if endIdx > 1 then rawName.Substring(1, endIdx - 1) else rawName
    else rawName

  /// The checks run in the walker's precedence order: collections before the
  /// F# union check, because an F# list is a union AND IEnumerable.
  let private classify (t: Type) : TypeShape =
    if t = typeof<string> || t = typeof<char> || t = typeof<bool> || t = typeof<float>
       || t = typeof<DateTime> || t.IsPrimitive || t.IsEnum then
      TypeShape.Scalar
    elif FSharpType.IsFunction t then
      t.GetFields(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)
      |> Array.truncate MaxChildren
      |> Array.map (fun fi -> captureLabel fi.Name, fi)
      |> TypeShape.Closure
    elif typeof<IDictionary>.IsAssignableFrom t then
      TypeShape.Dictionary
    elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<string, obj>> then
      let entryType =
        typedefof<Collections.Generic.KeyValuePair<obj, obj>>.MakeGenericType(t.GetGenericArguments())
      TypeShape.FSharpMap (entryType.GetProperty "Key", entryType.GetProperty "Value")
    elif typeof<IEnumerable>.IsAssignableFrom t then
      TypeShape.Sequence (if t.IsArray then NodeKind.Array else NodeKind.List)
    elif FSharpType.IsRecord t then
      TypeShape.Record (
        FSharpType.GetRecordFields t |> Array.map (fun p -> p.Name),
        FSharpValue.PreComputeRecordReader t)
    elif FSharpType.IsUnion t then
      let cases =
        FSharpType.GetUnionCases t
        |> Array.map (fun case ->
          { CaseName = case.Name
            FieldNames = case.GetFields() |> Array.map (fun fi -> fi.Name)
            ReadFields = FSharpValue.PreComputeUnionReader case })
      TypeShape.Union (FSharpValue.PreComputeUnionTagReader t, cases)
    elif FSharpType.IsTuple t then
      TypeShape.Tuple (FSharpValue.PreComputeTupleReader t)
    else
      t.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
      |> Array.filter (fun p -> p.GetIndexParameters().Length = 0 && p.CanRead)
      |> Array.truncate MaxChildren
      |> TypeShape.Class

  let private shapes = System.Runtime.CompilerServices.ConditionalWeakTable<Type, TypeShape>()
  let private classifyCallback =
    System.Runtime.CompilerServices.ConditionalWeakTable<Type, TypeShape>.CreateValueCallback classify

  let private shapeOf (t: Type) = shapes.GetValue(t, classifyCallback)

  // ── Reflection walker ─────────────────────────────────────────────

  let private isCycle (visited: System.Collections.Generic.HashSet<obj>) (value: obj) =
    not (isNull value) && not (value.GetType().IsValueType) && not (visited.Add value)

  let rec private buildNode
    (visited: System.Collections.Generic.HashSet<obj>)
    (budget: int ref)
    (label: string)
    (depth: int)
    (value: obj)
    : LiveValueNode =
    let t = if isNull value then typeof<obj> else value.GetType()

    // Cycle protection: reference types seen before are not re-expanded.
    if isCycle visited value then
      { Label = label; TypeName = t.Name; Preview = "↩ (cycle)"; Kind = NodeKind.Cycle
        Children = []; BestEffort = false; Depth = depth }
    elif depth >= MaxDepth then
      { Label = label; TypeName = t.Name; Preview = scalarPreview value; Kind = NodeKind.Truncated
        Children = []; BestEffort = false; Depth = depth }
    elif budget.Value <= 0 then
      // Total node budget exhausted — stop the walk for this snapshot. The
      // depth/children limits alone allow a 50^depth blow-up on hostile object
      // graphs; the budget forces a hard stop regardless of graph shape.
      { Label = label; TypeName = t.Name; Preview = "… (node budget)"; Kind = NodeKind.Truncated
        Children = []; BestEffort = false; Depth = depth }
    else
      budget.Value <- budget.Value - 1
      try
        let typeName = t.Name
        let leaf preview kind = {
          Label = label; TypeName = typeName; Preview = preview; Kind = kind
          Children = []; BestEffort = false; Depth = depth }

        match value with
        | null -> leaf "null" NodeKind.Leaf
        | _ ->
        match shapeOf t with
        | TypeShape.Scalar -> leaf (scalarPreview value) NodeKind.Leaf
        // F# function values are FSharpFunc subclasses; the compiler-generated
        // closure class carries captured variables as instance fields. In Debug
        // builds the fields are decorated (`<captured>v__`); the label strips the
        // decoration but the field is kept — they ARE the captures.
        | TypeShape.Closure captures ->
          let children =
            try
              captures
              |> Array.map (fun (name, fi) ->
                let child = buildNode visited budget name (depth + 1) (fi.GetValue value)
                { child with BestEffort = true })
              |> Array.toList
            with _ -> []
          { Label = label; TypeName = typeName; Preview = "<fun>"; Kind = NodeKind.Closure
            Children = children; BestEffort = true; Depth = depth }
        | TypeShape.Dictionary ->
          let d = value :?> IDictionary
          let entries = d |> Seq.cast<DictionaryEntry> |> Seq.truncate (MaxChildren + 1) |> Seq.toList
          let preview = entries |> List.truncate MaxChildren
                         |> List.map (fun e -> sprintf "(%s, %s)" (scalarPreview e.Key) (scalarPreview e.Value))
                         |> truncateList |> fun s -> "map [" + s + "]"
          let children =
            entries |> List.truncate MaxChildren
            |> List.mapi (fun i e -> buildNode visited budget (keyLabel e.Key) (depth + 1) e.Value)
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Map
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.FSharpMap (keyProp, valueProp) ->
          let entries =
            (value :?> IEnumerable)
            |> Seq.cast<obj>
            |> Seq.truncate (MaxChildren + 1)
            |> Seq.toList
            |> List.truncate MaxChildren
            |> List.map (fun kv -> keyProp.GetValue kv, valueProp.GetValue kv)
          let preview =
            entries
            |> List.map (fun (k, v) -> sprintf "(%s, %s)" (scalarPreview k) (scalarPreview v))
            |> truncateList |> fun s -> "map [" + s + "]"
          let children =
            entries
            |> List.map (fun (k, v) -> buildNode visited budget (keyLabel k) (depth + 1) v)
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Map
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.Sequence kind ->
          let items = (value :?> IEnumerable) |> Seq.cast<obj> |> Seq.truncate (MaxChildren + 1) |> Seq.toList
          let shown = items |> List.truncate MaxChildren
          let preview = shown |> List.map scalarPreview |> truncateList |> fun s -> "[" + s + "]"
          let children =
            shown
            |> List.mapi (fun i item -> buildNode visited budget (sprintf "[%d]" i) (depth + 1) item)
          { Label = label; TypeName = typeName; Preview = preview; Kind = kind
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.Record (fieldNames, readFields) ->
          let fields = readFields value
          let preview =
            fields
            |> Array.mapi (fun i f -> sprintf "%s = %s" fieldNames.[i] (scalarPreview f))
            |> Array.toList
            |> truncateList
            |> fun s -> "{ " + s + " }"
          let children =
            fields
            |> Array.mapi (fun i f -> buildNode visited budget fieldNames.[i] (depth + 1) f)
            |> Array.truncate MaxChildren
            |> Array.toList
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Record
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.Union (readTag, cases) ->
          let case = cases.[readTag value]
          let caseFields = case.ReadFields value
          let preview =
            match caseFields.Length with
            | 0 -> case.CaseName
            | _ ->
              let args =
                caseFields |> Array.map scalarPreview |> Array.toList |> truncateList
                |> fun s -> "(" + s + ")"
              case.CaseName + " " + args
          let children =
            case.FieldNames
            |> Array.mapi (fun i name -> buildNode visited budget name (depth + 1) caseFields.[i])
            |> Array.truncate MaxChildren
            |> Array.toList
          let kind = if case.CaseName = "Some" || case.CaseName = "None" then NodeKind.Option else NodeKind.Union
          { Label = label; TypeName = typeName; Preview = preview; Kind = kind
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.Tuple readFields ->
          let fields = readFields value
          let preview = fields |> Array.map scalarPreview |> Array.toList |> truncateList |> fun s -> "(" + s + ")"
          let children =
            fields
            |> Array.mapi (fun i f -> buildNode visited budget (sprintf "item%d" (i + 1)) (depth + 1) f)
            |> Array.truncate MaxChildren
            |> Array.toList
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Tuple
            Children = children; BestEffort = false; Depth = depth }
        | TypeShape.Class props ->
          // Class instance — public instance properties (best-effort for .NET types).
          let preview =
            props
            |> Array.map (fun p ->
              try sprintf "%s = %s" p.Name (scalarPreview (p.GetValue value))
              with _ -> sprintf "%s = <error>" p.Name)
            |> Array.toList
            |> truncateList
            |> fun s -> "{ " + s + " }"
          let children =
            props
            |> Array.mapi (fun i p ->
              try buildNode visited budget p.Name (depth + 1) (p.GetValue value)
              with _ -> { Label = p.Name; TypeName = "error"; Preview = "<error>"; Kind = NodeKind.Leaf
                          Children = []; BestEffort = false; Depth = depth + 1 })
            |> Array.toList
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Class
            Children = children; BestEffort = false; Depth = depth }
      with ex ->
        { Label = label; TypeName = t.Name; Preview = sprintf "<error: %s>" ex.Message
          Kind = NodeKind.Leaf; Children = []; BestEffort = false; Depth = depth }

  /// Build the root node for a binding's value.
  let buildValueNode (label: string) (value: obj) : LiveValueNode =
    let visited = System.Collections.Generic.HashSet<obj>(HashIdentity.Reference)
    buildNode visited (ref MaxNodes) label 0 value

  /// Detect whether any node hit a truncation/cycle limit.
  let rec private hasTruncation (node: LiveValueNode) =
    node.Kind = NodeKind.Truncated
    || node.Kind = NodeKind.Cycle
    || (node.Children |> List.exists hasTruncation)

  /// Build a full snapshot from the session's bound values.
  /// `getBoundValues` returns (name, typeSignature, value) triples — the worker
  /// adapts FsiBoundValue into this shape so this module stays pure.
  let buildSnapshot
    (sessionId: string)
    (generation: int64)
    (boundValues: (string * string * obj) list)
    : LiveValueSnapshot =
    // boundValues arrives oldest-first (FsiEvaluationSession.GetBoundValues's
    // own declaration order — verified empirically: the first-declared name is
    // first in the list, and rebinding a name does not move its position).
    // Truncating that directly would silently keep the OLDEST MaxBindings
    // names and drop everything the user has defined since — backwards for a
    // live value WATCH, which exists to show what was just defined. Reverse
    // to newest-first, truncate to the most recent MaxBindings, then reverse
    // back so the kept subset still displays oldest-of-the-kept first.
    let capped = boundValues |> List.rev |> List.truncate MaxBindings |> List.rev
    let bindings =
      capped
      |> List.map (fun (name, typeSig, value) ->
        { Name = name; TypeSignature = typeSig; Root = buildValueNode name value })
    let truncated =
      (boundValues.Length > MaxBindings)
      || bindings |> List.exists (fun b -> hasTruncation b.Root)
    { SessionId = sessionId; Generation = generation; Bindings = bindings
      Truncated = truncated; CapturedAt = DateTimeOffset.UtcNow }
