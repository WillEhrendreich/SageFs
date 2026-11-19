/// TDD for the granular write-back planner. Pure decisions only: what would be
/// written where, what it depends on, and what order selected chunks may be
/// applied in. No eval, no IO, no MCP types.
module SageFs.Tests.WriteBackPlanTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.Tweak

let private source = "module App\n\nlet answer = 1\nlet helper = 2\n"
let private file = "/repo/App.fs"

let private chunk id dependsOn =
  { WriteBackPlan.Chunk.Id = id
    WriteBackPlan.Chunk.Name = id
    WriteBackPlan.Chunk.File = file
    WriteBackPlan.Chunk.StartLine = 1
    WriteBackPlan.Chunk.EndLine = 1
    WriteBackPlan.Chunk.Replacement = "let " + id + " = 2"
    WriteBackPlan.Chunk.DependsOn = dependsOn
    WriteBackPlan.Chunk.Status = WriteBackPlan.ChunkStatus.Ready }

let private planWith chunks =
  { WriteBackPlan.Plan.File = file
    WriteBackPlan.Plan.Source = source
    WriteBackPlan.Plan.Chunks = chunks }

let private planOf code file source =
  WriteBackPlan.plan
    { WriteBackPlan.PlanRequest.Code = code
      WriteBackPlan.PlanRequest.File = file
      WriteBackPlan.PlanRequest.Source = source }

let private depsHonest (p: WriteBackPlan.Plan) =
  let ids = p.Chunks |> List.map (fun c -> c.Id) |> Set.ofList
  p.Chunks
  |> List.forall (fun c ->
    c.DependsOn
    |> List.forall (fun d ->
      Set.contains d ids
      || match c.Status with
         | WriteBackPlan.ChunkStatus.NeedsDependency names -> List.contains d names
         | _ -> false))

[<Tests>]
let writeBackPlanTests =
  testList "WriteBackPlan — granular write decisions" [

    testCase "WHY — one declaration in the source becomes one named, ready chunk" <| fun _ ->
      match planOf"let answer = 42;;" file source with
      | Error e -> failtestf "expected a plan, got %A" e
      | Ok p ->
        p.Chunks
        |> List.map (fun c -> c.Name)
        |> Expect.equal "the submitted declaration" [ "answer" ]
        match p.Chunks.[0].Status with
        | WriteBackPlan.ChunkStatus.Ready -> ()
        | other -> failtestf "expected a ready chunk, got %A" other

    testCase "WHY — a name with no declaration in the file is refused with that name" <| fun _ ->
      match planOf"let absent = 42;;" file source with
      | Error e -> failtestf "expected a plan, got %A" e
      | Ok p ->
        match p.Chunks.[0].Status with
        | WriteBackPlan.ChunkStatus.Refused reason ->
          reason |> Expect.stringContains "names the missing declaration" "absent"
        | other -> failtestf "expected Refused, got %A" other

    testCase "WHY — a chunk that mentions a declaration outside the plan needs it first" <| fun _ ->
      match planOf"let answer = helper + 1;;" file source with
      | Error e -> failtestf "expected a plan, got %A" e
      | Ok p ->
        p.Chunks.[0].DependsOn
        |> Expect.equal "the mentioned declaration" [ "helper" ]

    testCase "WHY — empty code and a missing file are refused before parsing" <| fun _ ->
      planOf"" file source |> Expect.equal "empty" (Error WriteBackPlan.Refusal.EmptyCode)
      planOf"let x = 1" "" source |> Expect.equal "file" (Error WriteBackPlan.Refusal.NoFilePath)
      planOf"42;;" file source |> Expect.equal "expression" (Error WriteBackPlan.Refusal.NothingToWrite)

    testProperty "PROPERTY: a plan only names chunks that are in it, or marks them as dependencies" <|
      Prop.forAll (Arb.fromGen (Gen.elements [ "let answer = 42;;"; "let helper = 99;;"; "let answer = 1\nlet helper = 2;;" ])) (fun code ->
        match planOf code file source with
        | Error _ -> true
        | Ok p -> depsHonest p)

    testCase "WHY — order puts a dependency before the chunk that needs it" <| fun _ ->
      let p = planWith [ chunk "answer" [ "helper" ]; chunk "helper" [] ]
      match WriteBackPlan.order p [ "answer"; "helper" ] with
      | Error e -> failtestf "expected an order, got %A" e
      | Ok ids -> ids |> Expect.equal "helper first" [ "helper"; "answer" ]

    testCase "WHY — a cycle is refused by name instead of broken arbitrarily" <| fun _ ->
      let p = planWith [ chunk "a" [ "b" ]; chunk "b" [ "a" ] ]
      match WriteBackPlan.order p [ "a"; "b" ] with
      | Error (WriteBackPlan.OrderRefusal.Cycle id) -> id |> Expect.equal "names the cycle" "a"
      | Error other -> failtestf "expected a cycle refusal, got %A" other
      | Ok ids -> failtestf "a cycle must not have an order: %A" ids

    testCase "WHY — a chunk that was not accepted is never pulled in by order" <| fun _ ->
      let p = planWith [ chunk "answer" [ "helper" ]; chunk "helper" [] ]
      match WriteBackPlan.order p [ "answer" ] with
      | Error e -> failtestf "expected an order, got %A" e
      | Ok ids -> ids |> Expect.equal "only the accepted chunk" [ "answer" ]

    testCase "WHY — an id outside the plan is refused, not silently ignored" <| fun _ ->
      let p = planWith [ chunk "answer" [] ]
      match WriteBackPlan.order p [ "stranger" ] with
      | Error (WriteBackPlan.OrderRefusal.UnknownChunk id) -> id |> Expect.equal "names the bad id" "stranger"
      | Error other -> failtestf "expected an unknown-chunk refusal, got %A" other
      | Ok ids -> failtestf "unexpected order: %A" ids
  ]
