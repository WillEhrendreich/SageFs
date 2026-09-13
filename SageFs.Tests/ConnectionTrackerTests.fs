module SageFs.Tests.ConnectionTrackerTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs

[<Tests>]
let tests = testList "ConnectionTracker" [
  testCase "register and count browsers" (fun () ->
    let tracker = ConnectionTracker()
    tracker.Register("b1", Browser, "session-1")
    tracker.Register("b2", Browser, "session-1")
    let counts = tracker.GetCounts("session-1")
    counts.Browsers |> Expect.equal "two browsers" 2
    counts.McpAgents |> Expect.equal "no mcp" 0)

  testCase "register different kinds" (fun () ->
    let tracker = ConnectionTracker()
    tracker.Register("b1", Browser, "session-1")
    tracker.Register("m1", McpAgent, "session-1")
    tracker.Register("t1", Terminal, "session-1")
    let counts = tracker.GetCounts("session-1")
    counts.Browsers |> Expect.equal "one browser" 1
    counts.McpAgents |> Expect.equal "one mcp" 1
    counts.Terminals |> Expect.equal "one terminal" 1)

  testCase "counts per session" (fun () ->
    let tracker = ConnectionTracker()
    tracker.Register("b1", Browser, "session-1")
    tracker.Register("b2", Browser, "session-2")
    let c1 = tracker.GetCounts("session-1")
    let c2 = tracker.GetCounts("session-2")
    c1.Browsers |> Expect.equal "session-1 has 1 browser" 1
    c2.Browsers |> Expect.equal "session-2 has 1 browser" 1)

  testCase "unregister removes client" (fun () ->
    let tracker = ConnectionTracker()
    tracker.Register("b1", Browser, "session-1")
    tracker.TotalCount |> Expect.equal "one client" 1
    tracker.Unregister("b1")
    tracker.TotalCount |> Expect.equal "zero after unregister" 0
    let counts = tracker.GetCounts("session-1")
    counts.Browsers |> Expect.equal "no browsers" 0)

  testCase "getAll returns all clients" (fun () ->
    let tracker = ConnectionTracker()
    tracker.Register("b1", Browser, "session-1")
    tracker.Register("m1", McpAgent, "session-2")
    let all = tracker.GetAll()
    all.Length |> Expect.equal "two total" 2)

  testCase "empty tracker returns zeros" (fun () ->
    let tracker = ConnectionTracker()
    let counts = tracker.GetCounts("nonexistent")
    counts |> Expect.equal "empty = zero" ConnectionCounts.zero)

  testCase "getAllCounts sums all sessions" (fun () ->
    let tracker = ConnectionTracker()
    tracker.Register("b1", Browser, "session-1")
    tracker.Register("m1", McpAgent, "session-2")
    tracker.Register("t1", Terminal, "session-1")
    let counts = tracker.GetAllCounts()
    counts.Browsers |> Expect.equal "one browser" 1
    counts.McpAgents |> Expect.equal "one mcp" 1
    counts.Terminals |> Expect.equal "one terminal" 1)

  testCase "ConnectionCounts.zero is identity" (fun () ->
    let zero = ConnectionCounts.zero
    zero.Browsers |> Expect.equal "zero browsers" 0
    zero.McpAgents |> Expect.equal "zero mcp" 0
    zero.Terminals |> Expect.equal "zero terminals" 0)

  testCase "ConnectionCounts.ofClients matches manual count" (fun () ->
    let clients = [
      { Id = "b1"; Kind = Browser; SessionId = Some "s1"; ConnectedAt = System.DateTime.UtcNow }
      { Id = "b2"; Kind = Browser; SessionId = Some "s1"; ConnectedAt = System.DateTime.UtcNow }
      { Id = "m1"; Kind = McpAgent; SessionId = Some "s1"; ConnectedAt = System.DateTime.UtcNow }
    ]
    let counts = ConnectionCounts.ofClients clients
    counts |> Expect.equal "matches" { Browsers = 2; McpAgents = 1; Terminals = 0 })
]
