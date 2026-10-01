module SageFs.Tests.SessionManagerSupervisorAlarmTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.SupervisorWatchdog
open SageFs.WorkerProtocol

// The session manager's loop tells somebody when it is in trouble: a handler
// that never returns (Wedged), a handler that throws (CommandFailed), and a
// fault in the loop itself (LoopRestarted). Each shows up as daemon-level
// health on the snapshot. The timings are real but tiny, and every wait is
// on an event, never a sleep.


let private patience = TestTimeouts.patience

let private fastWatchdog : Settings =
  { WedgeAfter = TestTimeouts.watchdogWedgeAfter
    CheckEvery = TestTimeouts.watchdogCheckEvery }

type private Harness =
  { Mailbox: MailboxProcessor<SessionCommand>
    ReadSnapshot: unit -> QuerySnapshot
    Alarms: ConcurrentQueue<SupervisorAlarm>
    AlarmSeen: SemaphoreSlim
    /// Every health change the daemon's OnSupervisorHealth hook was told about, in order.
    HealthChanges: ConcurrentQueue<SupervisorHealth> }

let private okStart (_call: int) : Result<Process, SageFsError> = Ok (Process.GetCurrentProcess())

let private runtimeStarting (start: int -> Result<Process, SageFsError>) (stopWorker: unit -> Async<unit>) : SessionManagerRuntime =
  let startCalls = ref 0
  { StartWorkerProcess =
      fun _ _ _ _ _ _ ->
        start (Interlocked.Increment startCalls) |> Result.map (fun p -> ({ Process = p; AdoptedCore = None } : SessionManager.SpawnedWorker))
    AwaitWorkerPort = fun _ _ _ _ -> ()
    StopWorker = fun _ -> stopWorker ()
    RunBuildAsync = fun _ _ -> async { return Ok "build ok" } }

let private runtimeWith (stopWorker: unit -> Async<unit>) : SessionManagerRuntime = runtimeStarting okStart stopWorker

let private withHarness (runtime: SessionManagerRuntime) (onCommandStart: string -> unit) (run: Harness -> Task<unit>) : Task<unit> =
  task {
    use cancellation = new CancellationTokenSource()
    let alarms = ConcurrentQueue<SupervisorAlarm>()
    let seen = new SemaphoreSlim(0)
    let healthChanges = ConcurrentQueue<SupervisorHealth>()
    let callbacks =
      { SessionManagerCallbacks.silent with
          OnSupervisorAlarm = fun alarm -> alarms.Enqueue alarm; seen.Release() |> ignore
          OnSupervisorHealth = healthChanges.Enqueue
          OnCommandStart = onCommandStart
          Watchdog = fastWatchdog }
    let mailbox, readSnapshot = createWithAlarm runtime cancellation.Token callbacks
    try
      do! run { Mailbox = mailbox; ReadSnapshot = readSnapshot; Alarms = alarms; AlarmSeen = seen; HealthChanges = healthChanges }
    finally
      cancellation.Cancel()
  }

let private awaitAlarm (harness: Harness) : Task<SupervisorAlarm> =
  task {
    let! arrived = harness.AlarmSeen.WaitAsync patience
    match arrived with
    | true ->
      match harness.Alarms.TryDequeue() with
      | true, alarm -> return alarm
      | false, _ -> return failtest "an alarm was signalled but none was queued"
    | false -> return failtest "no alarm arrived within the patience ceiling"
  }

let private createSession (harness: Harness) : Task<SessionInfo> =
  task {
    let! created =
      harness.Mailbox.PostAndAsyncReply(fun reply ->
        SessionCommand.CreateSession([ SessionProjectTarget.Project "Test.fsproj" ], @"C:\Test", true, WorkflowTypes.SessionWorkflow.Interactive, reply))
      |> Async.StartAsTask
    match created with
    | Ok info -> return info
    | Error err -> return failtestf "create session failed: %s" (SageFsError.describe err)
  }

/// A reply from the loop, proving it is draining again.
let private loopAnswers (harness: Harness) : Task<unit> =
  task {
    let! _ = harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.ListSessions reply) |> Async.StartAsTask
    return ()
  }

