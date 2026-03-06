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
]
