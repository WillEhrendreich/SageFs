module SageFs.Tests.SageFsUpdatePropertyTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.SharedGenerators

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 200 }
let private pick gen = (Gen.sample 1 gen).[0]

// ── Generators ──

let private genOutputLine =
  gen {
    let! sid = genSessionId |> Gen.map WorkerProtocol.SessionId.value
    let! text =
      Gen.elements [
        "val x = 42"
        "it: int = 1"
        "error FS0001"
        ""
        "module Foo"
      ]
    let! kind = Gen.elements [ OutputKind.Result; OutputKind.Error; OutputKind.Info; OutputKind.System ]
    return {
      Kind = kind
      Text = text
      Timestamp = DateTime.UtcNow
      SessionId = sid
    }
  }

let private genSessionDisplayStatus =
  Gen.oneof [
    Gen.constant SessionDisplayStatus.Running
    Gen.constant SessionDisplayStatus.Starting
    Gen.constant SessionDisplayStatus.Suspended
    Gen.constant SessionDisplayStatus.Stale
    Gen.constant SessionDisplayStatus.Restarting
    Gen.constant (SessionDisplayStatus.Errored "test error")
  ]

let private genSessionSnapshot =
  gen {
    let! sid = genSessionId
    let! status = genSessionDisplayStatus
    return {
      Id = sid
      Name = Some "test-session"
      Projects = [ "Test.fsproj" ]
      Status = status
      LastActivity = DateTime.UtcNow
      EvalCount = 0
      UpSince = DateTime.UtcNow
      WorkingDirectory = "C:\\test"
    }
  }

/// Safe events that don't require complex dependent state.
let private genSafeEvent =
  let fixedSid = testSessionId "deadbeef" |> WorkerProtocol.SessionId.value
  Gen.oneof [
    Gen.constant (SageFsEvent.SessionStopped fixedSid)
    genOutputLine |> Gen.map SageFsEvent.OutputEmitted
    gen {
      let! status = genSessionDisplayStatus
      return SageFsEvent.SessionStatusChanged(fixedSid, status)
    }
    gen {
      let! snaps = Gen.listOfLength 2 genSessionSnapshot
      return SageFsEvent.SessionsRefreshed snaps
    }
    Gen.constant (SageFsEvent.EvalCancelled fixedSid)
    Gen.constant (SageFsEvent.LiveTestingEnabled)
    Gen.constant (SageFsEvent.LiveTestingDisabled)
  ]

// ── Property Tests ──

