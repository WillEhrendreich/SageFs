module SageFs.Tests.SessionDisplayTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

let now = DateTime(2026, 2, 14, 12, 0, 0)

let mkInfo id status lastActive : SessionInfo =
  { Id = id
    Name = None
    Projects = ["Test.fsproj"]
    WorkingDirectory = @"C:\Code"
    SolutionRoot = None
    CreatedAt = DateTime(2026, 2, 14, 10, 0, 0)
    LastActivity = lastActive
    Status = status
    WorkerPid = Some 1234 }

[<Tests>]
let displayStatusTests = testList "SessionDisplay.displayStatus" [
  testCase "Ready session maps to Running" <| fun _ ->
    let info = mkInfo "s1" SessionStatus.Ready now
    SessionDisplay.displayStatus now info
    |> Expect.equal "should be Running" SessionDisplayStatus.Running

  testCase "Evaluating session maps to Running" <| fun _ ->
    let info = mkInfo "s1" SessionStatus.Evaluating now
    SessionDisplay.displayStatus now info
    |> Expect.equal "should be Running" SessionDisplayStatus.Running

  testCase "Ready but stale maps to Stale" <| fun _ ->
    let staleTime = now.AddMinutes(-15.0)
    let info = mkInfo "s1" SessionStatus.Ready staleTime
    SessionDisplay.displayStatus now info
    |> Expect.equal "should be Stale" SessionDisplayStatus.Stale

  testCase "Starting maps to Starting" <| fun _ ->
    let info = mkInfo "s1" SessionStatus.Starting now
    SessionDisplay.displayStatus now info
    |> Expect.equal "should be Starting" SessionDisplayStatus.Starting

  testCase "Faulted maps to Errored" <| fun _ ->
    let info = mkInfo "s1" SessionStatus.Faulted now
    SessionDisplay.displayStatus now info
    |> function
      | SessionDisplayStatus.Errored _ -> ()
      | other -> failwith (sprintf "Expected Errored, got %A" other)

  testCase "Restarting maps to Restarting" <| fun _ ->
    let info = mkInfo "s1" SessionStatus.Restarting now
    SessionDisplay.displayStatus now info
    |> Expect.equal "should be Restarting" SessionDisplayStatus.Restarting

  testCase "Stopped maps to Errored" <| fun _ ->
    let info = mkInfo "s1" SessionStatus.Stopped now
    SessionDisplay.displayStatus now info
    |> function
      | SessionDisplayStatus.Errored msg ->
        msg |> Expect.stringContains "should mention stopped" "stopped"
      | other -> failwith (sprintf "Expected Errored, got %A" other)

  testCase "Evaluating but stale maps to Stale" <| fun _ ->
    let staleTime = now.AddMinutes(-15.0)
    let info = mkInfo "s1" SessionStatus.Evaluating staleTime
    SessionDisplay.displayStatus now info
    |> Expect.equal "should be Stale" SessionDisplayStatus.Stale

  testCase "exactly 10min idle is not stale (strict >)" <| fun _ ->
    let tenMin = now.AddMinutes(-10.0)
    let info = mkInfo "s1" SessionStatus.Ready tenMin
    SessionDisplay.displayStatus now info
    |> Expect.equal "should be Running" SessionDisplayStatus.Running

  testCase "10min + 1sec idle is stale" <| fun _ ->
    let overTen = now.AddMinutes(-10.0).AddSeconds(-1.0)
    let info = mkInfo "s1" SessionStatus.Ready overTen
    SessionDisplay.displayStatus now info
    |> Expect.equal "should be Stale" SessionDisplayStatus.Stale
]

[<Tests>]
let snapshotTests = testList "SessionDisplay.snapshot" [
  testCase "active session marked as active" <| fun _ ->
    let info = mkInfo "s1" SessionStatus.Ready now
    let snap = SessionDisplay.snapshot now (ActiveSession.Viewing "s1") info
    snap.IsActive |> Expect.isTrue "should be active"

  testCase "inactive session marked as not active" <| fun _ ->
    let info = mkInfo "s1" SessionStatus.Ready now
    let snap = SessionDisplay.snapshot now (ActiveSession.Viewing "s2") info
    snap.IsActive |> Expect.isFalse "should not be active"

  testCase "awaiting session means no session is active" <| fun _ ->
    let info = mkInfo "s1" SessionStatus.Ready now
    let snap = SessionDisplay.snapshot now ActiveSession.AwaitingSession info
    snap.IsActive |> Expect.isFalse "should not be active when awaiting"

  testCase "snapshot preserves projects" <| fun _ ->
    let info = mkInfo "s1" SessionStatus.Ready now
    let snap = SessionDisplay.snapshot now ActiveSession.AwaitingSession info
    snap.Projects |> Expect.equal "should match" ["Test.fsproj"]
]

