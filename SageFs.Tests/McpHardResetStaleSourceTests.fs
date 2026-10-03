module SageFs.Tests.McpHardResetStaleSourceTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.Tests.SourceStateFixtures

/// WHY this file exists: `hard_reset_fsi_session` with rebuild=false respawns the
/// worker WITHOUT running a build. The replacement worker re-runs
/// `ActorCreation`, whose first real step is
/// `ShadowCopy.shadowCopySolution` (SageFs/Core/ActorCreation.fs:123-124) — it
/// COPIES the project's existing build output (bin/Debug/net11.0/*.dll) into a
/// fresh shadow dir and loads THAT. No `dotnet build` runs on that path, so a
/// source edit made since the last build is invisible: the new worker serves the
/// OLD bytes. Respawning replaces the process but not the build.
///
/// So a respawn-only restart cannot serve current code. What these tests settle
/// is what the TOOL does about it, and the answer they pin is: it must never
/// report a plain success while the session serves code older than the file on
/// disk. It reads the session's real `SourceState` — the same machinery the
/// session list and the stale warning already use — and either takes the honest
/// rebuild path or refuses in words.
///
/// The cost this buys: a respawn-only reset stays a respawn when the build
/// already matches the sources (the common "clear my REPL" case), and only pays
/// for a build when a respawn would demonstrably serve stale code.

/// A session record whose project is `p`'s, so the probe has a real project
/// file to inspect and a real build output to compare against.
let private sessionInfo (id: string) (p: SourceStateFixtures.Project) : WorkerProtocol.SessionInfo =
  { Id = WorkerProtocol.SessionId.validate id |> Result.defaultValue (WorkerProtocol.SessionId.newId ())
    Name = None
    Projects = [ p.ProjectFile ]
    WorkingDirectory = p.Dir
    SolutionRoot = None
    Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 4242; Port = Some 1 }
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    CreatedAt = SourceStateFixtures.t0
    LastActivity = SourceStateFixtures.t0
    ActiveProject = None
    ProjectRoles = [ SourceStateFixtures.classified p ]
    App = AppRun.AppRunState.NotRunning
    Rebuild = LastRebuild.NeverRebuilt
    Reload = SessionReload.NoReloadYet
    Freshness = SageFs.ReplFreshness.InSync }

/// Fake session ops that record what the tool asked the owner for, standing in
/// for the SessionManager that owns the registry.
type private Probe = {
  Ctx: McpContext
  Restarts: ResizeArray<SageFs.RestartPlan>
}

/// `GetWarmupContext` is the real production edge `warmupOf` (SageFs/Mcp.fs:1429)
/// reads: it is where the worker's own report of WHEN it loaded its build comes
/// from, which is what makes staleness decidable here at all.
let private mkProbe (sessionId: string) (p: SourceStateFixtures.Project) (warmup: WarmupContext option) : Probe =
  let restarts = ResizeArray<SageFs.RestartPlan>()
  let info = sessionInfo sessionId p
  let ops : SessionManagementOps =
    { SessionManagementOps.stub with
        GetSessionInfo = fun _ -> Task.FromResult (Some info)
        RestartSession = fun _ plan ->
          restarts.Add plan
          Task.FromResult (Result.Ok "Hard reset accepted — replacement worker spawning.") }
  let sessionMap = Collections.Concurrent.ConcurrentDictionary<string, string>()
  sessionMap.["agent1"] <- sessionId
  let ctx : McpContext =
    { FrictionStore = None
      DiagnosticsChanged = Event<Features.DiagnosticsStore.T>().Publish
      StateChanged = None; SessionOps = ops; SessionMap = sessionMap; McpPort = 0
      Dispatch = None
      GetElmModel = None; GetElmRegions = None
      GetWarmupContext = Some (fun _ -> Task.FromResult warmup)
      GetFeatureState = None; RecordEval = None
      ActivityTracker = AgentActivityTracker.create (); LiveBindings = None; CohortSupport = SageFs.Features.CohortOwners.Wiring.Unwired
      GetDaemonHealth = fun () -> None
      GetProcessTelemetry = fun () -> None }
  { Ctx = ctx; Restarts = restarts }

/// The reading, with no tool involved — so a failure below is always the TOOL's
/// behaviour and never a fixture that failed to go stale.
let private probeToken (p: SourceStateFixtures.Project) (warmup: WarmupContext option) =
  SourceState.token (SageFs.SourceStateProbe.ofSessionRecord (Some (sessionInfo "probe" p)) warmup)

let private plansAsked (p: Probe) = p.Restarts |> Seq.toList

