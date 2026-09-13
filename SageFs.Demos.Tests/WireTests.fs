/// Proves the one JSON contract that crosses the sandbox namespace wall
/// (demo-gif-plan.md §4.1): `ScenarioPlan`/`StepLog` round-trip through
/// `serialize*`/`deserialize*` unchanged — the exact mechanism the Phase-0
/// spike proved end to end, exercised here without ever spawning a cell.
module SageFs.Demos.Tests.WireTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Wire

let private samplePlan: ScenarioPlan =
  { ScenarioId = "hello-dashboard"
    ChromePath = "/chrome-bin/chrome"
    PageUrl = "http://127.0.0.1:47750/dashboard"
    UserDataDir = "/home/demo/chrome-profile"
    OutDir = "/out"
    Steps =
      [ { Index = 0
          Caption = "1/1 · Press Quick Start"
          PreClickSelector = None
          ClickSelector = Some "[data-testid=quick-start]"
          TypeText = None
          SubmitSelector = None
          ExpectSelector = Some "[data-testid=session-card]"
          DwellMs = 1500
          TargetActor = None } ]
    Client = "dashboard"
    VsCode = None
    Nvim = None
    App = None }

let private sampleStepLog: StepLog =
  { ScenarioId = "hello-dashboard"
    Steps =
      [ { Index = 0
          Caption = "1/1 · Press Quick Start"
          Segment = "/out/step-00.mkv"
          StartedMs = 0L
          EndedMs = 2140L
          PointerPath = [ [| 620; 300 |]; [| 630; 298 |] ]
          ObservedAtMs = 1980L
          Outcome = "Passed"
          Message = "'[data-testid=session-card]' appeared" } ] }

[<Tests>]
let tests =
  testList "Wire" [

    testCase "ScenarioPlan round-trips through serializePlan/deserializePlan unchanged" <| fun _ ->
      samplePlan |> serializePlan |> deserializePlan |> Expect.equal "round-tripped plan equals the original" samplePlan

    testCase "StepLog round-trips through serializeStepLog/deserializeStepLog unchanged" <| fun _ ->
      sampleStepLog
      |> serializeStepLog
      |> deserializeStepLog
      |> Expect.equal "round-tripped StepLog equals the original" sampleStepLog

    testCase "serializePlan produces one JSON line (the stdio pipe carries exactly one line, §4.1)" <| fun _ ->
      samplePlan |> serializePlan |> (fun s -> s.Contains "\n") |> Expect.isFalse "no embedded newline"

    testCase "a ScenarioPlan step with no click/type/expect (an Await step) still round-trips" <| fun _ ->
      let awaitOnly =
        { samplePlan with
            Steps =
              [ { Index = 0
                  Caption = "wait"
                  PreClickSelector = None
                  ClickSelector = None
                  TypeText = None
                  SubmitSelector = None
                  ExpectSelector = None
                  DwellMs = 500
                  TargetActor = None } ] }

      awaitOnly |> serializePlan |> deserializePlan |> Expect.equal "round-trips with every optional field None" awaitOnly

    testCase "a TypeThenClick step's SubmitSelector round-trips (the chained 'type, then click Eval' beat, §9)" <| fun _ ->
      let typeThenClick =
        { samplePlan with
            Steps =
              [ { Index = 0
                  Caption = "3/3 · Evaluate F#"
                  PreClickSelector = None
                  ClickSelector = Some "#eval-textarea"
                  TypeText = Some "[1..10] |> List.sum"
                  SubmitSelector = Some "[data-testid=eval]"
                  ExpectSelector = Some "[data-testid=session-output]:has-text(\"55\")"
                  DwellMs = 2000
                  TargetActor = None } ] }

      typeThenClick |> serializePlan |> deserializePlan |> Expect.equal "round-trips with SubmitSelector populated" typeThenClick

    testCase "a ClickThenTypeThenClick step's PreClickSelector round-trips (the 'expand the collapsed accordion, then type, then click Eval' beat, §9)" <| fun _ ->
      let clickThenTypeThenClick =
        { samplePlan with
            Steps =
              [ { Index = 0
                  Caption = "3/3 · Evaluate F#"
                  PreClickSelector = Some "#evaluate-section summary"
                  ClickSelector = Some "#eval-textarea"
                  TypeText = Some "[1..10] |> List.sum"
                  SubmitSelector = Some "[data-testid=eval]"
                  ExpectSelector = Some "[data-testid=session-output]:has-text(\"55\")"
                  DwellMs = 2000
                  TargetActor = None } ] }

      clickThenTypeThenClick
      |> serializePlan
      |> deserializePlan
      |> Expect.equal "round-trips with PreClickSelector populated" clickThenTypeThenClick
  ]
