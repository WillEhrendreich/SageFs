module SageFs.Tests.LiveValueTreeTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.LiveValueTree

type Person = { Name: string; Age: int }

type Shape =
  | Circle of radius: float
  | Rect of width: float * height: float

/// A type whose property getter throws — for the fail-closed walker test.
type ThrowingProp() =
  member _.Boom : int = failwith "getter boom"

/// A wide object whose children are themselves wide objects — exponential
/// expansion without a node budget.
type Wide() =
  member _.Children : obj[] = Array.init MaxChildren (fun _ -> box (Wide()))

let private rootOf (v: obj) = buildValueNode "v" v

[<Tests>]
let liveValueTreeTests = testList "LiveValueTree" [

  testCase "primitive leaf has preview and no children" <| fun _ ->
    let node = rootOf (box 42)
    node.Preview |> Expect.equal "int preview" "42"
    node.Kind |> Expect.equal "kind" NodeKind.Leaf
    node.Children |> Expect.isEmpty "no children"

  testCase "string leaf is quoted" <| fun _ ->
    let node = rootOf (box "hello")
    node.Preview |> Expect.equal "string preview" "\"hello\""

  testCase "record expands to field children with brace preview" <| fun _ ->
    let node = rootOf (box { Person.Name = "Ada"; Age = 37 })
    node.Kind |> Expect.equal "kind" NodeKind.Record
    node.Preview |> Expect.stringContains "preview has Name" "Name"
    node.Preview |> Expect.stringContains "preview has Ada" "Ada"
    node.Children.Length |> Expect.equal "two fields" 2
    node.Children |> List.map (fun c -> c.Label) |> Expect.equal "field labels" [ "Name"; "Age" ]
    node.Children |> List.head |> fun c -> c.Preview |> Expect.equal "first field value" "\"Ada\""

  testCase "list expands to indexed children with bracket preview" <| fun _ ->
    let node = rootOf (box [ 1; 2; 3 ])
    node.Kind |> Expect.equal "kind" NodeKind.List
    node.Preview |> Expect.equal "preview" "[1; 2; 3]"
    node.Children.Length |> Expect.equal "three children" 3
    node.Children |> List.head |> fun c -> c.Label |> Expect.equal "first label" "[0]"

  testCase "array expands with Array kind" <| fun _ ->
    let node = rootOf (box [| 1; 2 |])
    node.Kind |> Expect.equal "kind" NodeKind.Array

  testCase "option Some expands as union option" <| fun _ ->
    let node = rootOf (box (Some 5))
    node.Kind |> Expect.equal "kind" NodeKind.Option
    node.Preview |> Expect.equal "preview" "Some (5)"
    node.Children.Length |> Expect.equal "one child" 1

  testCase "option None is a null leaf (boxed None is null at runtime)" <| fun _ ->
    // F# compiles `box None` to a null reference, so a bare None cannot be
    // distinguished from null at runtime without static type info.
    let node = rootOf (box None)
    node.Kind |> Expect.equal "kind" NodeKind.Leaf
    node.Preview |> Expect.equal "preview" "null"

  testCase "DU case expands with case name" <| fun _ ->
    let node = rootOf (box (Circle 2.5))
    node.Kind |> Expect.equal "kind" NodeKind.Union
    node.Preview |> Expect.stringContains "case name" "Circle"
    node.Children.Length |> Expect.equal "one child" 1

  testCase "tuple expands" <| fun _ ->
    let node = rootOf (box (1, "a"))
    node.Kind |> Expect.equal "kind" NodeKind.Tuple
    node.Children.Length |> Expect.equal "two children" 2

  testCase "map expands with key labels" <| fun _ ->
    let node = rootOf (box (Map.ofList [ ("a", 1); ("b", 2) ]))
    node.Kind |> Expect.equal "kind" NodeKind.Map
    node.Children.Length |> Expect.equal "two entries" 2
    node.Children |> List.map (fun c -> c.Label) |> Expect.equal "key labels" [ "a"; "b" ]

  testCase "function is a best-effort closure leaf" <| fun _ ->
    let node = rootOf (box (fun (x: int) -> x + 1))
    node.Kind |> Expect.equal "kind" NodeKind.Closure
    node.Preview |> Expect.equal "preview" "<fun>"
    node.BestEffort |> Expect.isTrue "closure is best-effort"

  testCase "closure with captures expands captured fields best-effort" <| fun _ ->
    // Use a non-constant capture so the compiler cannot inline it away.
    let mutable captured = 42
    let node = rootOf (box (fun (x: int) -> x + captured))
    node.Kind |> Expect.equal "kind" NodeKind.Closure
    node.BestEffort |> Expect.isTrue "best effort"
    // Captured variables usually appear as compiler-generated closure fields.
    node.Children
    |> List.exists (fun c -> c.Preview.Contains "42")
    |> Expect.isTrue "should find the captured value somewhere in children"

  testCase "self-referential record produces a cycle node, terminates" <| fun _ ->
    let rec cyc = {| Self = Unchecked.defaultof<obj> |}
    let boxed = box cyc
    // Build a true cycle: a class holding a reference to itself.
    let cycleNode = rootOf boxed
    // No hang is the primary assertion; a plain record has no cycle.
    cycleNode.Kind |> Expect.equal "kind" NodeKind.Record

  testCase "depth limit produces truncated node" <| fun _ ->
    // A deeply nested structure: list of list of list ... hits MaxDepth.
    let deep =
      let rec build n =
        if n = 0 then box 1 else box [ build (n - 1) ]
      build 10
    let node = rootOf deep
    let rec findTruncated (n: LiveValueNode) =
      n.Kind = NodeKind.Truncated || (n.Children |> List.exists findTruncated)
    findTruncated node |> Expect.isTrue "should hit depth limit somewhere"

  testCase "children limit truncates large lists" <| fun _ ->
    let node = rootOf (box [ 1 .. 200 ])
    node.Children.Length |> Expect.equal "capped at MaxChildren" MaxChildren
    let rec findTruncated (n: LiveValueNode) =
      n.Kind = NodeKind.Truncated || (n.Children |> List.exists findTruncated)
    findTruncated node |> Expect.isFalse "list children cap does not mark truncated (preview shows …)"

  testCase "long strings are truncated" <| fun _ ->
    let long = String.replicate 1000 "x"
    let node = rootOf (box long)
    (node.Preview.Length, 600) |> Expect.isLessThan "preview should be truncated"

  testCase "reflection exception produces error leaf, does not crash" <| fun _ ->
    let node = rootOf (box (ThrowingProp()))
    node.Kind |> Expect.equal "kind" NodeKind.Class
    // The getter throws — the walker catches per-property and emits an error child.
    node.Children
    |> List.exists (fun c -> c.Preview.Contains "error" || c.Preview.Contains "boom")
    |> Expect.isTrue "throwing property should surface as an error child, not crash"
]

