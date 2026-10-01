/// Who lets a click's guards go. The click waits on its getter's thread, and the thread runs the getter; either can
/// finish first, and either can be the last to touch the guards. The rule is one pure function, so every order of the
/// two events is settled here, and the guard simulation folds the same function through seeded schedules.
module SageFs.Tests.ClickLifecycleTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features

/// Each event happens at most once in a click: a thread ends once and a click gives up once. These are every order.
let private events : Arbitrary<ClickEvent list> =
  Gen.elements
    [ []
      [ ClickEvent.ThreadEnded ]
      [ ClickEvent.GaveUp ]
      [ ClickEvent.ThreadEnded; ClickEvent.GaveUp ]
      [ ClickEvent.GaveUp; ClickEvent.ThreadEnded ] ]
  |> Arb.fromGen

let private fold (list: ClickEvent list) : (ClickPhase * GuardDuty) list =
  list
  |> List.scan (fun (phase, _) event -> ClickLifecycle.step phase event) (ClickPhase.Running, GuardDuty.NothingToDo)
  |> List.tail

[<Tests>]
let clickLifecycleTests =
  testList "click lifecycle (who lets the guards go)" [

    testCase "WHY - the thread ending first leaves the release to the click, which sees it when it looks" <| fun _ ->
      ClickLifecycle.step ClickPhase.Running ClickEvent.ThreadEnded
      |> Expect.equal "ended, the click releases" (ClickPhase.Ended, GuardDuty.LeaveToTheClick)

    testCase "WHY - the click giving up first leaves the guards on for the thread, which may still run guarded code" <| fun _ ->
      ClickLifecycle.step ClickPhase.Running ClickEvent.GaveUp
      |> Expect.equal "abandoned, the thread releases" (ClickPhase.Abandoned, GuardDuty.LeaveToTheThread)

    testCase "WHY - an abandoned thread that ends releases the guards itself, as the last to use them" <| fun _ ->
      ClickLifecycle.step ClickPhase.Abandoned ClickEvent.ThreadEnded
      |> Expect.equal "ended, released now" (ClickPhase.Ended, GuardDuty.ReleaseNow)

    testCase "WHY - a thread that ended in the instant before the click's verdict was never abandoned: the click releases now" <| fun _ ->
      ClickLifecycle.step ClickPhase.Ended ClickEvent.GaveUp
      |> Expect.equal "ended, released now" (ClickPhase.Ended, GuardDuty.ReleaseNow)

    testCase "WHY - an event that cannot follow the one before changes nothing" <| fun _ ->
      ClickLifecycle.step ClickPhase.Ended ClickEvent.ThreadEnded
      |> Expect.equal "still ended" (ClickPhase.Ended, GuardDuty.NothingToDo)
      ClickLifecycle.step ClickPhase.Abandoned ClickEvent.GaveUp
      |> Expect.equal "still abandoned" (ClickPhase.Abandoned, GuardDuty.NothingToDo)

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 300 } "WHY - whatever order the events come in, the guards are told to be released now at most once, and a click never goes back to running"
      (Prop.forAll events (fun list ->
        let steps = fold list
        let releases = steps |> List.filter (fun (_, duty) -> duty = GuardDuty.ReleaseNow) |> List.length
        let neverBackToRunning = steps |> List.forall (fun (phase, _) -> phase <> ClickPhase.Running)
        releases <= 1 && neverBackToRunning))

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 300 } "WHY - once the thread has ended somebody owes the release, and before that nobody does"
      (Prop.forAll events (fun list ->
        let steps = fold list
        let ended = steps |> List.exists (fun (phase, _) -> phase = ClickPhase.Ended)
        let owed = steps |> List.filter (fun (_, duty) -> duty = GuardDuty.ReleaseNow || duty = GuardDuty.LeaveToTheClick) |> List.length
        match ended with
        | true -> owed >= 1
        | false -> owed = 0))
  ]
