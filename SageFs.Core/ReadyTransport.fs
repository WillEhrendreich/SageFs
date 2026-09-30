namespace SageFs

open System
open SageFs.WorkerProtocol

/// Whether a worker that says it is ready has handed us a transport we can
/// actually talk to. Pure, so it stands apart from the SessionManager mailbox
/// that decides what to do about a bad one.
module ReadyTransport =

  let private hasValidProxy (proxy: SessionProxy) =
    not (isNull (box proxy))

  let isValid (baseUrl: string) (proxy: SessionProxy) =
    not (String.IsNullOrWhiteSpace baseUrl)
    && hasValidProxy proxy

  let describeInvalid (transportKind: string) (baseUrl: string) (proxy: SessionProxy) =
    match String.IsNullOrWhiteSpace baseUrl, hasValidProxy proxy with
    | true, false -> sprintf "%s reported ready without a valid base URL or proxy" transportKind
    | true, true -> sprintf "%s reported ready without a valid base URL" transportKind
    | false, false -> sprintf "%s reported ready without a valid proxy" transportKind
    | false, true -> sprintf "%s reported ready with a valid transport" transportKind
