module SageFs.Tests.EvalLensTests

open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.EvalLens

[<Tests>]
let pipelineDecomposeTests =
  testList "EvalLens pipeline decomposition" [

    testCase "simple pipeline splits at |>" <| fun _ ->
      let stages = decomposePipeline "xs |> List.filter pred |> List.map f"
      stages |> Expect.hasLength "three stages" 3
      stages.[0].Code |> Expect.equal "source" "xs"
      stages.[1].Code |> Expect.equal "filter" "List.filter pred"
      stages.[2].Code |> Expect.equal "map" "List.map f"

    testCase "single expression yields one stage" <| fun _ ->
      let stages = decomposePipeline "42"
      stages |> Expect.hasLength "one stage" 1
      stages.[0].Code |> Expect.equal "literal" "42"

    testCase "stages have sequential indices" <| fun _ ->
      let stages = decomposePipeline "a |> b |> c"
      stages.[0].StageIndex |> Expect.equal "idx 0" 0
      stages.[1].StageIndex |> Expect.equal "idx 1" 1
      stages.[2].StageIndex |> Expect.equal "idx 2" 2

    testCase "whitespace is trimmed" <| fun _ ->
      let stages = decomposePipeline "  x  |>  f  |>  g  "
      stages.[0].Code |> Expect.equal "trimmed" "x"
      stages.[1].Code |> Expect.equal "trimmed" "f"
      stages.[2].Code |> Expect.equal "trimmed" "g"

    testCase "pipe inside parens is not split" <| fun _ ->
      let stages = decomposePipeline "xs |> List.filter (fun x -> x |> isValid)"
      stages |> Expect.hasLength "two stages" 2
      stages.[1].Code |> Expect.stringContains "preserved" "|> isValid"
  ]

[<Tests>]
let lensClassificationTests =
  testList "EvalLens classification" [

    testCase "List module is pure" <| fun _ ->
      classifyStage "List.map f" |> Expect.equal "pure" Pure

    testCase "Array module is pure" <| fun _ ->
      classifyStage "Array.filter pred" |> Expect.equal "pure" Pure

    testCase "Seq module is pure" <| fun _ ->
      classifyStage "Seq.head" |> Expect.equal "pure" Pure

    testCase "String module is pure" <| fun _ ->
      classifyStage "String.concat sep" |> Expect.equal "pure" Pure

    testCase "arithmetic is pure" <| fun _ ->
      classifyStage "x + 1" |> Expect.equal "pure" Pure

    testCase "unknown function is Unknown" <| fun _ ->
      classifyStage "doSomething x" |> Expect.equal "unknown" Unknown

    testCase "async expression is effectful" <| fun _ ->
      classifyStage "Async.RunSynchronously" |> Expect.equal "effectful" Effectful

    testCase "IO-related is effectful" <| fun _ ->
      classifyStage "File.ReadAllText path" |> Expect.equal "effectful" Effectful
  ]

[<Tests>]
let lensResultTests =
  testList "EvalLens result formatting" [

    testCase "format lens with type and value" <| fun _ ->
      let r = { StageIndex = 1; Code = "List.map f"; TypeSig = Some "int list"; Value = Some "[1; 2; 3]" }
      let s = formatLensResult r
      s |> Expect.stringContains "has type" "int list"
      s |> Expect.stringContains "has value" "[1; 2; 3]"

    testCase "format lens type only" <| fun _ ->
      let r = { StageIndex = 0; Code = "xs"; TypeSig = Some "string list"; Value = None }
      let s = formatLensResult r
      s |> Expect.stringContains "has type" "string list"

    testCase "format lens no info" <| fun _ ->
      let r = { StageIndex = 0; Code = "??"; TypeSig = None; Value = None }
      let s = formatLensResult r
      s |> Expect.stringContains "has code" "??"

    testCase "annotate pipeline produces results per stage" <| fun _ ->
      let stages = decomposePipeline "xs |> List.map f |> List.length"
      let annotated = annotatePipeline stages
      annotated |> Expect.hasLength "three results" 3
      annotated |> List.forall (fun r -> r.TypeSig = None)
      |> Expect.isTrue "no types yet (pure logic, no FCS)"
  ]
