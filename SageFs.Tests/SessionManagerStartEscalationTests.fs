module SageFs.Tests.SessionManagerStartEscalationTests

/// A worker that runs out of patience while it starts: the daemon retries with MORE patience, never
/// the same, and when it gives up the session is faulted with the whole story. Before this, the daemon
/// killed the worker at the inactivity limit and the restart policy started it again with the same
/// limit, five times, ending in "Worker process exited with code 137 (abandoned after max retries)"
/// (observed on a 4 core machine that needed 40 s for what the limit gave 30 s: scripts/machine-bench.fsx,
/// 2026-10-01).
///
/// Part one drives the session manager with a fake runtime and posts the timeouts itself. Part two runs
/// the REAL `awaitWorkerPort` against a real silent child process.
open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol
open SageFs.Tests.StartEscalationTimeouts

type private Harness =
  { Mailbox: MailboxProcessor<SessionCommand>
    FaultedEvents: ResizeArray<SessionId * string>
    ProgressEvents: ResizeArray<SessionId * string>
    /// The budget each AwaitWorkerPort call was given, oldest first.
    Budgets: ResizeArray<StartBudget>
    StartCalls: unit -> int }

let private withHarness (run: Harness -> unit) =
  use cancellation = new CancellationTokenSource()
  let faulted = ResizeArray<SessionId * string>()
  let progress = ResizeArray<SessionId * string>()
  let budgets = ResizeArray<StartBudget>()
  let mutable startCalls = 0
  let runtime : SessionManagerRuntime =
    { StartWorkerProcess =
        fun _ _ _ _ _ _ ->
          startCalls <- startCalls + 1
          Ok ({ Process = Process.GetCurrentProcess(); AdoptedCore = None } : SessionManager.SpawnedWorker)
      AwaitWorkerPort = fun _ _ _ _ budget -> budgets.Add budget
      StopWorker = fun _ -> async { return () }
      RunBuildAsync = fun _ _ -> async { return Ok "build ok" }
      Ledger = StartLedger.closed }
  let mailbox, _ =
    createWith
      runtime
      cancellation.Token
      ignore
      (fun _ _ -> ())
      (fun _ _ -> ())
      ignore
      (fun sid text -> progress.Add(sid, text))
      (fun sid msg -> faulted.Add(sid, msg))
      (fun _ _ -> ())
  try
    run
      { Mailbox = mailbox
        FaultedEvents = faulted
        ProgressEvents = progress
        Budgets = budgets
        StartCalls = fun () -> startCalls }
  finally
    try mailbox.PostAndReply(fun reply -> SessionCommand.StopAll reply) with _ -> ()
    cancellation.Cancel()

let private createSession (harness: Harness) =
  match harness.Mailbox.PostAndReply(fun reply ->
    SessionCommand.CreateSession([ SageFs.SessionProjectTarget.Project "Test.fsproj" ], "/test", true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
  | Ok info ->
    // The create handler replies BEFORE it starts watching the worker, so a second round trip is what
    // says the watch has begun: the mailbox handles one command at a time.
    harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply)) |> ignore
    info
  | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)

let private getSession (harness: Harness) (id: SessionId) =
  match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(id, reply)) with
  | Some session -> session
  | None -> failtestf "expected session %s to exist" (SessionId.value id)

let private timeoutOf (budget: StartBudget) : StartTimeout =
  { Stage = StartStage.WorkerPort
    Budget = budget
    Waited = budget.Inactivity
    Progress = ProgressSeen.NoneYet }

/// Tell the manager the worker it last spawned ran out of patience, and wait until it has been handled.
let private timeOut (harness: Harness) (id: SessionId) =
  let session = getSession harness id
  let pid = SessionLifecycleStatus.workerPid session.Info.Status |> Option.defaultWith (fun () -> failtest "expected a worker pid")
  let budget = harness.Budgets.[harness.Budgets.Count - 1]
  harness.Mailbox.Post(SessionCommand.WorkerStartTimedOut(id, pid, timeoutOf budget, ""))
  harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(id, reply)) |> ignore

