module SageFs.Tests.BindingExplorerTests

open Expecto
open Expecto.Flip
open SageFs.Features.BindingExplorer

let private knownNames =
  [| "x"; "myFunc"; "xs"; "counter"; "result"; "acc"; "it"; "f"; "longName" |]

let private knownTypes =
  [| "int"; "string"; "bool"; "float"; "unit"; "int list"; "string option"
     "int -> int"; "string -> int -> bool"; "Map<string, int>"
     "Result<int, string>"; "Async<unit>"; "int * string"
     "System.IO.Stream" |]

let private knownValues =
  [| "1"; "42"; "\"hello\""; "true"; "false"; "()"; "[1; 2; 3]"
     "<fun:f@1>"; "None"; "Some 42"; "seq []"; "Error \"bad\"" |]

let private valuesWithEquals =
  [| "{| Name = \"Alice\" |}"; "{ contents = 0 }"; "\"a=b\""
     "{| X = 1; Y = 2 |}"; "{ contents = ref 0 }" |]

[<Tests>]
let bindingExplorerTests = testList "BindingExplorer" [

  testList "parseBinding" [
    testCase "extracts name and type from val line" <| fun () ->
      parseBinding "val x : int = 1"
      |> Expect.equal "should parse x:int" (Some ("x", "int", Some "1"))

    testCase "rejects non-val lines" <| fun () ->
      parseBinding "let x = 1"
      |> Expect.isNone "should reject let"

    testCase "handles val with no colon" <| fun () ->
      parseBinding "val mutable x"
      |> Expect.equal "should parse with empty typesig" (Some ("mutable x", "", None))
  ]

  testList "parseBinding properties" [
    testProperty "val name : type = value always parseable" <|
      fun (nameIdx: uint8) (typeIdx: uint8) (valueIdx: uint8) ->
        let name = knownNames.[int nameIdx % knownNames.Length]
        let typeSig = knownTypes.[int typeIdx % knownTypes.Length]
        let value = knownValues.[int valueIdx % knownValues.Length]
        let line = sprintf "val %s : %s = %s" name typeSig value
        match parseBinding line with
        | Some (n, t, Some _) -> n = name && t = typeSig
        | _ -> false

    testProperty "val without = gives type but no value" <|
      fun (nameIdx: uint8) (typeIdx: uint8) ->
        let name = knownNames.[int nameIdx % knownNames.Length]
        let typeSig = knownTypes.[int typeIdx % knownTypes.Length]
        let line = sprintf "val %s : %s" name typeSig
        match parseBinding line with
        | Some (n, t, None) -> n = name && t = typeSig
        | _ -> false

    testProperty "non-val lines always return None" <|
      fun (prefix: string) ->
        let line = (if isNull prefix then "" else prefix)
        match line.TrimStart().StartsWith("val ") with
        | true -> true
        | false -> parseBinding line = None
  ]

  testList "parseBinding bug fixes: values containing =" [
    testCase "anonymous record value with =" <| fun () ->
      let line = """val r : {| Name: string |} = {| Name = "Alice" |}"""
      let result = parseBinding line
      result |> Expect.isSome "should parse"
      let (n, t, v) = result.Value
      n |> Expect.equal "name" "r"
      t |> Expect.equal "type" "{| Name: string |}"
      v |> Expect.isSome "should have value"
      v.Value |> Expect.stringContains "value contains Name =" "Name ="

    testCase "ref cell value with =" <| fun () ->
      let line = "val counter : int ref = { contents = 0 }"
      let result = parseBinding line
      result |> Expect.isSome "should parse"
      let (n, t, v) = result.Value
      n |> Expect.equal "name" "counter"
      t |> Expect.equal "type" "int ref"
      v |> Expect.isSome "should have value"
      v.Value |> Expect.stringContains "value contains contents =" "contents ="

    testCase "quoted string with = in value" <| fun () ->
      let line = """val msg : string = "a=b" """
      let result = parseBinding line
      result |> Expect.isSome "should parse"
      let (n, t, v) = result.Value
      n |> Expect.equal "name" "msg"
      t |> Expect.equal "type" "string"
      v |> Expect.isSome "should have value"
      v.Value |> Expect.stringContains "value contains a=b" "a=b"

    testProperty "values containing = parse correctly" <|
      fun (nameIdx: uint8) (typeIdx: uint8) (valIdx: uint8) ->
        let name = knownNames.[int nameIdx % knownNames.Length]
        let typeSig = knownTypes.[int typeIdx % knownTypes.Length]
        let value = valuesWithEquals.[int valIdx % valuesWithEquals.Length]
        let line = sprintf "val %s : %s = %s" name typeSig value
        match parseBinding line with
        | Some (n, t, Some v) ->
          n = name && t = typeSig && v.Contains("=")
        | _ -> false
  ]

  testList "parseBinding edge cases" [
    testCase "val it : int = 42" <| fun () ->
      let result = parseBinding "val it : int = 42"
      result |> Expect.isSome "should parse 'it'"
      let (n, t, v) = result.Value
      n |> Expect.equal "name" "it"
      t |> Expect.equal "type" "int"
      v |> Expect.equal "value" (Some "42")

    testCase "val with no colon (edge)" <| fun () ->
      let result = parseBinding "val something"
      result |> Expect.isSome "should parse"
      let (n, t, _) = result.Value
      n |> Expect.equal "name" "something"
      t |> Expect.equal "type" ""

    testCase "generic multi-word type" <| fun () ->
      let line = "val xs : System.Collections.Generic.List<int> = seq [1; 2; 3]"
      let result = parseBinding line
      result |> Expect.isSome "should parse"
      let (n, t, v) = result.Value
      n |> Expect.equal "name" "xs"
      t |> Expect.equal "type" "System.Collections.Generic.List<int>"
      v |> Expect.isSome "should have value"

    testCase "function type" <| fun () ->
      let line = "val f : int -> string -> bool = <fun:f@1>"
      let result = parseBinding line
      result |> Expect.isSome "should parse"
      let (n, t, _) = result.Value
      n |> Expect.equal "name" "f"
      t |> Expect.equal "type" "int -> string -> bool"

    testCase "tuple pattern binding" <| fun () ->
      let line = "val (a, b) : int * string = (1, \"hi\")"
      let result = parseBinding line
      result |> Expect.isSome "should parse"
      let (n, t, _) = result.Value
      n |> Expect.equal "name" "(a, b)"
      t |> Expect.equal "type" "int * string"

    testCase "empty line returns None" <| fun () ->
      parseBinding "" |> Expect.isNone "empty line"

    testCase "non-val line returns None" <| fun () ->
      parseBinding "type Foo = { Bar: int }" |> Expect.isNone "type line"

    testCase "module line returns None" <| fun () ->
      parseBinding "module MyModule" |> Expect.isNone "module line"
  ]

  testList "buildScopeSnapshot" [
    testCase "single cell creates active binding" <| fun () ->
      let cells = [
        { CellIndex = 0; FsiOutput = "val x : int = 1"; Source = "let x = 1" }
      ]
      let snapshot = buildScopeSnapshot cells
      snapshot.ActiveBindings |> Map.containsKey "x"
      |> Expect.isTrue "x should be active"

    testCase "shadow detection: later cell shadows earlier" <| fun () ->
      let cells = [
        { CellIndex = 0; FsiOutput = "val x : int = 1"; Source = "let x = 1" }
        { CellIndex = 1; FsiOutput = "val x : int = 2"; Source = "let x = 2" }
      ]
      let snapshot = buildScopeSnapshot cells
      snapshot.ShadowedBindings |> List.length
      |> Expect.equal "should have one shadowed binding" 1
      snapshot.ShadowedBindings.[0].CellIndex
      |> Expect.equal "shadowed binding is from cell 0" 0

    testCase "reference tracking: cross-cell usage" <| fun () ->
      let cells = [
        { CellIndex = 0; FsiOutput = "val x : int = 1"; Source = "let x = 1" }
        { CellIndex = 1; FsiOutput = "val y : int = 2"; Source = "let y = x + 1" }
      ]
      let snapshot = buildScopeSnapshot cells
      let xBinding = snapshot.Bindings |> List.find (fun b -> b.Name = "x")
      xBinding.ReferencedIn |> Expect.equal "x referenced in cell 1" [1]

    testCase "empty cells produce empty snapshot" <| fun () ->
      let snapshot = buildScopeSnapshot []
      snapshot.Bindings |> Expect.isEmpty "should have no bindings"
      snapshot.ActiveBindings |> Map.isEmpty
      |> Expect.isTrue "should have no active bindings"
  ]

  testList "buildScopeSnapshot properties" [
    testProperty "latest definition always wins (shadow property)" <|
      fun (count: uint8) ->
        let n = max 2 (int count % 10 + 2)
        let cells =
          [ for i in 0 .. n - 1 do
              { CellIndex = i
                FsiOutput = sprintf "val x : int = %d" i
                Source = sprintf "let x = %d" i } ]
        let snapshot = buildScopeSnapshot cells
        match snapshot.ActiveBindings |> Map.tryFind "x" with
        | Some b -> b.CellIndex = n - 1
        | None -> false

    testProperty "shadow count equals definitions minus one" <|
      fun (count: uint8) ->
        let n = max 2 (int count % 10 + 2)
        let cells =
          [ for i in 0 .. n - 1 do
              { CellIndex = i
                FsiOutput = sprintf "val x : int = %d" i
                Source = sprintf "let x = %d" i } ]
        let snapshot = buildScopeSnapshot cells
        let shadowedXs = snapshot.ShadowedBindings |> List.filter (fun b -> b.Name = "x")
        shadowedXs.Length = n - 1

    testProperty "active binding count never exceeds unique name count" <|
      fun (nameIdxs: uint8 list) ->
        let names = [| "a"; "b"; "c"; "d"; "e" |]
        let indices = nameIdxs |> List.truncate 20
        match indices with
        | [] -> true
        | _ ->
          let cells =
            indices |> List.mapi (fun i idx ->
              let name = names.[int idx % names.Length]
              { CellIndex = i
                FsiOutput = sprintf "val %s : int = %d" name i
                Source = sprintf "let %s = %d" name i })
          let snapshot = buildScopeSnapshot cells
          let uniqueNames = indices |> List.map (fun idx -> names.[int idx % names.Length]) |> List.distinct
          snapshot.ActiveBindings.Count <= uniqueNames.Length

    testCase "multi-binding cell: multiple val lines per output" <| fun () ->
      let cells = [
        { CellIndex = 0
          FsiOutput = "val x : int = 1\nval y : string = \"hi\""
          Source = "let x = 1\nlet y = \"hi\"" }
      ]
      let snapshot = buildScopeSnapshot cells
      snapshot.ActiveBindings.Count
      |> Expect.equal "should have 2 active bindings" 2
      snapshot.ActiveBindings |> Map.containsKey "x"
      |> Expect.isTrue "x should be active"
      snapshot.ActiveBindings |> Map.containsKey "y"
      |> Expect.isTrue "y should be active"

    testCase "reference tracking: binding referenced in multiple cells" <| fun () ->
      let cells = [
        { CellIndex = 0; FsiOutput = "val x : int = 1"; Source = "let x = 1" }
        { CellIndex = 1; FsiOutput = "val y : int = 2"; Source = "let y = x + 1" }
        { CellIndex = 2; FsiOutput = "val z : int = 3"; Source = "let z = x + y" }
      ]
      let snapshot = buildScopeSnapshot cells
      let xBinding = snapshot.Bindings |> List.find (fun b -> b.Name = "x" && b.CellIndex = 0)
      xBinding.ReferencedIn |> List.length
      |> Expect.equal "x referenced in 2 cells" 2

    testProperty "all bindings are either active or shadowed (partition property)" <|
      fun (steps: uint8 list) ->
        let names = [| "a"; "b"; "c" |]
        let indices = steps |> List.truncate 15
        match indices with
        | [] -> true
        | _ ->
          let cells =
            indices |> List.mapi (fun i idx ->
              let name = names.[int idx % names.Length]
              { CellIndex = i
                FsiOutput = sprintf "val %s : int = %d" name i
                Source = sprintf "let %s = %d" name i })
          let snapshot = buildScopeSnapshot cells
          let activeCount = snapshot.ActiveBindings.Count
          let shadowedCount = snapshot.ShadowedBindings.Length
          let totalBindings = snapshot.Bindings.Length
          activeCount + shadowedCount = totalBindings
  ]

  testList "fromRawOutput" [
    testCase "empty string returns None" <| fun () ->
      fromRawOutput "" |> Expect.isNone "empty string"

    testCase "whitespace-only returns None" <| fun () ->
      fromRawOutput "   \n  " |> Expect.isNone "whitespace only"

    testCase "non-val lines return None" <| fun () ->
      fromRawOutput "type Foo = { Bar: int }" |> Expect.isNone "no val lines"

    testCase "single val line returns Some with binding" <| fun () ->
      let result = fromRawOutput "val x : int = 42"
      result |> Expect.isSome "should find x"
      result.Value.ActiveBindings |> Map.containsKey "x" |> Expect.isTrue "x in active"

    testCase "multiple val lines returns all bindings" <| fun () ->
      let output = "val x : int = 42\nval y : string = hello"
      let result = fromRawOutput output
      result |> Expect.isSome "should parse"
      result.Value.ActiveBindings |> Map.containsKey "x" |> Expect.isTrue "x present"
      result.Value.ActiveBindings |> Map.containsKey "y" |> Expect.isTrue "y present"

    testCase "last definition wins when same name defined twice" <| fun () ->
      let output = "val x : int = 1\nval x : string = hello"
      let result = fromRawOutput output
      result |> Expect.isSome "should parse"
      result.Value.ActiveBindings |> Map.count |> Expect.equal "one active x" 1
      result.Value.ActiveBindings.["x"].TypeSig |> Expect.equal "last x is string" "string"

    testProperty "any single valid val line produces Some" <|
      fun (nameIdx: uint8) (typeIdx: uint8) (valueIdx: uint8) ->
        let names = [| "x"; "y"; "z"; "acc"; "result" |]
        let types = [| "int"; "string"; "bool"; "float" |]
        let values = [| "1"; "42"; "true"; "\"hello\"" |]
        let name = names.[int nameIdx % names.Length]
        let typeSig = types.[int typeIdx % types.Length]
        let value = values.[int valueIdx % values.Length]
        let line = sprintf "val %s : %s = %s" name typeSig value
        match fromRawOutput line with
        | Some snap -> snap.ActiveBindings |> Map.containsKey name
        | None -> false
  ]
]

