namespace SageFs.Simulation

open System
open SageFs
open SageFs.WorkerProtocol
open SageFs.Simulation.Scenario

/// Deterministic fold of a scenario through the REAL supervision core.
module Runner =

  /// The accumulated fold state as the simulated clock advances. Pure data —
  /// the policy is closed over by `run`, not carried here. Terminality is
  /// derived from `Status` (Scenario.isTerminal), never a separate bool.
  type private FoldState =
    { Steps: Step list                 // reversed while folding
      Now: DateTime
      Restart: RestartPolicy.State
      Status: SessionLifecycleStatus
      Pid: int }

  /// A conceptual starting worker pid; a restart spawns a fresh worker, so we
  /// bump the pid on each restart. Only feeds statusAfterExit a plausible
  /// exited-pid — it affects no invariant.
  let private startingPid = 1000

  /// Run a scenario deterministically, producing its trace. No IO; fully
  /// reproducible from (StartTime, Policy, Events) — hence from the Seed.
  ///
  /// Models the real SessionManager control flow faithfully: a Faulted
  /// (gave-up) or Stopped (graceful) session is TERMINAL, and the supervisor
  /// stops feeding it exit events — so a crash after terminal is a recorded
  /// no-op, not another restart decision.
  let run (scenario: Scenario) : Trace =
    let policy = scenario.Policy

    let fold (s: FoldState) (idx: int, ev: SimEvent) : FoldState =
      let record now effect rs status pid =
        let st =
          { Index = idx; At = now; Event = ev; Effect = effect; RestartState = rs; Status = status }
        { s with Steps = st :: s.Steps; Now = now; Restart = rs; Status = status; Pid = pid }

      match ev with
      | SimEvent.ClockAdvance span ->
        // Time passing alone changes nothing but the clock and the At stamp.
        record (s.Now + span) StepEffect.NoEffect s.Restart s.Status s.Pid

      | _ when isTerminal s.Status ->
        // A crash/graceful for an already-terminal session: ignored no-op.
        record s.Now StepEffect.NoEffect s.Restart s.Status s.Pid

      | SimEvent.WorkerExitedGracefully ->
        let outcome = SessionLifecycle.onWorkerExited policy s.Restart 0 s.Now
        let status' = SessionLifecycle.statusAfterExit (Some s.Pid) outcome
        // Graceful => Stopped => terminal. Restart state untouched.
        record s.Now StepEffect.Stopped s.Restart status' s.Pid

      | SimEvent.WorkerCrashed ->
        let outcome = SessionLifecycle.onWorkerExited policy s.Restart 1 s.Now
        match outcome with
        | SessionLifecycle.ExitOutcome.RestartAfter(delay, newState) ->
          let status' = SessionLifecycle.statusAfterExit (Some s.Pid) outcome
          record s.Now (StepEffect.Restarted delay) newState status' (s.Pid + 1)
        | SessionLifecycle.ExitOutcome.Abandoned err ->
          let status' = SessionLifecycle.statusAfterExit (Some s.Pid) outcome
          // Give-up => Faulted => terminal. Restart state carries unchanged
          // (onWorkerExited drops decide's returned state on Abandoned).
          record s.Now (StepEffect.GaveUp err) s.Restart status' s.Pid
        | SessionLifecycle.ExitOutcome.Graceful ->
          // Unreachable for exit code 1, but keep the match total.
          record s.Now StepEffect.Stopped s.Restart SessionLifecycleStatus.Stopped s.Pid

    let init =
      { Steps = []
        Now = scenario.StartTime
        Restart = RestartPolicy.emptyState
        Status = SessionLifecycleStatus.Ready { Pid = startingPid; Port = None }
        Pid = startingPid }

    let final =
      scenario.Events
      |> List.mapi (fun i e -> (i, e))
      |> List.fold fold init

    { Scenario = scenario; Steps = List.rev final.Steps }
