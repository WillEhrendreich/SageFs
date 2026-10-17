namespace SageFs

open System

/// The thin supervisor: spawn / watch / broker / restart the FSI host
/// process, as an in-daemon MailboxProcessor actor (see plan:
/// fsi-host-supervisor-isolation).
///
/// This module holds the PURE decision logic (port-handshake validation,
/// host-exited-before-ready translation, restart decisions) separated from
/// the actor loop so it is unit-testable without processes. The port-handshake
/// check is called by `WorkerStartup.await` on every WORKER_PORT= line the
/// worker prints; the rest of the actor lives in the daemon (SessionManager).
module Supervisor =

  /// The host's ready line prefix on stdout (unchanged from the worker era).
  let hostReadyLine = "WORKER_PORT="

  /// Why a host stdout line was refused as the ready signal.
  [<RequireQualifiedAccess>]
  type HostReadyRejection =
    /// The line does not start with the ready prefix.
    | NotAReadyLine of prefix: string * line: string
    /// The line is a ready line, but the URL it carries is not a loopback address.
    | NotLoopback of url: string

  /// The one place a rejection becomes text, so a new case cannot be left without a message.
  let describeHostReadyRejection (rejection: HostReadyRejection) : string =
    match rejection with
    | HostReadyRejection.NotAReadyLine (prefix, line) ->
      sprintf "host stdout line is not a ready signal (missing '%s'): %s" prefix line
    | HostReadyRejection.NotLoopback url ->
      sprintf "host ready URL must be loopback, got: %s" url

  /// Classify a host stdout line as the ready signal. Fail-closed: only a
  /// loopback URL is acceptable; anything else (or a host that never printed
  /// the line) is an Error naming why.
  let classifyHostReadyLine (prefix: string) (line: string) : Result<string, HostReadyRejection> =
    match line.StartsWith(prefix, StringComparison.Ordinal) with
    | false -> Result.Error(HostReadyRejection.NotAReadyLine (prefix, line))
    | true ->
      let url = line.Substring prefix.Length
      let isLoopback =
        Uri.TryCreate(url, UriKind.Absolute)
        |> function
          | false, _ -> false
          | true, uri ->
            uri.IsLoopback
      match isLoopback with
      | true -> Result.Ok url
      | false -> Result.Error(HostReadyRejection.NotLoopback url)

  /// `classifyHostReadyLine` with the reason as text, for callers that only report it.
  let validateHostReadyLine (prefix: string) (line: string) : Result<string, string> =
    classifyHostReadyLine prefix line |> Result.mapError describeHostReadyRejection

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
