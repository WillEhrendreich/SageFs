module SageFs.Tests.SessionCreationUxTests

open System
open Expecto
open Expecto.Flip
open SageFs

let mkSnap id = {
  SessionSnapshot.Id = id
  Name = None
  Status = SessionDisplayStatus.Starting
  IsActive = false
  Projects = ["test.fsproj"]
  EvalCount = 0
  UpSince = DateTime.UtcNow
  LastActivity = DateTime.UtcNow
  WorkingDirectory = "."
}

let creatingSessionGuardTests = testList "CreatingSession guard" [
  testCase "model starts with CreatingSession = false" <| fun () ->
    SageFsModel.initial.CreatingSession
    |> Expect.isFalse "should start false"

  testCase "CreateSession action sets CreatingSession true" <| fun () ->
    let model', effects =
      SageFsUpdate.update
        (SageFsMsg.Editor (EditorAction.CreateSession ["test.fsproj"]))
        SageFsModel.initial
    model'.CreatingSession
    |> Expect.isTrue "should be true after CreateSession"
    effects
    |> List.exists (function
      | SageFsEffect.Editor (EditorEffect.RequestSessionCreate _) -> true
      | _ -> false)
    |> Expect.isTrue "should emit RequestSessionCreate effect"

  testCase "duplicate CreateSession is blocked when already creating" <| fun () ->
    let model = { SageFsModel.initial with CreatingSession = true }
    let _, effects =
      SageFsUpdate.update
        (SageFsMsg.Editor (EditorAction.CreateSession ["test.fsproj"]))
        model
    effects
    |> Expect.isEmpty "should emit no effects when already creating"

  testCase "SessionCreated event clears CreatingSession" <| fun () ->
    let model = { SageFsModel.initial with CreatingSession = true }
    let model', _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.SessionCreated (mkSnap "test-123")))
        model
    model'.CreatingSession
    |> Expect.isFalse "should be false after SessionCreated"

  testCase "EvalFailed with 'Create failed' clears CreatingSession" <| fun () ->
    let model = { SageFsModel.initial with CreatingSession = true }
    let model', _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.EvalFailed ("", "Create failed: something went wrong")))
        model
    model'.CreatingSession
    |> Expect.isFalse "should be false after create failure"

  testCase "EvalFailed without 'Create failed' keeps CreatingSession" <| fun () ->
    let model = { SageFsModel.initial with CreatingSession = true }
    let model', _ =
      SageFsUpdate.update
        (SageFsMsg.Event (SageFsEvent.EvalFailed ("", "Some other error")))
        model
    model'.CreatingSession
    |> Expect.isTrue "should still be true for non-create errors"
]

let sessionsRenderTests = testList "Sessions panel creating indicator" [
  testCase "sessions region shows creating indicator when CreatingSession is true" <| fun () ->
    let model = { SageFsModel.initial with CreatingSession = true }
    let regions = SageFsRender.render model
    let sessionsRegion = regions |> List.find (fun r -> r.Id = "sessions")
    sessionsRegion.Content
    |> Expect.stringContains "should contain creating text" "⏳ Creating session..."

  testCase "sessions region hides creating indicator when false" <| fun () ->
    let regions = SageFsRender.render SageFsModel.initial
    let sessionsRegion = regions |> List.find (fun r -> r.Id = "sessions")
    sessionsRegion.Content.Contains("⏳ Creating session...")
    |> Expect.isFalse "should not contain creating text"

  testCase "dashboard parseSessionLines filters creating line" <| fun () ->
    let content =
      "  ses-abc [running] * (Test.fsproj) evals:5 up:2m dir:. last:just now\n\
       ⏳ Creating session...\n\
       ─── ↑↓ nav · Enter switch · Del stop · Ctrl+Tab cycle"
    let parsed = SageFs.Server.Dashboard.parseSessionLines content
    parsed
    |> List.length
    |> Expect.equal "should have 1 session" 1

  testCase "dashboard isCreatingSession detects creating line" <| fun () ->
    SageFs.Server.Dashboard.isCreatingSession "ses\n⏳ Creating session..."
    |> Expect.isTrue "should detect creating"

  testCase "dashboard isCreatingSession returns false without it" <| fun () ->
    SageFs.Server.Dashboard.isCreatingSession "ses\nother"
    |> Expect.isFalse "should not detect creating"
]

let tests = testList "Session creation UX" [
  creatingSessionGuardTests
  sessionsRenderTests
]
