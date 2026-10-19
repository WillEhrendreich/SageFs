module SageFs.Tests.SessionManagerStopOffMailboxTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol
open SageFs.Simulation
open SageFs.Simulation.StopOffLoopSim

// RED→GREEN for roast R-SERVER-3: a stop must not hold the session manager's one mailbox. The real SessionManager runs
// with a fake runtime whose StopWorker waits on a gate the test opens, so "the worker takes seconds to leave" is a thing
// the test controls, not a sleep. The same scenarios and invariants as the model (StopOffLoopSimTests.fs) judge it.

/// What the fake runtime knows about one session's stop: the gate its StopWorker waits on, and a signal for each call
/// that began and each that ended (a restarted session has two workers, so a stop can make two calls).
type private Gate =
  { Open: TaskCompletionSource
    Began: SemaphoreSlim
    Ended: SemaphoreSlim
    mutable InFlight: int }

type private Gates() =
  let gates = ConcurrentDictionary<SessionId, Gate>()
  /// Once the scenario is over every gate is open, including those not made yet (teardown stops the rest).
  let mutable draining = false
  member _.For(id: SessionId) : Gate =
    gates.GetOrAdd(id, fun _ ->
      let gate =
        { Open = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
          Began = new SemaphoreSlim(0)
          Ended = new SemaphoreSlim(0)
          InFlight = 0 }
      match Volatile.Read(&draining) with
      | true -> gate.Open.TrySetResult() |> ignore
      | false -> ()
      gate)
  /// A stop begun and not ended: the worker is still leaving.
  member this.Leaving(id: SessionId) : bool = Volatile.Read(&(this.For id).InFlight) > 0
  member _.OpenAll() =
    Volatile.Write(&draining, true)
    for g in gates.Values do g.Open.TrySetResult() |> ignore

let private stopThatWaitsOnItsGate (gates: Gates) (session: ManagedSession) : Async<unit> =
  async {
    let g = gates.For session.Info.Id
    Interlocked.Increment(&g.InFlight) |> ignore
    g.Began.Release() |> ignore
    do! Async.AwaitTask g.Open.Task
    Interlocked.Decrement(&g.InFlight) |> ignore
    g.Ended.Release() |> ignore
  }

let private mkRuntime (gates: Gates) : SessionManagerRuntime =
  { StartWorkerProcess =
      fun _ _ _ _ _ _ -> Ok ({ Process = Process.GetCurrentProcess(); AdoptedCore = None } : SessionManager.SpawnedWorker)
    AwaitWorkerPort = fun _ _ _ _ _ -> ()
    Ledger = StartLedger.closed
    StopWorker = stopThatWaitsOnItsGate gates
    RunBuildAsync = fun _ _ -> async { return Ok "build ok" } }

type private Harness =
  { Mailbox: MailboxProcessor<SessionCommand>
    Gates: Gates }

let private withHarness (run: Harness -> Task<unit>) : Task<unit> =
  task {
    use cancellation = new CancellationTokenSource()
    let gates = Gates()
    let mailbox, _ =
      createWith (mkRuntime gates) cancellation.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ _ -> ())
    let! failure =
      task {
        try
          do! run { Mailbox = mailbox; Gates = gates }
          return None
        with ex -> return Some (Runtime.ExceptionServices.ExceptionDispatchInfo.Capture ex)
      }
    // Open every gate so no worker is left waiting, then bound the teardown: a mailbox the scenario wedged must not hang the suite.
    gates.OpenAll()
    let teardown = mailbox.PostAndAsyncReply(fun reply -> SessionCommand.StopAll reply) |> Async.StartAsTask
    let! _ = Task.WhenAny(teardown, Task.Delay TestTimeouts.patience)
    cancellation.Cancel()
    match failure with
    | Some captured -> captured.Throw()
    | None -> ()
  }

