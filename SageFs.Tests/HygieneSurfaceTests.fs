/// Where workspace hygiene meets the people and agents who use it: the plan as text, the nudge in replies, the
/// owner ledger, and the two MCP tools run against a real temp repository.
module SageFs.Tests.HygieneSurfaceTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.WorkspaceHygiene
open SageFs.WorkspaceHygieneRender
open SageFs.HygieneGather
open SageFs.Tests.HygieneFixtures
open SageFs.Tests.HygieneSandbox

let private summaryWith (worktrees: int) (gateBytes: int64) : Summary =
  { LeftoverWorktrees = worktrees
    LeftoverWorktreeBytes = 3L * 1024L * 1024L * 1024L
    GateBytes = gateBytes
    SafeCount = 4
    SafeBytes = 2L * 1024L * 1024L * 1024L
    ReviewCount = 1
    ReviewBytes = 0L
    PlanId = PlanId "abc" }

[<Tests>]
let renderTests =
  testList "Workspace hygiene: what it says" [

    testCase "sizes read as people read them" <| fun _ ->
      formatBytes 0L |> Expect.equal "zero" "0 B"
      formatBytes 1536L |> Expect.equal "kilobytes" "2 KB"
      formatBytes (5L * 1024L * 1024L) |> Expect.equal "megabytes" "5 MB"
      formatBytes (3L * 1024L * 1024L * 1024L) |> Expect.equal "gigabytes" "3.0 GB"

    testCase "the reply line stays silent while the workspace is tidy enough" <| fun _ ->
      nudge (summaryWith Thresholds.leftoverWorktreeNudge 0L) |> Expect.isNone "at the threshold is not over it"

    testCase "past the worktree threshold the line says how many, how big, how much is safe and what to call" <| fun _ ->
      match nudge (summaryWith (Thresholds.leftoverWorktreeNudge + 1) 0L) with
      | Some line ->
        line |> Expect.stringContains "says workspace" "workspace:"
        line |> Expect.stringContains "counts the worktrees" (sprintf "%d leftover worktrees" (Thresholds.leftoverWorktreeNudge + 1))
        line |> Expect.stringContains "names the size" "3.0 GB"
        line |> Expect.stringContains "names what is safe" "4 safe to reclaim"
        line |> Expect.stringContains "names the tool" "get_workspace_hygiene"
      | None -> failtest "expected a line"

    testCase "a gate dir past its threshold earns the line too, even with few worktrees" <| fun _ ->
      match nudge (summaryWith 0 (Thresholds.gateDirNudgeBytes + 1L)) with
      | Some line -> line |> Expect.stringContains "names the gate dir" "the gate dir holds"
      | None -> failtest "expected a line"

    testCase "the plan text separates what is safe from what needs a look, shows the saving command for the latter, and ends with the plan id" <| fun _ ->
      let safe = classify now (worktree "agent-1")
      let review = classify now (worktree "agent-2" |> unmergedWith [])
      let plan = Planner.plan [ safe; review ]
      let text = renderPlan [ safe; review ] plan
      text |> Expect.stringContains "says it is a dry run" "dry run"
      text |> Expect.stringContains "has the safe section" "Safe to reclaim"
      text |> Expect.stringContains "has the review section" "Has commits the base branch lacks"
      text |> Expect.stringContains "shows how to keep the commits" "to keep it: git -C"
      let (PlanId id) = plan.Id
      text |> Expect.stringContains "ends with the plan id to confirm" (sprintf "plan=%s" id)
      text |> Expect.stringContains "says what tidy never touches" "never touched by tidy"

    testCase "a long group lists the biggest few and says how many more" <| fun _ ->
      let many = [ for i in 1 .. Limits.itemsPerGroup + 3 -> classify now { worktree (sprintf "agent-%d" i) with SizeBytes = int64 i * 1000L } ]
      let text = renderPlan many (Planner.plan many)
      text |> Expect.stringContains "says how many more" "...and 3 more"

    testCase "the report counts what ran, what was already gone and what was skipped, and says why" <| fun _ ->
      let leftover = classify now (worktree "agent-1")
      let step = (Planner.plan [ leftover ]).Steps.Head
      let report =
        { Executed = [ { Step = step; Result = StepResult.Skipped(SkipReason.StandingChanged(Standing.DirtyReal(NonEmpty.ofHeadTail (realFile "a.fs") []))) } ]
          ReclaimedBytes = 0L }
      let text = renderReport report
      text |> Expect.stringContains "counts the skip" "1 skipped"
      text |> Expect.stringContains "says why" "changed since the plan"
  ]

