namespace SageFs

open System
open System.Threading

/// Validated timeout value — enforces 1s–10min range at construction.
type ValidTimeout = private ValidTimeout of TimeSpan

[<RequireQualifiedAccess>]
module ValidTimeout =
  let private minTimeout = TimeSpan.FromSeconds(1.0)
  let private maxTimeout = TimeSpan.FromMinutes(10.0)

  let create (t: TimeSpan) =
    match t >= minTimeout && t <= maxTimeout with
    | true -> Ok (ValidTimeout t)
    | false -> Error (sprintf "Timeout must be between 1s and 10min, got %A" t)

  let value (ValidTimeout t) = t

/// Centralized timeout and interval constants.
/// All timeouts in one place for discoverability and future configurability.
/// Top-5 timeouts support environment variable overrides (read once at startup).
[<RequireQualifiedAccess>]
module Timeouts =

  // -- Environment variable helpers --

  let private envOrDefault (varName: string) (defaultSeconds: float) =
    match Environment.GetEnvironmentVariable(varName) with
    | null | "" -> TimeSpan.FromSeconds(defaultSeconds)
    | value ->
      match Double.TryParse(value) with
      | true, v when v > 0.0 -> TimeSpan.FromSeconds(v)
      | _ -> TimeSpan.FromSeconds(defaultSeconds)

  let private envOrDefaultMinutes (varName: string) (defaultMinutes: float) =
    match Environment.GetEnvironmentVariable(varName) with
    | null | "" -> TimeSpan.FromMinutes(defaultMinutes)
    | value ->
      match Double.TryParse(value) with
      | true, v when v > 0.0 -> TimeSpan.FromMinutes(v)
      | _ -> TimeSpan.FromMinutes(defaultMinutes)

  // -- Build & Warmup --
  let warmupAbsoluteMax = envOrDefaultMinutes "SAGEFS_WARMUP_MAX_MINUTES" 10.0
  let warmupInactivityLimit = envOrDefault "SAGEFS_WARMUP_INACTIVITY_SECONDS" 30.0
  let softResetCancellation = TimeSpan.FromMinutes(5.0)
  let initSessionCancellation = TimeSpan.FromMinutes(5.0)

  // -- HTTP / Worker Communication --
  let workerHttpRead = envOrDefault "SAGEFS_WORKER_HTTP_READ_SECONDS" 30.0
  let healthCheck = TimeSpan.FromSeconds(2.0)
  let shutdownHttpClient = TimeSpan.FromSeconds(5.0)
  let sseKeepAlive = TimeSpan.FromHours(24.0)

  // -- Live Testing (configurable at runtime via MCP, thread-safe) --
  let private _perTestLock = obj ()
  let private _globalTestLock = obj ()
  let mutable private _perTestDefault =
    envOrDefault "SAGEFS_PER_TEST_TIMEOUT_SECONDS" 5.0
  let mutable private _globalTestRun = TimeSpan.FromMinutes(2.0)
  let perTestDefault () = lock _perTestLock (fun () -> _perTestDefault)
  let globalTestRun () = lock _globalTestLock (fun () -> _globalTestRun)

  let setPerTestTimeout (t: TimeSpan) =
    match ValidTimeout.create t with
    | Ok _ -> lock _perTestLock (fun () -> _perTestDefault <- t)
    | Error _ -> ()

  let setGlobalTestRunTimeout (t: TimeSpan) =
    match ValidTimeout.create t with
    | Ok _ -> lock _globalTestLock (fun () -> _globalTestRun <- t)
    | Error _ -> ()

  // -- Process Management --
  let buildCompletion = envOrDefaultMinutes "SAGEFS_BUILD_TIMEOUT_MINUTES" 10.0
  let processNormalExit = TimeSpan.FromSeconds(3.0)
  let processKillVerify = TimeSpan.FromSeconds(2.0)
  let stdioFlush = TimeSpan.FromSeconds(5.0)

  // -- Restart / Backoff --
  let restartBaseBackoff = TimeSpan.FromSeconds(1.0)
  let restartMaxBackoff = TimeSpan.FromSeconds(30.0)
  let restartCountResetWindow = TimeSpan.FromMinutes(5.0)

  // -- Watchdog / Supervision --
  let watchdogInterval = TimeSpan.FromSeconds(5.0)
  let watchdogGracePeriod = TimeSpan.FromSeconds(30.0)

  // -- Dashboard / UI --
  let dashboardPollInterval = TimeSpan.FromMilliseconds(100.0)
  let sseEventInterval = TimeSpan.FromSeconds(1.0)

  // -- Daemon / Server --
  let workerEndpointFetch = TimeSpan.FromMilliseconds(500.0)
  let scheduledGraceDelay = TimeSpan.FromSeconds(5.0)
  let startupDelay = TimeSpan.FromMilliseconds(200.0)
  let workerShutdownDelay = TimeSpan.FromSeconds(2.0)

  // -- Persistence --
  let periodicSaveInterval = TimeSpan.FromSeconds(60.0)

  // -- Session Lifecycle --
  let sessionDispose = TimeSpan.FromSeconds(10.0)
  let staleSessionThreshold = TimeSpan.FromMinutes(10.0)
