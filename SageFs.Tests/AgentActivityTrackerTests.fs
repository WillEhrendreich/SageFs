module SageFs.Tests.AgentActivityTrackerTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.SessionOperations
open SageFs.Tests.SharedGenerators

// ── Helpers ──────────────────────────────────────────────────────

let now = DateTime(2026, 3, 14, 22, 0, 0, DateTimeKind.Utc)
let fiveMinTimeout = TimeSpan.FromMinutes 5.0
let minutes (n: float) = TimeSpan.FromMinutes n

// ── Recording and presence tests ─────────────────────────────────

let recordingTests = testList "AgentActivityTracker — recording creates presence" [

  testCase "Recording a tool call for a new agent creates a presence entry" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" (Some "Main.fs") None now
    AgentActivityTracker.getPresence tracker "claude"
    |> Option.isSome
    |> Expect.isTrue "should have presence"

  testCase "Presence includes the file path from the tool call" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" (Some "Main.fs") None now
    let p = (AgentActivityTracker.getPresence tracker "claude").Value
    p.RecentFiles
    |> List.contains "Main.fs"
    |> Expect.isTrue "should contain Main.fs"

  testCase "Multiple tool calls accumulate recent files" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    let t1 = now
    let t2 = now + TimeSpan.FromSeconds 10.0
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" (Some "A.fs") None t1
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" (Some "B.fs") None t2
    let p = (AgentActivityTracker.getPresence tracker "claude").Value
    p.RecentFiles |> Set.ofList |> Set.contains "A.fs"
    |> Expect.isTrue "should contain A.fs"
    p.RecentFiles |> Set.ofList |> Set.contains "B.fs"
    |> Expect.isTrue "should contain B.fs"

  testCase "Tool call without file path still updates LastToolCall" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None now
    let p = (AgentActivityTracker.getPresence tracker "claude").Value
    p.LastToolCall |> Expect.equal "should be now" now

  testCase "Duplicate file paths are deduplicated" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" (Some "A.fs") None now
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" (Some "A.fs") None now
    let p = (AgentActivityTracker.getPresence tracker "claude").Value
    p.RecentFiles
    |> List.filter (fun f -> f = "A.fs")
    |> List.length
    |> Expect.equal "should appear once" 1

  testCase "EvalCount increments on each tool call" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None now
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None now
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None now
    let p = (AgentActivityTracker.getPresence tracker "claude").Value
    p.EvalCount |> Expect.equal "should be 3" 3

  testCase "Unknown agent returns None" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.getPresence tracker "ghost"
    |> Expect.isNone "should be None for unknown agent"
]

// ── Intent tracking tests ────────────────────────────────────────

let intentTests = testList "AgentActivityTracker — intent tracking" [

  testCase "Intent is stored when provided" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None (Some "refactoring warmup") now
    let p = (AgentActivityTracker.getPresence tracker "claude").Value
    p.Intent |> Expect.equal "should have intent" (Some "refactoring warmup")

  testCase "Intent is preserved when None is provided on subsequent call" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None (Some "writing tests") now
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None now
    let p = (AgentActivityTracker.getPresence tracker "claude").Value
    p.Intent |> Expect.equal "should preserve previous intent" (Some "writing tests")

  testCase "Intent is overwritten by each new non-None value" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None (Some "A") now
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None (Some "B") now
    let p = (AgentActivityTracker.getPresence tracker "claude").Value
    p.Intent |> Expect.equal "should be B" (Some "B")
]

// ── getAllPresences tests ────────────────────────────────────────

