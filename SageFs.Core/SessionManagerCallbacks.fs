namespace SageFs

open SageFs.WorkerProtocol

/// Everything the manager tells the outside world, plus the supervisor's alarm.
type SessionManagerCallbacks =
  { OnSessionProgressChanged: unit -> unit
    OnTestDiscovery: SessionId -> TestDiscoveryReport -> unit
    OnInstrumentationMaps: SessionId -> Features.LiveTesting.InstrumentationMap array -> unit
    OnSessionReady: SessionId -> unit
    OnWarmupProgress: SessionId -> string -> unit
    OnSessionFaulted: SessionId -> string -> unit
    /// The registry no longer lists the session, whatever command removed it: a stop by any route, a worker that
    /// exited cleanly, shutdown. Runs on the loop thread once that command is done (`SessionRemoval.report`).
    OnSessionRemoved: SessionId -> unit
    OnAppOutput: SessionId -> SageFs.AppOutput.Stream -> string -> unit
    /// The loop is wedged, restarted, or failed a command (see SupervisorWatchdog).
    OnSupervisorAlarm: SupervisorWatchdog.SupervisorAlarm -> unit
    /// Health changed (an alarm degraded it, or a good command cleared it); the daemon feeds SupervisorHealthWatch.
    OnSupervisorHealth: SupervisorHealth -> unit
    /// Runs on the loop thread as each command starts, outside the per-command
    /// guard, so a throw restarts the loop: an observability hook and a test seam.
    OnCommandStart: string -> unit
    Watchdog: SupervisorWatchdog.Settings }

module SessionManagerCallbacks =
  let silent : SessionManagerCallbacks =
    { OnSessionProgressChanged = ignore
      OnTestDiscovery = fun _ _ -> ()
      OnInstrumentationMaps = fun _ _ -> ()
      OnSessionReady = ignore
      OnWarmupProgress = fun _ _ -> ()
      OnSessionFaulted = fun _ _ -> ()
      OnSessionRemoved = ignore
      OnAppOutput = fun _ _ _ -> ()
      OnSupervisorAlarm = ignore
      OnSupervisorHealth = ignore
      OnCommandStart = ignore
      Watchdog = SupervisorWatchdog.defaultSettings }
