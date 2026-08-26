namespace SageFs

open System

/// The thin supervisor: spawn / watch / broker / restart the FSI host
/// process, as an in-daemon MailboxProcessor actor (see plan:
/// fsi-host-supervisor-isolation).
///
/// This module holds the PURE decision logic (port-handshake validation,
/// host-exited-before-ready translation, restart decisions) separated from
/// the actor loop so it is unit-testable without processes. The actor itself
/// lives in the daemon (SessionManager wiring, Phase 1 step 6).
module Supervisor =

  /// The host's ready line prefix on stdout (unchanged from the worker era).
  let hostReadyLine = "WORKER_PORT="

  /// Validate a host stdout line as the ready signal. Fail-closed: only a
  /// loopback URL is acceptable; anything else (or a host that never printed
  /// the line) is an Error.
  let validateHostReadyLine (prefix: string) (line: string) : Result<string, string> =
    match line.StartsWith prefix with
    | false ->
      Error(sprintf "host stdout line is not a ready signal (missing '%s'): %s" prefix line)
    | true ->
      let url = line.Substring prefix.Length
      let isLoopback =
        Uri.TryCreate(url, UriKind.Absolute)
        |> function
          | false, _ -> false
          | true, uri ->
            uri.IsLoopback
      match isLoopback with
      | true -> Ok url
      | false ->
        Error(sprintf "host ready URL must be loopback, got: %s" url)

  /// The host exited before printing the ready line. Carries the exit code.
  type HostExitBeforeReady = HostExitBeforeReady of exitCode: int

  /// Translate "host died before ready" into the session-manager failure path
  /// (WorkerSpawnFailed) — never a hang, never a false "ready".
  let hostExitedBeforeReady (exit: HostExitBeforeReady) : Result<unit, SageFsError> =
    match exit with
    | HostExitBeforeReady code ->
      Error(
        SageFsError.WorkerSpawnFailed(
          sprintf "FSI host exited (code %d) before reporting ready" code))
