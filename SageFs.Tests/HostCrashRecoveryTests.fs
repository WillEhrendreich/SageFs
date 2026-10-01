/// The outcome, against a real FSI host: user code takes the host down, and the session says so, says why, refuses
/// to pretend, and a reset brings it back. Found live on 0.6.865, where the session stayed Ready after the host
/// aborted and told the agent that nothing was lost.
module SageFs.Tests.HostCrashRecoveryTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState
open SageFs.ActorCreation
open SageFs.Features.Events
open SageFs.WorkerProtocol
open SageFs.Tests
open SageFs.Tests.TestInfrastructure

/// A thread whose body throws: the unhandled exception aborts the process, which is the crash found live.
let private crashTheHost = "System.Threading.Thread((fun () -> failwith \"host-crash-marker\")).Start();;"

let private eval (result: ActorResult) (code: string) : Task<EvalResponse> =
  result.Actor.PostAndAsyncReply(fun reply -> Eval({ Code = code; Args = Map.empty }, CancellationToken.None, reply))
  |> Async.StartAsTask

let private within (bound: TimeSpan) (pending: Task<'a>) : Task<'a> =
  task {
    let! first = Task.WhenAny(pending :> Task, Task.Delay bound)
    match obj.ReferenceEquals(first, pending) with
    | true -> return! pending
    | false -> return failtestf "did not complete within %A" bound
  }

[<Tests>]
let tests =
  Integration.hostList "an isolated session whose host crashes" [
    testTask "leaves Ready with the crash, refuses evals truthfully, and a hard reset brings it back" {
      let faulted = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
      let onEvent (event: SageFsEvent) =
        match event with
        | SessionFaulted fault -> faulted.TrySetResult fault.Error |> ignore
        | _ -> ()
      let args = { mkCommonActorArgs TestInfrastructure.quietLogger false onEvent SageFs.Args.ProjectLoadConfig.empty with FsiKind = SessionKinds.Isolated }
      let! session = createActor args
      session.GetSessionStatus() |> Expect.equal "a healthy session is Ready" SessionStatus.Ready

      // The thread starts and the eval returns; the host dies a moment later, on its own.
      let! started = within TestTimeouts.patience (eval session crashTheHost)
      started.EvaluationResult |> Result.isOk |> Expect.isTrue "starting the thread is not itself an error"
      let! announced = within TestTimeouts.patience faulted.Task
      announced |> Expect.stringContains "the event carries the crash" "host-crash-marker"

      match session.GetSessionStatus() with
      | SessionStatus.HostCrashed crash ->
        crash.Output |> Expect.stringContains "the unhandled exception text from the host's stderr" "host-crash-marker"
        match crash.Exit with
        | ExitedWith _ -> ()
        | ConnectionClosed -> failtest "the host process exited, so its exit code is known"
      | other -> failtestf "a session whose host is dead must not report %A" other
      session.GetSessionState() |> Expect.equal "the state gates like Faulted, where reset is offered" SessionState.Faulted

      // The next eval is refused with the typed crash, not run against a dead connection.
      let! refused = within TestTimeouts.patience (eval session "1 + 1;;")
      match refused.EvaluationResult with
      | Result.Error(:? SageFsErrorException as raised) ->
        match raised.Error with
        | SageFsError.FsiHostCrashed _ -> ()
        | other -> failtestf "expected FsiHostCrashed, got %A" other
      | other -> failtestf "expected a refusal, got %A" other

      // A hard reset starts a fresh host, and the session is usable again.
      let! reset = within TestTimeouts.patience (session.Actor.PostAndAsyncReply(fun reply -> HardResetSession(false, reply)) |> Async.StartAsTask)
      reset |> Result.isOk |> Expect.isTrue "the hard reset succeeds"
      session.GetSessionStatus() |> Expect.equal "Ready again" SessionStatus.Ready
      let! again = within TestTimeouts.patience (eval session "1 + 1;;")
      again.EvaluationResult |> Result.isOk |> Expect.isTrue "the fresh host evaluates"
    }

    testTask "a purposeful hard reset is not reported as a crash" {
      let faulted = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
      let onEvent (event: SageFsEvent) =
        match event with
        | SessionFaulted fault -> faulted.TrySetResult fault.Error |> ignore
        | _ -> ()
      let args = { mkCommonActorArgs TestInfrastructure.quietLogger false onEvent SageFs.Args.ProjectLoadConfig.empty with FsiKind = SessionKinds.Isolated }
      let! session = createActor args
      let! reset = within TestTimeouts.patience (session.Actor.PostAndAsyncReply(fun reply -> HardResetSession(false, reply)) |> Async.StartAsTask)
      reset |> Result.isOk |> Expect.isTrue "the hard reset succeeds"
      // The reset retired the old host. Anything it posted reaches the actor before this eval does.
      let! after = within TestTimeouts.patience (eval session "1 + 1;;")
      after.EvaluationResult |> Result.isOk |> Expect.isTrue "the session is usable"
      session.GetSessionStatus() |> Expect.equal "still Ready" SessionStatus.Ready
      faulted.Task.IsCompleted |> Expect.isFalse "retiring the old host announced no crash"
    }
  ]
