namespace SageFs

open System
open System.Threading.Tasks
open SageFs.AppState

/// A hold on the worker's test-run gate: while it is held, a level waits for it. Disposing it leaves the gate.
type TestRunHold =
  { Hold: unit -> Async<IDisposable> }

module TestRunHold =
  /// A worker with no gate (a test host, a transport test): every run is admitted at once.
  let none : TestRunHold =
    { Hold = fun () -> async { return { new IDisposable with member _.Dispose() = () } } }

/// Whether the host holds a test for a debugger.
[<RequireQualifiedAccess>]
type DebugHold =
  | HoldsATest
  | HoldsNothing

/// The worker's side of bringing the FSI host up to the patched build.
///
/// It owns the test-run gate (`TestRunGate`, the pure state machine) and the one wait it needs: a run holds the gate for its
/// whole life, a level asks the gate first and the actor second, and a run that arrives while a level is pending or under way
/// waits for the level to end and is then served on the new host. The check that nothing the host holds would be lost is
/// the actor's, made in the same message as the reset (`EvalLevel`), so it cannot race an eval. The gate keeps the same promise
/// for test runs, which the actor cannot see.
///
/// Nothing here waits on a clock: a held run waits for the level's end, a pending level waits for the last run's exit.
type LevelKeeper(actor: AppActor, dynamicTests: unit -> DynamicTests) =
  let sync = obj ()
  let mutable standing = GateStanding.Idle
  let mutable debugHold = DebugHold.HoldsNothing
  // Completed when the level that is pending or under way has ended. Replaced by each level; a held run waits on the one it saw.
  let mutable levelEnded : TaskCompletionSource<unit> = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
  // Completed when the last run has left a pending level, and so the level may act.
  let mutable levelMayAct : TaskCompletionSource<unit> = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

  let freshSignal () = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

  let leave () =
    let mayAct =
      lock sync (fun () ->
        let next, exit = TestRunGate.leave standing
        standing <- next
        match exit with
        | GateExit.LevelMayStart -> Some levelMayAct
        | GateExit.Left
        | GateExit.Drained
        | GateExit.NotRunning -> None)
    match mayAct with
    | Some signal -> signal.TrySetResult() |> ignore
    | None -> ()

  /// A run enters, or waits for the level that holds the gate and tries again.
  let rec enter () : Async<IDisposable> =
    async {
      let held =
        lock sync (fun () ->
          let next, entry = TestRunGate.enter standing
          standing <- next
          match entry with
          | GateEntry.Admitted -> None
          | GateEntry.Held -> Some levelEnded)
      match held with
      | None ->
        let left = ref 0
        return
          { new IDisposable with
              member _.Dispose() =
                // A hold is left once, however many times it is disposed.
                match System.Threading.Interlocked.Exchange(&left.contents, 1) with
                | 0 -> leave ()
                | _ -> () }
      | Some ended ->
        do! ended.Task |> Async.AwaitTask
        return! enter ()
    }

  /// Hold the gate for one run. The run is admitted at once unless a level is pending or under way, which it waits out.
  member _.Hold : TestRunHold = { Hold = enter }

  /// How many runs are in flight, for a status line.
  member _.Standing : GateStanding = lock sync (fun () -> standing)

  /// Whether the host holds a test for a debugger now. Set when the hold begins, before its entry to the gate is left, and cleared
  /// when the host says it ended.
  member _.SetDebugHold (hold: DebugHold) : unit = lock sync (fun () -> debugHold <- hold)

  /// Ask the worker's host to level. A run in flight finishes first. A level already pending or under way is not asked twice.
  member _.Level () : Async<LevelAnswer> =
    async {
      let request, ended, mayAct =
        lock sync (fun () ->
          let next, request = TestRunGate.requestLevel standing
          standing <- next
          match request with
          | GateLevelRequest.Start ->
            levelEnded <- freshSignal ()
            levelMayAct <- freshSignal ()
            levelMayAct.TrySetResult() |> ignore
          | GateLevelRequest.WaitFor _ ->
            levelEnded <- freshSignal ()
            levelMayAct <- freshSignal ()
          | GateLevelRequest.AlreadyRequested -> ()
          request, levelEnded, levelMayAct)
      match request with
      | GateLevelRequest.AlreadyRequested -> return LevelAnswer.LevelDeferred [ LevelBlocker.LevelAlreadyRunning ]
      | GateLevelRequest.Start
      | GateLevelRequest.WaitFor _ ->
        try
          do! mayAct.Task |> Async.AwaitTask
          // A test held for a debugger is a run in flight that the level does not wait for: the editor may take as long as it likes,
          // and a level waiting for it would hold every other run behind it. The actor refuses on it instead.
          let outside : OutsideFacts =
            lock sync (fun () ->
              let runs =
                match debugHold, TestRunGate.runsOf standing with
                | DebugHold.HoldsATest, TestRuns.Idle -> TestRuns.InFlight 1
                | DebugHold.HoldsATest, TestRuns.InFlight count -> TestRuns.InFlight(count + 1)
                | DebugHold.HoldsNothing, running -> running
              { DynamicTests = dynamicTests (); TestRuns = runs })
          // A failure of the actor is a value here, so the gate below is always opened and a held run is never left behind it.
          let! asked = actor.PostAndAsyncReply(fun reply -> LevelSession(outside, reply)) |> Async.Catch
          match asked with
          | Choice1Of2 answer -> return answer
          | Choice2Of2 error -> return LevelAnswer.LevelFailed error.Message
        finally
          lock sync (fun () -> standing <- TestRunGate.finishLevel standing)
          ended.TrySetResult() |> ignore
    }