[<Tests>]
let manager =
  testList "Session manager: a worker that runs out of patience while starting" [

    testCase "WHY — the first attempt is given the tier's silence allowance and the absolute bound, and says it is attempt 1" <| fun _ ->
      withHarness <| fun harness ->
        createSession harness |> ignore
        harness.Budgets.Count |> Expect.equal "one worker awaited" 1
        harness.Budgets.[0].Attempt |> Expect.equal "attempt 1" 1
        harness.Budgets.[0].Inactivity |> Expect.equal "the tier's allowance" Timeouts.warmupInactivityLimit
        harness.Budgets.[0].Absolute |> Expect.equal "the tier's absolute bound" Timeouts.warmupAbsoluteMax

    testCase "WHY — a timeout starts the worker again with strictly MORE patience, and the session is Starting again, not Faulted and not Restarting" <| fun _ ->
      withHarness <| fun harness ->
        let info = createSession harness
        timeOut harness info.Id
        harness.StartCalls() |> Expect.equal "the worker was started again" 2
        harness.Budgets.Count |> Expect.equal "and awaited again" 2
        harness.Budgets.[1].Attempt |> Expect.equal "attempt 2" 2
        (harness.Budgets.[1].Inactivity > harness.Budgets.[0].Inactivity) |> Expect.isTrue "strictly more patience"
        (match (getSession harness info.Id).Info.Status with
         | SessionLifecycleStatus.Starting _ -> ()
         | other -> failtestf "expected Starting, got %A" other)
        harness.FaultedEvents.Count |> Expect.equal "not faulted" 0

    testCase "WHY — the retry says so in the session's progress text, so the dashboard card and get_session_status show what is happening" <| fun _ ->
      withHarness <| fun harness ->
        let info = createSession harness
        timeOut harness info.Id
        harness.ProgressEvents
        |> Seq.map snd
        |> Seq.exists (fun text -> text.Contains("attempt 2", StringComparison.Ordinal))
        |> Expect.isTrue "a progress line names attempt 2"

    testCase "WHY — once the attempts are used up the session is Faulted with a start failure that names the wait, and the fault callback carries the same words" <| fun _ ->
      withHarness <| fun harness ->
        let info = createSession harness
        for _ in 1 .. StartEscalation.MaxAttempts do
          timeOut harness info.Id
        harness.StartCalls() |> Expect.equal "one start per attempt, and none after the last" StartEscalation.MaxAttempts
        (match (getSession harness info.Id).Info.Status with
         | SessionLifecycleStatus.Faulted (FaultReason.StartTimedOut failure) ->
           failure.Attempts |> Expect.equal "every attempt is counted" StartEscalation.MaxAttempts
           failure.Stage |> Expect.equal "what it waited for" StartStage.WorkerPort
           failure.Tier |> Expect.equal "the tier it ran on" Timeouts.machineTier
           failure.Budgets |> List.pairwise |> List.forall (fun (a, b) -> b > a) |> Expect.isTrue "each attempt was given more"
         | other -> failtestf "expected a start failure, got %A" other)
        harness.FaultedEvents.Count |> Expect.equal "one fault callback" 1
        let message = harness.FaultedEvents.[0] |> snd
        message |> Expect.stringContains "says what it waited for" (StartStage.describe StartStage.WorkerPort)
        message.Contains("abandoned after max retries", StringComparison.Ordinal) |> Expect.isFalse "not the old message"

    testCase "WHY — the faulted session's reason, as every surface reads it, is the full explanation and not a bare 'faulted'" <| fun _ ->
      withHarness <| fun harness ->
        let info = createSession harness
        for _ in 1 .. StartEscalation.MaxAttempts do
          timeOut harness info.Id
        match (getSession harness info.Id).Info.Status |> SessionLifecycleStatus.faultReason with
        | Some reason ->
          let text = FaultReason.describe reason
          text |> Expect.stringContains "the tier" (MachineTier.toString Timeouts.machineTier)
          text |> Expect.stringContains "what to do" "hard_reset_fsi_session"
        | None -> failtest "the session should be faulted"

    testCase "WHY — a timeout reported by a worker the session has already replaced is ignored, so a stale event cannot fault a healthy start" <| fun _ ->
      withHarness <| fun harness ->
        let info = createSession harness
        let stalePid = (SessionLifecycleStatus.workerPid (getSession harness info.Id).Info.Status |> Option.get) + 1
        harness.Mailbox.Post(SessionCommand.WorkerStartTimedOut(info.Id, stalePid, timeoutOf harness.Budgets.[0], ""))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply)) |> ignore
        harness.StartCalls() |> Expect.equal "no second start" 1
        harness.FaultedEvents.Count |> Expect.equal "not faulted" 0
  ]

