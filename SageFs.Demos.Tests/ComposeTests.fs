/// Proves `Compose.plan` (demo-gif-plan.md §4.6, §5): a pure `StepLog` ->
/// `ComposePlan` planner. RED first — these fail against the `failwith` stub
/// until `Compose.fs` is implemented.
module SageFs.Demos.Tests.ComposeTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Demos.Domain
open SageFs.Demos.Compose

// ---------------------------------------------------------------------------
// A StepLog shaped like the §6.1 worked scenario (hrDashboardVscodeWeb from
// DomainTests): 5 steps, one of them (the dashboard click) with a non-empty
// pointer path, the rest with no recorded motion (e.g. `Await`, `Chord`).
// ---------------------------------------------------------------------------

let private scenarioId = ScenarioId.derive Capability.HotReload Client.VsCode AppKind.Web

let private stepRecord index caption segment pointerPath : StepRecord =
  { Index = index
    Caption = Caption.mk caption
    Segment = segment
    StartedMs = index * 1000
    EndedMs = index * 1000 + 800
    PointerPath = pointerPath
    ObservedAtMs = index * 1000 + 600
    Outcome = Outcome.Passed }

let private heroStepLog : StepLog =
  { ScenarioId = scenarioId
    Steps =
      [ stepRecord 0 "Run the web app" "/out/step-00.mkv" []
        stepRecord 1 "1/4 · Press Run on the session card" "/out/step-01.mkv"
          [ { X = 620; Y = 300 }; { X = 630; Y = 298 } ]
        stepRecord 2 "2/4 · Change the heading" "/out/step-02.mkv" []
        stepRecord 3 "3/4 · Save" "/out/step-03.mkv" []
        stepRecord 4 "4/4 · The running site repaints — no reload" "/out/step-04.mkv" [] ] }

let private editorLeftLayout : Map<ActorId, Rect> =
  Map.ofList [
    ActorId.VsCode, { X = 0; Y = 0; W = 704; H = 720 }
    ActorId.Dashboard, { X = 712; Y = 0; W = 568; H = 356 }
    ActorId.App, { X = 712; Y = 364; W = 568; H = 356 }
  ]

let private dashboardOnlyLayout : Map<ActorId, Rect> =
  Map.ofList [ ActorId.Dashboard, { X = 0; Y = 0; W = 1280; H = 720 } ]

// ---------------------------------------------------------------------------
// A hand-rolled FsCheck generator for StepLog. `Caption`/`ScenarioId` have
// private constructors with total smart constructors, so this builds through
// those rather than relying on FsCheck's structural auto-derivation (which
// cannot see inside a private DU case).
// ---------------------------------------------------------------------------

let private genPoint =
  Gen.map2 (fun x y -> { X = x; Y = y }) (Gen.choose (0, 1280)) (Gen.choose (0, 720))

let private genPointerPath = Gen.listOf genPoint

let private genOutcome = Gen.elements [ Outcome.Passed; Outcome.Failed; Outcome.Skipped ]

let private genStepRecord =
  gen {
    let! index = Gen.choose (0, 20)
    let! captionText = Gen.elements [ "a"; "Save the file"; "3/4 · Save"; String.replicate 90 "x" ]
    let! segment = Gen.elements [ "/out/step-00.mkv"; "/out/step-01.mkv"; "/out/step-02.mkv" ]
    let! startedMs = Gen.choose (0, 5000)
    let! durationMs = Gen.choose (0, 5000)
    let! path = genPointerPath
    let! observedMs = Gen.choose (0, 5000)
    let! outcome = genOutcome
    return
      { Index = index
        Caption = Caption.mk captionText
        Segment = segment
        StartedMs = startedMs
        EndedMs = startedMs + durationMs
        PointerPath = path
        ObservedAtMs = observedMs
        Outcome = outcome }
  }

let private genStepLog =
  gen {
    let! steps = Gen.listOf genStepRecord
    return { ScenarioId = scenarioId; Steps = steps }
  }

[<Tests>]
let tests =
  testList "Compose" [

    testCase "plan produces one caption per step, in step order (§4.6, §6.1)" <| fun _ ->
      let composePlan = plan heroStepLog editorLeftLayout Style.kanagawa
      composePlan.Captions
      |> List.map Caption.value
      |> Expect.equal
        "one caption per step, matching the StepLog's own step order"
        [ "Run the web app"
          "1/4 · Press Run on the session card"
          "2/4 · Change the heading"
          "3/4 · Save"
          "4/4 · The running site repaints — no reload" ]

    testCase "plan produces one pointer-path entry per step, empty where the step had no motion" <| fun _ ->
      let composePlan = plan heroStepLog editorLeftLayout Style.kanagawa
      composePlan.PointerPaths
      |> List.map List.isEmpty
      |> Expect.equal
        "only the dashboard-click step (index 1) carries a recorded pointer path"
        [ true; false; true; true; true ]

    testCase "plan carries exactly one non-empty pointer path for the §6.1 scenario's one click step" <| fun _ ->
      let composePlan = plan heroStepLog editorLeftLayout Style.kanagawa
      composePlan.PointerPaths
      |> List.filter (List.isEmpty >> not)
      |> List.length
      |> Expect.equal "one overlay-worthy pointer path (the click step)" 1

    testCase "plan carries each step's own StartedMs/EndedMs/ObservedAtMs as Timings, in step order (§4.6)" <| fun _ ->
      let composePlan = plan heroStepLog editorLeftLayout Style.kanagawa
      composePlan.Timings
      |> List.map (fun t -> t.StartedMs, t.EndedMs, t.ObservedAtMs)
      |> Expect.equal
        "one StepTiming per step, matching the StepLog's own timing fields"
        (heroStepLog.Steps |> List.map (fun s -> s.StartedMs, s.EndedMs, s.ObservedAtMs))

    testCase "plan segments line up with the StepLog's own segment paths" <| fun _ ->
      let composePlan = plan heroStepLog editorLeftLayout Style.kanagawa
      composePlan.Segments
      |> Expect.equal
        "segments in step order"
        [ "/out/step-00.mkv"; "/out/step-01.mkv"; "/out/step-02.mkv"; "/out/step-03.mkv"; "/out/step-04.mkv" ]

    testCase "plan resolves the magnifier from the editor pane when the layout has one (EditorLeft, §9)" <| fun _ ->
      let composePlan = plan heroStepLog editorLeftLayout Style.kanagawa
      composePlan.Magnifier
      |> Expect.equal "the VS Code pane's own rect" (Some { X = 0; Y = 0; W = 704; H = 720 })

    testCase "plan has no magnifier when the layout has no editor pane (DashboardOnly, §9)" <| fun _ ->
      let composePlan = plan heroStepLog dashboardOnlyLayout Style.kanagawa
      composePlan.Magnifier |> Expect.isNone "no editor pane to magnify"

    testCase "plan threads the given style through unchanged" <| fun _ ->
      let composePlan = plan heroStepLog editorLeftLayout Style.kanagawa
      composePlan.Style |> Expect.equal "style is passed through" Style.kanagawa

    testCase
      "property: Captions and PointerPaths always match the StepLog's own step count"
      <| fun _ ->
        Prop.forAll (Arb.fromGen genStepLog) (fun log ->
          let composePlan = plan log editorLeftLayout Style.kanagawa
          composePlan.Captions.Length = log.Steps.Length
          && composePlan.PointerPaths.Length = log.Steps.Length
          && composePlan.Segments.Length = log.Steps.Length
          && composePlan.Timings.Length = log.Steps.Length)
        |> Check.QuickThrowOnFailure
  ]
