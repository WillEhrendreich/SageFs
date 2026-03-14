module SageFs.Tests.MultiAgentCoordinationTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.SessionOperations
open SageFs.WorkerProtocol
open SageFs.Tests.SharedGenerators

// ── Helpers ──────────────────────────────────────────────────────

let mkOccupancy (name: string) (role: OccupantRole) : SessionOccupancy = {
  AgentName = name
  Role = role
}

let mkPresence
  (name: string)
  (sessionId: string)
  (files: string list)
  (lastCall: DateTime)
  : AgentPresence = {
  AgentName = name
  Role = OccupantRole.classify name
  SessionId = sessionId
  LastToolCall = lastCall
  Intent = None
  RecentFiles = files
  EvalCount = 1
}

let now = DateTime(2026, 3, 14, 22, 0, 0, DateTimeKind.Utc)
let fiveMinTimeout = TimeSpan.FromMinutes 5.0

// ── SessionGuidance behavior ─────────────────────────────────────

let sessionGuidanceTests = testList "SessionGuidance — session tells agents what to do" [

  testCase "Uncontested when no workers present" <| fun _ ->
    SessionGuidance.compute [] SessionStatus.Ready
    |> Expect.equal "should be Uncontested" SessionGuidance.Uncontested

  testCase "OccupiedBy lists worker names when workers present" <| fun _ ->
    let occupants = [mkOccupancy "claude" OccupantRole.Worker]
    SessionGuidance.compute occupants SessionStatus.Ready
    |> Expect.equal "should list claude" (SessionGuidance.OccupiedBy ["claude"])

  testCase "OccupiedBy ignores observers" <| fun _ ->
    let occupants = [
      mkOccupancy "tui" OccupantRole.Observer
      mkOccupancy "agent-1" OccupantRole.Worker
    ]
    SessionGuidance.compute occupants SessionStatus.Ready
    |> Expect.equal "should only list agent-1" (SessionGuidance.OccupiedBy ["agent-1"])

  testCase "Unhealthy when session is Faulted regardless of occupancy" <| fun _ ->
    let occupants = [mkOccupancy "claude" OccupantRole.Worker]
    SessionGuidance.compute occupants SessionStatus.Faulted
    |> Expect.equal "should be Unhealthy"
      (SessionGuidance.Unhealthy "Session is faulted")

  testCase "Unhealthy when session is Stopped" <| fun _ ->
    SessionGuidance.compute [] SessionStatus.Stopped
    |> Expect.equal "should be Unhealthy"
      (SessionGuidance.Unhealthy "Session is stopped")

  testCase "Multiple workers are all listed" <| fun _ ->
    let occupants = [
      mkOccupancy "claude" OccupantRole.Worker
      mkOccupancy "agent-1" OccupantRole.Worker
      mkOccupancy "copilot" OccupantRole.Worker
    ]
    SessionGuidance.compute occupants SessionStatus.Ready
    |> Expect.equal "should list all workers"
      (SessionGuidance.OccupiedBy ["claude"; "agent-1"; "copilot"])

  testCase "Label for Uncontested" <| fun _ ->
    SessionGuidance.Uncontested
    |> SessionGuidance.label
    |> Expect.equal "should be readable" "Uncontested"

  testCase "Label for OccupiedBy" <| fun _ ->
    SessionGuidance.OccupiedBy ["claude"; "agent-1"]
    |> SessionGuidance.label
    |> Expect.equal "should list agents" "OccupiedBy: claude, agent-1"

  testCase "Label for Unhealthy" <| fun _ ->
    SessionGuidance.Unhealthy "Session is faulted"
    |> SessionGuidance.label
    |> Expect.equal "should include reason" "Unhealthy: Session is faulted"
]

// ── SessionGuidance property tests ───────────────────────────────

let genOccupantRole =
  Gen.elements [OccupantRole.Worker; OccupantRole.Observer]

let genOccupancy =
  gen {
    let! name = Gen.elements ["claude"; "copilot"; "agent-1"; "tui"; "dashboard"; "mcp-client"]
    let role = OccupantRole.classify name
    return mkOccupancy name role
  }

let genSessionStatus =
  Gen.elements [
    SessionStatus.Ready
    SessionStatus.Faulted
    SessionStatus.Stopped
    SessionStatus.Starting
    SessionStatus.Evaluating
    SessionStatus.Restarting
  ]

