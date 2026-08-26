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

  SageFs.Server.WorkerMain.run sessionId httpPort
  |> Async.RunSynchronously
  0
