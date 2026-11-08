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
    /// The cohort this integration serves: the repository `set_integration_ref` was called for.
    Scope: SageFs.CohortScope
    /// The repository the integration worktree was cut from (its common git root). Replacing the worktree removes it from HERE:
    /// git refuses to remove one repository's worktree from another.
    Repository: string
    WorktreePath: string
    Branch: string
    /// The trunk checkout: where the cohort's landings are carried to, and the working directory of a trunk session. Detached,
    /// at the integration head, and moved to each landing's commit once the landing has landed.
    TrunkPath: string
    Session: IntegrationSession
  }

  /// The one process-global integration binding consumed by the landing
  /// performer and the MCP status/setup tools.
  let cohortIntegrationRef : CohortIntegrationBinding option ref = ref None

  /// Raised after every write of `cohortIntegrationRef` through `setBinding`: the moment a pending integration starts or fails.
  let integrationBindingChanged = Event<unit>()

  /// Write the binding and wake whoever waits for it to stop being pending.
  let setBinding (binding: CohortIntegrationBinding option) : unit =
    cohortIntegrationRef.Value <- binding
    integrationBindingChanged.Trigger ()

  /// Is the integration still being built? A landing that arrives while it is waits in the queue (finding C-pending).
  let isPending (binding: CohortIntegrationBinding option) : bool =
    match binding with
    | Some { Session = IntegrationSession.Pending } -> true
    | Some _
    | None -> false

  /// The binding once it is no longer pending: started, failed, or never configured. Parks on the change signal and does not
  /// poll, so a landing requested while `set_integration_ref` is still building the session starts the moment the session does.
  let awaitIntegrationNotPending () : Async<CohortIntegrationBinding option> =
    Features.IntegrationSettle.awaitChange integrationBindingChanged.Publish (fun () -> cohortIntegrationRef.Value) (isPending >> not)

  /// Hold `work` until the integration is no longer pending (finding C-pending). A landing's first step is the rebase, which
  /// rewrites the worktree the pending build is still working in, so the landing waits BEFORE it, in the queue, and starts
  /// when the session does.
  let whenIntegrationIsNotPending (work: Async<'a>) : Async<'a> =
    async {
      let! _binding = awaitIntegrationNotPending ()
      return! work
    }

  /// What the daemon recorded when a landing's run failed, by landing id: each failing test's full name and the file with the
  /// whole list (finding C-names). The cohort blocker holds only test ids, hashes of the names, so the names live here.
  /// Process-local like the binding; after a daemon restart the status reads such a landing by count and id and says why.
  let failingTestReports = System.Collections.Concurrent.ConcurrentDictionary<string, Features.FailingTestsText.Report>()

  let failingTestReportFor (landingId: string) : Features.FailingTestsText.Report option =
    match failingTestReports.TryGetValue landingId with
    | true, report -> Some report
    | false, _ -> None

  /// Write what a landing's run found into the integration worktree's test output and remember it for the status: the tests that
  /// did not pass with their names, why the ones that could not run did not, and the tests the gate left to the release gate.
  /// A file that cannot be written leaves the names in the status and says there is no file.
  let recordLandingReport
    (worktree: string)
    (landingId: string)
    (failures: Features.FailingTestsText.Failure list)
    (reasons: Map<string, string>)
    (notRun: Features.FailingTestsText.Failure list)
    : unit =
    let path = Features.FailingTestsText.reportPath worktree landingId
    let draft : Features.FailingTestsText.Report =
      { Failures = failures; Reasons = reasons; NotRun = notRun; File = Features.FailingTestsText.ReportFile.NotWritten }
    let file =
      try
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, Features.FailingTestsText.fullListing landingId draft)
        Features.FailingTestsText.ReportFile.Written path
      with ex when (ex :? IOException || ex :? UnauthorizedAccessException) ->
        Log.warn "[cohort-landing] could not write the report of landing %s to %s: %s" landingId path ex.Message
        Features.FailingTestsText.ReportFile.NotWritten
    failingTestReports.[landingId] <- { draft with File = file }

  /// The files evaluated into the integration session over its build, one per rebased file of a landing. They run as evaluated code,
  /// which is not instrumented, so no test's coverage names them again: a test's cache key and the affected-test selection must be
  /// told about them or an edit to one is invisible (`InputHashCoverage.trustBeyond`). Process-local like the binding. The landing
  /// queue is serial, but the cache key reads it from another thread, hence the lock.
  let private evaluatedOverBuild : Set<string> ref = ref Set.empty
  let private evaluatedOverBuildLock = obj ()

  let noteEvaluatedOverBuild (path: string) : unit =
    lock evaluatedOverBuildLock (fun () -> evaluatedOverBuild.Value <- Set.add path evaluatedOverBuild.Value)

  /// The files evaluated over the integration session's build, sorted.
  let filesEvaluatedOverBuild () : string list =
    lock evaluatedOverBuildLock (fun () -> Set.toList evaluatedOverBuild.Value)

  let forgetFilesEvaluatedOverBuild () : unit =
    lock evaluatedOverBuildLock (fun () -> evaluatedOverBuild.Value <- Set.empty)

  /// The commit the integration session's build was last REBUILT from (a landing of compiled source rebuilds the
  /// worktree at the rebased commit). Whether that commit is in the head's history is asked at verification time
  /// (`CohortLandingPlan.buildOffHead`): a refused landing's commit is not, and the session then holds a build the
  /// head does not have. Process-local like the binding; a new integration tree is a new session, so it starts empty.
  let private sessionRebuiltFor : string option ref = ref None
  let private sessionRebuiltForLock = obj ()

  let noteSessionRebuiltFor (commit: string) : unit =
    lock sessionRebuiltForLock (fun () -> sessionRebuiltFor.Value <- Some commit)

  let sessionRebuiltForCommit () : string option =
    lock sessionRebuiltForLock (fun () -> sessionRebuiltFor.Value)

  let forgetSessionRebuiltFor () : unit =
    lock sessionRebuiltForLock (fun () -> sessionRebuiltFor.Value <- None)

  /// One step at a time in the integration worktree. A landing's steps (rebase, verify, fast-forward) and the reset that
  /// puts the worktree back at the head when a landing ends without landing both move it, and two git checkouts at once
  /// would leave it on neither. The landing queue is serial; the reset is the second party.
  let integrationWorktreeGate = new System.Threading.SemaphoreSlim(1, 1)

  /// The one process-global trunk owner, which the daemon starts and the status tools read. Process-local like the binding.
  let trunkFollowRef : Features.TrunkFollowOwner.Handle option ref = ref None

  /// The integration worktree of one cohort, under the data dir. One path per cohort, so two repositories never share it.
  let integrationWorktreePathFor (dataDir: string) (scope: SageFs.CohortScope) : string =
    Path.Combine(dataDir, Features.CohortIntegrationScope.integrationWorktreeName (Scope.label scope))

  /// The trunk checkout of one cohort, by the same rule.
  let trunkCheckoutPathFor (dataDir: string) (scope: SageFs.CohortScope) : string =
    Path.Combine(dataDir, Features.CohortIntegrationScope.trunkCheckoutName (Scope.label scope))

  /// What replacing the integration worktree touches, as ports so the order of the steps can be proven against real git without a
  /// daemon: which landings are in the queue, how the verifying session is stopped, and the one git act (run in a repository).
  type ReplacePorts = {
    /// The landings in the queue of the cohort the CURRENT integration serves.
    LandingsInFront: unit -> string list
    StopSession: string -> Task<unit>
    RemoveWorktree: string -> string -> Async<Result<unit, string>>
    DirectoryExists: string -> bool
  }

  [<RequireQualifiedAccess>]
  type ReplaceOutcome =
    | Replaced
    /// A landing is in the queue: nothing was stopped or removed. The text says which and what to do.
    | Refused of text: string
    /// Git would not remove a worktree. The text names the path and what git said; the integration is left failed, never half.
    | RemovalFailed of text: string

  /// Replace the integration worktree: refuse while a landing is queued, stop the verifying session, then remove every old
  /// worktree from the repository that made it and READ what git said. The caller holds `integrationWorktreeGate`.
  let replaceIntegrationWorktree
    (ports: ReplacePorts)
    (previous: CohortIntegrationBinding option)
    (newRepository: string)
    (newWorktreePath: string)
    : Task<ReplaceOutcome> =
    task {
      match Features.CohortIntegrationScope.replacementDecision (ports.LandingsInFront()) with
      | Features.CohortIntegrationScope.Replacement.Refused landings ->
        let label =
          match previous with
          | Some binding -> Scope.label binding.Scope
          | None -> newRepository
        return ReplaceOutcome.Refused(Features.CohortIntegrationScope.replacementRefusal label landings)
      | Features.CohortIntegrationScope.Replacement.Allowed ->
        // A session still running in the worktree about to be replaced would be left with its sources deleted, and its project would
        // refuse the new session ("a session for this project already exists", F4). Stopping it is what makes calling this again, the
        // retry a faulted integration asks for, work.
        match previous with
        | Some { Session = IntegrationSession.Started sessionId } -> do! ports.StopSession sessionId
        | _ -> ()
        let old =
          previous
          |> Option.map (fun binding -> ({ Repository = binding.Repository; WorktreePath = binding.WorktreePath } : Features.CohortIntegrationScope.PreviousIntegration))
        let targets =
          Features.CohortIntegrationScope.removalTargets old newRepository newWorktreePath
          |> List.filter (fun (_, worktree) -> ports.DirectoryExists worktree)
        let failure : string option ref = ref None
        for repository, worktree in targets do
          match failure.Value with
          | Some _ -> ()
          | None ->
            match! ports.RemoveWorktree repository worktree |> Async.StartAsTask with
            | Ok () -> ()
            | Error said ->
              failure.Value <-
                Some(
                  sprintf
                    "could not remove the old integration worktree %s: git (run in %s) said: %s. Nothing was replaced. Remove it by hand with `git -C %s worktree remove --force %s` once nothing uses it, then call set_integration_ref again."
                    worktree repository said repository worktree)
        match failure.Value with
        | Some text -> return ReplaceOutcome.RemovalFailed text
        | None -> return ReplaceOutcome.Replaced
    }

  /// The landings in the queue of the cohort the CURRENT integration serves, by id.
  let private landingsInFront (ctx: McpContext) : string list =
    match cohortIntegrationRef.Value with
    | None -> []
    | Some binding ->
      match CohortOwnerResolution.ownerForScope ctx.CohortSupport binding.Scope with
      | None -> []
      | Some owner -> owner.ReadCohortState().Queue |> List.map (fun (Cohort.LandingId id) -> id)

  let private replacePortsFor (ctx: McpContext) : ReplacePorts =
    { LandingsInFront = fun () -> landingsInFront ctx
      StopSession =
        fun sessionId ->
          task {
            let! _ = ctx.SessionOps.StopSession sessionId
            return ()
          }
      RemoveWorktree = Features.CohortGit.removeWorktree
      DirectoryExists = Directory.Exists }

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
    let candidates = Features.CohortIntegrationScope.selectIntegrationProjects declared walked
    // A landing is verified by tests, so the session needs the test projects and what they test. The samples and demos a solution
    // also lists (a Rust, a Python, a Java project) can never fail a landing and cost a cold build before the first one.
    let isTestProject (relative: string) =
      try Features.CohortIntegrationScope.isTestProjectXml (File.ReadAllText(Path.Combine(dir, relative)))
      with :? IOException | :? UnauthorizedAccessException -> false
    Features.CohortIntegrationScope.narrowToTestProjects candidates isTestProject

  /// The text a faulted integration says: the cause and what to do about it, for the cohort it serves.
  let private failureText (binding: CohortIntegrationBinding) (reason: string) : string =
    Features.CohortIntegrationScope.failureAdvice reason binding.WorktreePath (Scope.label binding.Scope)

  /// Everything `set_integration_ref` does once it knows the repository and the commit: replace the worktree, cut the new one, build,
  /// start the session. The caller holds `integrationWorktreeGate` for all of it: a landing's rebase waits behind it, so nothing moves
  /// the worktree while it is being replaced or built.
  let private configureIntegration
    (ctx: McpContext)
    (agentName: string)
    (integrationRef: string)
    (projects: string option)
    (integrationScope: SageFs.CohortScope)
    (mainRepoDir: string)
    (repository: string)
    (sha: string)
    : Task<Result<string, SageFsError>> =
    ignore integrationRef
    task {
        let worktreePath = integrationWorktreePathFor DaemonState.SageFsDir integrationScope
        // The queue is empty (or the call is refused), the verifying session is stopped, and every old worktree is removed from the
        // repository that made it, with git's answer read. All of it under the gate the landing's steps take.
        let previous = cohortIntegrationRef.Value
        match! replaceIntegrationWorktree (replacePortsFor ctx) previous repository worktreePath with
        | ReplaceOutcome.Refused text -> return Error (SageFsError.SessionCreationFailed text)
        | ReplaceOutcome.RemovalFailed text ->
          // The previous session was stopped, so the binding says so instead of naming a session that is gone.
          setBinding (previous |> Option.map (fun binding -> { binding with Session = IntegrationSession.Failed text }))
          return Error (SageFsError.SessionCreationFailed text)
        | ReplaceOutcome.Replaced ->
        // A branch of an earlier call may hold landings, so it is never reset or reused: a repeated call cuts a branch of its own (F7).
        let! existingBranches = Features.CohortGit.branchNamesUnder mainRepoDir "sagefs/"
        let takenBranches = existingBranches |> Result.defaultValue Set.empty
        let branch = Features.CohortIntegrationScope.freshBranchName sha takenBranches.Contains
        let! addResult = Features.CohortGit.addWorktree mainRepoDir worktreePath branch sha
        // The trunk checkout: where landings are carried to once they land. Detached, so the integration branch stays free for the
        // landing step to move, and made fresh here for the same reason the integration worktree is.
        let trunkPath = trunkCheckoutPathFor DaemonState.SageFsDir integrationScope
        let! trunkResult =
          match addResult with
          | Error _ -> async { return Ok () }
          | Ok () ->
            // A trunk checkout that is already there is moved to the new head and kept: a session may be running an app in it, and
            // removing the directory under that session would take its build and its sources with it.
            match Directory.Exists trunkPath with
            | true -> Features.CohortGit.moveCheckoutTo trunkPath sha
            | false -> Features.CohortGit.addDetachedWorktree mainRepoDir trunkPath sha
        match addResult, trunkResult with
        | Error reason, _ ->
          return Error (SageFsError.SessionCreationFailed (sprintf "could not create the integration worktree at %s on branch %s: %s" worktreePath branch reason))
        | Ok (), Error reason ->
          return Error (SageFsError.SessionCreationFailed (sprintf "could not create the trunk checkout at %s: %s" trunkPath reason))
        | Ok (), Ok () ->
          let namedProjects = Features.CohortIntegrationScope.parseNamedProjects (defaultArg projects "")
          let missingProjects = namedProjects |> List.filter (fun p -> not (File.Exists(Path.Combine(worktreePath, p))))
          match missingProjects with
          | _ :: _ ->
            return Error (SageFsError.SessionCreationFailed (sprintf "projects not found in the integration worktree %s: %s. Name them relative to the repository root (for example SageFs.Tests/SageFs.Tests.fsproj), or leave projects empty to load the repository's test projects." worktreePath (String.concat ", " missingProjects)))
          | [] ->
          setBinding (Some { Scope = integrationScope; Repository = repository; WorktreePath = worktreePath; Branch = branch; TrunkPath = trunkPath; Session = IntegrationSession.Pending })
          // A new integration tree is a new session to build: nothing is evaluated over it yet, and nothing was rebuilt for a landing.
          forgetFilesEvaluatedOverBuild ()
          forgetSessionRebuiltFor ()
          let who = memberIdFor agentName
          let! commitResult =
            commitCohort ctx (
              Cohort.CohortCommand.SetIntegrationHead(who, sha, integrationScope))
          match commitResult with
          | Error e ->
            // The integration stays Failed, not Pending: a landing that arrives while it is pending waits for it to start, and a
            // refused call would otherwise leave that wait with nothing to end it.
            let refusal = sprintf "set_integration_ref was refused: %s" (SageFsError.describeForAgent e)
            setBinding (Some { Scope = integrationScope; Repository = repository; WorktreePath = worktreePath; Branch = branch; TrunkPath = trunkPath; Session = IntegrationSession.Failed refusal })
            return Error e
          | Ok _ ->
            let projects = Features.CohortIntegrationScope.chooseIntegrationProjects namedProjects (discoverProjects worktreePath)
            let! buildResult = SessionBuild.runBuildAsync projects worktreePath
            let buildOutcome =
              buildResult
              |> Result.mapError SageFsError.describeForAgent
              |> Features.CohortIntegrationScope.worktreeBuildOutcome
            match buildOutcome with
            | Features.CohortIntegrationScope.WorktreeBuildOutcome.BuildFailed reason ->
              let failed = { Scope = integrationScope; Repository = repository; WorktreePath = worktreePath; Branch = branch; TrunkPath = trunkPath; Session = IntegrationSession.Failed reason }
              setBinding (Some failed)
              Log.warn "[set_integration_ref] git side configured (head=%s worktree=%s branch=%s) but the worktree build failed before session creation: %s" sha worktreePath branch reason
              return Ok (
                sprintf
                  "Integration configured: head=%s worktree=%s branch=%s trunk=%s. WARNING: the worktree build failed, so no integration session was started. %s Landings report this until it is fixed."
                  sha worktreePath branch trunkPath (failureText failed ("The worktree build failed: " + reason)))
            | Features.CohortIntegrationScope.WorktreeBuildOutcome.ReadyForSession ->
              let targets = SessionProjectTarget.tryCreateMany projects |> Result.defaultValue []
              let! sessionResult = ctx.SessionOps.CreateSession targets worktreePath WorkflowTypes.SessionWorkflow.Interactive
              match sessionResult with
              | Ok sessionId ->
                // The landing gate drives this session. Saves in its worktree must not also drive its live loop, whose confirming
                // build would swap the worker under a verification (LiveTestFileRouting.SessionPurpose.Verifying). Said BEFORE the
                // binding turns Started: a landing waiting for the session starts at once, and its rebase is such a save.
                ctx.Dispatch |> Option.iter (fun dispatch -> dispatch (SageFsMsg.SessionVerifiesLandings sessionId))
                setBinding (Some { Scope = integrationScope; Repository = repository; WorktreePath = worktreePath; Branch = branch; TrunkPath = trunkPath; Session = IntegrationSession.Started sessionId })
                return Ok (
                  sprintf
                    "Integration configured: head=%s worktree=%s branch=%s trunk=%s session=%s"
                    sha worktreePath branch trunkPath sessionId)
              | Error sessionErr ->
                let reason = SageFsError.describeForAgent sessionErr
                let failed = { Scope = integrationScope; Repository = repository; WorktreePath = worktreePath; Branch = branch; TrunkPath = trunkPath; Session = IntegrationSession.Failed reason }
                setBinding (Some failed)
                Log.warn "[set_integration_ref] git side configured (head=%s worktree=%s branch=%s) but the integration session failed to start: %s" sha worktreePath branch reason
                return Ok (
                  sprintf
                    "Integration configured: head=%s worktree=%s branch=%s trunk=%s. WARNING: the integration session failed to start. %s Landings report this until it is fixed."
                    sha worktreePath branch trunkPath (failureText failed reason))
    }


  /// `workingDirectory` is the repository the call is about, the same `working_directory` every other cohort tool takes. It decides
  /// the repository the integration worktree is cut from AND the cohort the integration serves, so a conductor whose daemon was
  /// started somewhere else (install-local starts it in its install folder) configures its own repository's cohort.
  let setIntegrationRef (ctx: McpContext) (agentName: string) (integrationRef: string) (workingDirectory: string option) (projects: string option) : Task<Result<string, SageFsError>> =
    task {
      let! callerSessionWorkingDirectory = task {
        match activeSessionId ctx agentName with
        | "" -> return None
        | sid ->
          let! infoOpt = ctx.SessionOps.GetSessionInfo (toSessionId sid)
          return infoOpt |> Option.map (fun info -> info.WorkingDirectory)
      }
      let named = workingDirectory |> Option.filter (String.IsNullOrWhiteSpace >> not)
      let integrationScope = SageFs.ScopeOf.ofWorkingDirectory (named |> Option.orElse callerSessionWorkingDirectory)
      let rootSource = Features.CohortIntegrationScope.chooseIntegrationRepoSource named callerSessionWorkingDirectory Environment.CurrentDirectory
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
        // One cohort, one worktree path: derived from the repository, so two repositories never share it. A retry for the same
        // repository finds its own path and replaces it.
        let repository = Features.CohortGit.commonRepositoryRoot candidateDir |> Option.defaultValue mainRepoDir
        // A landing in the queue forbids replacing the worktree under it. Asked before waiting for the gate so the refusal is
        // immediate, and asked again under the gate by `replaceIntegrationWorktree`.
        match Features.CohortIntegrationScope.replacementDecision (landingsInFront ctx) with
        | Features.CohortIntegrationScope.Replacement.Refused landings ->
          let label = cohortIntegrationRef.Value |> Option.map (fun binding -> Scope.label binding.Scope) |> Option.defaultValue (Scope.label integrationScope)
          return Error (SageFsError.SessionCreationFailed (Features.CohortIntegrationScope.replacementRefusal label landings))
        | Features.CohortIntegrationScope.Replacement.Allowed ->
        do! integrationWorktreeGate.WaitAsync()
        try
          return! configureIntegration ctx agentName integrationRef projects integrationScope mainRepoDir repository sha
        finally
          integrationWorktreeGate.Release() |> ignore
    }

  let getCohortStatus (ctx: McpContext) (workingDirectory: string option) : Task<Result<string, SageFsError>> =
    task {
      // The CALLER's cohort. `requireCohortOwner` with no directory answers about the
      // daemon's own scope, so a caller that had just joined a second repository read back
      // a frame from the first — "Members (0)" beside a join that plainly succeeded, which
      // reads as a broken join rather than a status read about the wrong cohort.
      match cohortOwnerFor ctx workingDirectory with
      | None -> return Error (SageFsError.SessionCreationFailed "no cohort owner is configured for this daemon")
      | Some owner ->
        // The daemon holds ONE integration, configured for the cohort of the repository `set_integration_ref` named. Reading it for
        // any other cohort would show a session and a trunk that this cohort's landings never touch.
        let binding = cohortIntegrationRef.Value
        let fit = Features.CohortIntegrationScope.bindingFit (binding |> Option.map (fun b -> b.Scope)) owner.Scope
        let integration =
          match fit, binding with
          | Features.CohortIntegrationScope.BindingFit.NotConfigured, _
          | _, None ->
            sprintf "Integration session: (not configured for %s — call set_integration_ref with working_directory set to its repository)" (Scope.label owner.Scope)
          | Features.CohortIntegrationScope.BindingFit.ConfiguredForOther other, _ ->
            sprintf
              "Integration session: (none for %s. The daemon's integration is configured for %s: %s)"
              (Scope.label owner.Scope)
              (Scope.label other)
              (Features.CohortIntegrationScope.moveAdvice (Scope.label other))
          | Features.CohortIntegrationScope.BindingFit.ForThisCohort, Some b ->
            match b.Session with
            | IntegrationSession.Started sid -> sprintf "Integration session: %s (started)" sid
            | IntegrationSession.Failed reason -> sprintf "Integration session: FAILED to start. %s" (failureText b reason)
            // The first line is the one clients parse, so it stays exactly "pending"; the explanation is its own line.
            | IntegrationSession.Pending -> "Integration session: pending\n  The worktree is building. A landing requested now waits in the queue and starts when the session does."
        // The trunk: where landings are carried once they land, and what became of each in the sessions that work there.
        let trunk =
          match (match fit with Features.CohortIntegrationScope.BindingFit.ForThisCohort -> binding | _ -> None), trunkFollowRef.Value with
          | Some binding, Some follower ->
            let machine = follower.Read()
            let lines = Features.TrunkFollow.statusLines machine
            let header = sprintf "Trunk: checkout=%s landings (%d):" binding.TrunkPath (List.length machine.Records)
            String.concat "\n" (header :: lines) + "\n"
          | Some _, None
          | None, _ -> ""
        // The holder's standing is read off the cohort's own state (a frame carries no lease), at the clock of this read.
        let silence = Features.CohortStatusText.HolderSilence.ofState (owner.ReadCohortState()) DateTime.UtcNow
        return Ok (Features.CohortStatusText.renderWithSilenceAndReports silence failingTestReportFor (owner.ReadFrame()) + integration + "\n" + trunk)
    }
