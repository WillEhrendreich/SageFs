module SageFs.Tests.McpToolAuditTests

open Expecto
open Expecto.Flip
open SageFs.McpToolAudit

// ── ToolStats pure function tests ──

let toolStatsTests = testList "ToolStats" [
  test "empty stats have zero counts" {
    let s = ToolStats.empty "test_tool"
    s.CallCount |> Expect.equal "call count" 0
    s.SuccessCount |> Expect.equal "success count" 0
    s.FailureCount |> Expect.equal "failure count" 0
    s.AffordanceViolations |> Expect.equal "violations" 0
    s.TotalDurationMs |> Expect.equal "total duration" 0.0
  }

  test "recording success increments correct counters" {
    let s =
      ToolStats.empty "tool"
      |> ToolStats.record 42.0 Success
    s.CallCount |> Expect.equal "call count" 1
    s.SuccessCount |> Expect.equal "success" 1
    s.FailureCount |> Expect.equal "failure" 0
    s.TotalDurationMs |> Expect.equal "duration" 42.0
  }

  test "recording failure increments failure counter" {
    let s =
      ToolStats.empty "tool"
      |> ToolStats.record 10.0 Failure
    s.CallCount |> Expect.equal "call count" 1
    s.SuccessCount |> Expect.equal "success" 0
    s.FailureCount |> Expect.equal "failure" 1
  }

  test "recording affordance violation increments violation counter" {
    let s =
      ToolStats.empty "tool"
      |> ToolStats.record 5.0 AffordanceViolation
    s.CallCount |> Expect.equal "call count" 1
    s.AffordanceViolations |> Expect.equal "violations" 1
    s.SuccessCount |> Expect.equal "success" 0
    s.FailureCount |> Expect.equal "failure" 0
  }

  test "multiple records accumulate correctly" {
    let s =
      ToolStats.empty "tool"
      |> ToolStats.record 10.0 Success
      |> ToolStats.record 20.0 Success
      |> ToolStats.record 30.0 Failure
    s.CallCount |> Expect.equal "call count" 3
    s.SuccessCount |> Expect.equal "success" 2
    s.FailureCount |> Expect.equal "failure" 1
    s.TotalDurationMs |> Expect.equal "total" 60.0
    s.MinDurationMs |> Expect.equal "min" 10.0
    s.MaxDurationMs |> Expect.equal "max" 30.0
  }

  test "averageDurationMs computes correctly" {
    let s =
      ToolStats.empty "tool"
      |> ToolStats.record 10.0 Success
      |> ToolStats.record 30.0 Success
    ToolStats.averageDurationMs s |> Expect.equal "avg" 20.0
  }

  test "averageDurationMs of empty is zero" {
    ToolStats.averageDurationMs (ToolStats.empty "tool")
    |> Expect.equal "avg of empty" 0.0
  }

  test "successRate computes correctly" {
    let s =
      ToolStats.empty "tool"
      |> ToolStats.record 1.0 Success
      |> ToolStats.record 1.0 Success
      |> ToolStats.record 1.0 Failure
    ToolStats.successRate s
    |> Expect.floatClose "success rate" Accuracy.medium (2.0 / 3.0)
  }

  test "successRate of empty is 1.0" {
    ToolStats.successRate (ToolStats.empty "tool")
    |> Expect.equal "empty success rate" 1.0
  }
]

// ── AuditSnapshot pure function tests ──

let auditSnapshotTests = testList "AuditSnapshot" [
  test "empty snapshot has zero totals" {
    let snap = AuditSnapshot.empty ()
    snap.TotalCalls |> Expect.equal "total calls" 0
    snap.Tools |> Map.count |> Expect.equal "tools" 0
  }

  test "recording a tool call creates tool entry" {
    let snap =
      AuditSnapshot.empty ()
      |> AuditSnapshot.record "send_fsharp_code" 100.0 Success
    snap.TotalCalls |> Expect.equal "total" 1
    snap.Tools |> Map.containsKey "send_fsharp_code"
    |> Expect.isTrue "tool exists"
  }

  test "recording multiple tools tracks each separately" {
    let snap =
      AuditSnapshot.empty ()
      |> AuditSnapshot.record "send_fsharp_code" 100.0 Success
      |> AuditSnapshot.record "get_fsi_status" 5.0 Success
      |> AuditSnapshot.record "send_fsharp_code" 80.0 Success
    snap.TotalCalls |> Expect.equal "total" 3
    snap.Tools.Count |> Expect.equal "unique tools" 2
    snap.Tools.["send_fsharp_code"].CallCount |> Expect.equal "send count" 2
    snap.Tools.["get_fsi_status"].CallCount |> Expect.equal "status count" 1
  }

]

// ── AuditTracker thread-safety tests ──

let auditTrackerTests = testList "AuditTracker" [
  test "tracker records and snapshots" {
    let tracker = AuditTracker()
    tracker.Record("tool_a", 10.0, Success)
    tracker.Record("tool_b", 20.0, Failure)
    let snap = tracker.Snapshot
    snap.TotalCalls |> Expect.equal "total" 2
    snap.Tools.["tool_a"].SuccessCount |> Expect.equal "a success" 1
    snap.Tools.["tool_b"].FailureCount |> Expect.equal "b failure" 1
  }

  test "tracker reset clears all data" {
    let tracker = AuditTracker()
    tracker.Record("tool_a", 10.0, Success)
    tracker.Reset()
    let snap = tracker.Snapshot
    snap.TotalCalls |> Expect.equal "total after reset" 0
    snap.Tools.Count |> Expect.equal "tools after reset" 0
  }

  test "concurrent recording doesn't lose data" {
    let tracker = AuditTracker()
    let tasks =
      [| for i in 1..100 ->
           System.Threading.Tasks.Task.Run(fun () ->
             for _ in 1..10 do
               tracker.Record("tool", 1.0, Success)) |]
    System.Threading.Tasks.Task.WaitAll(tasks)
    let snap = tracker.Snapshot
    snap.TotalCalls |> Expect.equal "total concurrent" 1000
    snap.Tools.["tool"].CallCount |> Expect.equal "tool concurrent" 1000
  }
]

[<Tests>]
let allMcpToolAuditTests = testList "MCP Tool Audit" [
  toolStatsTests
  auditSnapshotTests
  auditTrackerTests
]
