namespace SageFs

open System
open System.Diagnostics
open System.Threading
open SageFs.Utils

/// What a worker start tells whoever started it. Callbacks, not session manager commands, so this file
/// does not depend on the session manager (which depends on it): the manager turns each into the
/// command it posts to its own mailbox.
type WorkerStartupEvents =
  { /// One `WARMUP_PROGRESS=` line's payload.
    OnProgress: string -> unit
    /// The worker reported the port it listens on: its pid, then its base URL.
    OnReady: int -> string -> unit
    /// The worker is not coming up and will not be retried by the escalation: it exited before it
    /// reported a port, ran past the absolute bound, or could not be read. The worker's pid, then the reason.
    OnSpawnFailed: int -> string -> unit
    /// The worker went silent for longer than this attempt was given. Not a crash: the manager decides
    /// whether to try again with more patience (`StartEscalation`) or to give up and say what it waited for.
    /// The worker's pid, what it was given and waited, then the tail of its stderr, kept to explain a give-up.
    OnStartTimedOut: int -> StartTimeout -> string -> unit
    /// One `APP_OUTPUT=` line of a run_app'd app's stdout.
    OnAppOutput: string -> unit }

module WorkerStartup =

  /// Read the worker's stdout until WORKER_PORT is reported, and report what happened through `events`.
  /// Runs completely off the caller's loop: it never blocks a MailboxProcessor.
  ///
  /// Bounded by the attempt's `budget`: `Inactivity` (reset on every WARMUP_PROGRESS= line, so silence,
  /// not slowness, is what trips this) and `Absolute` (the hard ceiling neither progress nor silence can
  /// argue past). Silence past the allowance is `OnStartTimedOut`, not a spawn failure: the manager
  /// retries it with more patience instead of the restart policy repeating it identically. A start that
  /// reaches its port is recorded in `ledger`, so the next first attempt is as patient as this machine has
  /// shown it needs to be. See WarmupSupervision.decidePoll for the pure decision this mirrors.
  let await
    (ledger: Ledger)
    (proc: Process)
    (ct: CancellationToken)
    (budget: StartBudget)
    (events: WorkerStartupEvents)
    : unit =
    Async.Start(async {
      use cts =
        CancellationTokenSource.CreateLinkedTokenSource(ct)
      // Inactivity-bounded, not flat-bounded: reset on every WARMUP_PROGRESS=
      // line so a large repo that is genuinely still discovering/compiling
      // projects gets to keep going, while a process that goes SILENT — the
      // "20+ minutes, no error, no sign of life" failure three onboarding
      // trials hit (fcs-trial-a/b/c, 2026-09-22) — is caught within the
      // attempt's allowance of the moment it stopped talking, not after some
      // flat ceiling that a big-but-healthy warmup could also trip.
      // absoluteDeadline is the hard ceiling neither progress nor silence can
      // argue past.
      let started = DateTime.UtcNow
      let absoluteDeadline = started + budget.Absolute
      let mutable timeoutReason : string option = None
      let mutable lastProgress = ProgressSeen.NoneYet
      let workerPid = proc.Id
      cts.CancelAfter(budget.Inactivity)
      let linkedCt = cts.Token
      // Bounded: the last StderrTail.capacity lines, for the life of the worker.
      // Read only when the worker exits or goes silent, to explain why.
      let stderrTail = StderrTail.create ()
      try
        let stderrTask =
          WorkerSpawn.runOnDedicatedThread "sagefs-worker-stderr-reader" (fun () ->
            try
              let mutable line = proc.StandardError.ReadLine()
              while not (isNull line) do
                stderrTail.Push line
                line <- proc.StandardError.ReadLine()
            with _ -> ())
        let mutable found = None
        while Option.isNone found do
          let! line = proc.StandardOutput.ReadLineAsync(linkedCt).AsTask() |> Async.AwaitTask
          match isNull line with
          | true ->
            // stdout closed, so stderr is about to close too: let the reader
            // finish (bounded) so the tail holds the worker's last words.
            do! System.Threading.Tasks.Task.WhenAny(stderrTask, System.Threading.Tasks.Task.Delay StderrTail.drainGrace) |> Async.AwaitTask |> Async.Ignore
            let stderrSummary = StderrTail.summary stderrTail
            try proc.EnableRaisingEvents <- false with _ -> ()
            try proc.Dispose() with _ -> ()
            events.OnSpawnFailed
              workerPid
              (match String.IsNullOrWhiteSpace stderrSummary with
               | true -> "Worker process exited before reporting port"
               | false -> sprintf "Worker process exited before reporting port. stderr:\n%s" stderrSummary)
            found <- Some ""
          | false when DateTime.UtcNow > absoluteDeadline ->
            // The absolute ceiling tripped exactly as this line arrived —
            // treat it the same as the OperationCanceledException path below
            // rather than accepting one more line past the hard bound.
            timeoutReason <-
              Some (StderrTail.withTail stderrTail (WarmupSupervision.absoluteTimeoutReason (DateTime.UtcNow - started)))
            found <- Some ""
          | false ->
            match line.StartsWith("WARMUP_PROGRESS=", System.StringComparison.Ordinal) with
            | true ->
              let payload = line.Substring("WARMUP_PROGRESS=".Length)
              events.OnProgress payload
              lastProgress <- ProgressSeen.Last payload
              // Progress observed — reset the inactivity clock so a slow-but-
              // working large-repo discovery isn't killed for being slow.
              try cts.CancelAfter(budget.Inactivity) with :? ObjectDisposedException -> ()
            | false ->
              match line.StartsWith("WORKER_PORT=", System.StringComparison.Ordinal) with
              | true ->
                found <- Some (line.Substring("WORKER_PORT=".Length))
              | false -> ()
        match found with
        | Some baseUrl when baseUrl.Length > 0 ->
          // Port found: disable the startup-timeout guard so the long-lived
          // post-startup stdout read below can't trip it and kill a live worker.
          cts.CancelAfter(System.Threading.Timeout.Infinite)
          ledger.Record StartStage.WorkerPort (DateTime.UtcNow - started)
          events.OnReady workerPid baseUrl
          // #82: keep reading stdout past the port line for a run_app'd app's
          // APP_OUTPUT= lines (to EOF; read errors/EOF swallowed, not a spawn fail).
          let appOutTask =
            WorkerSpawn.runOnDedicatedThread "sagefs-worker-stdout-reader" (fun () ->
              try
                let mutable l = proc.StandardOutput.ReadLine()
                while not (isNull l) do
                  (match AppOutput.tryParse l with
                   | Some payload -> events.OnAppOutput payload
                   | None -> ())
                  l <- proc.StandardOutput.ReadLine()
              with _ -> ())
          do! stderrTask |> Async.AwaitTask
          do! appOutTask |> Async.AwaitTask
        | Some _ ->
          // Absolute-deadline branch above: found <- Some "" with a reason
          // parked in timeoutReason, distinct from "process exited" (which
          // already reported its own spawn failure before setting found).
          match timeoutReason with
          | Some reason ->
            // Exit events off BEFORE the kill: a kill that also reported a crash would start the worker
            // again through the restart policy, and this is a failure, not a crash.
            try proc.EnableRaisingEvents <- false with _ -> ()
            try proc.Kill(entireProcessTree = true) with ex2 ->
              Log.warn "[SessionManager] Kill on absolute warmup deadline: %s" ex2.Message
            try proc.Dispose() with _ -> ()
            events.OnSpawnFailed workerPid reason
          | None -> ()
          do! stderrTask |> Async.AwaitTask
        | None ->
          do! stderrTask |> Async.AwaitTask
      with
      | :? OperationCanceledException when not ct.IsCancellationRequested ->
        // Linked CTS fired with no line arriving within the inactivity
        // window: the worker has gone SILENT, not merely slow — a
        // Progressed observation would have reset this timer (see
        // WarmupSupervision.decidePoll's Invariant 4). This is the fix for
        // "warmup on a big repo is unbounded and silent": a healthy big
        // repo keeps resetting this clock by printing WARMUP_PROGRESS=
        // lines; a stuck one goes quiet and is caught within
        // the attempt's allowance of going quiet.
        let waited = DateTime.UtcNow - started
        // Exit events off BEFORE the kill: a kill that also reported a crash would start the worker again
        // through the restart policy with this same allowance, which is the loop this replaced.
        try proc.EnableRaisingEvents <- false with _ -> ()
        try proc.Kill(entireProcessTree = true) with ex2 ->
          Log.warn "[SessionManager] Kill on startup timeout: %s" ex2.Message
        try proc.Dispose() with _ -> ()
        events.OnStartTimedOut
          workerPid
          { Stage = StartStage.WorkerPort
            Budget = budget
            Waited = waited
            Progress = lastProgress }
          (StderrTail.summary stderrTail)
      | ex ->
        try proc.EnableRaisingEvents <- false with _ -> ()
        try proc.Kill(entireProcessTree = true) with ex2 ->
          Log.warn "[SessionManager] Kill on spawn failure: %s" ex2.Message
        try proc.Dispose() with _ -> ()
        events.OnSpawnFailed workerPid (StderrTail.withTail stderrTail (sprintf "Failed to connect to worker: %s" ex.Message))
    }, ct)