/// The reply if the mailbox gives one within the bound, None otherwise: "answered at once" versus "left pending".
let private answeredWithin (mailbox: MailboxProcessor<SessionCommand>) (build: AsyncReplyChannel<'a> -> SessionCommand) : Task<'a option> =
  task {
    let work = mailbox.PostAndAsyncReply(build) |> Async.StartAsTask
    let! winner = Task.WhenAny(work :> Task, Task.Delay TestTimeouts.stalledWorkBound)
    match Object.ReferenceEquals(winner, work) with
    | true ->
      let! reply = work
      return Some reply
    | false -> return None
  }

/// Wait for `count` signals from a semaphore, failing the test if one does not come within the in-process ceiling.
let private awaitSignals (what: string) (signal: SemaphoreSlim) (count: int) : Task<unit> =
  task {
    for _ in 1 .. count do
      let! arrived = signal.WaitAsync TestTimeouts.patienceInProcess
      Expect.isTrue what arrived
  }

let private createIn (dir: string) (harness: Harness) : Task<SessionInfo> =
  task {
    let! created =
      harness.Mailbox.PostAndAsyncReply(fun reply ->
        SessionCommand.CreateSession([ SessionProjectTarget.Project "Test.fsproj" ], dir, true, WorkflowTypes.SessionWorkflow.Interactive, reply))
      |> Async.StartAsTask
    match created with
    | Ok info -> return info
    | Error err -> return failtestf "create session failed: %s" (SageFsError.describe err)
  }

let private dirFor (n: int) = sprintf "/stop-off-mailbox/session-%d" n

/// Drive the REAL mailbox through a scenario and record the same Trace the model produces.
let private execute (scenario: Scenario) (harness: Harness) : Task<Trace> =
  task {
    let ids = ResizeArray<SessionId>()
    for n in 1 .. scenario.Sessions do
      let! info = createIn (dirFor n) harness
      ids.Add info.Id
    let indexOf (id: SessionId) = ids.IndexOf id + 1
    let idOf (session: int) : SessionId option =
      match session >= 1 && session <= ids.Count with
      | true -> Some ids.[session - 1]
      | false -> None
    let leaving () : Set<int> =
      [ for n in 1 .. ids.Count do if harness.Gates.Leaving ids.[n - 1] then yield n ] |> Set.ofList
    let stoppedAnswered = HashSet<int>()
    let restarted = HashSet<int>()
    // Workers a stop of this session stops: the current one, and the parked one if it was restarted.
    let workersOf (session: int) = if restarted.Contains session then 2 else 1
    let steps = ResizeArray<Step>()
    for op in scenario.Ops do
      let before = leaving ()
      let! reply, listed =
        task {
          match op with
          | Op.Stop s ->
            match idOf s with
            | None -> return Reply.Pending, Set.empty
            | Some id ->
              match! answeredWithin harness.Mailbox (fun r -> SessionCommand.StopSession(id, r)) with
              | Some (Ok ()) ->
                stoppedAnswered.Add s |> ignore
                // The workers are told to leave on a side task: wait for that to begin so "leaving" is observable.
                do! awaitSignals (sprintf "an answered stop of session %d begins stopping its worker" s) (harness.Gates.For id).Began (workersOf s)
                return Reply.Answered, Set.empty
              | Some (Error err) -> return failtestf "stop of session %d failed: %s" s (SageFsError.describe err)
              | None -> return Reply.Pending, Set.empty
          | Op.List ->
            match! answeredWithin harness.Mailbox (fun r -> SessionCommand.ListSessions r) with
            | Some infos -> return Reply.Answered, (infos |> List.map (fun i -> indexOf i.Id) |> Set.ofList)
            | None -> return Reply.Pending, Set.empty
          | Op.Get s ->
            match idOf s with
            | None -> return Reply.Pending, Set.empty
            | Some id ->
              match! answeredWithin harness.Mailbox (fun r -> SessionCommand.GetSession(id, r)) with
              | Some _ -> return Reply.Answered, Set.empty
              | None -> return Reply.Pending, Set.empty
          | Op.Create ->
            let dir = dirFor (ids.Count + 1)
            match! answeredWithin harness.Mailbox (fun r ->
                     SessionCommand.CreateSession([ SessionProjectTarget.Project "Test.fsproj" ], dir, true, WorkflowTypes.SessionWorkflow.Interactive, r)) with
            | Some (Ok info) ->
              ids.Add info.Id
              return Reply.Answered, Set.empty
            | Some (Error err) -> return failtestf "create failed: %s" (SageFsError.describe err)
            | None -> return Reply.Pending, Set.empty
          | Op.Restart s ->
            match idOf s with
            | None -> return Reply.Pending, Set.empty
            | Some id ->
              match! answeredWithin harness.Mailbox (fun r -> SessionCommand.RestartSession(id, RestartPlan.RespawnOnly, r)) with
              | Some (Ok _) ->
                restarted.Add s |> ignore
                return Reply.Answered, Set.empty
              | Some (Error err) -> return failtestf "restart of session %d failed: %s" s (SageFsError.describe err)
              | None -> return Reply.Pending, Set.empty
          | Op.Release s ->
            match idOf s with
            | None -> return Reply.Answered, Set.empty
            | Some id ->
              let gate = harness.Gates.For id
              gate.Open.TrySetResult() |> ignore
              // A worker told to leave is gone once its stop ends: wait for exactly that, no sleeping.
              match stoppedAnswered.Contains s with
              | true -> do! awaitSignals (sprintf "the workers of session %d finish leaving once released" s) gate.Ended (workersOf s)
              | false -> ()
              return Reply.Answered, Set.empty
        }
      steps.Add
        { Op = op
          Reply = reply
          InFlightBefore = before
          InFlightAfter = leaving ()
          Listed = listed
          StoppedAnswered = Set.ofSeq stoppedAnswered
          Created = Set.ofList [ 1 .. ids.Count ] }
    return { Scenario = scenario; Steps = List.ofSeq steps; Reducer = "real SessionManager mailbox" }
  }

let private assertHolds (t: Trace) =
  match StopOffLoopInvariants.violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Sessions=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Sessions t.Scenario.Ops vs

let private judge (scenario: Scenario) : Task<unit> =
  withHarness (fun harness -> task {
    let! trace = execute scenario harness
    assertHolds trace
  })

[<Tests>]
let sessionManagerStopOffMailboxTests =
  testList "SessionManager stop off the mailbox" [

    testTask "WHY — while one stop is held open, a second command is answered (a stop does not hold the mailbox)" {
      do! withHarness (fun harness -> task {
        let! a = createIn (dirFor 1) harness
        let! b = createIn (dirFor 2) harness
        // Start the stop of A without awaiting it: its worker will not leave until the gate opens.
        let stopA = harness.Mailbox.PostAndAsyncReply(fun r -> SessionCommand.StopSession(a.Id, r)) |> Async.StartAsTask
        do! awaitSignals "the stop began stopping A's worker" (harness.Gates.For a.Id).Began 1
        match! answeredWithin harness.Mailbox (fun r -> SessionCommand.ListSessions r) with
        | Some infos ->
          let listed = infos |> List.map (fun i -> i.Id)
          listed |> Expect.contains "B is listed" b.Id
          listed |> List.contains a.Id |> Expect.isFalse "A, whose stop is under way, is not listed as a live session"
        | None -> failtest "ListSessions was left pending behind the stop of A: the stop holds the mailbox"
        harness.Gates.OpenAll()
        let! answered = Task.WhenAny(stopA, Task.Delay TestTimeouts.patienceInProcess)
        Expect.isTrue "the stop of A is answered" (obj.ReferenceEquals(answered, stopA))
      })
    }

    testTask "WHY — two stops of different sessions overlap: both workers are leaving at the same time" {
      do! withHarness (fun harness -> task {
        let! a = createIn (dirFor 1) harness
        let! b = createIn (dirFor 2) harness
        let stopA = harness.Mailbox.PostAndAsyncReply(fun r -> SessionCommand.StopSession(a.Id, r)) |> Async.StartAsTask
        do! awaitSignals "the worker of A began leaving" (harness.Gates.For a.Id).Began 1
        let stopB = harness.Mailbox.PostAndAsyncReply(fun r -> SessionCommand.StopSession(b.Id, r)) |> Async.StartAsTask
        // B's worker must begin leaving while A's is still leaving: stops that queue behind one another never get here.
        let! began = (harness.Gates.For b.Id).Began.WaitAsync TestTimeouts.stalledWorkBound
        Expect.isTrue "the worker of B began leaving while A's was still leaving" began
        Expect.isTrue "both workers are leaving together" (harness.Gates.Leaving a.Id && harness.Gates.Leaving b.Id)
        harness.Gates.OpenAll()
        let! _ = Task.WhenAny(Task.WhenAll([| stopA; stopB |]) :> Task, Task.Delay TestTimeouts.patienceInProcess)
        Expect.isTrue "both stops are answered" (stopA.IsCompletedSuccessfully && stopB.IsCompletedSuccessfully)
      })
    }

    testTask "WHY — the named scenarios hold the invariants on the real mailbox" {
      for scenario in [ StopOffLoopGenerators.aListDuringAStop; StopOffLoopGenerators.fourStopsAtOnce; StopOffLoopGenerators.everyCommandDuringAStop ] do
        do! judge scenario
    }

    testTask "WHY — seeded histories of stops, lists, gets, creates, restarts and releases hold the invariants on the real mailbox" {
      for seed in 1 .. 40 do
        do! judge (StopOffLoopGenerators.fromSeed seed)
    }
  ]
