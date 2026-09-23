/// `SessionLifecycleStatus.ofWorkerReport` reconciles the daemon's registry
/// entry for a session with what the worker's OWN `/status` reply just said.
/// `get_fsi_status` (Mcp.fs) calls it on every poll and writes the result
/// straight back into the registry when it differs from what was there.
///
/// Found 2026-09-22 in a real run: a Hot Reload session faulted, and
/// `/health` went on to report `status: "Starting"` for it while the
/// session's own registry entry already said `Faulted`. The root cause was
/// here, not at `/health` or `SessionHealth.classify` (which already reports
/// `Failed` correctly for a registry entry that says `Faulted`): a worker
/// process does not always die the instant the daemon decides it has —
/// a hung process the parent-death watchdog has not reaped yet, or a reply
/// that raced the fault signal the daemon learned about through another
/// channel (WorkerExited, a spawn failure) — so a `GetStatus` poll can still
/// get back "I'm Starting" from a process the daemon has ALREADY, correctly,
/// recorded as Faulted. The old `ofWorkerReport` took that reply at face
/// value and reconstructed a live `Starting`/`Ready` status from it — and
/// because `get_fsi_status` persists whatever this function returns, calling
/// it could resurrect a session the daemon had already pronounced dead.
///
/// The fix: Faulted and Stopped are TERMINAL inputs to this function. Once
/// the registry says either, no worker report changes that — a genuine
/// restart never goes through `ofWorkerReport` at all; it replaces the
/// registry entry outright with a fresh status of its own.
module SageFs.Tests.SessionLifecycleStatusReconciliationTests

open Expecto
open Expecto.Flip
open SageFs.WorkerProtocol

let private handle = { Pid = 4242; Port = Some 5000 }

[<Tests>]
let ofWorkerReportStickyTerminalTests =
  testList "SessionLifecycleStatus.ofWorkerReport — Faulted/Stopped are sticky" [

    testCase "WHY — a stale 'Starting' reply from a worker the daemon already faulted must not resurrect the session" <| fun _ ->
      let current = SessionLifecycleStatus.Faulted (Some "boom")
      SessionLifecycleStatus.ofWorkerReport current SessionStatus.Starting
      |> Expect.equal "a live-sounding reply must not undo a recorded fault" current

    testCase "WHY — the same holds for every non-terminal reply a lagging worker could send" <| fun _ ->
      let current = SessionLifecycleStatus.Faulted (Some "boom")
      [ SessionStatus.Starting; SessionStatus.Ready; SessionStatus.Evaluating
        SessionStatus.Building "restoring"; SessionStatus.Restarting ]
      |> List.iter (fun reported ->
        SessionLifecycleStatus.ofWorkerReport current reported
        |> Expect.equal (sprintf "a %A reply must not undo a recorded fault" reported) current)

    testCase "WHY — Stopped is sticky the same way Faulted is" <| fun _ ->
      let current = SessionLifecycleStatus.Stopped
      SessionLifecycleStatus.ofWorkerReport current SessionStatus.Ready
      |> Expect.equal "a live-sounding reply must not undo a recorded stop" current

    testCase "WHY — a worker reporting its own fault is still honored while the registry isn't terminal yet" <| fun _ ->
      let current = SessionLifecycleStatus.Ready handle
      match SessionLifecycleStatus.ofWorkerReport current SessionStatus.Faulted with
      | SessionLifecycleStatus.Faulted _ -> ()
      | other -> failtestf "a worker-reported fault on a live registry entry must land as Faulted, got %A" other

    testCase "WHY — ordinary reconciliation is unaffected for a non-terminal registry entry" <| fun _ ->
      let current = SessionLifecycleStatus.Starting handle
      match SessionLifecycleStatus.ofWorkerReport current SessionStatus.Ready with
      | SessionLifecycleStatus.Ready h -> h |> Expect.equal "pid/port carry over from the registry" handle
      | other -> failtestf "a Ready report on a Starting entry must reconcile to Ready, got %A" other

    // WHY — the invariant this whole function exists to protect: once the
    // registry status is a get_fsi_status-observable Failed verdict via
    // SessionHealth.classify, reconciling against ANY worker report must
    // keep it that way — health and the registry can never be made to
    // disagree by the one function that writes the registry back.
    testCase "WHY — health stays Failed no matter what a lagging worker reports afterward" <| fun _ ->
      let current = SessionLifecycleStatus.Faulted (Some "boom")
      [ SessionStatus.Starting; SessionStatus.Ready; SessionStatus.Evaluating; SessionStatus.Stopped ]
      |> List.iter (fun reported ->
        let reconciled = SessionLifecycleStatus.ofWorkerReport current reported
        let health = SageFs.SessionHealth.classify reconciled [] None
        match health with
        | SageFs.SessionHealth.Failed _ -> ()
        | other -> failtestf "health must stay Failed after reconciling against %A, got %A" reported other)
  ]