let sessionGuidancePropertyTests = testList "SessionGuidance properties" [

  testPropertyWithConfig propConfig
    "guidance is deterministic — same inputs always produce same output"
    <| fun () ->
      let gen =
        gen {
          let! occupants = Gen.listOf genOccupancy
          let! status = genSessionStatus
          return occupants, status
        }
      Prop.forAll (Arb.fromGen gen) (fun (occupants, status) ->
        SessionGuidance.compute occupants status
          = SessionGuidance.compute occupants status)

  testPropertyWithConfig propConfig
    "OccupiedBy never contains observer names"
    <| fun () ->
      let gen =
        gen {
          let! occupants = Gen.listOf genOccupancy
          let! status = genSessionStatus
          return occupants, status
        }
      Prop.forAll (Arb.fromGen gen) (fun (occupants, status) ->
        match SessionGuidance.compute occupants status with
        | SessionGuidance.OccupiedBy names ->
          names |> List.forall (fun n ->
            OccupantRole.classify n = OccupantRole.Worker)
        | _ -> true)

  testPropertyWithConfig propConfig
    "Uncontested is impossible when workers are present"
    <| fun () ->
      let genWithWorker =
        gen {
          let! observers = Gen.listOf (gen { return mkOccupancy "tui" OccupantRole.Observer })
          let! workers = Gen.nonEmptyListOf (gen {
            let! name = Gen.elements ["claude"; "mcp-agent"; "agent-1"]
            return mkOccupancy name OccupantRole.Worker
          })
          return observers @ workers
        }
      let gen =
        gen {
          let! occupants = genWithWorker
          return occupants
        }
      Prop.forAll (Arb.fromGen gen) (fun occupants ->
        SessionGuidance.compute occupants SessionStatus.Ready
          <> SessionGuidance.Uncontested)
]

// ── AgentPresence freshness tests ────────────────────────────────

let agentPresenceTests = testList "AgentPresence — freshness classification" [

  testCase "Agent within timeout is Fresh" <| fun _ ->
    let presence = mkPresence "claude" "sess-1" [] (now - TimeSpan.FromMinutes 2.0)
    AgentPresence.freshness now fiveMinTimeout presence
    |> Expect.equal "should be Fresh" AgentFreshness.Fresh

  testCase "Agent beyond timeout is Stale" <| fun _ ->
    let presence = mkPresence "claude" "sess-1" [] (now - TimeSpan.FromMinutes 10.0)
    AgentPresence.freshness now fiveMinTimeout presence
    |> Expect.equal "should be Stale" AgentFreshness.Stale

  testCase "Agent exactly at timeout is Fresh" <| fun _ ->
    let presence = mkPresence "claude" "sess-1" [] (now - fiveMinTimeout)
    AgentPresence.freshness now fiveMinTimeout presence
    |> Expect.equal "at boundary should be Fresh" AgentFreshness.Fresh

  testCase "isStale and isFresh are mutually exclusive" <| fun _ ->
    let presence = mkPresence "claude" "sess-1" [] (now - TimeSpan.FromMinutes 3.0)
    let stale = AgentPresence.isStale now fiveMinTimeout presence
    let fresh = AgentPresence.isFresh now fiveMinTimeout presence
    (stale <> fresh)
    |> Expect.isTrue "stale and fresh must be mutually exclusive"
]

let agentPresencePropertyTests = testList "AgentPresence freshness properties" [

  testPropertyWithConfig propConfig
    "Fresh and Stale are always mutually exclusive"
    <| fun () ->
      let gen =
        gen {
          let! minutesAgo = Gen.choose (0, 30) |> Gen.map float
          let presence = mkPresence "claude" "sess-1" [] (now - TimeSpan.FromMinutes minutesAgo)
          return presence
        }
      Prop.forAll (Arb.fromGen gen) (fun presence ->
        AgentPresence.isStale now fiveMinTimeout presence
          <> AgentPresence.isFresh now fiveMinTimeout presence)

  testPropertyWithConfig propConfig
    "Agent at or within timeout is always Fresh"
    <| fun () ->
      let gen =
        gen {
          let! minutesAgo = Gen.choose (0, 4) |> Gen.map float
          let presence = mkPresence "claude" "sess-1" [] (now - TimeSpan.FromMinutes minutesAgo)
          return presence
        }
      Prop.forAll (Arb.fromGen gen) (fun presence ->
        AgentPresence.isFresh now fiveMinTimeout presence)
]

// ── FileOverlapAdvisory tests ────────────────────────────────────