let getAllPresencesTests = testList "AgentActivityTracker — getAllPresences" [

  testCase "Returns all agents when no session filter" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None now
    AgentActivityTracker.recordToolCall tracker "copilot" "sess-2" None None now
    let all = AgentActivityTracker.getAllPresences tracker None
    all |> Expect.hasLength "should have 2 agents" 2

  testCase "Filters by session when sessionId provided" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None now
    AgentActivityTracker.recordToolCall tracker "copilot" "sess-2" None None now
    let filtered = AgentActivityTracker.getAllPresences tracker (Some "sess-1")
    filtered |> Expect.hasLength "should have 1 agent" 1
    filtered.[0].AgentName |> Expect.equal "should be claude" "claude"

  testCase "getActivePresences excludes stale agents" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None (now - minutes 2.0)
    AgentActivityTracker.recordToolCall tracker "stale-agent" "sess-1" None None (now - minutes 10.0)
    let active = AgentActivityTracker.getActivePresences tracker (Some "sess-1") fiveMinTimeout now
    active |> Expect.hasLength "should exclude stale agent" 1
    active.[0].AgentName |> Expect.equal "should be claude" "claude"
]

// ── Cleanup tests ────────────────────────────────────────────────

let cleanupTests = testList "AgentActivityTracker — occupancy cleanup" [

  testCase "Fresh agents survive cleanup" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None (now - minutes 2.0)
    let outcome = AgentActivityTracker.cleanup tracker fiveMinTimeout now
    outcome |> Expect.equal "should be NothingToClean" OccupancyCleanupOutcome.NothingToClean
    AgentActivityTracker.getPresence tracker "claude"
    |> Option.isSome
    |> Expect.isTrue "claude should still be present"

  testCase "Stale agents are evicted by cleanup" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None (now - minutes 10.0)
    let outcome = AgentActivityTracker.cleanup tracker fiveMinTimeout now
    match outcome with
    | OccupancyCleanupOutcome.EvictedStale names ->
      names |> Expect.contains "should contain claude" "claude"
    | other -> failwithf "expected EvictedStale, got %A" other
    AgentActivityTracker.getPresence tracker "claude"
    |> Expect.isNone "claude should be gone"

  testCase "Cleanup returns EvictedStale with all evicted names" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None (now - minutes 10.0)
    AgentActivityTracker.recordToolCall tracker "copilot" "sess-1" None None (now - minutes 10.0)
    AgentActivityTracker.recordToolCall tracker "agent-1" "sess-1" None None (now - minutes 1.0)
    let outcome = AgentActivityTracker.cleanup tracker fiveMinTimeout now
    match outcome with
    | OccupancyCleanupOutcome.EvictedStale names ->
      names |> Set.ofList |> Set.count
      |> Expect.equal "should have evicted 2" 2
    | other -> failwithf "expected EvictedStale, got %A" other
    AgentActivityTracker.getPresence tracker "agent-1"
    |> Option.isSome
    |> Expect.isTrue "agent-1 should survive"

  testCase "Cleanup with all fresh agents returns NothingToClean" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None (now - minutes 1.0)
    AgentActivityTracker.recordToolCall tracker "copilot" "sess-1" None None (now - minutes 2.0)
    let outcome = AgentActivityTracker.cleanup tracker fiveMinTimeout now
    outcome |> Expect.equal "should be NothingToClean" OccupancyCleanupOutcome.NothingToClean
]

// ── Property tests ───────────────────────────────────────────────

