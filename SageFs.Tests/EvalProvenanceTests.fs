module SageFs.Tests.EvalProvenanceTests

open Expecto
open Expecto.Flip
open SageFs.Features.CellDependencyGraph
open SageFs.Features.LiveTesting
open SageFs.Features.EvalProvenance

[<Tests>]
let evalProvenanceTypeTests =
  testList "EvalProvenance types" [

    testCase "EvalProvenance is a struct" <| fun _ ->
      typeof<EvalProvenance>.IsValueType
      |> Expect.isTrue "EvalProvenance should be [<Struct>]"

    testCase "Staleness is a struct" <| fun _ ->
      typeof<Staleness>.IsValueType
      |> Expect.isTrue "Staleness should be [<Struct>]"

    testCase "Fresh staleness" <| fun _ ->
      match Staleness.Fresh with
      | Staleness.Fresh -> ()
      | _ -> failtest "should be Fresh"

    testCase "StaleUpstream carries cell ids" <| fun _ ->
      match Staleness.StaleUpstream [1; 2; 3] with
      | Staleness.StaleUpstream ids ->
        ids |> Expect.equal "should carry upstream ids" [1; 2; 3]
      | _ -> failtest "should be StaleUpstream"
  ]

[<Tests>]
let provenanceComputationTests =
  testList "EvalProvenance compute" [

    testCase "fresh cell has no stale upstream" <| fun _ ->
      let graph = buildGraph [
        { Id = 1; Source = "let x = 5"; Produces = ["x"]; Consumes = [] }
        { Id = 2; Source = "let y = x + 1"; Produces = ["y"]; Consumes = ["x"] }
      ]
      EvalProvenance.compute graph 2 Set.empty
      |> fun p -> p.Staleness
      |> Expect.equal "no changes → fresh" Staleness.Fresh

    testCase "cell is stale when upstream changed" <| fun _ ->
      let graph = buildGraph [
        { Id = 1; Source = "let x = 5"; Produces = ["x"]; Consumes = [] }
        { Id = 2; Source = "let y = x + 1"; Produces = ["y"]; Consumes = ["x"] }
      ]
      let changedCells = Set.ofList [1]
      let prov = EvalProvenance.compute graph 2 changedCells
      match prov.Staleness with
      | Staleness.StaleUpstream ids ->
        ids |> Expect.contains "should include cell 1" 1
      | Staleness.Fresh ->
        failtest "should be stale when upstream changed"

    testCase "transitive staleness propagates through chain" <| fun _ ->
      let graph = buildGraph [
        { Id = 1; Source = "let a = 1"; Produces = ["a"]; Consumes = [] }
        { Id = 2; Source = "let b = a + 1"; Produces = ["b"]; Consumes = ["a"] }
        { Id = 3; Source = "let c = b + 1"; Produces = ["c"]; Consumes = ["b"] }
      ]
      let changedCells = Set.ofList [1]
      let prov = EvalProvenance.compute graph 3 changedCells
      match prov.Staleness with
      | Staleness.StaleUpstream ids ->
        ids |> Expect.isNonEmpty "should have stale upstream"
      | Staleness.Fresh ->
        failtest "should be stale transitively"

    testCase "unrelated cell is fresh" <| fun _ ->
      let graph = buildGraph [
        { Id = 1; Source = "let a = 1"; Produces = ["a"]; Consumes = [] }
        { Id = 2; Source = "let b = 42"; Produces = ["b"]; Consumes = [] }
      ]
      let changedCells = Set.ofList [1]
      EvalProvenance.compute graph 2 changedCells
      |> fun p -> p.Staleness
      |> Expect.equal "unrelated cell should be fresh" Staleness.Fresh

    testCase "compute tracks dependencies" <| fun _ ->
      let graph = buildGraph [
        { Id = 1; Source = "let x = 5"; Produces = ["x"]; Consumes = [] }
        { Id = 2; Source = "let y = 10"; Produces = ["y"]; Consumes = [] }
        { Id = 3; Source = "let z = x + y"; Produces = ["z"]; Consumes = ["x"; "y"] }
      ]
      let prov = EvalProvenance.compute graph 3 Set.empty
      prov.DependsOn |> List.sort
      |> Expect.equal "should depend on cells 1 and 2" [1; 2]

    testCase "root cell has no dependencies" <| fun _ ->
      let graph = buildGraph [
        { Id = 1; Source = "let x = 5"; Produces = ["x"]; Consumes = [] }
      ]
      EvalProvenance.compute graph 1 Set.empty
      |> fun p -> p.DependsOn
      |> Expect.equal "root should have no deps" []
  ]

