module SageFs.Tests.ConnectionTrackerTests

open Expecto
open SageFs

[<Tests>]
let tests = testList "ConnectionTracker" [
  testCase "register and count browsers" (fun () ->
    let tracker = ConnectionTracker()
    tracker.Register("b1", Browser, "session-1")
    tracker.Register("b2", Browser, "session-1")
    let counts = tracker.GetCounts("session-1")
    Expect.equal counts.Browsers 2 "two browsers"
    Expect.equal counts.McpAgents 0 "no mcp")

  testCase "register different kinds" (fun () ->
    let tracker = ConnectionTracker()
    tracker.Register("b1", Browser, "session-1")
    tracker.Register("m1", McpAgent, "session-1")
    tracker.Register("t1", Terminal, "session-1")
    let counts = tracker.GetCounts("session-1")
    Expect.equal counts.Browsers 1 "one browser"
    Expect.equal counts.McpAgents 1 "one mcp"
    Expect.equal counts.Terminals 1 "one terminal")

  testCase "counts per session" (fun () ->
    let tracker = ConnectionTracker()
    tracker.Register("b1", Browser, "session-1")
    tracker.Register("b2", Browser, "session-2")
    let c1 = tracker.GetCounts("session-1")
    let c2 = tracker.GetCounts("session-2")
    Expect.equal c1.Browsers 1 "session-1 has 1 browser"
    Expect.equal c2.Browsers 1 "session-2 has 1 browser")

  testCase "unregister removes client" (fun () ->
    let tracker = ConnectionTracker()
    tracker.Register("b1", Browser, "session-1")
    Expect.equal tracker.TotalCount 1 "one client"
    tracker.Unregister("b1")
    Expect.equal tracker.TotalCount 0 "zero after unregister"
    let counts = tracker.GetCounts("session-1")
    Expect.equal counts.Browsers 0 "no browsers")

  testCase "getAll returns all clients" (fun () ->
    let tracker = ConnectionTracker()
    tracker.Register("b1", Browser, "session-1")
    tracker.Register("m1", McpAgent, "session-2")
    let all = tracker.GetAll()
    Expect.equal all.Length 2 "two total")

  testCase "empty tracker returns zeros" (fun () ->
    let tracker = ConnectionTracker()
    let counts = tracker.GetCounts("nonexistent")
    Expect.equal counts.Browsers 0 "zero browsers"
    Expect.equal counts.McpAgents 0 "zero mcp"
    Expect.equal counts.Terminals 0 "zero terminals")
]
