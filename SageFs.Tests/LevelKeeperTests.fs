/// The worker's side of leveling: the test-run gate and the level that waits for it. The eval actor is replaced by a mailbox that
/// records the facts a level message carried and answers when the test lets it, so every order the gate has to survive is
/// scripted with completion sources and nothing waits on a clock.
module SageFs.Tests.LevelKeeperTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.AppState

/// A stand-in for the router: answers every `LevelSession` through `answer` and records the facts it was given.
type private FakeActor =
  { Actor: AppActor
    Seen: OutsideFacts list ref
    Reached: SemaphoreSlim }

let private fakeActor (answer: OutsideFacts -> Task<LevelAnswer>) : FakeActor =
  let seen = ref []
  let reached = new SemaphoreSlim(0)
  let actor =
    MailboxProcessor<Command>.Start(fun inbox ->
      let rec loop () =
        async {
          let! cmd = inbox.Receive()
          match cmd with
          | LevelSession(outside, reply) ->
            seen.Value <- outside :: seen.Value
            reached.Release() |> ignore
            // The reply is given from its own task, so the mailbox is free while the test holds the answer.
            Started.andForget (
              async {
                try
                  let! result = answer outside |> Async.AwaitTask
                  reply.Reply result
                with ex -> reply.Reply (LevelAnswer.LevelFailed ex.Message)
              })
          | _ -> ()
          return! loop ()
        }
      loop ())
  { Actor = actor; Seen = seen; Reached = reached }

