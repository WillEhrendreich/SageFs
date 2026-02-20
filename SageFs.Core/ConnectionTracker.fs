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
    let bySession = clients.Values |> Seq.filter (fun c -> c.SessionId = Some sessionId)
    let browsers = bySession |> Seq.filter (fun c -> c.Kind = Browser) |> Seq.length
    let mcpAgents = bySession |> Seq.filter (fun c -> c.Kind = McpAgent) |> Seq.length
    let terminals = bySession |> Seq.filter (fun c -> c.Kind = Terminal) |> Seq.length
    {| Browsers = browsers; McpAgents = mcpAgents; Terminals = terminals |}

  member _.GetAllCounts() =
    let all = clients.Values
    let browsers = all |> Seq.filter (fun c -> c.Kind = Browser) |> Seq.length
    let mcpAgents = all |> Seq.filter (fun c -> c.Kind = McpAgent) |> Seq.length
    let terminals = all |> Seq.filter (fun c -> c.Kind = Terminal) |> Seq.length
    {| Browsers = browsers; McpAgents = mcpAgents; Terminals = terminals |}

  member _.TotalCount = clients.Count

  member _.GetAll() = clients.Values |> Seq.toList