/// A cell whose source is `source` and whose FSI output is `fsiOutput`.
let cellAt (index: int) (source: string) (fsiOutput: string) : CellInput =
  { CellIndex = index; Source = source; FsiOutput = fsiOutput }

[<Tests>]
let scopeShadowDetectionTests = testList "buildScopeSnapshot shadow detection" [

  testCase "a single binding has no shadows and is active" <| fun _ ->
    let scope = buildScopeSnapshot [ cellAt 0 "let x = 1" "val x: int = 1" ]
    scope.Bindings |> Expect.hasLength "one binding" 1
    scope.Bindings.[0].ShadowedBy |> Expect.isEmpty "a unique name has no shadows"
    scope.ActiveBindings |> Map.containsKey "x" |> Expect.isTrue "x is active"

  testCase "a redefined binding is shadowed by the later cell, and the later one is active" <| fun _ ->
    let scope = buildScopeSnapshot [ cellAt 0 "let x = 1" "val x: int = 1"; cellAt 1 "let x = 2" "val x: int = 2" ]
    let original = scope.Bindings |> List.find (fun b -> b.Name = "x" && b.CellIndex = 0)
    original.ShadowedBy |> Expect.equal "cell 0's x is shadowed by cell 1" [ 1 ]
    (scope.ActiveBindings |> Map.find "x").CellIndex |> Expect.equal "the active x is from cell 1" 1

  testCase "a triple redefinition shadows every earlier version" <| fun _ ->
    let scope =
      buildScopeSnapshot
        [ cellAt 0 "let v = 1" "val v: int = 1"
          cellAt 1 "let v = 2" "val v: int = 2"
          cellAt 2 "let v = 3" "val v: int = 3" ]
    let v0 = scope.Bindings |> List.find (fun b -> b.Name = "v" && b.CellIndex = 0)
    let v1 = scope.Bindings |> List.find (fun b -> b.Name = "v" && b.CellIndex = 1)
    v0.ShadowedBy |> List.length |> Expect.equal "v0 is shadowed by the 2 later cells" 2
    v1.ShadowedBy |> Expect.equal "v1 is shadowed by cell 2 only" [ 2 ]
    scope.ShadowedBindings |> Expect.hasLength "2 shadowed bindings" 2

  testCase "distinct names never shadow each other" <| fun _ ->
    let scope =
      buildScopeSnapshot
        [ cellAt 0 "let a = 1" "val a: int = 1"
          cellAt 1 "let b = 2" "val b: int = 2"
          cellAt 2 "let c = 3" "val c: int = 3" ]
    scope.Bindings |> List.forall (fun b -> List.isEmpty b.ShadowedBy)
    |> Expect.isTrue "distinct names produce no shadows"
    scope.ActiveBindings |> Map.count |> Expect.equal "all three names are active" 3

  testCase "100 cells defining the same name leave one active binding and 99 shadowed" <| fun _ ->
    let scope =
      buildScopeSnapshot [ for i in 0 .. 99 -> cellAt i (sprintf "let x = %d" i) (sprintf "val x: int = %d" i) ]
    scope.ActiveBindings |> Map.count |> Expect.equal "one active binding (x)" 1
    (scope.ActiveBindings |> Map.find "x").CellIndex |> Expect.equal "the active x is from cell 99" 99
    scope.ShadowedBindings |> Expect.hasLength "99 shadowed definitions" 99
]

