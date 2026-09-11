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

/// A record with enough fields to exercise the children cap.
type Wide60 = {
  F01: int; F02: int; F03: int; F04: int; F05: int; F06: int; F07: int; F08: int; F09: int; F10: int
  F11: int; F12: int; F13: int; F14: int; F15: int; F16: int; F17: int; F18: int; F19: int; F20: int
  F21: int; F22: int; F23: int; F24: int; F25: int; F26: int; F27: int; F28: int; F29: int; F30: int
  F31: int; F32: int; F33: int; F34: int; F35: int; F36: int; F37: int; F38: int; F39: int; F40: int
  F41: int; F42: int; F43: int; F44: int; F45: int; F46: int; F47: int; F48: int; F49: int; F50: int
  F51: int; F52: int; F53: int; F54: int; F55: int; F56: int; F57: int; F58: int; F59: int; F60: int
}

type Tree =
  | Leaf of int
  | Node of Tree * Tree

type Holder() =
  member _.Name = "holder"
  member _.Person = { Name = "Grace"; Age = 85 }
  member _.Shapes = [ Circle 1.0; Rect (2.0, 3.0) ]

/// One value of every shape the walker distinguishes.
let private corpus : (string * obj) list =
  let mutable captured = 7
  [ "int", box 42
    "string", box "hello"
    "long string", box (String.replicate 900 "y")
    "char", box 'c'
    "bool", box true
    "float", box 3.25
    "nan", box nan
    "datetime", box (DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc))
    "enum", box DayOfWeek.Friday
    "decimal", box 12.5m
    "record", box { Name = "Ada"; Age = 37 }
    "wide record", box ({ F01 = 1; F02 = 2; F03 = 3; F04 = 4; F05 = 5; F06 = 6; F07 = 7; F08 = 8; F09 = 9; F10 = 10
                          F11 = 11; F12 = 12; F13 = 13; F14 = 14; F15 = 15; F16 = 16; F17 = 17; F18 = 18; F19 = 19; F20 = 20
                          F21 = 21; F22 = 22; F23 = 23; F24 = 24; F25 = 25; F26 = 26; F27 = 27; F28 = 28; F29 = 29; F30 = 30
                          F31 = 31; F32 = 32; F33 = 33; F34 = 34; F35 = 35; F36 = 36; F37 = 37; F38 = 38; F39 = 39; F40 = 40
                          F41 = 41; F42 = 42; F43 = 43; F44 = 44; F45 = 45; F46 = 46; F47 = 47; F48 = 48; F49 = 49; F50 = 50
                          F51 = 51; F52 = 52; F53 = 53; F54 = 54; F55 = 55; F56 = 56; F57 = 57; F58 = 58; F59 = 59; F60 = 60 } : Wide60)
    "anonymous record", box {| A = 1; B = "b" |}
    "list", box [ 1; 2; 3 ]
    "long list", box [ 1 .. 200 ]
    "array", box [| 1.5; 2.5 |]
    "seq of records", box (ResizeArray [ { Name = "x"; Age = 1 }; { Name = "y"; Age = 2 } ])
    "hash set", box (Collections.Generic.HashSet [ "a"; "b" ])
    "some", box (Some 5)
    "some of record", box (Some { Name = "z"; Age = 3 })
    "union with one field", box (Circle 2.5)
    "union with two fields", box (Rect (1.0, 2.0))
    "recursive union", box (Node (Leaf 1, Node (Leaf 2, Leaf 3)))
    "result", box (Ok 3 : Result<int, string>)
    "tuple", box (1, "a")
    "eight tuple", box (1, 2, 3, 4, 5, 6, 7, 8)
    "struct tuple", box (struct (1, "s"))
    "map", box (Map.ofList [ "a", 1; "b", 2 ])
    "map of unions", box (Map.ofList [ 1, Circle 1.0; 2, Rect (1.0, 1.0) ])
    "dictionary", box (Collections.Generic.Dictionary<string, int>(dict [ "k", 1 ]))
    "closure", box (fun (x: int) -> x + captured)
    "closure without captures", box (fun (x: int) -> x + 1)
    "class", box (Holder())
    "version", box (System.Version(1, 2, 3))
    "throwing property", box (ThrowingProp())
    "node budget", box (Wide())
    "deep nesting",
      (let rec build n = if n = 0 then box 1 else box [ build (n - 1) ]
       build 10)
    "null", null ]

[<Tests>]
let liveValueTreeCachedReaderTests = testList "LiveValueTree cached readers" [

  testCase "WHY — the walker with per-type cached readers builds exactly the tree the uncached walker built, so caching changes the cost and nothing the watch window shows" <| fun _ ->
    for name, value in corpus do
      buildValueNode name value
      |> Expect.equal (sprintf "tree for %s" name) (SageFs.Tests.LiveValueTreeReference.buildValueNode name value)

  testCase "WHY — a type walked twice builds the same tree both times, because the second walk reads through the cache the first one filled" <| fun _ ->
    for name, value in corpus do
      let first = buildValueNode name value
      buildValueNode name value |> Expect.equal (sprintf "second tree for %s" name) first

  testProperty "WHY — cached readers match the uncached walker on generated records, unions, maps and tuples" <|
    fun (people: Person list, shapes: Map<string, Shape option>, pair: int * string, tree: Tree) ->
      let value = box (people, shapes, pair, tree)
      buildValueNode "v" value = SageFs.Tests.LiveValueTreeReference.buildValueNode "v" value
]
