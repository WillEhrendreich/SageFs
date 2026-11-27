/// A broken reply to a tool call is not proof that its worker died. On a loaded
/// machine one HTTP response can end early while the process is still answering.
/// This probe asks the same worker for its status before the caller marks it dead,
/// so a dropped reply cannot destroy every definition the session holds.
module SageFs.McpWorkerLiveness

open System.Threading.Tasks
open SageFs.WorkerProtocol

let stillAlive (send: SessionProxy) (probeId: string) : Task<bool> =
  task {
    let probe = send (WorkerMessage.GetStatus probeId) |> Async.StartAsTask
    let! first = Task.WhenAny(probe :> Task, Task.Delay(int SageFs.Timeouts.workerHealthProbeTimeout.TotalMilliseconds))
    if not (obj.ReferenceEquals(first, probe)) then
      return false
    else
      let! outcome = (async { return! Async.AwaitTask probe } |> Async.Catch |> Async.StartAsTask)
      match outcome with
      | Choice1Of2 (WorkerResponse.StatusResult _) -> return true
      | Choice1Of2 _ -> return false
      | Choice2Of2 _ -> return false
  }

/// Ask the worker through its current proxy, then mark the session dead only if
/// it also fails to answer. Returns true when the session was marked dead.
let markIfDead (ops: SessionManagementOps) (sid: SessionId) (reason: string) : Task<bool> =
  task {
    let mutable alive = false
    match! ops.GetProxy sid with
    | Some send ->
      let! answered = stillAlive send (SessionId.value (SessionId.newId ()))
      alive <- answered
    | None -> ()
    if alive then
      return false
    else
      ops.NotifyWorkerDied sid
      do! ops.UpdateSessionStatus sid (SessionLifecycleStatus.Faulted (FaultReason.report reason))
      return true
  }
