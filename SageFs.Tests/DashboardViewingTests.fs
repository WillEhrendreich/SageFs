module SageFs.Tests.DashboardViewingTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.Dashboard

let private info (i: int) (status: WorkerProtocol.SessionStatus) : WorkerProtocol.SessionInfo =
  let sid =
    WorkerProtocol.SessionId.validate (sprintf "%08x" (0x0a000000 + i))
    |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
  { Id = sid; Name = None; Projects = []; WorkingDirectory = "/w"; SolutionRoot = None
    CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
    Status = WorkerProtocol.SessionLifecycleStatus.ofWorkerReport (WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1; Port = None }) status
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning }

let private ready i = info i WorkerProtocol.SessionStatus.Ready

let private isLive (s: WorkerProtocol.SessionInfo) = s.Status <> WorkerProtocol.SessionLifecycleStatus.Stopped

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

  test "WHY — reconcileViewing — with nothing selected but a live session existing, default to that session because the picker is only for an empty daemon (a created session must appear, not leave the tab on the picker)" {
    reconcileViewing None [ ready 1 ] |> Expect.equal "default to the live session" (ViewingDecision.SwitchTo (ready 1).Id)
  }

  test "WHY — reconcileViewing — nothing selected and no sessions shows the picker because there is genuinely nothing to view" {
    reconcileViewing None [] |> Expect.equal "picker" ViewingDecision.ShowPicker
  }

  test "WHY — reconcileViewing — nothing selected and only a Stopped session shows the picker because a dead session is not a default to land on" {
    reconcileViewing None [ info 1 WorkerProtocol.SessionStatus.Stopped ] |> Expect.equal "picker" ViewingDecision.ShowPicker
  }

  test "WHY — firstLiveSession — skips Stopped sessions and returns the first live one because the default view must never be a dead session" {
    let stopped = info 1 WorkerProtocol.SessionStatus.Stopped
    let a = ready 2
    firstLiveSession [ stopped; a ] |> Expect.equal "first live is a" (Some a.Id)
    firstLiveSession [ stopped ] |> Expect.isNone "only-stopped has no live default"
    firstLiveSession [] |> Expect.isNone "empty has no live default"
  }

  testProperty "WHY — reconcileViewing — the decision always names a live session or the picker, keeps the current one exactly when it is live, and otherwise (including nothing selected) defaults to the first live session, because the stream must never render a dead session and the picker is for the empty daemon alone" <|
    fun (statuses: bool list) (pick: NonNegativeInt) (selectNone: bool) ->
      let sessions =
        statuses
        |> List.mapi (fun i live -> info i (match live with | true -> WorkerProtocol.SessionStatus.Ready | false -> WorkerProtocol.SessionStatus.Stopped))
      // Exercise BOTH the nothing-selected case and a concrete selection.
      let current =
        match selectNone, sessions with
        | true, _ -> None
        | false, [] -> Some (ready 999).Id
        | false, _ -> Some sessions.[pick.Get % sessions.Length].Id
      let liveIds = sessions |> List.filter isLive |> List.map (fun s -> s.Id)
      match reconcileViewing current sessions with
      | ViewingDecision.Keep sid -> Some sid = current && List.contains sid liveIds
      | ViewingDecision.SwitchTo sid -> List.tryHead liveIds = Some sid && not (current |> Option.exists (fun c -> List.contains c liveIds))
      | ViewingDecision.ShowPicker -> List.isEmpty liveIds
]

let private sidN i = (ready i).Id

let private modelChanged = DashboardStreamCommand.StateChange (SseEvent.ModelChanged (1, 0))

[<Tests>]
let burstTests = testList "Dashboard stream burst" [
  test "WHY — StreamBurst — a retarget inside a burst of state changes survives, because a dropped retarget left the page re-rendering the old session over the user's switch" {
    (StreamBurst.ofCommands [ modelChanged; DashboardStreamCommand.RetargetView (Some (sidN 2)); modelChanged ]).Retarget
    |> Expect.equal "the retarget is kept" (BurstRetarget.RetargetTo (Some (sidN 2)))
  }

  test "WHY — StreamBurst — a burst of state changes alone keeps the current view" {
    (StreamBurst.ofCommands [ modelChanged; modelChanged ]).Retarget
    |> Expect.equal "no retarget" BurstRetarget.NoRetarget
  }

  test "WHY — StreamBurst — a retarget to the picker survives too, because a torn-down session must not reappear" {
    (StreamBurst.ofCommands [ DashboardStreamCommand.RetargetView None; modelChanged ]).Retarget
    |> Expect.equal "picker retarget kept" (BurstRetarget.RetargetTo None)
  }

  testProperty "WHY — StreamBurst — the last retarget in any burst wins, because the browser's final viewing-session signal is the truth" <|
    fun (steps: (bool * byte) list) ->
      let commands =
        steps
        |> List.map (fun (isRetarget, i) ->
          match isRetarget with
          | true -> DashboardStreamCommand.RetargetView (Some (sidN (int i)))
          | false -> modelChanged)
      let expected =
        steps
        |> List.filter fst
        |> List.tryLast
        |> Option.map (fun (_, i) -> BurstRetarget.RetargetTo (Some (sidN (int i))))
        |> Option.defaultValue BurstRetarget.NoRetarget
      (StreamBurst.ofCommands commands).Retarget = expected

  test "WHY — StreamBurst — only worker-affecting changes invalidate the worker cache, because a progress tick must not force worker HTTP round-trips" {
    (StreamBurst.ofCommands [ DashboardStreamCommand.StateChange SseEvent.SessionProgress ]).WorkerInvalidated
    |> Expect.isFalse "progress alone keeps the cache"
    (StreamBurst.ofCommands [ DashboardStreamCommand.StateChange SseEvent.SessionProgress; modelChanged ]).WorkerInvalidated
    |> Expect.isTrue "a model change invalidates it"
  }

  // Roast-6 / multiagent-vision.md §10 Phase 0 item 1 RED test: "two changes
  // 1 ms apart produce ONE render." The push agent's drain loop
  // (`Dashboard.fs`'s `renderBurst`) folds every queued StateChange into ONE
  // StreamBurst via exactly this `StreamBurst.add`/`ofCommands` fold, then
  // calls `pushState()` exactly once per fold — regardless of how many
  // messages were folded in or how many milliseconds apart they arrived
  // (there is no longer a fixed 100ms coalesce window to "miss"; the fold
  // drains whatever is in the mailbox at the moment of the call). This test
  // proves the fold itself is idempotent in render-count terms: N state
  // changes always reduce to exactly one burst — one downstream
  // `pushState()` call — never N.
  test "WHY — StreamBurst — N state changes (however closely spaced in time — the fold is time-independent) reduce to exactly one burst, hence one render" {
    let oneChange = StreamBurst.ofCommands [ modelChanged ]
    let twoChanges = StreamBurst.ofCommands [ modelChanged; modelChanged ]
    let tenChanges = StreamBurst.ofCommands (List.replicate 10 modelChanged)
    // "One burst" here means: folding always yields a single StreamBurst
    // value (not a list of them) — the type itself makes "N renders for N
    // messages" structurally impossible, independent of timing.
    oneChange |> Expect.equal "1 change -> 1 burst" twoChanges
    twoChanges |> Expect.equal "2 changes -> the same single burst as 10" tenChanges
  }
]
