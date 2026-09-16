namespace SageFs

open System
open System.IO

/// The failure channel of `McpTools.routeToSession` and its pure classifiers,
/// extracted from the Mcp.fs accretion hub (roast-8 §2/§14 item 2). No
/// McpContext, no IO — just a DU plus pure exception→error mapping — so it is
/// testable on its own and shrinks the MCP tool file. McpTools re-exposes it via
/// `open`, so `routeToSession` and every consumer inside Mcp.fs are unchanged.
module McpRouteError =

  type RouteError =
    | Message of string
    | TransportFailure of string
    /// Session is deliberately starting/restarting; transport unavailability is
    /// expected and must not be treated as a crash.
    | RestartInProgress of string

  let routeErrorMessage = function
    | Message msg -> msg
    | TransportFailure msg -> msg
    | RestartInProgress msg -> msg

  let routeErrorIsTransportFailure = function
    | TransportFailure _ -> true
    | Message _ -> false
    | RestartInProgress _ -> false

  /// Classify a `RouteError` (routeToSession's failure channel) into the
  /// `SageFsError` algebra: a plain `Message` means the session itself could
  /// not be routed to (not found, still warming up, invalid id — the same
  /// "not routable right now" family `SessionNotRoutable` already covers at
  /// every other resolveSessionId boundary); `TransportFailure` and
  /// `RestartInProgress` both mean the worker process could not be reached,
  /// which is exactly what `WorkerCommunicationFailed` describes.
  let routeErrorToSageFsError (sid: string) = function
    | Message msg -> SageFsError.SessionNotRoutable msg
    | TransportFailure msg -> SageFsError.WorkerCommunicationFailed (sid, msg)
    | RestartInProgress msg -> SageFsError.WorkerCommunicationFailed (sid, msg)

  let innermostException (ex: exn) =
    let rec loop (current: exn) =
      match current.InnerException with
      | null -> current
      | inner -> loop inner
    loop ex

  let tryMapTransportFailure (sessionId: string) (ex: exn) =
    let rec unwrap (error: exn) =
      match error with
      | :? AggregateException as aggregate when not (isNull aggregate.InnerException) ->
        unwrap aggregate.InnerException
      | other -> other

    let transport = unwrap ex
    let describe reason =
      SageFsError.WorkerCommunicationFailed(sessionId, sprintf "Session transport closed — %s" reason)
      |> SageFsError.describeForAgent

    match transport with
    | :? OperationCanceledException -> None
    | :? System.Net.Http.HttpRequestException as httpError ->
      let reason =
        match httpError.InnerException with
        | null when String.IsNullOrWhiteSpace httpError.Message ->
          "HTTP request failed"
        | null ->
          httpError.Message
        | inner ->
          let root = innermostException inner
          match String.IsNullOrWhiteSpace root.Message with
          | true -> httpError.Message
          | false -> root.Message
      Some (TransportFailure (describe reason))
    | :? IOException as ioError ->
      Some (TransportFailure (describe ioError.Message))
    | :? ObjectDisposedException as disposed ->
      Some (TransportFailure (describe disposed.Message))
    | _ ->
      None