[<Tests>]
let scopeReferenceMatchingTests = testList "buildScopeSnapshot ReferencedIn matches whole words" [

  testCase "a short name does not match inside a longer identifier" <| fun _ ->
    // `x` appears in `maxValue` only as a substring, so nothing references it.
    let scope =
      buildScopeSnapshot
        [ cellAt 0 "let x = 1" "val x: int = 1"
          cellAt 1 "let maxValue = 100" "val maxValue: int = 100"
          cellAt 2 "printfn \"%d\" maxValue" "" ]
    (scope.Bindings |> List.find (fun b -> b.Name = "x")).ReferencedIn
    |> Expect.isEmpty "x only appears as a substring, so it has no references"

  testCase "a name matches when it is used as a standalone word, and not inside another word" <| fun _ ->
    let scope =
      buildScopeSnapshot
        [ cellAt 0 "let count = 5" "val count: int = 5"
          cellAt 1 "let doubled = count * 2" ""
          cellAt 2 "let discounted = price - 1" "" ]   // `count` inside `discounted` is a substring only
    (scope.Bindings |> List.find (fun b -> b.Name = "count")).ReferencedIn
    |> Expect.equal "count is referenced only in cell 1" [ 1 ]

  testCase "a single-letter name matches only as a standalone word" <| fun _ ->
    let scope =
      buildScopeSnapshot
        [ cellAt 0 "let i = 42" "val i: int = 42"
          cellAt 1 "printfn \"result\" " ""         // no standalone `i`
          cellAt 2 "let j = i + 1" "" ]             // `i` as a standalone word
    (scope.Bindings |> List.find (fun b -> b.Name = "i")).ReferencedIn
    |> Expect.equal "i is referenced only in cell 2" [ 2 ]

  testCase "20 bindings each referenced once still resolve to the right cell" <| fun _ ->
    // More bindings than a small per-name regex cache would hold: every reference must still land.
    let bindingCount = 20
    let definitions = [ for i in 0 .. bindingCount - 1 -> cellAt i (sprintf "let binding%d = %d" i i) (sprintf "val binding%d: int = %d" i i) ]
    let references = [ for i in 0 .. bindingCount - 1 -> cellAt (bindingCount + i) (sprintf "let result%d = binding%d * 2" i i) "" ]
    let binding0 = (buildScopeSnapshot (definitions @ references)).Bindings |> List.find (fun b -> b.Name = "binding0")
    binding0.ReferencedIn |> Expect.hasLength "binding0 is referenced in exactly 1 cell" 1
    binding0.ReferencedIn.Head |> Expect.equal "binding0 is referenced in the right cell" bindingCount
]
