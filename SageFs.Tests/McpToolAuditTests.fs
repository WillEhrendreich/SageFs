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

  test "topTools returns most-used first" {
    let snap =
      AuditSnapshot.empty ()
      |> AuditSnapshot.record "rarely_used" 1.0 Success
      |> AuditSnapshot.record "most_used" 1.0 Success
      |> AuditSnapshot.record "most_used" 1.0 Success
      |> AuditSnapshot.record "most_used" 1.0 Success
      |> AuditSnapshot.record "medium_used" 1.0 Success
      |> AuditSnapshot.record "medium_used" 1.0 Success
    let top = AuditSnapshot.topTools snap
    top.[0].ToolName |> Expect.equal "first" "most_used"
    top.[1].ToolName |> Expect.equal "second" "medium_used"
    top.[2].ToolName |> Expect.equal "third" "rarely_used"
  }

  test "unusedTools finds tools with no calls" {
    let allTools = [ "tool_a"; "tool_b"; "tool_c" ]
    let snap =
      AuditSnapshot.empty ()
      |> AuditSnapshot.record "tool_a" 1.0 Success
    let unused = AuditSnapshot.unusedTools allTools snap
    unused |> Expect.equal "unused" [ "tool_b"; "tool_c" ]
  }

  test "unusedTools with empty snapshot returns all" {
    let allTools = [ "tool_a"; "tool_b" ]
    let unused = AuditSnapshot.unusedTools allTools (AuditSnapshot.empty ())
    unused |> Expect.equal "all unused" [ "tool_a"; "tool_b" ]
  }

  test "problematicTools finds tools with >5% affordance violations" {
    let snap =
      AuditSnapshot.empty ()
      |> AuditSnapshot.record "good_tool" 1.0 Success
      |> AuditSnapshot.record "good_tool" 1.0 Success
      |> AuditSnapshot.record "bad_tool" 1.0 Success
      |> AuditSnapshot.record "bad_tool" 1.0 AffordanceViolation
    let problematic = AuditSnapshot.problematicTools snap
    problematic.Length |> Expect.equal "count" 1
    problematic.[0].ToolName |> Expect.equal "name" "bad_tool"
  }

  test "problematicTools ignores tools at exactly 5%" {
    // 1 violation out of 20 calls = 5% exactly, should NOT be flagged (> not >=)
    let snap =
      List.init 19 (fun _ -> ("tool", 1.0, Success))
      |> List.append [ ("tool", 1.0, AffordanceViolation) ]
      |> List.fold (fun s (name, dur, out) -> AuditSnapshot.record name dur out s) (AuditSnapshot.empty ())
    let problematic = AuditSnapshot.problematicTools snap
    problematic.Length |> Expect.equal "none flagged at 5%" 0
  }
]

// ── AuditSummary tests ──

let auditSummaryTests = testList "AuditSummary" [
  test "summarize empty snapshot" {
    let allTools = [ "a"; "b"; "c" ]
    let summary = AuditSnapshot.summarize allTools (AuditSnapshot.empty ())
    summary.TotalCalls |> Expect.equal "total" 0
    summary.UniqueToolsUsed |> Expect.equal "unique" 0
    summary.UnusedTools |> Expect.equal "unused" [ "a"; "b"; "c" ]
    summary.AverageDurationMs |> Expect.equal "avg" 0.0
  }

  test "summarize with data" {
    let allTools = [ "tool_a"; "tool_b"; "tool_c" ]
    let snap =
      AuditSnapshot.empty ()
      |> AuditSnapshot.record "tool_a" 10.0 Success
      |> AuditSnapshot.record "tool_a" 20.0 Success
      |> AuditSnapshot.record "tool_b" 30.0 Failure
    let summary = AuditSnapshot.summarize allTools snap
    summary.TotalCalls |> Expect.equal "total" 3
    summary.UniqueToolsUsed |> Expect.equal "unique" 2
    summary.UnusedTools |> Expect.equal "unused" [ "tool_c" ]
    summary.TopToolsByUsage |> List.head |> fst |> Expect.equal "top tool" "tool_a"
    summary.ToolsWithHighFailRate.Length |> Expect.equal "high fail" 1
  }

  test "summarize caps top tools at 10" {
    let allTools = List.init 15 (fun i -> sprintf "tool_%02d" i)
    let snap =
      allTools
      |> List.fold (fun s name -> AuditSnapshot.record name 1.0 Success s) (AuditSnapshot.empty ())
    let summary = AuditSnapshot.summarize allTools snap
    summary.TopToolsByUsage.Length |> Expect.equal "capped at 10" 10
  }

  test "summarize tracks affordance violations" {
    let allTools = [ "tool_a" ]
    let snap =
      AuditSnapshot.empty ()
      |> AuditSnapshot.record "tool_a" 1.0 AffordanceViolation
    let summary = AuditSnapshot.summarize allTools snap
    summary.ToolsWithAffordanceViolations
    |> Expect.equal "violations" [ ("tool_a", 1) ]
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
  auditSummaryTests
  auditTrackerTests
]
