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
        let! _resp = child
        return WorkerHealthProbe.ProbeOutcome.Healthy
      with _ ->
        return WorkerHealthProbe.ProbeOutcome.Missed
    }

  /// Everything a just-ready worker needs started.
  type Launch =
    { Label: string
      Proxy: SessionProxy
      Cancel: CancellationToken
      Outcomes: WatchdogOutcomes
      OnDiscoveryResponse: WorkerResponse -> unit
      OnDiscoveryFailed: string -> unit
      PublishInstrumentationMaps: Features.LiveTesting.InstrumentationMap array -> unit }

  /// Start the three fetches. None of them is awaited.
  let launch (l: Launch) : unit =
    Async.Start(watchUntilReady l.Label l.Proxy l.Cancel l.Outcomes, l.Cancel)
    Async.Start(fetchInitialDiscovery l.Label l.Proxy l.OnDiscoveryResponse l.OnDiscoveryFailed, l.Cancel)
    Async.Start(fetchInstrumentationMaps l.Label l.Proxy l.PublishInstrumentationMaps, l.Cancel)
