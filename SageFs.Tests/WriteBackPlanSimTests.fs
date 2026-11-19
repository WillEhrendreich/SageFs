/// DST over chunk sets and dependency graphs. The order in every step is the
/// real WriteBackPlan.order; a failure prints the seed and ops for replay.
module SageFs.Tests.WriteBackPlanSimTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.Tweak
open SageFs.Simulation
open SageFs.Simulation.WriteBackPlanSim

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private genOp =
  Gen.oneof
    [ gen { let! id = Gen.elements [ "a"; "b"; "c"; "d" ]
            let! dep = Gen.elements [ "a"; "b"; "c"; "d"; "missing" ]
            return Op.Add(id, if dep = id then [] else [ dep ]) }
      gen { let! id = Gen.elements [ "a"; "b"; "c"; "d"; "stranger" ]
            return Op.Accept id } ]

let private genScenario =
  gen {
    let! seed = Gen.choose (0, 10000)
    let! count = Gen.choose (1, 14)
    let! ops = Gen.listOfLength count genOp
    return { Seed = seed; Ops = ops }
  }

let private replay (t: Trace) = sprintf "seed=%d ops=%A" t.Scenario.Seed t.Scenario.Ops

let private check (label: string) (violations: string list) (t: Trace) =
  match violations with
  | [] -> ()
  | _ -> failtestf "INVARIANT VIOLATION (%s) — replay: %s\n%A" label (replay t) violations

[<Tests>]
let writeBackPlanSimTests =
  testList "DST write back plan" [

    testPropertyWithConfig simConfig "seeded chunk graphs hold the granularity invariants" <|
      Prop.forAll (Arb.fromGen genScenario) (fun scenario ->
        let t = run scenario
        let violations =
          t.Steps
          |> List.collect (fun step ->
            match step.Order with
            | Error _ -> []
            | Ok ids ->
              let positions = ids |> List.mapi (fun i id -> id, i) |> Map.ofList
              let accepted = step.Accepted |> Set.ofList
              let depsOf id =
                step.Graph
                |> List.tryPick (fun (x, deps) -> if x = id then Some deps else None)
                |> Option.defaultValue []
              [ if ids |> List.exists (fun id -> not (Set.contains id accepted)) then
                  sprintf "an unaccepted chunk was ordered: %A" ids
                for id in ids do
                  for dep in depsOf id |> List.filter (fun d -> Set.contains d accepted) do
                    match Map.tryFind dep positions with
                    | Some p when p > Map.find id positions ->
                      sprintf "%s was ordered before its dependency %s" id dep
                    | _ -> () ])
        check "granularity" violations t)

    testCase "the scenarios are deterministic: the same seed gives the same trace" <| fun _ ->
      let s = { Seed = 4242; Ops = [ Op.Add("a", [ "b" ]); Op.Add("b", []); Op.Accept "a" ] }
      run s |> Expect.equal "same scenario" (run s)

    testCase "a refused order names the unknown id, never a silent drop" <| fun _ ->
      let t = run { Seed = 7; Ops = [ Op.Accept "stranger" ] }
      match t.Steps.[0].Order with
      | Error (WriteBackPlan.OrderRefusal.UnknownChunk id) -> id |> Expect.equal "names it" "stranger"
      | Error other -> failtestf "expected an unknown-chunk refusal, got %A" other
      | Ok ids -> failtestf "unexpected order: %A" ids
  ]
