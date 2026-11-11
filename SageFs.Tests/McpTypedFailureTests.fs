module SageFs.Tests.McpTypedFailureTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.Server.McpTools
open SageFs.McpEcho
open SageFs.McpSessionRouting
open SageFs.Features.MessageJournal
open SageFs.Tests.TestInfrastructure

/// Tool bodies in Mcp.fs used to answer a failure with a sentence and no mark (the duplicate-session text, the
/// workflow message, "Session %s not found."), so the client read a success. Each of these functions now returns
/// an `EchoResult`: the failure is a typed `SageFsError` with its reason and next step, and the wrapper
/// (`withEchoResult`) reports it with isError. These pin that, one function at a time.

/// The typed error a tool call reached the client with, through the same wrapper the tools use.
let private reachedClient (call: Task<EchoResult>) : Task<SageFsError> =
  task {
    let ctx = { sharedCtx () with FrictionStore = None }
    try
      let! text = withEchoResult ctx "typed_failure_probe" call
      return failwithf "the failure came back as a success: %s" text
    with :? SageFsErrorException as ex -> return ex.Error
  }

/// A failure says what failed and what to do next, and the protocol marks it isError.
let private expectUsable (what: string) (err: SageFsError) : unit =
  SageFsError.describe err |> Expect.isNotEmpty (what + ": it says what failed")
  SageFsError.suggestedAction err |> Expect.isNotEmpty (what + ": it names a next step")
  (SageFs.Server.McpServer.structuredToolErrorResult err).IsError |> Expect.equal (what + ": the protocol says it failed") (Nullable true)

let private sessionInfoAt (id: string) (dir: string) : WorkerProtocol.SessionInfo =
  { Id = toSessionId id
    Name = None
    Projects = []
    WorkingDirectory = dir
    SolutionRoot = None
    Status = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 0; Port = None }
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    CreatedAt = DateTime.UtcNow
    LastActivity = DateTime.UtcNow
    ActiveProject = None
    ProjectRoles = []
    App = AppRun.AppRunState.NotRunning
    Rebuild = LastRebuild.NeverRebuilt
    Reload = SessionReload.NoReloadYet
    Freshness = ReplFreshness.InSync }

/// A context whose registry holds exactly `sessions`, with no worker proxy and no active session.
let private ctxWithSessions (sessions: WorkerProtocol.SessionInfo list) : McpContext =
  let ctx = sharedCtx ()
  ctx.SessionMap.Clear()
  { ctx with
      SessionOps =
        { ctx.SessionOps with
            GetAllSessions = fun () -> Task.FromResult sessions
            GetProxy = fun _ -> Task.FromResult None
            GetSessionInfo = fun id -> Task.FromResult (sessions |> List.tryFind (fun s -> s.Id = id)) } }

/// A context whose active session has a worker but no registry record.
let private ctxWithActiveButNoRecord () : McpContext =
  let ctx = sharedCtx ()
  ctx.SessionMap.["mcp"] <- "aaaaaa01"
  { ctx with SessionOps = { ctx.SessionOps with GetSessionInfo = fun _ -> Task.FromResult None } }

let private tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)

let private failedWith (echo: EchoResult) : SageFsError =
  match echo with
  | EchoResult.Failed err -> err
  | EchoResult.Answered text -> failtestf "read as an answer: %s" text

