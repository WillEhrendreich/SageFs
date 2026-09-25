namespace SageFs

open System
open System.IO
open System.Threading.Tasks
open SageFs.Server
open SageFs.Utils
open SageFs.McpTools
open SageFs.McpSessionRouting

// ── Integration ref/worktree (item 14c) ─────────────────────────────────────
//
// `Cohort.CohortState.IntegrationHead` (the git sha) is the only piece of this
// daemon's cohort-integration configuration that lives in the replayable
// ledger. The worktree path, branch, and daemon-owned session id are process-
// local handles and intentionally do not.
module McpCohortIntegration =

  [<RequireQualifiedAccess>]
  type IntegrationSession =
    | Started of sessionId: string
    | Failed of reason: string
    | Pending

  type CohortIntegrationBinding = {
    WorktreePath: string
    Branch: string
    Session: IntegrationSession
  }

  /// The one process-global integration binding consumed by the landing
  /// performer and the MCP status/setup tools.
  let cohortIntegrationRef : CohortIntegrationBinding option ref = ref None

  let private integrationWorktreePath () =
    Path.Combine(DaemonState.SageFsDir, "cohort-integration")

  let private discoverProjects (dir: string) : string list =
    let walked =
      try
        let result = SafeDirectoryWalk.walkFiles dir McpAdapter.isProjectFile McpAdapter.isNoiseProjectPath SafeDirectoryWalk.Bounds.standard
        if result.Truncated then
          Log.warn "[discoverProjects] walk in %s hit its depth/entry bound — some projects may be missing" dir
        result.Files
        |> List.map (fun p -> Path.GetRelativePath(dir, p))
      with _ -> []
    let declared =
      try
        Directory.EnumerateFiles(dir, "*.slnx")
        |> Seq.tryHead
        |> Option.map (File.ReadAllText >> Features.CohortIntegrationScope.parseSlnxProjectPaths)
        |> Option.defaultValue []
      with _ -> []
    Features.CohortIntegrationScope.selectIntegrationProjects declared walked

  let setIntegrationRef (ctx: McpContext) (agentName: string) (integrationRef: string) : Task<Result<string, SageFsError>> =
    task {
      let! callerSessionWorkingDirectory = task {
        match activeSessionId ctx agentName with
        | "" -> return None
        | sid ->
          let! infoOpt = ctx.SessionOps.GetSessionInfo (toSessionId sid)
          return infoOpt |> Option.map (fun info -> info.WorkingDirectory)
      }
      let rootSource = Features.CohortIntegrationScope.chooseMainRepoRootSource callerSessionWorkingDirectory Environment.CurrentDirectory
      let candidateDir = Features.CohortIntegrationScope.candidateDirectory rootSource
      let mainRepoDir =
        match Checkout.root (Checkout.classify candidateDir) with
        | Some root -> root
        | None -> candidateDir
      let! shaResult = Features.CohortGit.revParse mainRepoDir integrationRef
      match shaResult with
      | Error reason ->
        return Error (SageFsError.SessionCreationFailed (sprintf "could not resolve '%s' to a commit in %s: %s" integrationRef mainRepoDir reason))
      | Ok sha ->
        let worktreePath = integrationWorktreePath ()
        let! _removed =
          match Directory.Exists worktreePath with
          | true -> Features.CohortGit.removeWorktree mainRepoDir worktreePath
          | false -> async { return Ok () }
        let branch = sprintf "sagefs/cohort-%s" (sha.Substring(0, min 8 sha.Length))
        let! addResult = Features.CohortGit.addWorktree mainRepoDir worktreePath branch sha
        match addResult with
        | Error reason ->
          return Error (SageFsError.SessionCreationFailed (sprintf "could not create the integration worktree at %s on branch %s: %s" worktreePath branch reason))
        | Ok () ->
          cohortIntegrationRef.Value <- Some { WorktreePath = worktreePath; Branch = branch; Session = IntegrationSession.Pending }
          let who = memberIdFor agentName
          let! commitResult = commitCohort ctx (Cohort.CohortCommand.SetIntegrationHead(who, sha))
          match commitResult with
          | Error e -> return Error e
          | Ok _ ->
            let projects = discoverProjects worktreePath
            let! buildResult = SessionBuild.runBuildAsync projects worktreePath
            let buildOutcome =
              buildResult
              |> Result.mapError SageFsError.describeForAgent
              |> Features.CohortIntegrationScope.worktreeBuildOutcome
            match buildOutcome with
            | Features.CohortIntegrationScope.WorktreeBuildOutcome.BuildFailed reason ->
              cohortIntegrationRef.Value <- Some { WorktreePath = worktreePath; Branch = branch; Session = IntegrationSession.Failed reason }
              Log.warn "[set_integration_ref] git side configured (head=%s worktree=%s branch=%s) but the worktree build failed before session creation: %s" sha worktreePath branch reason
              return Ok (
                sprintf
                  "Integration configured: head=%s worktree=%s branch=%s. WARNING: the worktree build failed (%s) — landings will report this reason until it is retried."
                  sha worktreePath branch reason)
            | Features.CohortIntegrationScope.WorktreeBuildOutcome.ReadyForSession ->
              let targets = SessionProjectTarget.tryCreateMany projects |> Result.defaultValue []
              let! sessionResult = ctx.SessionOps.CreateSession targets worktreePath WorkflowTypes.SessionWorkflow.Interactive
              match sessionResult with
              | Ok sessionId ->
                cohortIntegrationRef.Value <- Some { WorktreePath = worktreePath; Branch = branch; Session = IntegrationSession.Started sessionId }
                return Ok (
                  sprintf
                    "Integration configured: head=%s worktree=%s branch=%s session=%s"
                    sha worktreePath branch sessionId)
              | Error sessionErr ->
                let reason = SageFsError.describeForAgent sessionErr
                cohortIntegrationRef.Value <- Some { WorktreePath = worktreePath; Branch = branch; Session = IntegrationSession.Failed reason }
                Log.warn "[set_integration_ref] git side configured (head=%s worktree=%s branch=%s) but the integration session failed to start: %s" sha worktreePath branch reason
                return Ok (
                  sprintf
                    "Integration configured: head=%s worktree=%s branch=%s. WARNING: the integration session failed to start (%s) — landings will report this reason until it is retried."
                    sha worktreePath branch reason)
    }

  let getCohortStatus (ctx: McpContext) : Task<Result<string, SageFsError>> =
    task {
      match requireCohortOwner ctx with
      | Error e -> return Error e
      | Ok owner ->
        let integration =
          match cohortIntegrationRef.Value with
          | None -> "Integration session: (not configured — call set_integration_ref)"
          | Some b ->
            match b.Session with
            | IntegrationSession.Started sid -> sprintf "Integration session: %s (started)" sid
            | IntegrationSession.Failed reason -> sprintf "Integration session: FAILED to start — %s" reason
            | IntegrationSession.Pending -> "Integration session: pending"
        return Ok (renderCohortFrame (owner.ReadFrame()) + integration + "\n")
    }