let sageFsUpdatePropertyTests =
  testList "SageFsUpdate algebraic properties" [

    // 1. CycleTheme is N-periodic (N = number of theme presets)
    testCase "CycleTheme is periodic — cycling through all themes returns to original" <| fun _ ->
      let themeCount = ThemePresets.all.Length
      let model = SageFsModel.initial ()
      let originalTheme = model.Theme
      let originalName = model.ThemeName
      let cycled =
        (model, [ 1 .. themeCount ])
        ||> List.fold (fun m _ ->
          SageFsUpdate.update SageFsMsg.CycleTheme m |> fst)
      cycled.Theme
      |> Expect.equal "theme should return to original after full cycle" originalTheme
      cycled.ThemeName
      |> Expect.equal "theme name should return to original after full cycle" originalName

    testCase "CycleTheme produces a different theme on first cycle" <| fun _ ->
      let model = SageFsModel.initial ()
      let after, _ = SageFsUpdate.update SageFsMsg.CycleTheme model
      after.ThemeName
      |> Expect.notEqual "theme should change after one cycle" model.ThemeName

    // 2. EnableLiveTesting + DisableLiveTesting roundtrip
    testCase "EnableLiveTesting then DisableLiveTesting restores activation state" <| fun _ ->
      let model = SageFsModel.initial ()
      let originalActivation = model.LiveTesting.TestState.Activation
      let enabled, _ = SageFsUpdate.update SageFsMsg.EnableLiveTesting model
      let restored, _ = SageFsUpdate.update SageFsMsg.DisableLiveTesting enabled
      restored.LiveTesting.TestState.Activation
      |> Expect.equal "activation should return to original" originalActivation

    testCase "DisableLiveTesting is idempotent on initial model" <| fun _ ->
      let model = SageFsModel.initial ()
      let once, _ = SageFsUpdate.update SageFsMsg.DisableLiveTesting model
      let twice, _ = SageFsUpdate.update SageFsMsg.DisableLiveTesting once
      twice.LiveTesting.TestState.Activation
      |> Expect.equal "double disable same as single" once.LiveTesting.TestState.Activation

    // 3. SessionsRefreshed is idempotent
    testCase "SessionsRefreshed applied twice yields same session count as once" <| fun _ ->
      let snap = {
        Id = testSessionId "aabbccdd"
        Name = Some "s1"
        Projects = [ "P.fsproj" ]
        Status = SessionDisplayStatus.Running
        LastActivity = DateTime.UtcNow
        EvalCount = 5
        UpSince = DateTime.UtcNow
        WorkingDirectory = "C:\\code"
      }
      let event = SageFsEvent.SessionsRefreshed [ snap ]
      let model = SageFsModel.initial ()
      let once, _ = SageFsUpdate.update (SageFsMsg.Event event) model
      let twice, _ = SageFsUpdate.update (SageFsMsg.Event event) once
      twice.Sessions.Sessions
      |> List.length
      |> Expect.equal "session count should be idempotent" (once.Sessions.Sessions |> List.length)

    testPropertyWithConfig cfg "SessionsRefreshed with random snapshots is idempotent on session list length" <|
      fun () ->
        let snaps = Gen.sample 3 genSessionSnapshot |> Array.toList
        let event = SageFsEvent.SessionsRefreshed snaps
        let model = SageFsModel.initial ()
        let once, _ = SageFsUpdate.update (SageFsMsg.Event event) model
        let twice, _ = SageFsUpdate.update (SageFsMsg.Event event) once
        List.length twice.Sessions.Sessions = List.length once.Sessions.Sessions

    // 4. Update never crashes on any safe SageFsEvent (totality)
    testPropertyWithConfig cfg "update never throws on any safe SageFsEvent" <|
      fun () ->
        let event = pick genSafeEvent
        let model = SageFsModel.initial ()
        try
          SageFsUpdate.update (SageFsMsg.Event event) model |> ignore
          true
        with _ ->
          false

    testPropertyWithConfig cfg "update never throws on CycleTheme from any cycled state" <|
      fun (NonNegativeInt n) ->
        let model = SageFsModel.initial ()
        let cycled =
          (model, [ 1 .. (n % 20) ])
          ||> List.fold (fun m _ ->
            SageFsUpdate.update SageFsMsg.CycleTheme m |> fst)
        try
          SageFsUpdate.update SageFsMsg.CycleTheme cycled |> ignore
          true
        with _ ->
          false

    // 5. Output is additive
    testPropertyWithConfig cfg "OutputEmitted increases or maintains output count" <|
      fun () ->
        let line = pick genOutputLine
        let model = SageFsModel.initial ()
        let before = model.RecentOutput.ActiveCount(model.Sessions.ActiveSessionId)
        let after, _ =
          SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.OutputEmitted line)) model
        let afterCount = after.RecentOutput.ActiveCount(after.Sessions.ActiveSessionId)
        afterCount >= before

    testCase "multiple OutputEmitted events accumulate lines" <| fun _ ->
      let sid = testSessionId "aabb0011" |> WorkerProtocol.SessionId.value
      let mkLine text = {
        Kind = OutputKind.Result
        Text = text
        Timestamp = DateTime.UtcNow
        SessionId = sid
      }
      let model = SageFsModel.initial ()
      let final =
        (model, [ "line1"; "line2"; "line3" ])
        ||> List.fold (fun m text ->
          SageFsUpdate.update (SageFsMsg.Event (SageFsEvent.OutputEmitted (mkLine text))) m
          |> fst)
      let buf = final.RecentOutput.GetBuffer(sid)
      buf.Count
      |> Expect.equal "should have 3 output lines" 3
  ]
