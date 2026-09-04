namespace SageFs

open System
open System.Collections.Concurrent

type ClientKind = Browser | McpAgent | Terminal

type ConnectedClient = {
  Id: string
  Kind: ClientKind
  SessionId: string option
  ConnectedAt: DateTime
}

/// Immutable count of connected clients by kind.
type ConnectionCounts = {
  Browsers: int
  McpAgents: int
  Terminals: int
}

module ConnectionCounts =
  let zero = { Browsers = 0; McpAgents = 0; Terminals = 0 }

  let ofClients (clients: ConnectedClient seq) =
    clients
    |> Seq.fold (fun acc c ->
      match c.Kind with
      | Browser -> { acc with Browsers = acc.Browsers + 1 }
      | McpAgent -> { acc with McpAgents = acc.McpAgents + 1 }
      | Terminal -> { acc with Terminals = acc.Terminals + 1 }
    ) zero

/// Thread-safe tracker for connected UI clients across sessions.
type ConnectionTracker() =
  let clients = ConcurrentDictionary<string, ConnectedClient>()

  member _.Register(clientId: string, kind: ClientKind, ?sessionId: string) =
    let client = {
      Id = clientId
      Kind = kind
      SessionId = sessionId
      ConnectedAt = DateTime.UtcNow
    }
    clients.[clientId] <- client

  member _.Unregister(clientId: string) =
    clients.TryRemove(clientId) |> ignore

  member _.GetBySession(sessionId: string) =
    clients.Values
    |> Seq.filter (fun c -> c.SessionId = Some sessionId)
    |> Seq.toList

  /// Snapshot-then-count: takes a point-in-time copy for consistent iteration.
  member _.GetCounts(sessionId: string) : ConnectionCounts =
    clients.Values
    |> Seq.toArray
    |> Array.filter (fun c -> c.SessionId = Some sessionId)
    |> ConnectionCounts.ofClients

  member _.GetAllCounts() : ConnectionCounts =
    clients.Values
    |> Seq.toArray
    |> ConnectionCounts.ofClients

  member _.TotalCount = clients.Count

  member _.GetAll() = clients.Values |> Seq.toList