[<Tests>]
let registryViewTests = testList "SessionDisplay.registryView" [
  testCase "builds view from session list" <| fun _ ->
    let infos = [
      mkInfo "s1" SessionStatus.Ready now
      mkInfo "s2" SessionStatus.Evaluating now
    ]
    let view = SessionDisplay.registryView now (ActiveSession.Viewing "s1") infos None StandbyInfo.NoPool
    view.Sessions.Length |> Expect.equal "should have 2 sessions" 2
    view.ActiveSessionId |> Expect.equal "active is Viewing s1" (ActiveSession.Viewing "s1")

  testCase "empty session list with awaiting" <| fun _ ->
    let view = SessionDisplay.registryView now ActiveSession.AwaitingSession [] None StandbyInfo.NoPool
    view.Sessions |> Expect.equal "should be empty" []
    view.ActiveSessionId |> Expect.equal "should be awaiting" ActiveSession.AwaitingSession
]

[<Tests>]
let affordanceTests = testList "SessionDisplay.sessionAffordances" [
  testCase "inactive session has Switch enabled" <| fun _ ->
    let snap = {
      Id = "s1"; Name = None; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = now; EvalCount = 5
      UpSince = now.AddHours(-1.0); IsActive = false; WorkingDirectory = "" }
    let affordances = SessionDisplay.sessionAffordances Map.empty snap
    affordances
    |> List.exists (fun a -> a.Label = "Switch" && a.Enabled)
    |> Expect.isTrue "should have enabled Switch"

  testCase "active session has Switch disabled" <| fun _ ->
    let snap = {
      Id = "s1"; Name = None; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = now; EvalCount = 5
      UpSince = now.AddHours(-1.0); IsActive = true; WorkingDirectory = "" }
    let affordances = SessionDisplay.sessionAffordances Map.empty snap
    affordances
    |> List.exists (fun a -> a.Label = "Switch" && not a.Enabled)
    |> Expect.isTrue "Switch should be disabled for active"

  testCase "errored session has Restart affordance" <| fun _ ->
    let snap = {
      Id = "s1"; Name = None; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Errored "crash"
      LastActivity = now; EvalCount = 0
      UpSince = now.AddHours(-1.0); IsActive = false; WorkingDirectory = "" }
    let affordances = SessionDisplay.sessionAffordances Map.empty snap
    affordances
    |> List.exists (fun a -> a.Label = "Restart")
    |> Expect.isTrue "should have Restart"

  testCase "running session has no Restart affordance" <| fun _ ->
    let snap = {
      Id = "s1"; Name = None; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = now; EvalCount = 5
      UpSince = now.AddHours(-1.0); IsActive = true; WorkingDirectory = "" }
    let affordances = SessionDisplay.sessionAffordances Map.empty snap
    affordances
    |> List.exists (fun a -> a.Label = "Restart")
    |> Expect.isFalse "should not have Restart"

  testCase "active running session has no Stop affordance" <| fun _ ->
    let snap = {
      Id = "s1"; Name = None; Projects = ["Test.fsproj"]
      Status = SessionDisplayStatus.Running
      LastActivity = now; EvalCount = 0
      UpSince = now; IsActive = true; WorkingDirectory = "" }
    let affordances = SessionDisplay.sessionAffordances Map.empty snap
    affordances
    |> List.exists (fun a -> a.Label = "Stop")
    |> Expect.isFalse "no Stop for active running session"

  testCase "stale session has Stop even when active" <| fun _ ->
    let snap = {
      Id = "s1"; Name = None; Projects = []
      Status = SessionDisplayStatus.Stale
      LastActivity = now; EvalCount = 0
      UpSince = now; IsActive = true; WorkingDirectory = "" }
    let affordances = SessionDisplay.sessionAffordances Map.empty snap
    affordances
    |> List.exists (fun a -> a.Label = "Stop")
    |> Expect.isTrue "Stop should show for stale session"

  testCase "inactive session has Stop affordance" <| fun _ ->
    let snap = {
      Id = "s1"; Name = None; Projects = []
      Status = SessionDisplayStatus.Running
      LastActivity = now; EvalCount = 0
      UpSince = now; IsActive = false; WorkingDirectory = "" }
    let affordances = SessionDisplay.sessionAffordances Map.empty snap
    affordances
    |> List.exists (fun a -> a.Label = "Stop")
    |> Expect.isTrue "Stop should show for inactive session"
]

[<Tests>]
let activeSessionTests = testList "ActiveSession" [
  testCase "sessionId returns Some for Viewing" <| fun _ ->
    ActiveSession.sessionId (ActiveSession.Viewing "s1")
    |> Expect.equal "should be Some s1" (Some "s1")

  testCase "sessionId returns None for AwaitingSession" <| fun _ ->
    ActiveSession.sessionId ActiveSession.AwaitingSession
    |> Expect.equal "should be None" None

  testCase "isViewing true for matching id" <| fun _ ->
    ActiveSession.isViewing "s1" (ActiveSession.Viewing "s1")
    |> Expect.isTrue "should match"

  testCase "isViewing false for different id" <| fun _ ->
    ActiveSession.isViewing "s2" (ActiveSession.Viewing "s1")
    |> Expect.isFalse "should not match"

  testCase "isViewing false for AwaitingSession" <| fun _ ->
    ActiveSession.isViewing "s1" ActiveSession.AwaitingSession
    |> Expect.isFalse "awaiting is never viewing"
]
