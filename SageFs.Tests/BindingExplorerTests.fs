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

  testList "appendCell equivalence" [
    // The incremental merge MUST produce exactly the same snapshot as the
    // full rebuild — this is the invariant that keeps recordEval's fast path
    // semantically identical to the O(n) rebuild it replaced.
    let snapEquals (a: BindingScopeSnapshot) (b: BindingScopeSnapshot) =
      a.Bindings = b.Bindings
      && a.ActiveBindings = b.ActiveBindings
      && a.ShadowedBindings = b.ShadowedBindings

    let poolName (raw: string) (pool: string[]) =
      let h = raw |> Seq.fold (fun acc c -> acc + int c) 0
      pool.[abs h % pool.Length]

    testProperty "appendCell fold equals full rebuild for shadowing" <|
      fun (rawNames: string list) ->
        let pool = [| "x"; "y"; "z" |]
        let cells =
          rawNames
          |> List.truncate 12
          |> List.mapi (fun i raw ->
            let name = poolName raw pool
            { CellIndex = i
              FsiOutput = sprintf "val %s : int = %d" name i
              Source = sprintf "let %s = %d" name i })
        match cells with
        | [] -> true
        | first :: rest ->
          let rebuilt = buildScopeSnapshot cells
          let incremental =
            rest
            |> List.fold (fun (priorCells, snap) cell ->
              (priorCells @ [ cell ], appendCell cell priorCells snap))
              ([ first ], buildScopeSnapshot [ first ])
            |> snd
          snapEquals rebuilt incremental

    testProperty "appendCell fold equals full rebuild with cross-cell references" <|
      fun (pairs: (string * bool) list) ->
        let pool = [| "a"; "b"; "c" |]
        let cells =
          pairs
          |> List.truncate 10
          |> List.mapi (fun i (rawName, usesPrev) ->
            let name = poolName rawName pool
            let source =
              match i, usesPrev with
              | 0, _ -> sprintf "let %s = %d" name i
              | _, true ->
                let prev = pool.[(i - 1) % pool.Length]
                sprintf "let %s = %s + %d" name prev i
              | _, false -> sprintf "let %s = %d" name i
            { CellIndex = i
              FsiOutput = sprintf "val %s : int = %d" name i
              Source = source })
        match cells with
        | [] -> true
        | first :: rest ->
          let rebuilt = buildScopeSnapshot cells
          let incremental =
            rest
            |> List.fold (fun (priorCells, snap) cell ->
              (priorCells @ [ cell ], appendCell cell priorCells snap))
              ([ first ], buildScopeSnapshot [ first ])
            |> snd
          snapEquals rebuilt incremental

    testProperty "appendCell fold equals full rebuild with same-name redefinition" <|
      fun (count: uint8) ->
        let n = max 2 (int count % 8 + 2)
        let cells =
          [ for i in 0 .. n - 1 ->
              { CellIndex = i
                FsiOutput = sprintf "val x : int = %d" i
                Source = sprintf "let y = x + 1\nlet x = %d" i } ]
        let rebuilt = buildScopeSnapshot cells
        let incremental =
          cells
          |> List.tail
          |> List.fold (fun (priorCells, snap) cell ->
            (priorCells @ [ cell ], appendCell cell priorCells snap))
            ([ cells.Head ], buildScopeSnapshot [ cells.Head ])
          |> snd
        snapEquals rebuilt incremental
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