let private within (pending: Task<'a>) : Task<'a> =
  task {
    let! first = Task.WhenAny(pending :> Task, Task.Delay TestTimeouts.patienceInProcess)
    match obj.ReferenceEquals(first, pending) with
    | true -> return! pending
    | false -> return failtestf "did not happen within %A" TestTimeouts.patienceInProcess
  }

/// The same bound for a task that has no result (a semaphore's wait).
let private withinSignal (pending: Task) : Task =
  task {
    let! first = Task.WhenAny(pending, Task.Delay TestTimeouts.patienceInProcess)
    match obj.ReferenceEquals(first, pending) with
    | true -> do! pending
    | false -> failtestf "did not happen within %A" TestTimeouts.patienceInProcess
  } :> Task

let private noDynamicTests () = DynamicTests.Absent

/// Starts on the calling thread, so the gate has been entered, or the run is parked, by the time this returns.
let private holdOne (keeper: LevelKeeper) : Task<IDisposable> = keeper.Hold.Hold() |> Async.StartImmediateAsTask

/// A hold taken and awaited within the bound.
let private heldWithin (pending: Task<IDisposable>) : Task<IDisposable> =
  task {
    let! first = Task.WhenAny(pending :> Task, Task.Delay TestTimeouts.patienceInProcess)
    match obj.ReferenceEquals(first, pending) with
    | true -> return! pending
    | false -> return failtestf "the run was not admitted within %A" TestTimeouts.patienceInProcess
  }

[<Tests>]
let tests =
  testList "LevelKeeper" [

    testTask "WHY - with nothing running a level goes straight to the actor, which is told no run is in flight" {
      let fake = fakeActor (fun _ -> Task.FromResult LevelAnswer.Leveled)
      let keeper = LevelKeeper(fake.Actor, noDynamicTests)
      let! answer = within (keeper.Level() |> Async.StartImmediateAsTask)
      answer |> Expect.equal "the actor's own answer" LevelAnswer.Leveled
      fake.Seen.Value |> Expect.equal "idle facts" [ { DynamicTests = DynamicTests.Absent; TestRuns = TestRuns.Idle } ]
      keeper.Standing |> Expect.equal "the gate is open again" GateStanding.Idle
    }

    testTask "WHY - the actor is told what the worker's run-test slot says, because only the worker can see it" {
      let fake = fakeActor (fun _ -> Task.FromResult (LevelAnswer.LevelDeferred [ LevelBlocker.LiveTestsDefined ]))
      let keeper = LevelKeeper(fake.Actor, fun () -> DynamicTests.Defined)
      let! answer = within (keeper.Level() |> Async.StartImmediateAsTask)
      answer |> Expect.equal "deferred, as the actor said" (LevelAnswer.LevelDeferred [ LevelBlocker.LiveTestsDefined ])
      fake.Seen.Value |> List.map (fun f -> f.DynamicTests) |> Expect.equal "the slot was passed" [ DynamicTests.Defined ]
    }

    testTask "WHY - a level asked while a run is in flight does not reach the actor until the run has left, and then no run is in flight" {
      let fake = fakeActor (fun _ -> Task.FromResult LevelAnswer.Leveled)
      let keeper = LevelKeeper(fake.Actor, noDynamicTests)
      let! (hold: IDisposable) = heldWithin (holdOne keeper)
      let level = keeper.Level() |> Async.StartImmediateAsTask
      keeper.Standing |> Expect.equal "pending, one run still in flight" (GateStanding.LevelPending 1)
      fake.Seen.Value |> Expect.isEmpty "the actor has not been asked"
      hold.Dispose()
      let! answer = within level
      answer |> Expect.equal "leveled" LevelAnswer.Leveled
      fake.Seen.Value |> Expect.equal "and it was asked with no run in flight" [ { DynamicTests = DynamicTests.Absent; TestRuns = TestRuns.Idle } ]
    }

    testTask "WHY - disposing a hold twice leaves the gate once, so a double dispose cannot open the gate under another run" {
      let fake = fakeActor (fun _ -> Task.FromResult LevelAnswer.Leveled)
      let keeper = LevelKeeper(fake.Actor, noDynamicTests)
      let! (first: IDisposable) = heldWithin (holdOne keeper)
      let! (second: IDisposable) = heldWithin (holdOne keeper)
      first.Dispose()
      first.Dispose()
      keeper.Standing |> Expect.equal "the second run is still in flight" (GateStanding.Running 1)
      second.Dispose()
      keeper.Standing |> Expect.equal "idle" GateStanding.Idle
    }

    testTask "WHY - a run that arrives while the level is under way is held, not dropped and not run on the old host, and is served when the level ends" {
      let release = TaskCompletionSource<LevelAnswer>(TaskCreationOptions.RunContinuationsAsynchronously)
      let fake = fakeActor (fun _ -> release.Task)
      let keeper = LevelKeeper(fake.Actor, noDynamicTests)
      let level = keeper.Level() |> Async.StartImmediateAsTask
      do! withinSignal (fake.Reached.WaitAsync())
      keeper.Standing |> Expect.equal "leveling" GateStanding.Leveling
      let arriving = holdOne keeper
      // The run is parked on the level's end: it has not been admitted.
      arriving.IsCompleted |> Expect.isFalse "held"
      keeper.Standing |> Expect.equal "still leveling, the run is not counted" GateStanding.Leveling
      release.SetResult LevelAnswer.Leveled
      let! _ = within level
      let! (hold: IDisposable) = heldWithin arriving
      keeper.Standing |> Expect.equal "the held run is now the one in flight" (GateStanding.Running 1)
      hold.Dispose()
    }

    testTask "WHY - a run that arrives while a level waits for the last run is held, so a stream of runs cannot starve the level" {
      let fake = fakeActor (fun _ -> Task.FromResult LevelAnswer.Leveled)
      let keeper = LevelKeeper(fake.Actor, noDynamicTests)
      let! (running: IDisposable) = heldWithin (holdOne keeper)
      let level = keeper.Level() |> Async.StartImmediateAsTask
      let arriving = holdOne keeper
      arriving.IsCompleted |> Expect.isFalse "held behind the pending level"
      running.Dispose()
      let! answer = within level
      answer |> Expect.equal "leveled with the arriving run held back" LevelAnswer.Leveled
      fake.Seen.Value |> Expect.equal "no run was in flight when the actor was asked" [ { DynamicTests = DynamicTests.Absent; TestRuns = TestRuns.Idle } ]
      let! (hold: IDisposable) = heldWithin arriving
      hold.Dispose()
    }

    testTask "WHY - tests an editor buffer defined block the level, and once an editor's hard reset has replaced the host the level proceeds" {
      let bound = SageFs.Server.HostBound()
      let fake =
        fakeActor (fun outside ->
          match outside.DynamicTests with
          | DynamicTests.Defined -> Task.FromResult (LevelAnswer.LevelDeferred [ LevelBlocker.LiveTestsDefined ])
          | DynamicTests.Absent -> Task.FromResult LevelAnswer.Leveled)
      let keeper =
        LevelKeeper(fake.Actor, fun () -> match bound.RunTest with | Some _ -> DynamicTests.Defined | None -> DynamicTests.Absent)
      bound.SetRunTest (fun _ -> async { return SageFs.Features.LiveTesting.TestResult.NotRun })
      let! blocked = within (keeper.Level() |> Async.StartImmediateAsTask)
      blocked |> Expect.equal "the buffer's tests are in the host" (LevelAnswer.LevelDeferred [ LevelBlocker.LiveTestsDefined ])
      // The editor's hard reset, answered Ok by the worker, seen through the wrapper that forgets a replaced host.
      let handler =
        SageFs.Server.WorkerMain.forgettingHost bound (fun _ -> async { return SageFs.WorkerProtocol.WorkerResponse.HardResetResult("r", Ok "Hard reset complete.") })
      let! _ = handler (SageFs.WorkerProtocol.WorkerMessage.HardResetSession(false, "r")) |> Async.StartAsTask
      let! proceeded = within (keeper.Level() |> Async.StartImmediateAsTask)
      proceeded |> Expect.equal "the host holds nothing of it now, so the level proceeds" LevelAnswer.Leveled
    }

    testTask "WHY - a test held for a debugger counts as a run in flight, and the level does not wait for it: the actor is told and refuses" {
      let fake = fakeActor (fun _ -> Task.FromResult (LevelAnswer.LevelDeferred [ LevelBlocker.TestsRunning 1 ]))
      let keeper = LevelKeeper(fake.Actor, noDynamicTests)
      keeper.SetDebugHold DebugHold.HoldsATest
      let! answer = within (keeper.Level() |> Async.StartImmediateAsTask)
      answer |> Expect.equal "deferred, not waiting" (LevelAnswer.LevelDeferred [ LevelBlocker.TestsRunning 1 ])
      fake.Seen.Value |> Expect.equal "the actor was told a run is in flight" [ { DynamicTests = DynamicTests.Absent; TestRuns = TestRuns.InFlight 1 } ]
      keeper.SetDebugHold DebugHold.HoldsNothing
      let! cleared = within (keeper.Level() |> Async.StartImmediateAsTask)
      ignore cleared
      fake.Seen.Value |> List.head |> Expect.equal "and once the hold ends it is told none" { DynamicTests = DynamicTests.Absent; TestRuns = TestRuns.Idle }
    }

    testTask "WHY - a second level asked while one is pending or under way is told so, and the first is undisturbed" {
      let release = TaskCompletionSource<LevelAnswer>(TaskCreationOptions.RunContinuationsAsynchronously)
      let fake = fakeActor (fun _ -> release.Task)
      let keeper = LevelKeeper(fake.Actor, noDynamicTests)
      let first = keeper.Level() |> Async.StartImmediateAsTask
      do! withinSignal (fake.Reached.WaitAsync())
      let! second = within (keeper.Level() |> Async.StartImmediateAsTask)
      second |> Expect.equal "already running" (LevelAnswer.LevelDeferred [ LevelBlocker.LevelAlreadyRunning ])
      release.SetResult LevelAnswer.Leveled
      let! firstAnswer = within first
      firstAnswer |> Expect.equal "the first still answers" LevelAnswer.Leveled
      fake.Seen.Value |> List.length |> Expect.equal "the actor was asked once" 1
    }

    testTask "WHY - an actor that fails leaves the gate open and the level failed with the reason, so a held run is not stuck behind it" {
      let release = TaskCompletionSource<LevelAnswer>(TaskCreationOptions.RunContinuationsAsynchronously)
      let fake = fakeActor (fun _ -> release.Task)
      let keeper = LevelKeeper(fake.Actor, noDynamicTests)
      let level = keeper.Level() |> Async.StartImmediateAsTask
      do! withinSignal (fake.Reached.WaitAsync())
      let arriving = holdOne keeper
      release.SetException(InvalidOperationException "the host would not start")
      match! within level with
      | LevelAnswer.LevelFailed reason -> reason |> Expect.stringContains "the reason" "the host would not start"
      | other -> failtestf "expected LevelFailed, got %A" other
      let! (hold: IDisposable) = heldWithin arriving
      hold.Dispose()
      keeper.Standing |> Expect.equal "idle" GateStanding.Idle
    }
  ]