let fileOverlapTests = testList "FileOverlapAdvisory — file conflict detection" [

  testCase "No overlap when agents touch different files" <| fun _ ->
    let others = [mkPresence "claude" "sess-1" ["B.fs"] now]
    FileOverlapAdvisory.compute "agent-1" ["A.fs"] others
    |> Expect.isEmpty "should have no advisories"

  testCase "Overlap when agents touch the same file" <| fun _ ->
    let others = [mkPresence "claude" "sess-1" ["A.fs"; "B.fs"] now]
    FileOverlapAdvisory.compute "agent-1" ["A.fs"] others
    |> Expect.equal "should detect A.fs overlap"
      [FileOverlapAdvisory.OverlappingFiles ("claude", ["A.fs"])]

  testCase "Self-overlap is excluded" <| fun _ ->
    let others = [mkPresence "claude" "sess-1" ["A.fs"] now]
    FileOverlapAdvisory.compute "claude" ["A.fs"] others
    |> Expect.isEmpty "should not warn about own files"

  testCase "Multiple overlapping agents generate separate advisories" <| fun _ ->
    let others = [
      mkPresence "claude" "sess-1" ["A.fs"] now
      mkPresence "copilot" "sess-1" ["A.fs"; "C.fs"] now
    ]
    let advisories = FileOverlapAdvisory.compute "agent-1" ["A.fs"] others
    advisories
    |> Expect.hasLength "should have 2 advisories" 2

  testCase "Empty current files produces no advisories" <| fun _ ->
    let others = [mkPresence "claude" "sess-1" ["A.fs"] now]
    FileOverlapAdvisory.compute "agent-1" [] others
    |> Expect.isEmpty "no files means no overlap"

  testCase "Format for NoOverlap is empty string" <| fun _ ->
    FileOverlapAdvisory.NoOverlap
    |> FileOverlapAdvisory.format
    |> Expect.equal "should be empty" ""

  testCase "Format for OverlappingFiles includes agent name and files" <| fun _ ->
    FileOverlapAdvisory.OverlappingFiles ("claude", ["A.fs"; "B.fs"])
    |> FileOverlapAdvisory.format
    |> Expect.stringContains "should mention claude" "claude"
]

let fileOverlapPropertyTests = testList "FileOverlapAdvisory properties" [

  testPropertyWithConfig propConfig
    "Overlap is symmetric — if A overlaps B, B overlaps A"
    <| fun () ->
      let genFiles = Gen.nonEmptyListOf (Gen.elements ["A.fs"; "B.fs"; "C.fs"; "D.fs"])
      let gen =
        gen {
          let! filesA = genFiles
          let! filesB = genFiles
          return filesA, filesB
        }
      Prop.forAll (Arb.fromGen gen) (fun (filesA, filesB) ->
        let intersectAB = (Set.intersect (Set.ofList filesA) (Set.ofList filesB)).Count > 0
        let intersectBA = (Set.intersect (Set.ofList filesB) (Set.ofList filesA)).Count > 0
        intersectAB = intersectBA)

  testPropertyWithConfig propConfig
    "Advisory never mentions the evaluating agent itself"
    <| fun () ->
      let genAgent = Gen.elements ["claude"; "copilot"; "agent-1"; "agent-2"]
      let genFiles = Gen.nonEmptyListOf (Gen.elements ["A.fs"; "B.fs"; "C.fs"])
      let gen =
        gen {
          let! currentAgent = genAgent
          let! currentFiles = genFiles
          let! otherAgents = Gen.listOfLength 3 genAgent
          let! otherFiles = Gen.listOfLength 3 genFiles
          let others =
            List.zip otherAgents otherFiles
            |> List.map (fun (name, files) -> mkPresence name "sess-1" files now)
          return currentAgent, currentFiles, others
        }
      Prop.forAll (Arb.fromGen gen) (fun (currentAgent, currentFiles, others) ->
        let advisories = FileOverlapAdvisory.compute currentAgent currentFiles others
        advisories |> List.forall (fun adv ->
          match adv with
          | FileOverlapAdvisory.OverlappingFiles (agent, _) -> agent <> currentAgent
          | FileOverlapAdvisory.NoOverlap -> true))
]

// ── OccupancyCleanupOutcome tests ────────────────────────────────

let cleanupOutcomeTests = testList "OccupancyCleanupOutcome — cleanup result modeling" [

  testCase "NothingToClean is distinct from EvictedStale []" <| fun _ ->
    OccupancyCleanupOutcome.NothingToClean
    |> Expect.notEqual "should differ from empty eviction"
      (OccupancyCleanupOutcome.EvictedStale [])

  testCase "CleanupSkipped carries a reason" <| fun _ ->
    match OccupancyCleanupOutcome.CleanupSkipped "during startup" with
    | OccupancyCleanupOutcome.CleanupSkipped reason ->
      reason |> Expect.equal "should have reason" "during startup"
    | other -> failwithf "unexpected: %A" other
]

// ── All tests ─────────────────────────────────────────────────────

[<Tests>]
let allTests = testList "Multi-agent coordination domain types" [
  sessionGuidanceTests
  sessionGuidancePropertyTests
  agentPresenceTests
  agentPresencePropertyTests
  fileOverlapTests
  fileOverlapPropertyTests
  cleanupOutcomeTests
]
