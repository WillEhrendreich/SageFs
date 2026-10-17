namespace SageFs

open System
open System.Threading
open SageFs.Utils
open SageFs.WorkerProtocol
open SageFs.ProjectLoading

/// What the SessionManager starts the moment a worker's transport is
/// installed: the once-a-second ready-poll watchdog, the initial test
/// discovery fetch and the instrumentation-maps fetch. Split out of the
/// WorkerReady arm. Each takes its dependencies as parameters, so none of it
/// needs the mailbox or the SessionCommand type, which is defined after this
/// file: results come back through the callbacks the caller passes.
module WorkerPostReady =

  /// How often the watchdog asks the worker for its status.
  let private readyPollIntervalMs = int Timeouts.workerReadyPoll.TotalMilliseconds

  let private stackOf (ex: exn) = ex.StackTrace |> Option.ofObj |> Option.defaultValue ""

  /// What the ready-poll reports back. The caller posts these to its mailbox.
  type WatchdogOutcomes =
    { OnReady: ClassifiedProject list -> unit
      OnFaulted: string -> unit
      OnTimedOut: string -> unit }

  /// One GetStatus round-trip, classified for `WarmupSupervision.decidePoll`.
  /// `lastStatusMessage` holds the previous tick's message so a CHANGED one
  /// counts as forward motion; it is captured unconditionally so the NEXT tick
  /// compares against what THIS tick saw.
  let private observe
    (label: string)
    (proxy: SessionProxy)
    (lastStatusMessage: string option ref)
    : Async<WarmupSupervision.PollObservation<ClassifiedProject list>> =
    async {
      try
        let rid = Guid.NewGuid().ToString("N").[..7]
        let! resp = proxy (WorkerMessage.GetStatus rid)
        match resp with
        | WorkerResponse.StatusResult(_, snapshot) ->
          match snapshot.Status with
          | SessionStatus.Ready -> return WarmupSupervision.PollObservation.Ready snapshot.Projects
          | SessionStatus.Faulted | SessionStatus.Stopped ->
            return WarmupSupervision.PollObservation.Faulted snapshot.StatusMessage
          | SessionStatus.HostCrashed crash ->
            return WarmupSupervision.PollObservation.Faulted(Some(HostCrash.describe crash))
          | SessionStatus.Starting
          | SessionStatus.Evaluating
          | SessionStatus.Building _
          | SessionStatus.Restarting ->
            let changed = snapshot.StatusMessage <> lastStatusMessage.Value
            lastStatusMessage.Value <- snapshot.StatusMessage
            match changed with
            | true -> return WarmupSupervision.PollObservation.Progressed
            | false -> return WarmupSupervision.PollObservation.StillWarming
        // default policy: this poll only cares about a StatusResult reply to its
        // own GetStatus request; WorkerResponse is an 18-case wire DU shared by
        // every request/response pair in the protocol, and any other reply here
        // is simply not what was asked for, whatever future cases it grows.
        | _ -> return WarmupSupervision.PollObservation.StillWarming
      with ex ->
        Log.warn "[SessionManager] Worker ready poll transport error for %s: %s (%s)\n%s" label ex.Message (ex.GetType().Name) (stackOf ex)
        return WarmupSupervision.PollObservation.ProbeFailed ex.Message
    }

  /// Poll the worker until it reports Ready, faults, or a bound trips, then
  /// report exactly once. Stops on cancellation. The bound decision is
  /// `WarmupSupervision.decidePoll`, the pure core the DST scenarios exercise;
  /// this loop only supplies the IO. A changed `StatusMessage` resets the
  /// inactivity clock: silence, not slowness, trips it.
  let watchUntilReady
    (label: string)
    (proxy: SessionProxy)
    (ct: CancellationToken)
    (outcomes: WatchdogOutcomes)
    : Async<unit> =
    async {
      let mutable finished = false
      let started = DateTime.UtcNow
      let mutable lastActivityAt = started
      let lastStatusMessage = ref None
      let bounds : WarmupSupervision.Bounds =
        { Absolute = Timeouts.warmupAbsoluteMax
          Inactivity = Timeouts.warmupInactivityLimit }
      while not finished && not ct.IsCancellationRequested do
        do! Async.Sleep readyPollIntervalMs
        let now = DateTime.UtcNow
        let! observation = observe label proxy lastStatusMessage
        match observation with
        | WarmupSupervision.PollObservation.Progressed -> lastActivityAt <- now
        | _ -> ()
        match WarmupSupervision.decidePoll bounds (now - started) (now - lastActivityAt) observation with
        | WarmupSupervision.PollDecision.MarkReady projects ->
          outcomes.OnReady projects
          finished <- true
        | WarmupSupervision.PollDecision.MarkFaulted reason ->
          outcomes.OnFaulted reason
          finished <- true
        | WarmupSupervision.PollDecision.TimedOut reason ->
          Log.warn "[SessionManager] %s (session %s)" reason label
          outcomes.OnTimedOut reason
          finished <- true
        | WarmupSupervision.PollDecision.KeepPolling -> ()
    }

  /// Ask the worker for its tests. The response is handed over as-is; a thrown
  /// failure is reported as text. A cancelled fetch reports nothing.
  let fetchInitialDiscovery
    (label: string)
    (proxy: SessionProxy)
    (onResponse: WorkerResponse -> unit)
    (onFailed: string -> unit)
    : Async<unit> =
    async {
      try
        let rid = Guid.NewGuid().ToString("N")
        let! resp = proxy (WorkerMessage.GetTestDiscovery rid)
        onResponse resp
      with
      | :? OperationCanceledException -> ()
      | ex ->
        Instrumentation.elmloopErrors.Add(1L, System.Collections.Generic.KeyValuePair("phase", "test_discovery" :> obj))
        Log.error "[SessionManager] Test discovery failed for %s: %s\n%s" label ex.Message (stackOf ex)
        onFailed ex.Message
    }

  /// Fetch the instrumentation maps and publish them when there are any.
  let fetchInstrumentationMaps
    (label: string)
    (proxy: SessionProxy)
    (publish: Features.LiveTesting.InstrumentationMap array -> unit)
    : Async<unit> =
    async {
      try
        let rid = Guid.NewGuid().ToString("N")
        let! resp = proxy (WorkerMessage.GetInstrumentationMaps rid)
        match resp with
        | WorkerResponse.InstrumentationMapsResult(_, maps) when not (Array.isEmpty maps) -> publish maps
        // default policy: an empty maps array, or any WorkerResponse other than
        // InstrumentationMapsResult, has nothing to publish: WorkerResponse is
        // the same 18-case wire DU as above.
        | _ -> ()
      with ex ->
        Instrumentation.elmloopErrors.Add(1L, System.Collections.Generic.KeyValuePair("phase", "instrumentation_maps" :> obj))
        Log.error "[SessionManager] Instrumentation maps fetch failed for %s: %s\n%s" label ex.Message (stackOf ex)
    }

  /// Real health probe: one `GetStatus` round-trip over the worker's own
  /// proxy, hard-timed out via `Async.StartChild`'s timeout overload. Any
  /// answer at all, whatever the worker's self-reported status, means it is
  /// alive and responsive; only a timeout or a transport exception counts as
  /// `Missed` (fail-closed, per `WorkerHealthProbe`'s doctrine). Production's
  /// implementation of the `probe` parameter of `WorkerHealthProbe.run`, which
  /// is the seam tests use, so `SessionManagerRuntime`'s shape is unchanged.
  let probeWorkerHealthOnce (timeoutMs: int) (proxy: SessionProxy) : Async<WorkerHealthProbe.ProbeOutcome> =
    async {
      try
        let rid = Guid.NewGuid().ToString("N")
        let! child = Async.StartChild(proxy (WorkerMessage.GetStatus rid), timeoutMs)
        match! child with
        | WorkerResponse.StatusResult(_, snapshot) -> return WorkerHealthProbe.ProbeOutcome.Reported snapshot.Status
        // Any answer at all means the worker is alive; only a status carries anything more.
        | _ -> return WorkerHealthProbe.ProbeOutcome.Healthy
      with _ ->
        return WorkerHealthProbe.ProbeOutcome.Missed
    }

  /// Roast-6 #3: start the per-worker health-probe loop once a worker is confirmed Ready (its proxy is
  /// installed and answering). `currentStatus` reads the wait-free published snapshot, the same CQRS
  /// discipline every other read in the SessionManager uses, never the mailbox, so the loop notices for
  /// itself once this pid stops being the session's current worker (a normal restart, hard reset, or
  /// stop) and simply exits: it never restarts a worker it no longer owns. `postWorkerDied` is how a
  /// confirmed-dead worker is reported to the mailbox.
  let startHealthProbe
    (label: string)
    (workerPid: int)
    (proxy: SessionProxy)
    (currentStatus: unit -> SessionLifecycleStatus option)
    (postStatus: SessionLifecycleStatus -> unit)
    (postWorkerDied: unit -> unit)
    (ct: CancellationToken)
    : unit =
    let shouldContinue () =
      match currentStatus () with
      | Some status -> SessionLifecycleStatus.workerPid status = Some workerPid
      | None -> false
    let onHealthRestart () =
      match shouldContinue () with
      | false -> ()
      | true ->
        Log.warn "[SessionManager] Worker pid %d for session %s missed %d consecutive health checks — restarting" workerPid label WorkerHealthProbe.defaultThreshold
        WorkerSpawn.killWorkerPids [ workerPid ]
        // Mirrors NotifyWorkerDied (DaemonMode.fs): posting pid=-1 always falls into WorkerExited's
        // "handle as real exit" branch regardless of the session's CURRENT recorded pid, and poisons
        // Status to Restarting(Some -1) — so the worker's own genuine Process.Exited event (which the
        // kill above will trigger, carrying the REAL pid) is then recognized as stale by the
        // pid-mismatch guard and ignored, closing the same double-restart race NotifyWorkerDied
        // already closes.
        postWorkerDied ()
    Async.Start(
      WorkerHealthProbe.run
        (fun () -> probeWorkerHealthOnce WorkerHealthProbe.defaultProbeTimeoutMs proxy)
        WorkerHealthProbe.defaultThreshold
        WorkerHealthProbe.defaultProbeIntervalMs
        shouldContinue
        (WorkerHealthProbe.syncRegistry currentStatus postStatus)
        onHealthRestart,
      ct)

  /// The two fetches that say what a worker's code looks like to the daemon: its tests and its
  /// instrumentation maps. A worker answers them for the copy it instrumented, so a new copy
  /// means new probe numbering and the daemon has to ask again.
  type Fetches =
    { Label: string
      Proxy: SessionProxy
      Cancel: CancellationToken
      OnDiscoveryResponse: WorkerResponse -> unit
      OnDiscoveryFailed: string -> unit
      PublishInstrumentationMaps: Features.LiveTesting.InstrumentationMap array -> unit }

  /// Everything a just-ready worker needs started.
  type Launch =
    { Outcomes: WatchdogOutcomes
      Fetches: Fetches }

  /// One half of a refetch, as the worker answered it. A failed discovery is the thrown failure's text.
  [<RequireQualifiedAccess>]
  type RefetchedState =
    | Discovery of WorkerResponse
    | DiscoveryFailed of reason: string
    | InstrumentationMaps of Features.LiveTesting.InstrumentationMap array

  module Fetches =
    /// The two fetches with every answer handed to `answered` as it arrives, for a caller that has to
    /// check the answer is still wanted before it uses it.
    let answeringWith (label: string) (proxy: SessionProxy) (ct: CancellationToken) (answered: RefetchedState -> unit) : Fetches =
      { Label = label
        Proxy = proxy
        Cancel = ct
        OnDiscoveryResponse = fun resp -> answered (RefetchedState.Discovery resp)
        OnDiscoveryFailed = fun reason -> answered (RefetchedState.DiscoveryFailed reason)
        PublishInstrumentationMaps = fun maps -> answered (RefetchedState.InstrumentationMaps maps) }

  /// Start the discovery and the maps fetch, without the ready watchdog. Neither is awaited.
  let launchFetches (f: Fetches) : unit =
    Async.Start(fetchInitialDiscovery f.Label f.Proxy f.OnDiscoveryResponse f.OnDiscoveryFailed, f.Cancel)
    Async.Start(fetchInstrumentationMaps f.Label f.Proxy f.PublishInstrumentationMaps, f.Cancel)

  /// Start the watchdog and both fetches. None of them is awaited.
  let launch (l: Launch) : unit =
    Async.Start(watchUntilReady l.Fetches.Label l.Fetches.Proxy l.Fetches.Cancel l.Outcomes, l.Fetches.Cancel)
    launchFetches l.Fetches

  /// Why the daemon will not fetch a session's live-testing state again right now. A refusal names
  /// its reason, so a caller that asked is told rather than left to wonder whether anything ran.
  [<RequireQualifiedAccess>]
  type RefetchRefusal =
    | NoSuchSession
    /// A replacement worker is warming while the old one still serves: whichever answers, the other's
    /// maps would be the ones the daemon keeps.
    | SwapInProgress
    | WorkerNotReady of status: SessionLifecycleStatus

  module RefetchRefusal =
    let describe (refusal: RefetchRefusal) : string =
      match refusal with
      | RefetchRefusal.NoSuchSession -> "no such session"
      | RefetchRefusal.SwapInProgress -> "a replacement worker is starting and the session is not settled"
      | RefetchRefusal.WorkerNotReady status -> sprintf "the session's worker is not Ready (%A)" status

  /// Whether a refetch's answer still belongs to the worker now serving the session.
  [<RequireQualifiedAccess>]
  type ReplyFate =
    | Current
    | Stale

  /// The one decision about refetching: only a settled session whose worker is Ready is asked, and an
  /// answer is kept only while that same worker is still the one serving. Pure, so the mailbox only applies it.
  module RefetchGate =
    let decide (status: SessionLifecycleStatus) (swap: WorkerReadyCommit.ParkedSwap<'S>) : Result<WorkerHandle, RefetchRefusal> =
      match swap, status with
      | WorkerReadyCommit.ParkedSwap.Parked _, _ -> Error RefetchRefusal.SwapInProgress
      | WorkerReadyCommit.ParkedSwap.NoSwap, SessionLifecycleStatus.Ready handle -> Ok handle
      | WorkerReadyCommit.ParkedSwap.NoSwap, other -> Error(RefetchRefusal.WorkerNotReady other)

    /// `None` is a session that is gone: nothing it was asked can still matter.
    let judgeReply (askedWorkerPid: int) (session: (SessionLifecycleStatus * WorkerReadyCommit.ParkedSwap<'S>) option) : ReplyFate =
      match session with
      | Some(status, swap) ->
        match decide status swap with
        | Ok handle when handle.Pid = askedWorkerPid -> ReplyFate.Current
        | Ok _
        | Error _ -> ReplyFate.Stale
      | None -> ReplyFate.Stale