[<Tests>]
let provenanceGutterTests =
  testList "EvalProvenance gutter icons" [

    testCase "CellStale gutter icon exists" <| fun _ ->
      let icon = GutterIcon.CellStale
      match icon with
      | GutterIcon.CellStale -> ()
      | _ -> failtest "should be CellStale"

    testCase "CellStale has warning char" <| fun _ ->
      GutterIcon.toChar GutterIcon.CellStale
      |> Expect.equal "stale icon should be ⚠" '⚠'

    testCase "CellStale has yellow/warning color" <| fun _ ->
      GutterIcon.toAnsiColor GutterIcon.CellStale
      |> Expect.equal "stale should be yellow" "\x1b[33m"

    testCase "CellStale emoji is warning" <| fun _ ->
      GutterIcon.toEmoji GutterIcon.CellStale
      |> Expect.equal "stale emoji should be ⚠️" "⚠️"

    testCase "CellStale statusText is stale" <| fun _ ->
      GutterIcon.toStatusText GutterIcon.CellStale
      |> Expect.equal "stale text should say stale" "stale"
  ]

[<Tests>]
let provenanceAnnotationTests =
  testList "EvalProvenance annotations" [

    testCase "toAnnotation produces stale annotation" <| fun _ ->
      let prov = {
        EvalProvenance.CellId = 2
        DependsOn = [1]
        Staleness = Staleness.StaleUpstream [1]
      }
      let ann = EvalProvenance.toAnnotation 5 prov
      ann.Icon |> Expect.equal "should be CellStale" GutterIcon.CellStale
      ann.Line |> Expect.equal "line should match" 5
      ann.Tooltip |> Expect.stringContains "tooltip should mention stale" "stale"

    testCase "toAnnotation produces no annotation for fresh" <| fun _ ->
      let prov = {
        EvalProvenance.CellId = 1
        DependsOn = []
        Staleness = Staleness.Fresh
      }
      EvalProvenance.tryAnnotation 5 prov
      |> Expect.isNone "fresh cell should not produce annotation"

    testCase "stale annotation tooltip includes upstream cell ids" <| fun _ ->
      let prov = {
        EvalProvenance.CellId = 3
        DependsOn = [1; 2]
        Staleness = Staleness.StaleUpstream [1; 2]
      }
      let ann = EvalProvenance.toAnnotation 10 prov
      ann.Tooltip |> Expect.stringContains "tooltip should mention cell 1" "1"
      ann.Tooltip |> Expect.stringContains "tooltip should mention cell 2" "2"
  ]

[<Tests>]
let provenanceDescribeTests =
  testList "EvalProvenance describe" [

    testCase "describe fresh" <| fun _ ->
      Staleness.describe Staleness.Fresh
      |> Expect.stringContains "should say up-to-date" "up-to-date"

    testCase "describe stale with one upstream" <| fun _ ->
      Staleness.describe (Staleness.StaleUpstream [1])
      |> Expect.stringContains "should mention stale" "stale"

    testCase "describe stale with multiple upstreams" <| fun _ ->
      Staleness.describe (Staleness.StaleUpstream [1; 2; 3])
      |> Expect.stringContains "should mention count" "3"
  ]