[<Tests>]
let ledgerTests =
  testList "Workspace hygiene: who made each session" [

    testCase "a recorded session comes back with its agent, connection and directory, and a re-record replaces it" <| fun _ ->
      let dir = Directory.CreateTempSubdirectory("sagefs-hygiene-ledger-").FullName
      try
        let entry id agent connection : HygieneService.OwnerRecord =
          { SessionId = id; WorkingDirectory = "/w/" + id; AgentName = agent; ConnectionId = connection; CreatedAt = DateTime.UtcNow }
        HygieneService.OwnerLedger.record dir (entry "s1" "claude" "conn-1")
        HygieneService.OwnerLedger.record dir (entry "s2" "codex" "conn-2")
        HygieneService.OwnerLedger.record dir (entry "s1" "claude" "conn-9")
        let records = HygieneService.OwnerLedger.read dir
        records |> List.map (fun r -> r.SessionId) |> Expect.equal "one record per session" [ "s2"; "s1" ]
        (records |> List.find (fun r -> r.SessionId = "s1")).ConnectionId |> Expect.equal "the newest wins" "conn-9"
      finally Directory.Delete(dir, true)

    testCase "the connection is the identity: two connections with one name stay two owners" <| fun _ ->
      let records : HygieneService.OwnerRecord list =
        [ { SessionId = "a"; WorkingDirectory = "/w/a"; AgentName = "claude"; ConnectionId = "conn-1"; CreatedAt = DateTime.UtcNow }
          { SessionId = "b"; WorkingDirectory = "/w/b"; AgentName = "claude"; ConnectionId = "conn-2"; CreatedAt = DateTime.UtcNow } ]
      let owners = HygieneService.OwnerLedger.toLive records (fun connection -> connection = "conn-1")
      owners
      |> Expect.equal "same name, different connection, different presence"
        [ "/w/a", Owner.CreatedByAgent("claude", "conn-1"), AgentConnection.StillConnected
          "/w/b", Owner.CreatedByAgent("claude", "conn-2"), AgentConnection.NotConnected ]

    testCase "the ledger keeps only the newest records, so it cannot grow without end" <| fun _ ->
      let dir = Directory.CreateTempSubdirectory("sagefs-hygiene-ledger-").FullName
      try
        let limit = 7
        for i in 1 .. limit + 5 do
          HygieneService.OwnerLedger.recordKeeping limit dir { SessionId = string i; WorkingDirectory = "/w"; AgentName = "a"; ConnectionId = "c"; CreatedAt = DateTime.UtcNow }
        let records = HygieneService.OwnerLedger.read dir
        records |> List.length |> Expect.equal "capped" limit
        (List.last records).SessionId |> Expect.equal "the newest is kept" (string (limit + 5))
        (List.head records).SessionId |> Expect.equal "the oldest went" "6"
      finally Directory.Delete(dir, true)
  ]

let private context : McpContext =
  { FrictionStore = None
    DiagnosticsChanged = Event<Features.DiagnosticsStore.T>().Publish
    StateChanged = None
    SessionOps = SessionManagementOps.stub
    SessionMap = Collections.Concurrent.ConcurrentDictionary<string, string>()
    McpPort = 0
    Dispatch = Some ignore
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None
    RecordEval = None
    ActivityTracker = AgentActivityTracker.create ()
    LiveBindings = None
    CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
    GetDaemonHealth = fun () -> None
    GetProcessTelemetry = fun () -> None }

let private planIdIn (text: string) : string =
  let marker = "plan="
  let i = text.LastIndexOf marker
  text.Substring(i + marker.Length).Split([| ' '; '.'; '\n'; '\r' |]).[0]

