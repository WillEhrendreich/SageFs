namespace SageFs

open System
open SageFs.WorkerProtocol

/// Whether a worker that says it is ready has handed us a transport we can
/// actually talk to. Pure, so it stands apart from the SessionManager mailbox
/// that decides what to do about a bad one.
module ReadyTransport =

  /// Which kind of transport reported ready. The closed set names the one kind that reports today.
  [<RequireQualifiedAccess>]
  type TransportKind = Worker

  module TransportKind =
    let label (kind: TransportKind) : string =
      match kind with
      | TransportKind.Worker -> "Worker"

  let private hasValidProxy (proxy: SessionProxy) =
    not (isNull (box proxy))

  let isValid (baseUrl: string) (proxy: SessionProxy) =
    not (String.IsNullOrWhiteSpace baseUrl)
    && hasValidProxy proxy

  let describeInvalid (transportKind: TransportKind) (baseUrl: string) (proxy: SessionProxy) =
    let kind = TransportKind.label transportKind
    match String.IsNullOrWhiteSpace baseUrl, hasValidProxy proxy with
    | true, false -> sprintf "%s reported ready without a valid base URL or proxy" kind
    | true, true -> sprintf "%s reported ready without a valid base URL" kind
    | false, false -> sprintf "%s reported ready without a valid proxy" kind
    | false, true -> sprintf "%s reported ready with a valid transport" kind