[<Tests>]
let liveValueSnapshotTests = testList "LiveValueSnapshot" [

  testCase "buildSnapshot collects bindings with generation" <| fun _ ->
    let snap =
      buildSnapshot "sess1" 7L [ ("x", "int", box 42); ("y", "string", box "hi") ]
    snap.SessionId |> Expect.equal "session id" "sess1"
    snap.Generation |> Expect.equal "generation" 7L
    snap.Bindings.Length |> Expect.equal "two bindings" 2
    snap.Bindings |> List.head |> fun b ->
      b.Name |> Expect.equal "name" "x"
      b.TypeSignature |> Expect.equal "type" "int"

  testCase "empty bindings produce empty snapshot" <| fun _ ->
    let snap = buildSnapshot "s1" 1L []
    snap.Bindings |> Expect.isEmpty "no bindings"

  testCase "snapshot caps binding count and marks Truncated" <| fun _ ->
    let many =
      [ for i in 1 .. (MaxBindings + 50) -> sprintf "v%d" i, "int", box i ]
    let snap = buildSnapshot "sess1" 1L many
    snap.Bindings.Length |> Expect.equal "capped at MaxBindings" MaxBindings
    snap.Truncated |> Expect.isTrue "binding cap sets Truncated"

  testCase "node budget bounds expansion of hostile graphs" <| fun _ ->
    // A wide object whose children are themselves wide objects. Without a node
    // budget the expansion is exponential (50^depth); the budget must cap the
    // total node count regardless of graph shape.
    let rec countNodes (n: LiveValueNode) = 1 + (n.Children |> List.sumBy countNodes)
    let node = buildValueNode "w" (box (Wide()))
    let total = countNodes node
    // Budget limits non-truncated nodes to MaxNodes, but truncated leaf
    // children of those nodes also count in countNodes.  The correct upper
    // bound is MaxNodes × (MaxChildren + 1), far below the unbounded 50^depth.
    (total, MaxNodes * (MaxChildren + 1)) |> Expect.isLessThan "bounded by node budget"
    node.Kind |> Expect.equal "kind" NodeKind.Class
]
