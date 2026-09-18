module SageFs.Tests.EvalActorStragglerTests

open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState

/// The generation-supersession / no-resurrection decision this file used to
/// prove via a real FSI-warmup Integration harness (spawning a bare
/// eval-actor, submitting a straggler eval that ignores cancellation, then
/// resetting while it's in flight) is now the `no-resurrection` invariant in
/// SageFs.Simulation/EvalActorSim.fs + EvalActorInvariants.fs, folding the
/// REAL SageFs.EvalActorDecision.decide, with a generation-blind
/// twin proving the invariant has teeth. Asserted in
/// SageFs.Tests/EvalActorSimTests.fs — proven in milliseconds. See
/// EvalActorSimTests.fs "resetDuringEval: a straggler Finished for the
/// superseded generation is dropped" and "REPRODUCED — generation-blind
/// twin resurrects a superseded straggler".
///
/// `supersededAtWorkerBoundaryTests` below is UNCHANGED — it is the one real
/// process-boundary smoke worth keeping: it proves the structured
/// `SageFsError.EvalSupersededByReset` case crosses the worker HTTP
/// boundary intact, which the pure DST above cannot exercise.
[<Tests>]
let evalActorStragglerTests = testList "Eval actor straggler" []

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
