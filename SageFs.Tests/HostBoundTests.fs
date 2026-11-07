/// What the worker keeps about the code in its FSI host has to go when the host is replaced. A reset used to leave the run-test
/// function an editor buffer's tests defined (it points into the disposed host), the tests that buffer discovered, and the
/// records of which modules were evaluated into the host. `HostBound` holds all four and `WorkerMain.forgettingHost` empties it
/// after every reset the worker answers Ok.
module SageFs.Tests.HostBoundTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState
open SageFs.Server
open SageFs.WorkerProtocol

let private aRunTest : Features.LiveTesting.TestCase -> Async<Features.LiveTesting.TestResult> =
  fun _ -> async { return Features.LiveTesting.TestResult.NotRun }

let private withModules (names: string list) : Middleware.CompilationContext.CompilationState =
  { Middleware.CompilationContext.CompilationState.empty with EvaluatedModules = Set.ofList names }

/// A bound that holds something of every kind.
let private loaded () : HostBound =
  let bound = HostBound()
  bound.SetRunTest aRunTest
  bound.SetBufferEvals (withModules [ "Buffer" ])
  bound.SetWatcherEvals (withModules [ "Watcher" ])
  bound

/// A worker whose handler answers every message with `answer`, seen through the wrapper that forgets a replaced host.
let private worker (bound: HostBound) (answer: WorkerResponse) : WorkerMessage -> Async<WorkerResponse> =
  WorkerMain.forgettingHost bound (fun _ -> async { return answer })

let private send (handler: WorkerMessage -> Async<WorkerResponse>) (message: WorkerMessage) : unit =
  handler message |> Async.RunSynchronously |> ignore

[<Tests>]
let tests =
  testList "HostBound" [
    testCase "WHY - forgetting the host empties the run-test slot, the buffer's discovered tests and both records of evaluated modules" <| fun _ ->
      let bound = loaded ()
      bound.RunTest |> Expect.isSome "something was held"
      bound.Forget()
      bound.RunTest |> Expect.isNone "the run-test function points into a host that is gone"
      bound.Discovery |> Expect.isEmpty "and the tests that buffer discovered"
      bound.BufferEvals.EvaluatedModules |> Expect.isEmpty "the modules the buffer evals put in the host are not in the new one"
      bound.WatcherEvals.EvaluatedModules |> Expect.isEmpty "nor are the file watcher's"

    testCase "WHY - an editor's hard reset that resolves Ok makes the worker forget the host, because the new host holds none of it" <| fun _ ->
      let bound = loaded ()
      send (worker bound (WorkerResponse.HardResetResult("r", Ok "Hard reset complete."))) (WorkerMessage.HardResetSession(false, "r"))
      bound.RunTest |> Expect.isNone "the run-test slot is cleared"
      bound.WatcherEvals.EvaluatedModules |> Expect.isEmpty "and so is the watcher's record"

    testCase "WHY - a soft reset that resolves Ok forgets it too: it disposes the host and builds another" <| fun _ ->
      let bound = loaded ()
      send (worker bound (WorkerResponse.ResetResult("r", Ok ()))) (WorkerMessage.ResetSession "r")
      bound.RunTest |> Expect.isNone "cleared"

    testCase "WHY - a level that answers Leveled forgets it, and one that answers Deferred or Failed does not, because it changed nothing" <| fun _ ->
      let leveled = loaded ()
      send (worker leveled (WorkerResponse.LevelResult("r", LevelAnswer.Leveled))) (WorkerMessage.LevelRepl "r")
      leveled.RunTest |> Expect.isNone "leveled: the host is new"
      for answer in [ LevelAnswer.LevelDeferred [ LevelBlocker.LiveTestsDefined ]; LevelAnswer.LevelFailed "no" ] do
        let kept = loaded ()
        send (worker kept (WorkerResponse.LevelResult("r", answer))) (WorkerMessage.LevelRepl "r")
        kept.RunTest |> Expect.isSome (sprintf "%A: the host is the old one" answer)

    testCase "WHY - a reset that failed leaves what the worker holds alone, because the old host may be the one still serving" <| fun _ ->
      let bound = loaded ()
      send (worker bound (WorkerResponse.HardResetResult("r", Error (SageFsError.HardResetFailed "boom")))) (WorkerMessage.HardResetSession(false, "r"))
      send (worker bound (WorkerResponse.ResetResult("r", Error (SageFsError.ResetFailed "boom")))) (WorkerMessage.ResetSession "r")
      bound.RunTest |> Expect.isSome "kept"

    testCase "WHY - nothing else the worker answers touches it" <| fun _ ->
      let bound = loaded ()
      send (worker bound (WorkerResponse.EvalCancelled true)) WorkerMessage.CancelEval
      send (worker bound (WorkerResponse.TestRunResults("r", [||]))) (WorkerMessage.RunTests([||], 1, "r"))
      bound.RunTest |> Expect.isSome "kept"
  ]
