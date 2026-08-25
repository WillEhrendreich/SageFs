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

  // ── Reflection walker ─────────────────────────────────────────────

  let private isCycle (visited: System.Collections.Generic.HashSet<obj>) (value: obj) =
    not (isNull value) && not (value.GetType().IsValueType) && not (visited.Add value)

  let rec private buildNode
    (visited: System.Collections.Generic.HashSet<obj>)
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
    else
      try
        let typeName = t.Name
        let leaf preview kind = {
          Label = label; TypeName = typeName; Preview = preview; Kind = kind
          Children = []; BestEffort = false; Depth = depth }

        match value with
        | null -> leaf "null" NodeKind.Leaf
        | :? string as s -> leaf (sprintf "\"%s\"" (truncateString s)) NodeKind.Leaf
        | :? char -> leaf (scalarPreview value) NodeKind.Leaf
        | :? bool -> leaf (scalarPreview value) NodeKind.Leaf
        | :? float -> leaf (scalarPreview value) NodeKind.Leaf
        | :? DateTime -> leaf (scalarPreview value) NodeKind.Leaf
        | _ when t.IsPrimitive || t.IsEnum -> leaf (scalarPreview value) NodeKind.Leaf
        // F# function values are FSharpFunc subclasses; the compiler-generated
        // closure class carries captured variables as instance fields. In Debug
        // builds the fields are decorated (`<captured>v__`); strip the decoration
        // for the label but keep the field — they ARE the captures.
        | _ when FSharpType.IsFunction t ->
          let children =
            try
              t.GetFields(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)
              |> Array.truncate MaxChildren
              |> Array.map (fun fi ->
                let rawName = fi.Name
                let name =
                  if rawName.StartsWith("<", StringComparison.Ordinal) then
                    let endIdx = rawName.IndexOf('>')
                    if endIdx > 1 then rawName.Substring(1, endIdx - 1) else rawName
                  else rawName
                let v = fi.GetValue value
                let child = buildNode visited name (depth + 1) v
                { child with BestEffort = true })
              |> Array.toList
            with _ -> []
          { Label = label; TypeName = typeName; Preview = "<fun>"; Kind = NodeKind.Closure
            Children = children; BestEffort = true; Depth = depth }
        // Collections BEFORE F# union checks: F# list is a union AND IEnumerable.
        | :? System.Collections.IDictionary as d ->
          let entries = d |> Seq.cast<DictionaryEntry> |> Seq.truncate (MaxChildren + 1) |> Seq.toList
          let preview = entries |> List.truncate MaxChildren
                         |> List.map (fun e -> sprintf "(%s, %s)" (scalarPreview e.Key) (scalarPreview e.Value))
                         |> truncateList |> fun s -> "map [" + s + "]"
          let children =
            entries |> List.truncate MaxChildren
            |> List.mapi (fun i e -> buildNode visited (keyLabel e.Key) (depth + 1) e.Value)
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Map
            Children = children; BestEffort = false; Depth = depth }
        | _ when t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<string, obj>> ->
          // F# Map — IEnumerable<KeyValuePair<K,V>>, detected by generic def.
          let entries =
            (value :?> System.Collections.IEnumerable)
            |> Seq.cast<obj>
            |> Seq.truncate (MaxChildren + 1)
            |> Seq.toList
          let preview =
            entries |> List.truncate MaxChildren
            |> List.map (fun kv ->
              let k = kv.GetType().GetProperty("Key").GetValue kv
              let v = kv.GetType().GetProperty("Value").GetValue kv
              sprintf "(%s, %s)" (scalarPreview k) (scalarPreview v))
            |> truncateList |> fun s -> "map [" + s + "]"
          let children =
            entries |> List.truncate MaxChildren
            |> List.mapi (fun i kv ->
              let k = kv.GetType().GetProperty("Key").GetValue kv
              let v = kv.GetType().GetProperty("Value").GetValue kv
              buildNode visited (keyLabel k) (depth + 1) v)
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Map
            Children = children; BestEffort = false; Depth = depth }
        | :? System.Collections.IEnumerable as e ->
          let items = e |> Seq.cast<obj> |> Seq.truncate (MaxChildren + 1) |> Seq.toList
          let shown = items |> List.truncate MaxChildren
          let preview = shown |> List.map scalarPreview |> truncateList |> fun s -> "[" + s + "]"
          let children =
            shown
            |> List.mapi (fun i item -> buildNode visited (sprintf "[%d]" i) (depth + 1) item)
          let kind = if t.IsArray then NodeKind.Array else NodeKind.List
          { Label = label; TypeName = typeName; Preview = preview; Kind = kind
            Children = children; BestEffort = false; Depth = depth }
        | _ when FSharpType.IsRecord t ->
          let fields = FSharpValue.GetRecordFields value
          let fieldInfos = FSharpType.GetRecordFields t
          let preview =
            fields
            |> Array.mapi (fun i f -> sprintf "%s = %s" fieldInfos.[i].Name (scalarPreview f))
            |> Array.toList
            |> truncateList
            |> fun s -> "{ " + s + " }"
          let children =
            fields
            |> Array.mapi (fun i f -> buildNode visited fieldInfos.[i].Name (depth + 1) f)
            |> Array.truncate MaxChildren
            |> Array.toList
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Record
            Children = children; BestEffort = false; Depth = depth }
        | _ when FSharpType.IsUnion t ->
          let case, caseFields = FSharpValue.GetUnionFields(value, t)
          let preview =
            match caseFields.Length with
            | 0 -> case.Name
            | _ ->
              let args =
                caseFields |> Array.map scalarPreview |> Array.toList |> truncateList
                |> fun s -> "(" + s + ")"
              case.Name + " " + args
          let children =
            case.GetFields()
            |> Array.mapi (fun i fi -> buildNode visited fi.Name (depth + 1) caseFields.[i])
            |> Array.truncate MaxChildren
            |> Array.toList
          let kind = if case.Name = "Some" || case.Name = "None" then NodeKind.Option else NodeKind.Union
          { Label = label; TypeName = typeName; Preview = preview; Kind = kind
            Children = children; BestEffort = false; Depth = depth }
        | _ when FSharpType.IsTuple t ->
          let fields = FSharpValue.GetTupleFields value
          let preview = fields |> Array.map scalarPreview |> Array.toList |> truncateList |> fun s -> "(" + s + ")"
          let children =
            fields
            |> Array.mapi (fun i f -> buildNode visited (sprintf "item%d" (i + 1)) (depth + 1) f)
            |> Array.truncate MaxChildren
            |> Array.toList
          { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Tuple
            Children = children; BestEffort = false; Depth = depth }
        | _ ->
          // Class instance — public instance properties (best-effort for .NET types).
          let props =
            t.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
            |> Array.filter (fun p -> p.GetIndexParameters().Length = 0 && p.CanRead)
            |> Array.truncate MaxChildren
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
              try buildNode visited p.Name (depth + 1) (p.GetValue value)
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
    buildNode visited label 0 value

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
    let bindings =
      boundValues
      |> List.map (fun (name, typeSig, value) ->
        { Name = name; TypeSignature = typeSig; Root = buildValueNode name value })
    let truncated = bindings |> List.exists (fun b -> hasTruncation b.Root)
    { SessionId = sessionId; Generation = generation; Bindings = bindings
      Truncated = truncated; CapturedAt = DateTimeOffset.UtcNow }
