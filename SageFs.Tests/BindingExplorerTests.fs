module SageFs.Tests.BindingExplorerTests

open Expecto
open Expecto.Flip
open SageFs.Features.BindingExplorer

[<Tests>]
let bindingExplorerTests = testList "BindingExplorer" [

  testList "parseBinding" [
    testCase "extracts name and type from val line" <| fun () ->
      parseBinding "val x : int = 1"
      |> Expect.equal "should parse x:int" (Some ("x", "int = 1"))

    testCase "rejects non-val lines" <| fun () ->
      parseBinding "let x = 1"
      |> Expect.isNone "should reject let"

    testCase "handles val with no colon" <| fun () ->
      parseBinding "val mutable x"
      |> Expect.equal "should parse with empty typesig" (Some ("mutable x", ""))
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
