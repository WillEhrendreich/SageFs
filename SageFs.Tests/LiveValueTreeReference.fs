/// The live-value walker as it was before per-type reader caching, kept
/// verbatim as the oracle for LiveValueTreeTests' equivalence tests: the cached
/// walker must build exactly the tree this one builds.
module SageFs.Tests.LiveValueTreeReference

open System
open System.Collections
open System.Reflection
open Microsoft.FSharp.Reflection
open SageFs.Features.LiveValueTree

let private truncateString (s: string) =
  match s.Length > MaxStringLen with
  | false -> s
  | true -> s.Substring(0, MaxStringLen) + "…"

let private truncateList (items: string list) =
  match items.Length > MaxChildren with
  | false -> String.concat "; " items
  | true -> String.concat "; " (items |> List.truncate MaxChildren) + "; …"

let private scalarPreview (value: obj) =
  match value with
  | null -> "null"
  | :? string as s -> sprintf "\"%s\"" (truncateString s)
  | :? char as c -> sprintf "'%c'" c
  | :? bool as b -> if b then "true" else "false"
  | :? float as f -> sprintf "%g" f
  | :? DateTime as dt -> dt.ToString("o")
  | _ -> truncateString (string value)

let private keyLabel (value: obj) =
  match value with
  | null -> "null"
  | :? string as s -> s
  | _ -> truncateString (string value)

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

  if isCycle visited value then
    { Label = label; TypeName = t.Name; Preview = "↩ (cycle)"; Kind = NodeKind.Cycle
      Children = []; BestEffort = false; Depth = depth }
  elif depth >= MaxDepth then
    { Label = label; TypeName = t.Name; Preview = scalarPreview value; Kind = NodeKind.Truncated
      Children = []; BestEffort = false; Depth = depth }
  elif budget.Value <= 0 then
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
      | :? string as s -> leaf (sprintf "\"%s\"" (truncateString s)) NodeKind.Leaf
      | :? char -> leaf (scalarPreview value) NodeKind.Leaf
      | :? bool -> leaf (scalarPreview value) NodeKind.Leaf
      | :? float -> leaf (scalarPreview value) NodeKind.Leaf
      | :? DateTime -> leaf (scalarPreview value) NodeKind.Leaf
      | _ when t.IsPrimitive || t.IsEnum -> leaf (scalarPreview value) NodeKind.Leaf
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
              let child = buildNode visited budget name (depth + 1) v
              { child with BestEffort = true })
            |> Array.toList
          with _ -> []
        { Label = label; TypeName = typeName; Preview = "<fun>"; Kind = NodeKind.Closure
          Children = children; BestEffort = true; Depth = depth }
      | :? System.Collections.IDictionary as d ->
        let entries = d |> Seq.cast<DictionaryEntry> |> Seq.truncate (MaxChildren + 1) |> Seq.toList
        let preview = entries |> List.truncate MaxChildren
                       |> List.map (fun e -> sprintf "(%s, %s)" (scalarPreview e.Key) (scalarPreview e.Value))
                       |> truncateList |> fun s -> "map [" + s + "]"
        let children =
          entries |> List.truncate MaxChildren
          |> List.mapi (fun i e -> buildNode visited budget (keyLabel e.Key) (depth + 1) e.Value)
        { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Map
          Children = children; BestEffort = false; Depth = depth }
      | _ when t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<string, obj>> ->
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
            buildNode visited budget (keyLabel k) (depth + 1) v)
        { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Map
          Children = children; BestEffort = false; Depth = depth }
      | :? System.Collections.IEnumerable as e ->
        let items = e |> Seq.cast<obj> |> Seq.truncate (MaxChildren + 1) |> Seq.toList
        let shown = items |> List.truncate MaxChildren
        let preview = shown |> List.map scalarPreview |> truncateList |> fun s -> "[" + s + "]"
        let children =
          shown
          |> List.mapi (fun i item -> buildNode visited budget (sprintf "[%d]" i) (depth + 1) item)
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
          |> Array.mapi (fun i f -> buildNode visited budget fieldInfos.[i].Name (depth + 1) f)
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
          |> Array.mapi (fun i fi -> buildNode visited budget fi.Name (depth + 1) caseFields.[i])
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
          |> Array.mapi (fun i f -> buildNode visited budget (sprintf "item%d" (i + 1)) (depth + 1) f)
          |> Array.truncate MaxChildren
          |> Array.toList
        { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Tuple
          Children = children; BestEffort = false; Depth = depth }
      | _ ->
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
            try buildNode visited budget p.Name (depth + 1) (p.GetValue value)
            with _ -> { Label = p.Name; TypeName = "error"; Preview = "<error>"; Kind = NodeKind.Leaf
                        Children = []; BestEffort = false; Depth = depth + 1 })
          |> Array.toList
        { Label = label; TypeName = typeName; Preview = preview; Kind = NodeKind.Class
          Children = children; BestEffort = false; Depth = depth }
    with ex ->
      { Label = label; TypeName = t.Name; Preview = sprintf "<error: %s>" ex.Message
        Kind = NodeKind.Leaf; Children = []; BestEffort = false; Depth = depth }

/// The pre-caching walker's tree for one binding.
let buildValueNode (label: string) (value: obj) : LiveValueNode =
  let visited = System.Collections.Generic.HashSet<obj>(HashIdentity.Reference)
  buildNode visited (ref MaxNodes) label 0 value
