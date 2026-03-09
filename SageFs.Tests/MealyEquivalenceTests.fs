module SageFs.Tests.MealyEquivalenceTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.SharedGenerators

// ============================================================
// Mealy Machine Equivalence Property Tests
// ============================================================
// The Elm update function is a Mealy machine:
//   update : Msg → Model → Model × Effect list
// These tests verify the machine's algebraic properties:
//   determinism, commutativity of independent messages,
//   and idempotence of toggle pairs.

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 200 }
let private pick gen = (Gen.sample 1 gen).[0]

let private fixedSid = testSessionId "deadbeef" |> WorkerProtocol.SessionId.value

/// Generate safe messages that don't require external state.
let private genSafeMsg =
  Gen.oneof [
    Gen.constant SageFsMsg.CycleTheme
    Gen.constant SageFsMsg.EnableLiveTesting
    Gen.constant SageFsMsg.DisableLiveTesting
    Gen.constant SageFsMsg.CycleRunPolicy
    Gen.constant SageFsMsg.ToggleCoverage
    Gen.constant (SageFsMsg.Event (SageFsEvent.SessionStopped fixedSid))
    Gen.constant (SageFsMsg.Event SageFsEvent.LiveTestingEnabled)
    Gen.constant (SageFsMsg.Event SageFsEvent.LiveTestingDisabled)
    Gen.constant (SageFsMsg.Event (SageFsEvent.EvalCancelled fixedSid))
  ]

/// Fold a message sequence through update, collecting effects.
let private foldMsgs (msgs: SageFsMsg list) (model: SageFsModel) =
  msgs
  |> List.fold (fun (m, effs) msg ->
    let m', e = SageFsUpdate.update msg m
    (m', effs @ e)
  ) (model, [])

/// Compare two models on their observable fields (excluding timestamps).
let private modelsEquivalent (a: SageFsModel) (b: SageFsModel) =
  a.Theme = b.Theme
  && a.ThemeName = b.ThemeName
  && a.CreatingSession = b.CreatingSession
  && a.LiveTesting.TestState.Activation = b.LiveTesting.TestState.Activation
  && a.LiveTesting.TestState.RunPolicies = b.LiveTesting.TestState.RunPolicies
  && a.LiveTesting.TestState.CoverageDisplay = b.LiveTesting.TestState.CoverageDisplay
  && a.Sessions.Sessions.Length = b.Sessions.Sessions.Length

[<Tests>]
let mealyEquivalenceTests = testList "Mealy machine equivalence" [

  testList "determinism" [
    testPropertyWithConfig cfg "same input sequence → same model" <|
      fun () ->
        let msgs = Gen.sample 5 genSafeMsg |> Array.toList
        let m1 = SageFsModel.initial ()
        let m2 = SageFsModel.initial ()
        let result1, effs1 = foldMsgs msgs m1
        let result2, effs2 = foldMsgs msgs m2
        modelsEquivalent result1 result2 && effs1 = effs2

    test "replay of fixed sequence is deterministic" {
      let msgs = [
        SageFsMsg.CycleTheme
        SageFsMsg.EnableLiveTesting
        SageFsMsg.CycleTheme
        SageFsMsg.DisableLiveTesting
        SageFsMsg.CycleRunPolicy
        SageFsMsg.ToggleCoverage
      ]
      let r1, e1 = foldMsgs msgs (SageFsModel.initial ())
      let r2, e2 = foldMsgs msgs (SageFsModel.initial ())
      modelsEquivalent r1 r2 |> Expect.isTrue "models must match"
      e1 |> Expect.equal "effects must match" e2
    }
  ]

  testList "commutativity of independent messages" [
    test "CycleTheme and ToggleCoverage commute" {
      let m0 = SageFsModel.initial ()
      let ab, _ = foldMsgs [ SageFsMsg.CycleTheme; SageFsMsg.ToggleCoverage ] m0
      let ba, _ = foldMsgs [ SageFsMsg.ToggleCoverage; SageFsMsg.CycleTheme ] m0
      ab.Theme |> Expect.equal "theme same regardless of order" ba.Theme
      ab.LiveTesting.TestState.CoverageDisplay
      |> Expect.equal "coverage same regardless of order" ba.LiveTesting.TestState.CoverageDisplay
    }

    test "Enable then CycleRunPolicy commutes" {
      let m0 = SageFsModel.initial ()
      let ab, _ = foldMsgs [ SageFsMsg.EnableLiveTesting; SageFsMsg.CycleRunPolicy ] m0
      let ba, _ = foldMsgs [ SageFsMsg.CycleRunPolicy; SageFsMsg.EnableLiveTesting ] m0
      ab.LiveTesting.TestState.Activation
      |> Expect.equal "activation same" ba.LiveTesting.TestState.Activation
    }
  ]

  testList "involution pairs" [
    test "Enable then Disable is involution on activation" {
      let m0 = SageFsModel.initial ()
      let original = m0.LiveTesting.TestState.Activation
      let roundtripped, _ =
        foldMsgs [ SageFsMsg.EnableLiveTesting; SageFsMsg.DisableLiveTesting ] m0
      roundtripped.LiveTesting.TestState.Activation
      |> Expect.equal "activation restored" original
    }

    testPropertyWithConfig cfg "ToggleCoverage twice restores original" <|
      fun () ->
        let m0 = SageFsModel.initial ()
        let original = m0.LiveTesting.TestState.CoverageDisplay
        let toggled, _ = foldMsgs [ SageFsMsg.ToggleCoverage; SageFsMsg.ToggleCoverage ] m0
        toggled.LiveTesting.TestState.CoverageDisplay = original
  ]

  testList "totality" [
    testPropertyWithConfig cfg "random safe message sequence never throws" <|
      fun () ->
        let msgs = Gen.sample 10 genSafeMsg |> Array.toList
        let m0 = SageFsModel.initial ()
        try
          foldMsgs msgs m0 |> ignore
          true
        with _ -> false

    testPropertyWithConfig cfg "single safe message from any safe-reachable state never throws" <|
      fun () ->
        let prefix = Gen.sample 5 genSafeMsg |> Array.toList
        let m0 = SageFsModel.initial ()
        let reachable, _ = foldMsgs prefix m0
        let msg = pick genSafeMsg
        try
          SageFsUpdate.update msg reachable |> ignore
          true
        with _ -> false
  ]
]