let propertyTests = testList "AgentActivityTracker properties" [

  testPropertyWithConfig propConfig
    "EvalCount is monotonically increasing per agent"
    <| fun () ->
      let gen =
        gen {
          let! callCount = Gen.choose (1, 20)
          return callCount
        }
      Prop.forAll (Arb.fromGen gen) (fun callCount ->
        let tracker = AgentActivityTracker.create()
        let mutable prevCount = 0
        let mutable monotonic = true
        for i in 1..callCount do
          let t = now + TimeSpan.FromSeconds (float i)
          AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None t
          let currentCount = (AgentActivityTracker.getPresence tracker "claude").Value.EvalCount
          match currentCount >= prevCount with
          | true -> prevCount <- currentCount
          | false -> monotonic <- false
        monotonic)

  testPropertyWithConfig propConfig
    "RecentFiles is bounded to MaxRecentFiles"
    <| fun () ->
      let gen =
        gen {
          let! fileCount = Gen.choose (1, 100)
          return fileCount
        }
      Prop.forAll (Arb.fromGen gen) (fun fileCount ->
        let tracker = AgentActivityTracker.create()
        for i in 1..fileCount do
          let t = now + TimeSpan.FromSeconds (float i)
          let file = sprintf "File%d.fs" i
          AgentActivityTracker.recordToolCall tracker "claude" "sess-1" (Some file) None t
        let p = (AgentActivityTracker.getPresence tracker "claude").Value
        p.RecentFiles.Length <= AgentActivityTracker.MaxRecentFiles)

  testPropertyWithConfig propConfig
    "Cleanup never evicts agents within the timeout window"
    <| fun () ->
      let gen =
        gen {
          let! minutesAgo = Gen.choose (0, 4) |> Gen.map float
          return minutesAgo
        }
      Prop.forAll (Arb.fromGen gen) (fun minutesAgo ->
        let tracker = AgentActivityTracker.create()
        let callTime = now - TimeSpan.FromMinutes minutesAgo
        AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None callTime
        let outcome = AgentActivityTracker.cleanup tracker fiveMinTimeout now
        match outcome with
        | OccupancyCleanupOutcome.NothingToClean -> true
        | _ -> false)

  testPropertyWithConfig propConfig
    "Cleanup always evicts agents beyond the timeout window"
    <| fun () ->
      let gen =
        gen {
          let! minutesBeyond = Gen.choose (1, 60) |> Gen.map float
          return minutesBeyond
        }
      Prop.forAll (Arb.fromGen gen) (fun minutesBeyond ->
        let tracker = AgentActivityTracker.create()
        let callTime = now - fiveMinTimeout - TimeSpan.FromMinutes minutesBeyond
        AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None callTime
        let outcome = AgentActivityTracker.cleanup tracker fiveMinTimeout now
        match outcome with
        | OccupancyCleanupOutcome.EvictedStale _ -> true
        | _ -> false)
]

// ── recordMemberActivity: role from the bound MemberId, not a ─────
// ── re-parsed string (sagefs-multiagent-vision.md §4.1 / §10 ──────
// ── Phase 0 item 4: "OccupantRole.classify by name prefix ─────────
// ── deleted" from the bound-connection path). ─────────────────────

