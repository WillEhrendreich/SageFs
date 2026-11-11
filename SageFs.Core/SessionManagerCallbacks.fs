namespace SageFs

open SageFs.WorkerProtocol

/// Everything the manager tells the outside world, plus the supervisor's alarm.
type SessionManagerCallbacks =
  { OnSessionProgressChanged: unit -> unit
    OnSessionsChanged: unit -> unit
    OnTestDiscovery: SessionId -> TestDiscoveryReport -> unit
    OnInstrumentationMaps: SessionId -> Features.LiveTesting.InstrumentationMap array -> unit
    OnSessionReady: SessionId -> unit
    OnWarmupProgress: SessionId -> string -> unit
    OnSessionFaulted: SessionId -> string -> unit
    /// The registry no longer lists the session, by whatever route; runs on the loop thread once the command is done.
    OnSessionRemoved: SessionId -> unit
    OnAppOutput: SessionId -> SageFs.AppOutput.Stream -> string -> unit
    WhoDrives: SessionId -> SessionDriver
    /// The loop is wedged, restarted, or failed a command (see SupervisorWatchdog).
    OnSupervisorAlarm: SupervisorWatchdog.SupervisorAlarm -> unit
    /// Health changed (an alarm degraded it, or a good command cleared it); the daemon feeds SupervisorHealthWatch.
    OnSupervisorHealth: SupervisorHealth -> unit
    /// Runs on the loop thread as each command starts, outside the per-command guard (a throw restarts the loop).
    OnCommandStart: string -> unit
    Watchdog: SupervisorWatchdog.Settings }

module SessionManagerCallbacks =
  let silent : SessionManagerCallbacks =
    { OnSessionProgressChanged = ignore
      OnSessionsChanged = ignore
      OnTestDiscovery = fun _ _ -> ()
      OnInstrumentationMaps = fun _ _ -> ()
      OnSessionReady = ignore
      OnWarmupProgress = fun _ _ -> ()
      OnSessionFaulted = fun _ _ -> ()
      OnSessionRemoved = ignore
      OnAppOutput = fun _ _ _ -> ()
      WhoDrives = fun _ -> SessionDriver.Everyone
      OnSupervisorAlarm = ignore
      OnSupervisorHealth = ignore
      OnCommandStart = ignore
      Watchdog = SupervisorWatchdog.defaultSettings }
