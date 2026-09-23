/// `resolveSessionStatus` used to take a worker's raw `GetStatus` reply at
/// face value. A worker that has not caught up to its own death yet — the
/// exact race `SessionLifecycleStatus.ofWorkerReport` (WorkerProtocol.fs) was
/// made sticky for — could still make `/health`'s label say "Starting" while
/// its own `health` field (computed off the registry via
/// `SessionHealth.classify`) said Faulted: two fields on one response
/// disagreeing about the same session. This pins that a lagging reply can no
/// longer produce that disagreement.
module SageFs.Tests.ResolveSessionStatusReconciliationTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Server.McpServer

/// A worker that is still, wrongly, answering "Starting" — the shape of a
/// hung process the parent-death watchdog has not reaped yet, or a reply
/// that raced a fault the daemon already learned about through another
/// channel (WorkerExited, a spawn failure).
let private staleWorkerStillSayingStarting : SessionProxy =
  fun msg ->
    async {
      match msg with
      | WorkerMessage.GetStatus rid ->
        let snap : WorkerStatusSnapshot = {
          Status = SessionStatus.Starting
          StatusMessage = None
          EvalCount = 0
          AvgDurationMs = 0L
          MinDurationMs = 0L
          MaxDurationMs = 0L
          Projects = []
          CoreVersion = "0.0.0-test"
        }
        return WorkerResponse.StatusResult(rid, snap)
      | _ ->
        return WorkerResponse.WorkerError(SageFsError.WorkerSpawnFailed "unexpected message")
    }

let private faultedSession : SessionInfo =
  { Id = SessionId.newId ()
    Name = None
    Projects = []
    WorkingDirectory = "/tmp/resolve-session-status-test"
    SolutionRoot = None
    CreatedAt = DateTime.UtcNow
    LastActivity = DateTime.UtcNow
    Status = SessionLifecycleStatus.Faulted(Some "worker crashed")
    Workflow = SageFs.WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning }

[<Tests>]
let resolveSessionStatusReconciliationTests =
  testList "resolveSessionStatus reconciles a live reply against the registry's terminal state" [
    testTask "a Faulted registry entry polled through a lagging worker still saying Starting reports Faulted, not resurrected" {
      let! label, health = resolveSessionStatus "health" faultedSession (Some staleWorkerStillSayingStarting)
      label |> Expect.equal "the label must not resurrect a terminal registry entry from a stale reply" "Faulted"
      health
      |> Expect.equal
        "health must agree with the label — this is the exact two-fields-disagree bug"
        SageFs.Features.SessionHealthStatus.Faulted
    }

    testTask "a live, non-terminal session still reports the worker's own answer unchanged" {
      let readySession = { faultedSession with Status = SessionLifecycleStatus.Ready { Pid = 1; Port = Some 9000 } }
      let! label, health = resolveSessionStatus "health" readySession (Some staleWorkerStillSayingStarting)
      label |> Expect.equal "a non-terminal registry entry defers to the worker's live answer" "Starting"
      health |> Expect.equal "and the health field agrees with that same live answer" SageFs.Features.SessionHealthStatus.WarmingUp
    }
  ]
