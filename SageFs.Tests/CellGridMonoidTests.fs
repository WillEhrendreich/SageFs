module SageFs.Tests.CellGridMonoidTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Tests.SharedGenerators

// ============================================================
// Cell Overlay Monoid — property tests for algebraic laws
// ============================================================

let cellA = Cell.create 'A' 0x00FF0000u 0x00000000u CellAttrs.Bold
let cellB = Cell.create 'B' 0x0000FF00u 0x00111111u CellAttrs.Dim
let cellC = Cell.create 'C' 0x000000FFu 0x00222222u CellAttrs.Inverse

[<Tests>]
let cellOverlayTests = testList "Cell.overlay monoid" [
  testList "identity" [
    test "overlay empty onto cell returns cell (right identity)" {
      Cell.overlay cellA Cell.empty |> Expect.equal "A overlay empty = A" cellA
    }
    test "overlay cell onto empty returns cell (left identity)" {
      Cell.overlay Cell.empty cellA |> Expect.equal "empty overlay A = A" cellA
    }
    test "overlay empty onto empty returns empty" {
      Cell.overlay Cell.empty Cell.empty |> Expect.equal "empty overlay empty = empty" Cell.empty
    }
  ]

  testList "overwrite semantics" [
    test "non-empty overlay replaces base" {
      Cell.overlay cellA cellB |> Expect.equal "A overlay B = B" cellB
    }
    test "empty base gets replaced by non-empty overlay" {
      Cell.overlay Cell.empty cellB |> Expect.equal "empty overlay B = B" cellB
    }
  ]

  testList "associativity" [
    test "three cells associate left" {
      let leftFirst = Cell.overlay (Cell.overlay cellA cellB) cellC
      let rightFirst = Cell.overlay cellA (Cell.overlay cellB cellC)
      leftFirst |> Expect.equal "(A⊕B)⊕C = A⊕(B⊕C)" rightFirst
    }
    test "with empty in middle" {
      let leftFirst = Cell.overlay (Cell.overlay cellA Cell.empty) cellC
      let rightFirst = Cell.overlay cellA (Cell.overlay Cell.empty cellC)
      leftFirst |> Expect.equal "associative with empty middle" rightFirst
    }
  ]

  testList "properties" [
    testPropertyWithConfig propConfig "right identity: overlay x empty = x" <|
      fun (ch: char) (fg: uint32) (bg: uint32) ->
        let cell = Cell.create ch fg bg CellAttrs.None
        Cell.overlay cell Cell.empty = cell

    testPropertyWithConfig propConfig "left identity: overlay empty x = x" <|
      fun (ch: char) (fg: uint32) (bg: uint32) ->
        let cell = Cell.create ch fg bg CellAttrs.None
        Cell.overlay Cell.empty cell = cell

    testPropertyWithConfig propConfig "associativity: (a⊕b)⊕c = a⊕(b⊕c)" <|
      fun (cha: char) (chb: char) (chc: char) ->
        let a = Cell.create cha 0x00FF0000u 0u CellAttrs.Bold
        let b = Cell.create chb 0x0000FF00u 0u CellAttrs.Dim
        let c = Cell.create chc 0x000000FFu 0u CellAttrs.None
        Cell.overlay (Cell.overlay a b) c = Cell.overlay a (Cell.overlay b c)
  ]
]

// ============================================================
// CellGrid Overlay Monoid — grid-level composition
// ============================================================

[<Tests>]
let cellGridOverlayTests = testList "CellGrid.overlay monoid" [
  testList "identity" [
    test "overlay empty grid returns base unchanged" {
      let base' = CellGrid.create 3 4
      CellGrid.writeString base' 0 0 0x00FFu 0u CellAttrs.None "ABCD"
      let empty = CellGrid.create 3 4
      let result = CellGrid.overlay base' empty
      CellGrid.toText result |> Expect.equal "base preserved" (CellGrid.toText base')
    }
    test "overlay onto empty grid returns overlay content" {
      let empty = CellGrid.create 3 4
      let over = CellGrid.create 3 4
      CellGrid.writeString over 1 1 0x00FFu 0u CellAttrs.None "XY"
      let result = CellGrid.overlay empty over
      CellGrid.toText result |> Expect.equal "overlay content" (CellGrid.toText over)
    }
  ]

  testList "overwrite semantics" [
    test "non-empty overlay cells replace base cells" {
      let base' = CellGrid.create 2 4
      CellGrid.writeString base' 0 0 0x00FFu 0u CellAttrs.None "ABCD"
      let over = CellGrid.create 2 4
      CellGrid.writeString over 0 1 0x00FFu 0u CellAttrs.None "XY"
      let result = CellGrid.overlay base' over
      (CellGrid.get result 0 0).Char |> Expect.equal "A untouched" 'A'
      (CellGrid.get result 0 1).Char |> Expect.equal "B replaced by X" 'X'
      (CellGrid.get result 0 2).Char |> Expect.equal "C replaced by Y" 'Y'
      (CellGrid.get result 0 3).Char |> Expect.equal "D untouched" 'D'
    }
  ]

  testList "associativity" [
    test "three grid overlays are associative" {
      let a = CellGrid.create 2 3
      CellGrid.writeString a 0 0 0x00FFu 0u CellAttrs.None "AAA"
      let b = CellGrid.create 2 3
      CellGrid.writeString b 0 1 0x00FFu 0u CellAttrs.None "B"
      let c = CellGrid.create 2 3
      CellGrid.writeString c 0 2 0x00FFu 0u CellAttrs.None "C"
      let leftFirst = CellGrid.overlay (CellGrid.overlay a b) c
      let rightFirst = CellGrid.overlay a (CellGrid.overlay b c)
      CellGrid.toText leftFirst |> Expect.equal "(a⊕b)⊕c = a⊕(b⊕c)" (CellGrid.toText rightFirst)
    }
  ]

  testList "properties" [
    testPropertyWithConfig propConfig "overlay with empty is identity" <|
      fun (rows: FsCheck.PositiveInt) (cols: FsCheck.PositiveInt) ->
        let r = (rows.Get % 10) + 1
        let c = (cols.Get % 10) + 1
        let grid = CellGrid.create r c
        CellGrid.writeString grid 0 0 0x00FFu 0u CellAttrs.None "X"
        let empty = CellGrid.create r c
        CellGrid.toText (CellGrid.overlay grid empty) = CellGrid.toText grid

    testPropertyWithConfig propConfig "overlay empty base yields overlay content" <|
      fun (rows: FsCheck.PositiveInt) (cols: FsCheck.PositiveInt) ->
        let r = (rows.Get % 10) + 1
        let c = (cols.Get % 10) + 1
        let empty = CellGrid.create r c
        let over = CellGrid.create r c
        CellGrid.writeString over 0 0 0x00FFu 0u CellAttrs.None "Y"
        CellGrid.toText (CellGrid.overlay empty over) = CellGrid.toText over
  ]
]
