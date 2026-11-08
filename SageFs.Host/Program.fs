module SageFs.Host.Program

/// Entry point for the SageFs.Host process — the minimal FSI host.
///
/// Spawned by the daemon's supervisor. Args: <sessionId> <httpPort> [<settings snapshot>]
/// (`SageFs.WorkerCommandLine`). The snapshot is the daemon's resolved settings (saved values and the machine
/// tier); it is established first, before anything reads `Timeouts`.
/// The project list, bare flag, and watch flag arrive via environment
/// variables (SAGEFS_SESSION_PROJECTS etc.), exactly as the worker received
/// them before the host extraction.
[<EntryPoint>]
let main args =
  let commandLine =
    match SageFs.WorkerCommandLine.parse args with
    | Ok line -> line
    | Error SageFs.MissingSessionId ->
      eprintfn "SageFs.Host: missing sessionId argument"
      exit 2
    | Error SageFs.MissingHttpPort ->
      eprintfn "SageFs.Host: missing httpPort argument"
      exit 2
    | Error (SageFs.InvalidHttpPort text) ->
      eprintfn "SageFs.Host: invalid httpPort argument: %s" text
      exit 2
    | Error (SageFs.InvalidSettings why) ->
      eprintfn "SageFs.Host: the settings snapshot argument could not be read: %A" why
      exit 2

  match SageFs.SettingsInForce.establish commandLine.Settings with
  | SageFs.Establishment.EstablishedAfterReads ->
    eprintfn "SageFs.Host: a setting was read before the snapshot was established, so that value holds the environment's."
  | SageFs.Establishment.Established
  | SageFs.Establishment.AlreadyEstablished
  | SageFs.Establishment.Conflicting _ -> ()

  let sessionId = commandLine.SessionId
  let httpPort = commandLine.HttpPort

  let hostDir = System.AppContext.BaseDirectory

  // Fail-closed: the host refuses to start if its own directory contains
  // anything outside the vetted manifest. A mis-packaged host must never
  // serve — it exits loudly so the supervisor can surface the reason.
  match SageFs.HostManifest.check hostDir with
  | Ok () -> ()
  | Error msg ->
    eprintfn "SageFs.Host: REFUSING TO START — %s" msg
    exit 3

  // Give `Log.*` a real destination before anything runs. The host has no
  // ILogger provider, so without this every Log call after startup (project
  // loading, hot reload, warmup) was dropped and a degraded session left no
  // trace. Fail-open: a worker that cannot open its log still serves, and says
  // why on stderr, which the daemon keeps a bounded tail of.
  use _workerLog =
    match SageFs.WorkerLogFile.tryInstall SageFs.DaemonState.SageFsDir sessionId with
    | Ok writer -> writer :> System.IDisposable
    | Error err ->
      eprintfn "SageFs.Host: worker log file unavailable, Log goes to stderr only: %A" err
      { new System.IDisposable with member _.Dispose() = () }

  // The process entry point: `main` has to wait for the worker to finish, and nothing else is waiting on the pool.
  SageFs.Server.WorkerMain.run sessionId httpPort
  |> Async.RunSynchronously
  0