// ---- the real awaitWorkerPort, against a real silent child process ----

/// A process that prints nothing and stays alive: what a worker looks like while it builds the FSI host.
let private startSilentChild () : Process =
  let psi = ProcessStartInfo()
  psi.FileName <- "sleep"
  psi.Arguments <- "600"
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  Process.Start psi

/// A process that says the worker port at once.
let private startPortChild () : Process =
  let psi = ProcessStartInfo()
  psi.FileName <- "sh"
  psi.Arguments <- "-c \"echo WORKER_PORT=http://localhost:1; sleep 600\""
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  Process.Start psi

/// A mailbox that hands the first command of interest to a task.
let private collect (wanted: SessionCommand -> bool) : MailboxProcessor<SessionCommand> * Task<SessionCommand> =
  let first = TaskCompletionSource<SessionCommand>(TaskCreationOptions.RunContinuationsAsynchronously)
  let inbox =
    MailboxProcessor<SessionCommand>.Start(fun box ->
      async {
        while true do
          let! command = box.Receive()
          match wanted command with
          | true -> first.TrySetResult command |> ignore
          | false -> ()
      })
  inbox, first.Task

let private shortBudget =
  StartEscalation.firstBudget StageHistory.NeverSeen shortSilence shortAbsolute

let private killQuietly (child: Process) : unit =
  try child.Kill(true) with _ -> ()

[<Tests>]
let realAwait =
  testList "awaitWorkerPort against a real child process" [

    testTask "WHY — a worker that goes silent past its allowance is reported as a START TIMEOUT carrying the budget it was given, not as a spawn failure the restart policy would retry identically" {
      match OperatingSystem.IsWindows() with
      | true -> skiptest "needs a POSIX `sleep`"
      | false ->
        let child = startSilentChild ()
        let cancellation = new CancellationTokenSource()
        try
          let childPid = child.Id
          let inbox, timedOut = collect (function SessionCommand.WorkerStartTimedOut _ | SessionCommand.WorkerSpawnFailed _ -> true | _ -> false)
          awaitWorkerPort StartLedger.closed (SessionId.newId ()) child inbox cancellation.Token shortBudget
          let! winner = Task.WhenAny(timedOut, Task.Delay TestTimeouts.patience)
          Expect.isTrue "a command arrived before the patience ran out" (obj.ReferenceEquals(winner, timedOut))
          match timedOut.Result with
          | SessionCommand.WorkerStartTimedOut (_, pid, timeout, _) ->
            pid |> Expect.equal "the pid of the silent child" childPid
            timeout.Budget |> Expect.equal "the budget it was given" shortBudget
            timeout.Progress |> Expect.equal "it never said anything" ProgressSeen.NoneYet
            (timeout.Waited >= shortSilence) |> Expect.isTrue "it waited the whole allowance"
          | other -> failtestf "expected WorkerStartTimedOut, got %A" other
        finally
          cancellation.Dispose()
          killQuietly child
    }

    testTask "WHY — a worker that reports its port is recorded in the start ledger, so the next daemon already knows how long a start takes here" {
      match OperatingSystem.IsWindows() with
      | true -> skiptest "needs a POSIX `sh`"
      | false ->
        let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sagefs-await-" + Guid.NewGuid().ToString("N"))
        let child = startPortChild ()
        let cancellation = new CancellationTokenSource()
        try
          let ledger =
            StartLedger.openAt dir (MachineProfile.ofProbe { LogicalCores = 4; CpuQuota = CpuQuota.Unlimited; TotalMemoryMb = 8000L; AvailableMemoryMb = 4000L; Storage = StorageKind.Unknown; Calibration = Calibration.NotMeasured "test" })
          let inbox, ready = collect (function SessionCommand.WorkerReady _ -> true | _ -> false)
          awaitWorkerPort ledger (SessionId.newId ()) child inbox cancellation.Token shortBudget
          let! winner = Task.WhenAny(ready, Task.Delay TestTimeouts.patience)
          Expect.isTrue "the port was reported" (obj.ReferenceEquals(winner, ready))
          match ledger.History StartStage.WorkerPort with
          | StageHistory.Seen estimate -> estimate.Samples |> Expect.equal "one observation" 1
          | StageHistory.NeverSeen -> failtest "the start was not recorded"
        finally
          cancellation.Dispose()
          killQuietly child
          try System.IO.Directory.Delete(dir, true) with _ -> ()
    }
  ]
