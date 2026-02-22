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

  member _.GetCounts(sessionId: string) =
    let mutable browsers = 0
    let mutable mcpAgents = 0
    let mutable terminals = 0
    for kv in clients do
      let c = kv.Value
      if c.SessionId = Some sessionId then
        match c.Kind with
        | Browser -> browsers <- browsers + 1
        | McpAgent -> mcpAgents <- mcpAgents + 1
        | Terminal -> terminals <- terminals + 1
    {| Browsers = browsers; McpAgents = mcpAgents; Terminals = terminals |}

  member _.GetAllCounts() =
    let mutable browsers = 0
    let mutable mcpAgents = 0
    let mutable terminals = 0
    for kv in clients do
      match kv.Value.Kind with
      | Browser -> browsers <- browsers + 1
      | McpAgent -> mcpAgents <- mcpAgents + 1
      | Terminal -> terminals <- terminals + 1
    {| Browsers = browsers; McpAgents = mcpAgents; Terminals = terminals |}

  member _.TotalCount = clients.Count

  member _.GetAll() = clients.Values |> Seq.toList