let private isDegraded (snapshot: QuerySnapshot) =
  match snapshot.SupervisorHealth with
  | SupervisorHealth.Degraded _ -> true
  | SupervisorHealth.Healthy -> false

[<Tests>]
let sessionManagerSupervisorAlarmTests =
  testList "SessionManager supervisor alarm" [

    testTask "a StopWorker that never returns raises Wedged, reports Degraded, and clears when the loop drains again" {
      let gate = TaskCompletionSource()
      let runtime = runtimeWith (fun () -> async { do! Async.AwaitTask gate.Task })
      do! withHarness runtime ignore (fun harness -> task {
        let! info = createSession harness
        // The stop parks the loop on the gate. The caller is never answered.
        harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.StopSession(info.Id, reply))
        |> Async.StartAsTask
        |> ignore

        let! alarm = awaitAlarm harness
        match alarm with
        | SupervisorAlarm.Wedged(command, since) ->
          command |> Expect.stringContains "the alarm names the stuck command" "StopSession"
          (since < DateTime.UtcNow) |> Expect.isTrue "it names when the command started"
        | other -> failtestf "expected Wedged, got %A" other
        harness.ReadSnapshot() |> isDegraded |> Expect.isTrue "daemon health says Degraded while the loop is wedged"

        gate.SetResult()
        do! loopAnswers harness
        do! loopAnswers harness
        harness.ReadSnapshot() |> isDegraded |> Expect.isFalse "health is Healthy again once the loop drains"
        harness.HealthChanges
        |> Seq.map (function SupervisorHealth.Degraded _ -> "Degraded" | SupervisorHealth.Healthy -> "Healthy")
        |> List.ofSeq
        |> Expect.equal "the daemon's hook hears the degrade and then the recovery, once each" [ "Degraded"; "Healthy" ]
      })
    }

    testTask "a workflow switch whose spawn throws N times raises CommandFailed each time and the loop keeps serving" {
      // The first spawn creates the session; every later spawn is the switch's
      // spawn-first replacement, which throws inside the handler.
      let failures = 3
      let runtime =
        runtimeStarting
          (fun call -> match call with 1 -> okStart call | _ -> failwith "spawn boom during workflow switch")
          (fun () -> async { return () })
      let hotReload = WorkflowTypes.SessionWorkflow.HotReload WorkflowTypes.BrowserRefreshConfig.defaults
      do! withHarness runtime ignore (fun harness -> task {
        let! info = createSession harness
        for _ in 1 .. failures do
          harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.SwitchWorkflow(info.Id, hotReload, reply))
          |> Async.StartAsTask
          |> ignore
        for _ in 1 .. failures do
          let! alarm = awaitAlarm harness
          match alarm with
          | SupervisorAlarm.CommandFailed command -> command |> Expect.stringContains "the alarm names the failing command" "SwitchWorkflow"
          | other -> failtestf "expected CommandFailed, got %A" other
        harness.ReadSnapshot() |> isDegraded |> Expect.isTrue "the last command failed, so health is Degraded until one succeeds"
        // A reply is sent before its command is marked done, so a second round
        // trip is what proves the first one's completion has been recorded.
        do! loopAnswers harness
        do! loopAnswers harness
        harness.ReadSnapshot() |> isDegraded |> Expect.isFalse "a command that succeeds clears it"
      })
    }

    testTask "a fault in the loop itself raises LoopRestarted carrying the running count" {
      let faults = 3
      let remaining = ref faults
      let injectFault (_command: string) =
        match Interlocked.Decrement remaining >= 0 with
        | true -> failwith "loop fault"
        | false -> ()
      do! withHarness (runtimeWith (fun () -> async { return () })) injectFault (fun harness -> task {
        for _ in 1 .. faults do
          harness.Mailbox.Post(SessionCommand.TouchSession (SessionId.newId ()))
        let counts = ResizeArray<int>()
        for _ in 1 .. faults do
          let! alarm = awaitAlarm harness
          match alarm with
          | SupervisorAlarm.LoopRestarted count -> counts.Add count
          | other -> failtestf "expected LoopRestarted, got %A" other
        counts |> List.ofSeq |> Expect.equal "each restart carries how many there have been" [ 1; 2; 3 ]
        do! loopAnswers harness
      })
    }
  ]
