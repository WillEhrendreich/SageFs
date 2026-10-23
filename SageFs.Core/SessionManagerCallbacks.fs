namespace SageFs

open SageFs.WorkerProtocol

/// What a worker said when asked for its tests.
[<RequireQualifiedAccess>]
type TestDiscoveryReport =
  | Discovered of tests: Features.LiveTesting.TestCase array * providers: Features.LiveTesting.ProviderDescription list
  | DiscoveryFailed of reason: string

module TestDiscoveryReport =
  let ofResponse (response: WorkerResponse) : TestDiscoveryReport =
    match response with
    | WorkerResponse.InitialTestDiscovery (tests, providers) -> TestDiscoveryReport.Discovered (tests, providers)
    | WorkerResponse.WorkerError err -> TestDiscoveryReport.DiscoveryFailed (SageFsError.describe err)
    | other ->
      let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(other, typeof<WorkerResponse>)
      TestDiscoveryReport.DiscoveryFailed (sprintf "Unexpected reply to test discovery: %s" case.Name)

/// Everything the manager tells the outside world, plus the supervisor's alarm.
type SessionManagerCallbacks =
  { OnSessionProgressChanged: unit -> unit
    OnTestDiscovery: SessionId -> TestDiscoveryReport -> unit
    OnInstrumentationMaps: SessionId -> Features.LiveTesting.InstrumentationMap array -> unit
    OnSessionReady: SessionId -> unit
    OnWarmupProgress: SessionId -> string -> unit
    OnSessionFaulted: SessionId -> string -> unit
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
      OnAppOutput = fun _ _ _ -> ()
      OnSupervisorAlarm = ignore
      OnSupervisorHealth = ignore
      OnCommandStart = ignore
      Watchdog = SupervisorWatchdog.defaultSettings }
