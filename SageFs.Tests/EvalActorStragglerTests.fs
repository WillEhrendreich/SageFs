module SageFs.Tests.EvalActorStragglerTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private quietLogger = SageFs.Tests.TestInfrastructure.quietLogger

/// Code the injected pipeline treats as an eval that outlives a reset.
[<Literal>]
let private StragglerCode = "straggler"

/// An eval-actor over a bare FSI session whose pipeline turns StragglerCode
/// into an eval that ignores cancellation and thread interrupts (user code
/// that swallows ThreadInterruptedException, a native call) and only returns
/// when the test opens the gate. Every other submission runs the real eval.
type private StragglerHarness = {
  Actor: AppActor
  /// Completes with the session the straggler started on.
  Started: TaskCompletionSource<FSharp.Compiler.Interactive.Shell.FsiEvaluationSession>
  /// Opening it lets the straggler return Ok with the AppState it started on.
  Gate: TaskCompletionSource<unit>
}

let private mkHarness () : StragglerHarness =
  let started = TaskCompletionSource<FSharp.Compiler.Interactive.Shell.FsiEvaluationSession>(TaskCreationOptions.RunContinuationsAsynchronously)
  let gate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
  let build : PipelineBuildFn =
    fun _middleware evalFn ->
      fun (request, st) ->
        match request.Code with
        | StragglerCode ->
          started.TrySetResult st.Session |> ignore
          let mutable released = false
          while not released do
            try
              gate.Task.Wait()
              released <- true
            with
            | :? ThreadInterruptedException -> ()
          { EvaluationResult = Ok "straggler finished"
            Diagnostics = [||]
            EvaluatedCode = request.Code
            Metadata = Map.empty }, st
        | _ -> evalFn (request, st)
  let actor, _, _, _, _, _, _, _, _ =
    mkAppStateActor
      quietLogger Map.empty IO.TextWriter.Null false
      ProjectLoading.emptySolution None false false ignore build
      ProjectLoading.emptySolution
  { Actor = actor; Started = started; Gate = gate }

/// Await a task, failing the test when it does not finish in time.
let private within (seconds: float) (what: string) (t: Task<'T>) : Task<'T> = task {
  let! winner = Task.WhenAny(t :> Task, Task.Delay(TimeSpan.FromSeconds seconds))
  obj.ReferenceEquals(winner, t) |> Expect.isTrue (sprintf "%s within %.0fs" what seconds)
  return! t
}

let private eval (actor: AppActor) (code: string) =
  actor.PostAndAsyncReply(fun reply -> Eval({ Code = code; Args = Map.empty }, CancellationToken.None, reply))
  |> Async.StartAsTask

[<Tests>]
let evalActorStragglerTests =
  Integration.hostList "Eval actor straggler" [

    testTask "WHY — an eval that outlives a reset must not bring back the disposed session, because every later eval would run against it and the fresh session would leak" {
      let h = mkHarness ()
      // AddMiddleware is answered only once warm-up finished and the loop runs.
      do! h.Actor.PostAndAsyncReply(fun reply -> AddMiddleware([], reply)) |> Async.StartAsTask |> within 60.0 "warm-up"
      let straggler = eval h.Actor StragglerCode
      let! oldSession = h.Started.Task |> within 10.0 "the straggler starts"

      let! reset =
        h.Actor.PostAndAsyncReply(fun reply -> ResetSession reply) |> Async.StartAsTask |> within 60.0 "the reset"
      reset |> Expect.isOk "the reset succeeds while the straggler is still running"

      h.Gate.SetResult()
      let! stragglerReply = straggler |> within 10.0 "the straggler's caller is answered"

      let! phase = h.Actor.PostAndAsyncReply(fun reply -> GetSessionPhase reply) |> Async.StartAsTask |> within 10.0 "the phase query"
      match phase with
      | Active (st, _) ->
        obj.ReferenceEquals(st.Session, oldSession)
        |> Expect.isFalse "the session is the fresh one the reset created, not the one it disposed"
      | other -> failtestf "expected an Active session after the reset, got %A" other

      match stragglerReply.EvaluationResult with
      | Error (:? SageFsErrorException as e) ->
        e.Error |> Expect.equal "the caller learns its eval was superseded by the reset" SageFsError.EvalSupersededByReset
      | other -> failtestf "expected the structured superseded-by-reset error, got %A" other

      let! after = eval h.Actor "1 + 1" |> within 30.0 "an eval after the reset"
      after.EvaluationResult |> Expect.isOk "the fresh session keeps evaluating"
    }
  ]

/// An actor that answers every Eval with a fixed response.
let private answeringActor (response: EvalResponse) : AppActor =
  MailboxProcessor.Start(fun inbox ->
    let rec loop () = async {
      match! inbox.Receive() with
      | Eval(_, _, reply) -> reply.Reply response
      | _ -> ()
      return! loop ()
    }
    loop ())

let private workerEval (actor: AppActor) =
  SageFs.Server.WorkerMain.handleMessage
    actor (fun () -> SessionState.Ready) (fun () -> Affordances.EvalStats.empty) (fun () -> None) []
    (fun () -> SageFs.Features.LiveTesting.LiveTestHookResult.noOp) (fun _ -> ()) (fun () -> [||], [])
    (fun _ _ -> async { return Result.Error (SageFsError.EvalFailed "EvalLiveTestFile not available on this test worker") })
    SageFs.Server.WorkerMain.noAppRuns
    (WorkerProtocol.WorkerMessage.EvalCode("x", "r1"))
  |> Async.StartAsTask

[<Tests>]
let supersededAtWorkerBoundaryTests =
  testList "Eval superseded by reset at the worker boundary" [

    testTask "WHY — the worker forwards the superseded-by-reset case instead of a stack dump, because the agent must learn to re-run its code rather than fix it" {
      let response =
        { EvaluationResult = Error (SageFsErrorException SageFsError.EvalSupersededByReset :> exn)
          Diagnostics = [||]
          EvaluatedCode = "x"
          Metadata = Map.empty }
      let! resp = workerEval (answeringActor response)
      match resp with
      | WorkerProtocol.WorkerResponse.EvalResult(_, Error err, _, _) ->
        err |> Expect.equal "the structured case crosses the process boundary" SageFsError.EvalSupersededByReset
      | other -> failtestf "expected an EvalResult error, got %A" other
    }
  ]
