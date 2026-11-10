namespace SageFs

open SageFs.Affordances

/// The one sentence `tools/list` carries for each tool.
///
/// A tool's `[<Description>]` in `SageFs/McpTools.fs` is its manual: the rules, the transaction semantics, the output
/// shape. A client that lists tools pays for every character of it on every session, so the list carries this sentence
/// instead and the manual stays one call away (`discover_features` with `tool`). The sentence says what the tool does and,
/// where a wrong call can damage work, says so. One case per tool, so a new tool is a compile error until it has one.
///
/// A sentence is held to `ToolSummaries.maxCharacters` by `ToolListBudgetTests`, and every sentence ends with a full stop.
module ToolSummaries =
  /// The longest one sentence may be. A sentence that needs more belongs in the manual.
  let maxCharacters = 160

  let sentenceOf (tool: ToolName) : string =
    match tool with
    | ToolName.GetDaemonStatus -> "Daemon health: versions, memory pressure, session counts, anomalies and lease summaries."
    | ToolName.GetSessionStatus -> "One session's lifecycle, projects, health and available tools. wait_seconds waits for Ready."
    | ToolName.ListSessions -> "List the live sessions with their ids and status."
    | ToolName.SwitchSession -> "Make a session the active one for calls that name no session."
    | ToolName.GetAvailableProjects -> "Find the .fsproj, .sln and .slnx files to name in a create session call."
    | ToolName.ListRunnableProjects -> "List the session's projects and which of them run_app can run."
    | ToolName.ListMembers -> "List the members of a namespace, module or type, one page at a time. Names only."
    | ToolName.ListReferences -> "Every definition and use of a qualified name across the session's projects, led by a scope line."
    | ToolName.GetFrictionReport -> "A structured report of the local friction records, for an agent to act on."
    | ToolName.GetFrictionSummary -> "A compact summary of the local friction records."
    | ToolName.GetRecentFsiEvents -> "The last N evals of a session with their outcomes, timestamps and durations."
    | ToolName.ReportFriction -> "Record what was confusing about a tool, in the local friction store."
    | ToolName.CreateProjectSession -> "Create an isolated FSI session for one .fsproj, then wait on get_session_status until Ready."
    | ToolName.CreateSolutionSession -> "Create an isolated FSI session for one .sln or .slnx. Then get_session_status until Ready."
    | ToolName.CreateBareSession -> "Create an isolated FSI session with no project loaded. Then get_session_status until Ready."
    | ToolName.ResetFsiSession -> "Clear every definition in the session and re-warm it. The session keeps running."
    | ToolName.HardResetFsiSession -> "Dispose and recreate the FSI session, optionally rebuilding first. Every definition is lost."
    | ToolName.SwitchWorkflow -> "Switch a session's workflow (REPL, live testing, hot reload). This recreates the session and loses its state; dryRun previews."
    | ToolName.StopSession -> "Stop a session by id and release its worker. Anything defined in it is gone."
    | ToolName.CancelEval -> "Cancel the session's running eval."
    | ToolName.SendFsharpCode -> "Run F# in the session. End each statement with ';;' and send small blocks."
    | ToolName.ManageScratchPad -> "View, export or promote the session's scratch pad snippets."
    | ToolName.CheckFsharpCode -> "Type-check F# against the session without running it."
    | ToolName.DecomposePipeline -> "Split an F# pipeline into its stages and say which stages are pure."
    | ToolName.Diagnose -> "Diagnose one session from what it evaluated and what its tests did."
    | ToolName.CoverageIntel -> "Judge one session's test coverage: blind spots, correlated failures, diagnostic power."
    | ToolName.ImpactForecast -> "Forecast what re-evaluating a cell costs: cells downstream of it and how long the session's evals take."
    | ToolName.SuggestNextAction -> "A prioritized list of what to do next in one session."
    | ToolName.PlanRipple -> "Plan the re-evaluation of the cells downstream of changed cells."
    | ToolName.PreviewWhatIf -> "Preview what would change if a binding had a different value. Nothing is applied."
    | ToolName.SuggestNextCell -> "Type-directed suggestions for what to evaluate next in one session."
    | ToolName.GetCellDependencies -> "The dependency graph of one session's evals: what each produced and consumed."
    | ToolName.DiscoverFeatures -> "Rank the daemon's tools, or pass tool for one tool's manual. A tool not listed here is still callable."
    | ToolName.GetSessionFilmstrip -> "A visual history of the session's evaluations."
    | ToolName.GetEvalTimeline -> "The session's eval timeline with a sparkline and percentile statistics."
    | ToolName.GetEvalDiff -> "A before and after diff of recent eval outputs."
    | ToolName.GetMessageJournal -> "A structured audit log of the session's eval events."
    | ToolName.ExportNotebook -> "Export the session as a notebook-style .fsx file with cell metadata."
    | ToolName.ExportSessionTranscript -> "Export the session as a clean, topologically sorted .fsx transcript."
    | ToolName.ExplainTestFailure -> "Why a test went from passing to failing: time since the last pass, what changed, and a summary."
    | ToolName.SuggestRepair -> "Compose a repair plan for a failing test from its failure, its causal symbol and the ripple."
    | ToolName.ListTests -> "List the session's discovered tests, optionally filtered by name or file."
    | ToolName.RunTests -> "Run the session's tests and get a receipt. The one way to run tests here."
    | ToolName.TargetedVerify -> "Plan a targeted verification pass for one changed behavior."
    | ToolName.RunApp -> "Run the session's executable project inside the session, with hot reload."
    | ToolName.StopApp -> "Stop the app that run_app started. The session keeps running."
    | ToolName.EnableHotReload -> "Turn on hot reload (browser refresh on save) for a session."
    | ToolName.DisableHotReload -> "Turn off hot reload for a session."
    | ToolName.ResetHotReloadState -> "List or reset the live state that hot reload kept."
    | ToolName.SetReflectionReadMode -> "List or switch how hot reload watches module values read by reflection."
    | ToolName.NudgeValue -> "Change one value in a source file the session owns and write just that expression back to the file."
    | ToolName.AcquireFullBuildLease -> "Ask for the lease that an external full build needs, and release it when done. Pass your own agent_name."
    | ToolName.AcquireTestSuiteLease -> "Ask for the lease that an external test-suite run needs, and release it when done. Pass your own agent_name."
    | ToolName.AcquireRunAppLease -> "Ask for the lease that an external run-app process needs, and release it when done. Pass your own agent_name."
    | ToolName.ReleaseWorkLease -> "Release a lease you hold by its id. A lease held by another connection is never released."
    | ToolName.JoinCohort -> "Join this repository's cohort: who is working here, file claims and landing requests."
    | ToolName.LeaveCohort -> "Leave the cohort. The claims you hold become orphaned until the conductor reassigns them."
    | ToolName.AcquireClaim -> "Claim a file or directory so other members know it is yours to edit now."
    | ToolName.ReleaseClaim -> "Release a claim you hold."
    | ToolName.ReassignClaim -> "Conductor only: give an orphaned claim to a present member."
    | ToolName.DelegateConductor -> "Conductor only: hand the conductor seat to another present member."
    | ToolName.SeatConductor -> "Ask the person at your client to fill an empty conductor seat or replace its holder; only their ticked yes acts."
    | ToolName.RequestLanding -> "Queue your commits to be rebased onto the integration head, verified and fast-forwarded in. Watch it with get_cohort_status."
    | ToolName.WithdrawLanding -> "Withdraw a landing you requested, in any state short of Landed."
    | ToolName.VetoLanding -> "Object to a live landing with a reason. It is blocked and leaves the queue."
    | ToolName.ResolveVeto -> "Conductor only: clear a veto and queue the landing again."
    | ToolName.GetCohortStatus -> "Read the cohort: members, claims, landings and the conductor. Wait-free."
    | ToolName.SetIntegrationRef -> "Conductor only: set the git ref and worktree that landings rebase onto."
    | ToolName.MintMember -> "Conductor only: mint a scoped, expiring, revocable member token for one agent run. The scope is ONE directory prefix, never a list."
    | ToolName.RevokeMember -> "Conductor only: revoke a member token."
    | ToolName.ManageLocalData -> "See what SageFs keeps under its data directory, and clear a store. Clearing asks for confirmation."
    | ToolName.GetWorkspaceHygiene -> "Dry run: what agents left behind on this machine and the plan that would tidy it. Nothing is touched."
    | ToolName.TidyWorkspace -> "Carry out the safe part of a get_workspace_hygiene plan. Needs confirm=true and the plan id."