let memberActivityTests = testList "AgentActivityTracker — recordMemberActivity (MemberId-bound)" [

  testCase "WHY — an Mcp-bound member is Worker regardless of what string is inside the connection id" <| fun _ ->
    // Naively re-classifying the connection id's own text ("observer-42" does
    // not start with "mcp"/"agent-") would wrongly derive Observer. Role must
    // come from the MemberId CASE (SageFs.MemberTable.MemberId.Mcp), never
    // from string-sniffing whatever happens to be inside it.
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordMemberActivity tracker (MemberTable.MemberId.Mcp "observer-42") "sess-1" None None now
    let p = (AgentActivityTracker.getPresence tracker (MemberTable.MemberId.display (MemberTable.MemberId.Mcp "observer-42"))).Value
    p.Role |> Expect.equal "Mcp connections are always Worker" OccupantRole.Worker

  testCase "WHY — a Browser-bound member is Observer regardless of what string is inside the client id" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordMemberActivity tracker (MemberTable.MemberId.Browser "mcp-lookalike") "sess-1" None None now
    let p = (AgentActivityTracker.getPresence tracker (MemberTable.MemberId.display (MemberTable.MemberId.Browser "mcp-lookalike"))).Value
    p.Role |> Expect.equal "Browser tabs are always Observer, even if the client id looks like an mcp name" OccupantRole.Observer

  testCase "WHY — a Minted (unbound) member still falls back to the name-prefix heuristic" <| fun _ ->
    // The heuristic survives ONLY for genuinely unbound callers (no real
    // connection to classify by) — this is unchanged, documented behavior.
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordMemberActivity tracker (MemberTable.MemberId.Minted "mcp-claude") "sess-1" None None now
    let p = (AgentActivityTracker.getPresence tracker "mcp-claude").Value
    p.Role |> Expect.equal "Minted \"mcp-claude\" still classifies as Worker via the prefix heuristic" OccupantRole.Worker

  testCase "recordMemberActivity keys the presence table identically to MemberId.display" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordMemberActivity tracker (MemberTable.MemberId.Mcp "conn-A") "sess-1" (Some "Main.fs") None now
    AgentActivityTracker.getPresence tracker "mcp:conn-A"
    |> Option.isSome
    |> Expect.isTrue "keyed by MemberId.display, same routing key space as recordToolCall/SessionMap"

  testCase "recordMemberActivity and recordToolCall accumulate into the SAME underlying table for a Minted id" <| fun _ ->
    // recordToolCall(agentName) is behavior-preserved as recordMemberActivity(Minted agentName) —
    // both entry points collapse onto one shared implementation/table.
    let tracker = AgentActivityTracker.create()
    AgentActivityTracker.recordToolCall tracker "claude" "sess-1" (Some "A.fs") None now
    AgentActivityTracker.recordMemberActivity tracker (MemberTable.MemberId.Minted "claude") "sess-1" (Some "B.fs") None (now + TimeSpan.FromSeconds 1.0)
    let p = (AgentActivityTracker.getPresence tracker "claude").Value
    p.EvalCount |> Expect.equal "both calls landed on the same presence entry" 2
    p.RecentFiles |> Set.ofList
    |> Expect.equal "files from both call styles accumulate together" (Set.ofList [ "A.fs"; "B.fs" ])

  testPropertyWithConfig propConfig
    "Role is always OccupantRole.ofMemberId of the id passed in — never re-derived from the resolved key string"
    <| fun () ->
      let genMemberId =
        Gen.oneof [
          Gen.map MemberTable.MemberId.Browser (Gen.elements [ "tab-1"; "mcp"; "agent-x"; "" ])
          Gen.map MemberTable.MemberId.Mcp (Gen.elements [ "conn-A"; "observer"; "tui"; "" ])
          Gen.map MemberTable.MemberId.Minted (Gen.elements [ "claude"; "mcp-x"; "tui"; "dashboard" ])
        ]
      Prop.forAll (Arb.fromGen genMemberId) (fun id ->
        let tracker = AgentActivityTracker.create()
        AgentActivityTracker.recordMemberActivity tracker id "sess-1" None None now
        let p = (AgentActivityTracker.getPresence tracker (MemberTable.MemberId.display id)).Value
        p.Role = OccupantRole.ofMemberId id)
]

// ── Thread safety tests ──────────────────────────────────────────

let threadSafetyTests = testList "AgentActivityTracker — thread safety" [

  testCase "Concurrent calls from different agents don't lose data" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    let agentCount = 50
    let tasks =
      [| for i in 1..agentCount do
           yield
             Task.Run(fun () ->
               let name = sprintf "agent-%d" i
               let t = now + TimeSpan.FromMilliseconds (float i)
               AgentActivityTracker.recordToolCall tracker name "sess-1" (Some (sprintf "File%d.fs" i)) None t
             ) |]
    Task.WaitAll(tasks)
    let all = AgentActivityTracker.getAllPresences tracker None
    all
    |> List.length
    |> Expect.equal "should have all agents" agentCount

  testCase "Concurrent calls from the same agent are all counted" <| fun _ ->
    let tracker = AgentActivityTracker.create()
    let callCount = 100
    let tasks =
      [| for i in 1..callCount do
           yield
             Task.Run(fun () ->
               let t = now + TimeSpan.FromMilliseconds (float i)
               AgentActivityTracker.recordToolCall tracker "claude" "sess-1" None None t
             ) |]
    Task.WaitAll(tasks)
    let p = (AgentActivityTracker.getPresence tracker "claude").Value
    p.EvalCount
    |> Expect.equal "should count all calls" callCount
]

// ── All tests ─────────────────────────────────────────────────────

[<Tests>]
let allTests = testList "Agent activity tracker" [
  recordingTests
  intentTests
  getAllPresencesTests
  cleanupTests
  memberActivityTests
  propertyTests
  threadSafetyTests
]