[<Tests>]
let tests = testList "MCP hard reset vs stale source" [

  // These two are `testTask` rather than a sync `test` wrapping a `Task`. Blocking on a Task
  // inside a test body starves the thread pool the other cases run on, and the repo ratchets
  // that count DOWN — so a sync wrapper here is debt the next case pays for. (Said without
  // spelling the call, because the ratchet counts comment lines too.)
  testTask "WHY — the fixture really does present a stale build after an edit, so a failing test below is the tool and not the fixture" {
    do!
      SourceStateFixtures.using (fun p ->
        task {
          SourceStateFixtures.editSource p
          probeToken p (Some (SourceStateFixtures.warmup p))
          |> Expect.equal "the source is now newer than the build the session loaded" "Stale" })
  }

  testTask "WHY — an unedited fixture is in sync, so the cheap path has a real case to stay cheap for" {
    do!
      SourceStateFixtures.using (fun p ->
        task {
          probeToken p (Some (SourceStateFixtures.warmup p))
          |> Expect.equal "nothing was edited after the build" "InSync" })
  }

  testTask "REGRESSION — hard_reset rebuild=false on a STALE session must not report a plain success, because the respawned worker serves the build from before the edit" {
    do! SourceStateFixtures.using (fun p ->
      task {
        SourceStateFixtures.editSource p
        let probe = mkProbe "aaa10001" p (Some (SourceStateFixtures.warmup p))
        let! reply = hardResetSession probe.Ctx "agent1" false (Some "aaa10001") None
        let claimedPlainSuccess =
          reply.StartsWith "⚠️ NOTE: hard reset restarts the session and clears all REPL definitions."
          && not (reply.Contains "STALE SOURCE")
        claimedPlainSuccess
        |> Expect.isFalse "a respawn serving stale code must not be reported as a clean success" })
  }

  testTask "REGRESSION — hard_reset rebuild=false on a STALE session must build rather than respawn, because a respawn alone cannot pick up the edit" {
    do! SourceStateFixtures.using (fun p ->
      task {
        SourceStateFixtures.editSource p
        let probe = mkProbe "aaa10002" p (Some (SourceStateFixtures.warmup p))
        let! _ = hardResetSession probe.Ctx "agent1" false (Some "aaa10002") None
        plansAsked probe
        |> Expect.equal "the plan that can actually serve the edit is the one that builds" [ SageFs.RestartPlan.Rebuild SageFs.GranularRestart.RestartSubject.Worker ] })
  }

  testTask "REGRESSION — the Result-returning sibling reaches the same verdict as the text one, so neither surface reports a stale respawn as clean" {
    do! SourceStateFixtures.using (fun p ->
      task {
        SourceStateFixtures.editSource p
        let probe = mkProbe "aaa10003" p (Some (SourceStateFixtures.warmup p))
        let! result = hardResetSessionResult probe.Ctx "agent1" false (Some "aaa10003") None
        match result with
        | Ok _ ->
          plansAsked probe
          |> Expect.equal "a bare Ok must still have built, never respawned onto stale code" [ SageFs.RestartPlan.Rebuild SageFs.GranularRestart.RestartSubject.Worker ]
        | Error _ -> () })
  }

  testTask "REGRESSION — hard_reset rebuild=false on an UP-TO-DATE session still respawns, because the cheap path is still correct there" {
    do! SourceStateFixtures.using (fun p ->
      task {
        let probe = mkProbe "aaa10004" p (Some (SourceStateFixtures.warmup p))
        let! _ = hardResetSession probe.Ctx "agent1" false (Some "aaa10004") None
        plansAsked probe
        |> Expect.equal "no build is needed when the build already matches the sources" [ SageFs.RestartPlan.RespawnOnly ] })
  }

  testTask "REGRESSION — a session with no BUILD is respawned, because there are no build bytes to be behind" {
    do! SourceStateFixtures.using (fun p ->
      task {
        // This used to demand `Rebuild Worker`, on the reasoning that "could not tell" is never
        // "in sync". That conflated two different silences. `LoadTimeNotReported` says the worker
        // never said WHEN it loaded its build — and `SourceState.Unknown`'s own doc says of this
        // family that the session "loaded no project, so there is no build to be behind". Nothing
        // was found out of date, so charging a caller who said "do not rebuild" for a build was
        // being done on evidence that does not exist.
        //
        // The distinction the rule now draws: `Stale` is a POSITIVE finding that the build is out
        // of date, and only it (plus `Rebuilding`, and the Unknown arms that describe a build
        // nobody could check) buys a build. The arms that describe NO build respawn.
        let probe = mkProbe "aaa10005" p None
        let! _ = hardResetSession probe.Ctx "agent1" false (Some "aaa10005") None
        plansAsked probe
        |> Expect.equal "a session with no build to be behind is respawned, not charged for a build" [ SageFs.RestartPlan.RespawnOnly ]
        let! reply = hardResetSession probe.Ctx "agent1" false (Some "aaa10005") None
        reply
        |> Expect.stringContains "and says nothing false about stale code" "hard reset restarts the session"
      })
  }

  testTask "REGRESSION — rebuild=true is unchanged by any of this, because a rebuild already builds" {
    do! SourceStateFixtures.using (fun p ->
      task {
        SourceStateFixtures.editSource p
        let probe = mkProbe "aaa10006" p (Some (SourceStateFixtures.warmup p))
        let! _ = hardResetSession probe.Ctx "agent1" true (Some "aaa10006") None
        plansAsked probe
        |> Expect.equal "rebuild=true builds whatever the sources say" [ SageFs.RestartPlan.Rebuild SageFs.GranularRestart.RestartSubject.Worker ] })
  }
]