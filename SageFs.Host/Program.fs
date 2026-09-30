module SageFs.Host.Program

/// Entry point for the SageFs.Host process — the minimal FSI host.
///
/// Spawned by the daemon's supervisor. Args: <sessionId> <httpPort>.
/// The project list, bare flag, and watch flag arrive via environment
/// variables (SAGEFS_SESSION_PROJECTS etc.), exactly as the worker received
/// them before the host extraction.
[<EntryPoint>]
let main args =
  let sessionId =
    match args |> Array.tryItem 0 with
    | Some id -> id
    | None ->
      eprintfn "SageFs.Host: missing sessionId argument"
      exit 2

  let httpPort =
    match args |> Array.tryItem 1 with
    | Some p ->
      match System.Int32.TryParse p with
      | true, port -> port
      | _ ->
        eprintfn "SageFs.Host: invalid httpPort argument: %s" p
        exit 2
    | None ->
      eprintfn "SageFs.Host: missing httpPort argument"
      exit 2

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

  SageFs.Server.WorkerMain.run sessionId httpPort
  |> Async.RunSynchronously
  0