[<Tests>]
let tests =
  testList "tool bodies report a failure as a typed error" [

    testList "createSession (the first call an onboarding agent makes)" [
      testTask "WHY — targets that are not one project, one solution or bare are refused with the three ways to ask" {
        let! echo = createSession (sharedCtx ()) "agent" [ SessionProjectTarget.Bare; SessionProjectTarget.Project "a.fsproj" ] tempDir ""
        let! err = reachedClient (Task.FromResult echo)
        expectUsable "bare plus a project" err
        SageFsError.suggestedAction err |> Expect.stringContains "names the bare tool" "create_bare_session"
      }

      testTask "WHY — a working directory that does not exist keeps its typed case" {
        let! echo = createSession (sharedCtx ()) "agent" [ SessionProjectTarget.Bare ] "/this/directory/does/not/exist" ""
        match failedWith echo with
        | SageFsError.UnsafeSessionPath(path, _) as err ->
          path |> Expect.equal "the offending value travels" "/this/directory/does/not/exist"
          expectUsable "missing directory" err
        | other -> failtestf "expected UnsafeSessionPath, got %A" other
      }

      testTask "WHY — an unknown workflow is refused, and the three that exist are the next step" {
        let! echo = createSession (sharedCtx ()) "agent" [ SessionProjectTarget.Bare ] tempDir "hotreoad"
        let err = failedWith echo
        expectUsable "typo" err
        SageFsError.describe err |> Expect.stringContains "names the typo" "hotreoad"
        SageFsError.suggestedAction err |> Expect.stringContains "names the right spelling" "hotreload"
      }

      testTask "WHY — a second session for the same target and directory is a DuplicateSession that names the first" {
        let existing = sessionInfoAt "bbbbbb02" tempDir
        let! echo = createSession (ctxWithSessions [ existing ]) "agent" [ SessionProjectTarget.Bare ] tempDir ""
        match failedWith echo with
        | SageFsError.DuplicateSession(sid, dir) as err ->
          sid |> Expect.equal "the existing session" "bbbbbb02"
          dir |> Expect.equal "its directory" tempDir
          expectUsable "duplicate" err
          SageFsError.suggestedAction err |> Expect.stringContains "points at switch_session" "switch_session"
        | other -> failtestf "expected DuplicateSession, got %A" other
      }

      testTask "WHY — a refusal from the session manager reaches the client as that refusal" {
        let refusal = SageFsError.SupervisorBusy(300, 256)
        let ctx = ctxWithSessions []
        let ctx = { ctx with SessionOps = { ctx.SessionOps with CreateSession = fun _ _ _ -> Task.FromResult (Result.Error refusal) } }
        let! echo = createSession ctx "agent" [ SessionProjectTarget.Bare ] tempDir ""
        let! err = reachedClient (Task.FromResult echo)
        err |> Expect.equal "the manager's own case" refusal
      }
    ]

    testList "switchSession" [
      testTask "WHY — a string that is not a session id is refused and says to list sessions" {
        let! echo = switchSession (sharedCtx ()) "agent" "not an id!"
        let! err = reachedClient (Task.FromResult echo)
        expectUsable "bad shape" err
        SageFsError.suggestedAction err |> Expect.stringContains "points at list_sessions" "list_sessions"
      }

      testTask "WHY — a well-formed id no session has is SessionNotFound" {
        let! echo = switchSession (ctxWithSessions []) "agent" "ffff0001"
        let! err = reachedClient (Task.FromResult echo)
        err |> Expect.equal "the typed case, with the id" (SageFsError.SessionNotFound "ffff0001")
        expectUsable "not found" err
      }

      testTask "WHY — a session that exists is an answer (the twin)" {
        let! echo = switchSession (ctxWithSessions [ sessionInfoAt "bbbbbb02" tempDir ]) "agent" "bbbbbb02"
        match echo with
        | EchoResult.Answered text -> text |> Expect.stringContains "says it switched" "Switched to session 'bbbbbb02'"
        | EchoResult.Failed err -> failtestf "a switch to a real session failed: %A" err
      }
    ]

    testList "switchWorkflow" [
      testTask "WHY — an unknown target workflow is refused with the valid ones" {
        let! echo = switchWorkflow (ctxWithSessions []) "agent" None "hotreoad" false
        let err = failedWith echo
        expectUsable "typo" err
        SageFsError.suggestedAction err |> Expect.stringContains "names the right spelling" "hotreload"
      }

      testTask "WHY — a directory with no session is a routing refusal with a next step, not a sentence" {
        let! echo = switchWorkflow (ctxWithSessions []) "agent" (Some "/nowhere/at/all") "livetesting" false
        let! err = reachedClient (Task.FromResult echo)
        expectUsable "no session" err
      }

      testTask "WHY — a switch the daemon refuses keeps the daemon's own typed error" {
        let refusal = SageFsError.HardResetFailed "A rebuild is in progress for this session."
        let session = sessionInfoAt "cccccc03" tempDir
        let ctx = ctxWithSessions [ session ]
        let proxy : WorkerProtocol.SessionProxy = fun _ -> async { return WorkerProtocol.WorkerResponse.WorkerReady }
        let ctx =
          { ctx with
              SessionOps =
                { ctx.SessionOps with
                    GetProxy = fun _ -> Task.FromResult (Some proxy)
                    SwitchWorkflow = fun _ _ -> Task.FromResult (Result.Error refusal) } }
        let! echo = switchWorkflow ctx "agent" (Some tempDir) "livetesting" false
        let! err = reachedClient (Task.FromResult echo)
        err |> Expect.equal "the daemon's case" refusal
      }
    ]

    testList "list_runnable_projects" [
      testTask "WHY — no session at all is a refusal that names how to start one" {
        let! echo = listRunnableProjects (ctxWithSessions [])
        let! err = reachedClient (Task.FromResult echo)
        expectUsable "no session" err
      }

      testTask "WHY — an active session the registry has dropped is SessionNotFound" {
        let! echo = listRunnableProjects (ctxWithActiveButNoRecord ())
        let! err = reachedClient (Task.FromResult echo)
        err |> Expect.equal "the typed case, with the id" (SageFsError.SessionNotFound "aaaaaa01")
      }
    ]

    testList "session routing refusals (routed)" [
      testCase "WHY — a warming session is SessionRestarting, whose next step is to wait" <| fun _ ->
        let warming = WorkerProtocol.SessionLifecycleStatus.Starting { Pid = 1; Port = None }
        match routed (WarmingUp ("dddddd04", warming)) with
        | Result.Error (SageFsError.SessionRestarting(sid, _) as err) ->
          sid |> Expect.equal "names the session" "dddddd04"
          SageFsError.suggestedAction err |> Expect.stringContains "says to wait" "get_session_status"
        | other -> failtestf "expected SessionRestarting, got %A" other

      testCase "WHY — a faulted session carries why it faulted and says to reset it" <| fun _ ->
        match routed (FaultedSession ("eeeeee05", FaultCause.Recorded "Not all DLLs are found")) with
        | Result.Error err ->
          SageFsError.describe err |> Expect.stringContains "the cause travels" "Not all DLLs are found"
          SageFsError.suggestedAction err |> Expect.stringContains "says to reset" "hard_reset_fsi_session"
        | Result.Ok sid -> failtestf "a faulted session routed to %s" sid

      testTask "WHY — the text helper keeps the 'Error: ' label and ends in the refusal's own next step, so the seam reads it whole" {
        let! text = withSessionWd (ctxWithSessions []) "agent" RouteTarget.ActiveSession (fun _ -> Task.FromResult "unreached")
        text |> Expect.stringContains "keeps the label readers of the text look for" "Error: "
        match classifyEcho text with
        | EchoResult.Failed (SageFsError.ToolFailed(_, next)) ->
          next |> Expect.notEqual "a real next step, not the seam's stand-in" McpEcho.EchoFailureText.noNextStep
          next |> Expect.stringContains "names the call that shows the sessions" "list_sessions"
        | other -> failtestf "expected a ToolFailed with a next step, got %A" other
      }

      testCase "WHY — a routable session is its id (the twin)" <| fun _ ->
        routed (Routable "ffffff06") |> Expect.equal "the id" (Result.Ok "ffffff06")
    ]

    testList "decomposePipeline" [
      testTask "WHY — blank code is refused, so a client sees it fail instead of an empty 'no stages' answer" {
        let! echo = McpJournalViews.decomposePipeline "   "
        let! err = reachedClient (Task.FromResult echo)
        expectUsable "blank" err
        SageFsError.suggestedAction err |> Expect.stringContains "shows an example" "|>"
      }

      testTask "WHY — a pipeline is an answer (the twin)" {
        let! echo = McpJournalViews.decomposePipeline "xs |> List.map f"
        match echo with
        | EchoResult.Answered text -> text |> Expect.stringContains "lists the stages" "Pipeline decomposition (2 stages)"
        | EchoResult.Failed err -> failtestf "a pipeline failed: %A" err
      }
    ]

    testList "getMessageJournal" [
      let ctxWithHistory (evals: (string * string) list) : McpContext =
        let state =
          evals
          |> List.fold
               (fun s (code, result) -> Features.FeatureHooks.recordEval code result 5L s)
               Features.FeatureHooks.FeaturePushState.empty
        { ctxWithSessions [] with GetFeatureState = Some (fun () -> state) }

      testTask "WHY — a caller with no session is refused and told how to start one" {
        let! echo = McpJournalViews.getMessageJournal { ctxWithSessions [] with GetFeatureState = None } None None
        let! err = reachedClient (Task.FromResult echo)
        expectUsable "no session" err
      }

      testTask "WHY — a session that evaluated nothing is an answer: the journal is empty" {
        let! echo = McpJournalViews.getMessageJournal (ctxWithHistory []) None None
        match echo with
        | EchoResult.Answered text -> text |> Expect.stringContains "says it is empty" "journal is empty"
        | EchoResult.Failed err -> failtestf "an empty journal failed: %A" err
      }

      testTask "WHY — a failed eval is an error entry, and min_level=error returns it alone" {
        let ctx = ctxWithHistory [ "let x = 1", "val x: int = 1"; "let y = z", "Error: The value 'z' is not defined." ]
        let! all = McpJournalViews.getMessageJournal ctx None None
        let! errorsOnly = McpJournalViews.getMessageJournal ctx (Some "error") None
        let textOf (echo: EchoResult) =
          match echo with
          | EchoResult.Answered text -> text
          | EchoResult.Failed err -> failtestf "failed: %A" err
        textOf all |> Expect.stringContains "counts one error" "1 error"
        textOf errorsOnly |> Expect.stringContains "returns the failed eval" "let y = z"
        textOf errorsOnly |> Expect.stringContains "marks it ERR" "ERR"
        (textOf errorsOnly).Contains "let x = 1" |> Expect.isFalse "the clean eval is filtered out"
      }
    ]

    testList "a cohort refusal keeps its kind, and a server with no cohort says so" [
      testCase "WHY — the core's refusals reach the client as CohortRefused with their own kind" <| fun _ ->
        let who = MemberTable.MemberId.Minted "alice"
        match CohortErrorMapping.toSageFsError (Cohort.CohortError.NotConductor who) with
        | SageFsError.CohortRefused(CohortFailureKind.NotConductor, reason, suggestion) as err ->
          reason |> Expect.stringContains "names the member" "alice"
          suggestion |> Expect.isNotEmpty "names a next step"
          expectUsable "not conductor" err
        | other -> failtestf "expected CohortRefused(NotConductor), got %A" other

      testCase "WHY — an owner that failed or went silent is told apart by kind, with the daemon log as the next step" <| fun _ ->
        match CohortErrorMapping.toSageFsError (Cohort.CohortError.OwnerUnresponsive Timeouts.ownerReply) with
        | SageFsError.CohortRefused(CohortFailureKind.OwnerUnresponsive, _, suggestion) ->
          suggestion |> Expect.stringContains "re-reads before repeating" "get_cohort_status"
          suggestion |> Expect.stringContains "names who to tell" "person who runs the daemon"
        | other -> failtestf "expected CohortRefused(OwnerUnresponsive), got %A" other
        match CohortErrorMapping.toSageFsError (Cohort.CohortError.OwnerStepFailed "disk full") with
        | SageFsError.CohortRefused(CohortFailureKind.OwnerStepFailed, reason, suggestion) ->
          reason |> Expect.stringContains "carries the step's reason" "disk full"
          suggestion |> Expect.stringContains "names the log line" "[cohort-owner]"
        | other -> failtestf "expected CohortRefused(OwnerStepFailed), got %A" other

      testTask "WHY — a cohort command on a server with no cohort wired is CohortOwnerNotWired, whose next step is not a build" {
        let ctx = sharedCtx ()
        let scope = ScopeOf.ofWorkingDirectory (Some tempDir)
        let! outcome = commitCohort ctx (Cohort.CohortCommand.OpenCohort scope)
        match outcome with
        | Result.Error SageFsError.CohortOwnerNotWired ->
          let err = SageFsError.CohortOwnerNotWired
          expectUsable "no owner" err
          SageFsError.suggestedAction err |> Expect.stringContains "points at the daemon" "get_daemon_status"
          (SageFsError.suggestedAction err).Contains "dotnet build" |> Expect.isFalse "it is not a project problem"
        | other -> failtestf "expected CohortOwnerNotWired, got %A" other
      }
    ]

    testList "an eval that gets an answer no eval expects" [
      testTask "WHY — a worker that answers an eval with a check result is a failure that names what arrived, never a clean eval" {
        let ctx = sharedCtx ()
        let sid = ctx.SessionMap.["test"]
        let proxy : WorkerProtocol.SessionProxy = fun _ -> async { return WorkerProtocol.WorkerResponse.CheckResult("r", []) }
        let ctx = { ctx with SessionOps = { ctx.SessionOps with GetProxy = fun _ -> Task.FromResult (Some proxy) } }
        let! _, outcome, _, err, _, _ =
          evalFSharpCodeWithOutcome ctx "test" "1 + 1" OutputFormat.Text None None None None None None (EvalLimit.Default Timeouts.evalCall)
        match outcome, err with
        | InfraFailure (SageFsError.WorkerCommunicationFailed(failedSid, reason)), Some typed ->
          failedSid |> Expect.equal "names the session" sid
          reason |> Expect.stringContains "names what arrived" "CheckResult"
          expectUsable "unexpected answer" typed
        | other -> failtestf "expected an infra failure naming CheckResult, got %A" other
      }

      testCase "WHY — the name of an answer is the case's own name" <| fun _ ->
        McpToolRefusals.workerAnswerName WorkerProtocol.WorkerResponse.WorkerReady |> Expect.equal "a case without fields" "WorkerReady"
        McpToolRefusals.workerAnswerName (WorkerProtocol.WorkerResponse.CheckResult("r", [])) |> Expect.equal "a case with fields" "CheckResult"
    ]

    testList "the journal built from history keeps each eval's own time and level" [
      testCase "WHY — recordEntries keeps what the entries carry, where recordAll stamped them all with the clock now" <| fun _ ->
        let long_ago = DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero)
        let entry level message : JournalEntry =
          { Timestamp = long_ago; Level = level; Source = "eval"; Message = message }
        let journal =
          Journal.create 8
          |> Journal.recordEntries [ entry JournalLevel.Info "let x = 1"; entry JournalLevel.Failure "let y = z" ]
        let entries = Journal.entries journal
        entries |> List.map (fun e -> e.Timestamp) |> List.distinct |> Expect.equal "the entries' own time" [ long_ago ]
        entries |> List.map (fun e -> e.Level) |> List.sort |> Expect.equal "each entry's own level" [ JournalLevel.Info; JournalLevel.Failure ]
    ]
  ]
