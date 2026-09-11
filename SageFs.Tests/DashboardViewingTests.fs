module SageFs.Tests.DashboardViewingTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Server.Dashboard

let private info (i: int) (status: WorkerProtocol.SessionStatus) : WorkerProtocol.SessionInfo =
  let sid =
    WorkerProtocol.SessionId.validate (sprintf "%08x" (0x0a000000 + i))
    |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
  { Id = sid; Name = None; Projects = []; WorkingDirectory = "/w"; SolutionRoot = None
    CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
    Status = status; FaultReason = None; WorkerPid = None; WorkerPort = None
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning }

let private ready i = info i WorkerProtocol.SessionStatus.Ready

let private isLive (s: WorkerProtocol.SessionInfo) = s.Status <> WorkerProtocol.SessionStatus.Stopped

[<Tests>]
let tests = testList "Dashboard viewing reconcile" [
  test "WHY — reconcileViewing — a viewed session that still exists stays viewed because a push must never move the user" {
    let a, b = ready 1, ready 2
    reconcileViewing (Some b.Id) [ a; b ] |> Expect.equal "keep b" (ViewingDecision.Keep b.Id)
  }

  test "WHY — reconcileViewing — a viewed session that vanished moves to the first live session because the header otherwise shows a dead session as Uninitialized" {
    let gone, a = ready 9, ready 1
    reconcileViewing (Some gone.Id) [ a ] |> Expect.equal "switch to a" (ViewingDecision.SwitchTo a.Id)
  }

  test "WHY — reconcileViewing — a Stopped session is not live because it has no worker to show" {
    let stopped = info 1 WorkerProtocol.SessionStatus.Stopped
    let a = ready 2
    reconcileViewing (Some stopped.Id) [ stopped; a ] |> Expect.equal "skip stopped" (ViewingDecision.SwitchTo a.Id)
  }

  test "WHY — reconcileViewing — when the viewed session vanished and none are live the picker shows because there is nothing left to view" {
    reconcileViewing (Some (ready 9).Id) [] |> Expect.equal "picker" ViewingDecision.ShowPicker
  }

  test "WHY — reconcileViewing — with nothing selected the picker stays because the landing page waits for the user's click" {
    reconcileViewing None [ ready 1 ] |> Expect.equal "picker stays" ViewingDecision.ShowPicker
  }

  testProperty "WHY — reconcileViewing — the decision always names a live session or the picker, and keeps the current one exactly when it is live, because the stream must never render a dead session" <|
    fun (statuses: bool list) (pick: NonNegativeInt) ->
      let sessions =
        statuses
        |> List.mapi (fun i live -> info i (match live with | true -> WorkerProtocol.SessionStatus.Ready | false -> WorkerProtocol.SessionStatus.Stopped))
      let current =
        match sessions with
        | [] -> Some (ready 999).Id
        | _ -> Some sessions.[pick.Get % sessions.Length].Id
      let liveIds = sessions |> List.filter isLive |> List.map (fun s -> s.Id)
      match reconcileViewing current sessions with
      | ViewingDecision.Keep sid -> Some sid = current && List.contains sid liveIds
      | ViewingDecision.SwitchTo sid -> List.tryHead liveIds = Some sid && not (current |> Option.exists (fun c -> List.contains c liveIds))
      | ViewingDecision.ShowPicker -> List.isEmpty liveIds
]