[<Tests>]
let toolTests =
  testList "Workspace hygiene: the MCP tools on a real repository" [

    testTask "get_workspace_hygiene is a dry run: it lists what is safe and what needs a look, and touches nothing" {
      use sb = new Sandbox()
      let paths = populate sb
      let! text = McpHygiene.getWorkspaceHygieneAt (fun _ -> sb.Locations) context sb.Repo
      text |> Expect.stringContains "is a dry run" "dry run"
      text |> Expect.stringContains "lists the safe section" "Safe to reclaim"
      text |> Expect.stringContains "lists uncommitted work" "uncommitted work"
      text |> Expect.stringContains "has a plan id to confirm with" "plan="
      for path in paths |> Map.toList |> List.map snd do
        Directory.Exists path |> Expect.isTrue "nothing was removed"
    }

    testTask "tidy_workspace without confirm, or without a plan id, does nothing and says so" {
      use sb = new Sandbox()
      let paths = populate sb
      let! noConfirm = McpHygiene.tidyWorkspaceAt (fun _ -> sb.Locations) context sb.Repo "whatever" false
      noConfirm |> Expect.stringContains "refuses" "Nothing was touched"
      let! noPlan = McpHygiene.tidyWorkspaceAt (fun _ -> sb.Locations) context sb.Repo "" true
      noPlan |> Expect.stringContains "refuses" "Nothing was touched"
      for path in paths |> Map.toList |> List.map snd do
        Directory.Exists path |> Expect.isTrue "nothing was removed"
    }

    testTask "tidy_workspace with a plan id that is not the current plan is refused and touches nothing" {
      use sb = new Sandbox()
      let paths = populate sb
      let! text = McpHygiene.tidyWorkspaceAt (fun _ -> sb.Locations) context sb.Repo "000000000000" true
      text |> Expect.stringContains "says the plan changed" "not the plan that exists now"
      for path in paths |> Map.toList |> List.map snd do
        Directory.Exists path |> Expect.isTrue "nothing was removed"
    }

    testTask "tidy_workspace with the id it was shown removes the safe part and keeps the rest" {
      use sb = new Sandbox()
      let paths = populate sb
      let! shown = McpHygiene.getWorkspaceHygieneAt (fun _ -> sb.Locations) context sb.Repo
      let! report = McpHygiene.tidyWorkspaceAt (fun _ -> sb.Locations) context sb.Repo (planIdIn shown) true
      report |> Expect.stringContains "reports what it did" "Tidied:"
      for name in [ "clean-merged"; "ff-merged"; "squashed"; "dirty-generated" ] do
        Directory.Exists paths.[name] |> Expect.isFalse (sprintf "%s is gone" name)
      for name in [ "unmerged"; "dirty-real" ] do
        Directory.Exists paths.[name] |> Expect.isTrue (sprintf "%s is still there" name)
    }

    testTask "a reply gets the hygiene line once the cached snapshot says the repo is buried, and not before" {
      use sb = new Sandbox()
      let reply = "Session created."
      McpHygiene.withHygieneLine context sb.Repo reply |> Expect.equal "no snapshot yet: nothing added" reply
      let buried = { summaryWith (Thresholds.leftoverWorktreeNudge + 2) 0L with PlanId = PlanId "x" }
      HygieneService.Cache.put
        { Repo = sb.Repo
          TakenAt = DateTime.UtcNow
          Leftovers = []
          Plan = Planner.plan []
          Summary = buried }
      let withLine = McpHygiene.withHygieneLine context sb.Repo reply
      withLine |> Expect.stringStarts "keeps the reply as it was" reply
      withLine |> Expect.stringContains "adds the line" "call get_workspace_hygiene"
    }

    testTask "a status reply gets a workspace field once the repo is buried, keeps its other fields, and is left alone otherwise" {
      use sb = new Sandbox()
      let json = """{"scope":"Session","state":"Ready"}"""
      let! (untouched: string) = McpHygiene.withHygieneForSession context "" sb.Repo json
      untouched |> Expect.equal "no snapshot: the reply is exactly as it was" json
      HygieneService.Cache.put
        { Repo = sb.Repo
          TakenAt = DateTime.UtcNow
          Leftovers = []
          Plan = Planner.plan []
          Summary = summaryWith (Thresholds.leftoverWorktreeNudge + 3) 0L }
      let! (buried: string) = McpHygiene.withHygieneForSession context "" sb.Repo json
      let doc = System.Text.Json.JsonDocument.Parse buried
      doc.RootElement.GetProperty("state").GetString() |> Expect.equal "the other fields are kept" "Ready"
      doc.RootElement.GetProperty("workspace").GetString() |> Expect.stringContains "the line is there" "call get_workspace_hygiene"
      doc.Dispose()
    }

    testTask "a context that is not a running daemon never scans the machine for a reply" {
      let quiet = { context with Dispatch = None }
      McpHygiene.withHygieneLine quiet "/anywhere" "reply" |> Expect.equal "unchanged" "reply"
    }
  ]
