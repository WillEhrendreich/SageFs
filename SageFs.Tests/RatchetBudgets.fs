/// The numbers the budget ratchets compare against, and the pure counting they compare them to.
///
/// Nothing here touches the product or the test framework: it reads source text and compares counts. That is on
/// purpose. `scripts/ratchets-source.fsx` loads THIS file and runs the same two checks against the working tree in
/// seconds, before anything is built, so a file that grew past its budget is reported at the start of a ship and not
/// after a Release build. The compiled lane (`ArchitectureTests.fs`) reads the very same values, so there is one
/// table and one way to count, and the early run can never disagree with the later one.
///
/// A budget only goes DOWN. `SageFs.Tests.dll --ratchets --tighten` rewrites the numbers in this file.
module SageFs.Tests.RatchetBudgets

open System.IO

/// The file this table lives in, as `--ratchets --tighten` needs to name it.
let sourceFile = "SageFs.Tests/RatchetBudgets.fs"

/// Lines per source file. The "god files" must not keep growing: split before you add, never raise the number.
let fileSize : (string * int) list =
  [ // 4200 -> 4270: a one-time bump for the F16/F5/F6 cohort-integration
    // bootstrap fix (cohort-dogfood-findings.md) — main-repo-root
    // resolution now reads the caller's own session instead of the
    // daemon's cwd, and a worktree build now runs before session
    // creation, fail-fast on failure. A deliberate, reviewed fix, not
    // silent accretion. Ratchet back DOWN when this file is split; never
    // bump to paper over drift.
    // 4252 -> 4337: a one-time bump for the fcs-onboarding-trial fix
    // (fcs-trial-a/b/c, 2026-09-22): get_fsi_status now surfaces
    // elapsed/bound/progress for a warming session and reconciles a
    // Routable transport failure against the registry instead of a bare
    // string (renderWarmingOrFaulted), and create_session warns before a
    // large-repo auto-discovery instead of silently hanging. Deliberate,
    // reviewed fixes, not silent accretion. Ratchet back DOWN when this
    // file is split; never bump to paper over drift.
    // 4337 -> 4342: the private `discoverProjects` (cohort integration
    // project scan) now walks through `SafeDirectoryWalk.walkFiles`
    // instead of `Directory.EnumerateFiles(_, _, AllDirectories)` — a
    // directory symlink cycle (found live: Wine's `dosdevices/z:` -> `/`
    // under `~/.local/share/Steam/...`) sent that walk into unbounded
    // recursion, which is what was eating the daemon's own RSS at
    // ~2GB/minute with zero sessions. Deliberate, reviewed fix, not
    // silent accretion. Ratchet back DOWN when this file is split; never
    // bump to paper over drift.
    // 4342 -> 4363: get_fsi_status now appends a stale-daemon affordance
    // (issue #136) in both the Routable and no-session branches, reading
    // UpdateCheckService.currentOutcome() — the daemon's own periodic
    // NuGet check — silent unless genuinely behind. Deliberate, reviewed,
    // matches the self-host-staleness (selfHostLine) and health
    // (healthLine) affordances already appended right beside it. Ratchet
    // back DOWN when this file is split; never bump to paper over drift.
    // 4363 -> 4389: issue #140. resolveSessionId no longer clears an
    // agent's cached active-session mapping just because the target
    // resolved to WarmingUp/Unroutable/FaultedSession — a switch_session
    // made during warmup was being silently undone by the next unrouted
    // status read. Deliberate, reviewed fix, not silent accretion.
    // Ratchet back DOWN when this file is split; never bump to paper
    // over drift.
    // 4389 -> 4400: issue #143. formatWorkerEvalResult and
    // loadFSharpScriptResult's success branch now strip ANSI escape codes
    // from eval output (AppState.stripAnsi, already used by the TUI)
    // before it reaches an MCP caller. Deliberate, reviewed fix, not
    // silent accretion. Ratchet back DOWN when this file is split; never
    // bump to paper over drift.
    // 4400 -> 4385: the cohort integration/worktree block moved to
    // McpCohortIntegration.fs. The MCP surface keeps the same tools, while
    // the process-global binding now has one explicit owner and the landing
    // performer reads that same binding. Exact post-split size.
    // 4385 -> 4388: ratcheted DOWN (never up) after moving the
    // session-status payload serialization and the Ready/WarmingUp state
    // rule out into SessionStatusPayload.fs. The payload is pure
    // serialization of facts the caller already computed, and taking it out
    // is what let the state/lifecycle/loadedProjects agreement become one
    // testable rule instead of an inline record. The remaining growth is the
    // call site; the rule itself now lives where the DST can fold it.
    // 4388 -> 4410: a one-time bump for the stale-session RECOVERY wiring.
    // The policy itself was EXTRACTED to StaleSessionRecovery.fs (it is a
    // policy with several honest outcomes, not a branch), which is why the
    // net is +22 rather than the ~+50 the inline version cost. The bump is
    // for the call site plus its honest log line, not for the logic.
    // Ratchet back DOWN when the file is split; never bump to paper over drift.
    // 4410 -> 4365: RebuildOutcome (a type and its pure functions, ~60 lines)
    // moved to RebuildOutcome.fs, which is what made room for the status
    // payload to report the outcome at all.
    // 4365 -> 4298: the cohort error mapping (a pure 76-line function of one
    // DU) moved to CohortErrorMapping.fs, and the two copies of the rebuild
    // block became one helper. Net of the lastRestart lookup and helper.
    // 4298 -> 4280: the eval dedup cache is gone (lookup, record and five
    // clearSession calls).
    // 4280 -> 4258: the rebuild outcome is recorded by the SessionManager now,
    // so the tool's own store and its recording are gone.
    // 4258 -> 4110: getStatus was dead (only tests called it; the registered
    // get_session_status tool calls getSessionStatus), and its `eventCount = 0`
    // printed "Events: 0" on every status. The registry sync it alone did now
    // lives in getSessionStatus.
    // 4110 -> 4257: get_session_status `wait_seconds` (the AwaitReady park; the
    // closed outcome set and the clamp live in SessionStatusPayload.fs) and
    // the gate's single resolution plus bounded status probe. Paid for by
    // deleting getStartupInfoJson and the unused
    // largeRepoAutoDiscoveryWarningThreshold. Still under the 4258 this
    // started the series at; never bump to paper over drift.
    // 4226 -> 4219: JSON goes through SageFs.Json, so liveTestJsonOpts and the
    // two ad hoc JsonSerializerOptions are gone.
    // 4219 -> 4229: a session whose FSI host crashed. The tool gate probes the worker's status and, for
    // a crashed host, refuses with the crash instead of the generic wait-for-Ready advice (a reset stays
    // admitted), and the eval formatter does not guess advice for the typed crash. Deliberate, reviewed;
    // ratchet back DOWN when this file is split.
    // 4229 -> 4129: the live-testing status payload and the failure-location parser moved to
    // LiveTestStatusView.fs, which paid for the pause/scope routes' call sites and left it below the
    // 4229 it started at. Exact size.
    // 4129 -> 3945: the seven analysis tools (diagnose, coverage_intel, impact_forecast, suggest_next_action,
    // suggest_repair, explain_test_failure, get_cell_dependencies) became pure functions of the model and the
    // feature state in McpAnalysisViews.fs, and Mcp.fs keeps only their "not started" guards. The file is 3645
    // lines, so this leaves 300 lines of headroom for the work that has to land here before the next split.
    // 3945 -> 3332: the eight analysis formatters the registered tools no longer call (diagnose, coverageIntel,
    // impactForecast, planRipple, previewWhatIf, suggestNextCell, getCellDependencies, discoverFeatures) and the
    // dead formatters of the retired tools (getCompletions, exploreType, visualizeDomainModel, getFileCoverage,
    // queryTestCoverage) are deleted, and suggest_next_action reads its session through McpAnalysis like the
    // other seven. The file is 3332 lines. Exact size.
    // 3332 -> 3217: the cohort tools' bodies (join, leave, acquire, release, reassign, request_landing and
    // their parsers) moved to McpCohortTools.fs, which paid for the member-token identity gate and the claim
    // path canonicalization that landed here and left it 112 lines under the 3332 it started at. The file
    // is 3220 lines. Exact size.
    // 3220 -> 3280: the tool-admission DECISION (the gate over the whole tool surface, and
    // the reasoning behind it) moved out to ToolAuthorityGate.fs, which is where the "a role is
    // not a sandbox" argument belongs rather than in the middle of an adapter file. That paid for
    // the five MCP cohort tools' new `workingDirectory` parameter (each of which needs its own
    // decision about which cohort it addresses) and for the whole-surface authority gate's call
    // site, which the new module now carries. The file is 3280 lines. Exact size.
    // 3280 -> 3292: RAISED, which this table normally forbids, and the reason is that the
    // line count is a proxy and the thing it stands for moved OUT. The multi-cohort work
    // put the "which cohort is this caller asking about" decision here (40 lines, the file's
    // accretion problem in miniature) plus the `CohortSupport` field that carries the wiring.
    // The decision is now `SageFs/CohortOwnerResolution.fs` and the owner that answered it
    // (`requireCohortOwner`, which resolved with no directory) is GONE — every site resolves
    // a scope explicitly, so a bare `None` left anywhere is a question, not a default. What
    // remains is a one-line field, a forwarding call, and the comments saying WHY each site
    // passes the directory it does — including the one that reads no directory at all,
    // because `commitCohort`'s command already carries its scope.
    "SageFs/Mcp.fs", 3289
    // 850 -> 830: ratcheted DOWN (never up) after moving the
    // session-path-containment validator (resolveRealSessionPath/
    // isUncPath/validateSessionCreateRequest) out into its own
    // SessionPathValidation.fs module, shared by Mcp.fs's create_session
    // MCP tool and McpServer.fs's /api/sessions/create route (one rule,
    // one implementation — sagefs-roast.md Finding #1/#13). File dropped
    // to 821 lines; budget set just above that, not left at the old
    // ceiling.
    // 830 -> 681: the five status formatters that took an eventCount
    // (formatStatus, formatStatusJson, formatEnhancedStatus,
    // formatEnhancedStatusJson, formatProxyStatus) and formatLoadedProjectsLine
    // had no production caller, so they are gone.
    // 681 -> 676: the startup info options object is SageFs.Json's, and the
    // structured eval result no longer carries three JsonIgnore attributes.
    "SageFs/McpAdapter.fs", 676
    // 5100 -> 5160: a one-time bump for the live-testing-asyoutype-plan.md
    // Brief 4 keystone (EvalThenRunRequest, TestCycleEffect.
    // EvalBufferThenRunAffected, TestCycleEffects.redirectToEvalBuffer,
    // and its handleFcsResult wiring) — a deliberate, reviewed feature,
    // not silent accretion. Ratchet back DOWN when this file is split;
    // never bump to paper over drift.
    // 5160 -> 4425: the split this entry asked for. The inline-feedback read
    // model (annotations, code lenses, coverage view, run explainer, session
    // invariants — ~800 lines of editor PRESENTATION, referenced by nothing
    // in the FSI host's embedded source closure) moved to
    // Features/TestAnnotations.fs. Set to the file's exact post-split size:
    // the next addition earns a reviewed bump rather than inheriting slack.
    // 4425 -> 4380: the requested-run identity (RunRequestId, RequestedRun,
    // LiveTestState.RunRequests/ResultGenerations) had to live beside
    // LiveTestState; its lifecycle module (Features/RequestedRuns.fs) and the
    // unrelated dashboard treemap projection (Features/TestTreemap.fs) moved
    // out, so the file SHRANK. Exact post-split size, per the rule above.
    // 4380 -> 4341: execution-integrity and SSE batch payload contracts
    // moved to TestExecutionContracts.fs, preserving their public namespace.
    // Exact post-split size.
    // 4341 -> 4355: the TestSummary tally fix. `bucketOf` replaces a
    // catch-all that silently dropped Detected/Queued/Skipped, so a suite of
    // three detected tests reported Total=3 with every other counter 0 — a
    // false green that made a dispatched-but-unrun live-testing run look
    // like a silent zero. Paying with a private Bucket DU and an exhaustive
    // match buys exhaustiveness; splitting the whole module out is not
    // possible without inverting the dependency, because TestSummary and
    // TestRunStatus live in this same file. Ratchet back DOWN when the file
    // is split; never bump to paper over drift.
    // 4355 -> 4353: the live-loop floor and the whitespace-aware trivia
    // normalizer paid for themselves by deleting the superseded fallback
    // conditions and comments. Exact size, per the rule above.
    // 4353 -> 3371: the daemon-only live-testing cycle (what a type-check decides, the debounce,
    // the effects and the per-session cycle state) moved to LiveTestingCycle.fs, which the isolated
    // FSI host never compiles. Exact post-split size.
    // 3371 -> 3708: the live-testing parity work (per-test coverage and line narrowing, result
    // provenance, pause and scope). Deliberate and reviewed; still below the 4353 this file stood at
    // before the cycle moved out. The confirmation machine went to its own file (BuildConfirmation.fs)
    // rather than here. Ratchet back DOWN when this file is split; never bump to paper over drift.
    "SageFs.Core/Features/LiveTestingTypes.fs", 3708
    // 2900 -> 2950: a one-time bump for the roast UX-6 keystone (per-session
    // live-testing enable/disable — EnableLiveTestingForSession /
    // DisableLiveTestingForSession, resolveOrCreateLiveTestingTarget) — a
    // deliberate, reviewed feature, not silent accretion. Ratchet back DOWN
    // when this file is split; never bump to paper over drift.
    // 2950 -> 3060: a one-time bump for the live-testing-asyoutype-plan.md
    // Brief 4 keystone (TuiEvent.LiveDiscoveryMerged handler and the
    // EvalBufferThenRunAffected effect interpreter — the daemon-side call
    // into WorkerMessage.EvalLiveTestFile) — a deliberate, reviewed
    // feature, not silent accretion. Ratchet back DOWN when this file is
    // split; never bump to paper over drift.
    // 3060 -> 3130: a one-time bump for wiring QuarantineLogic into
    // production (sagefs-roast.md: the module was complete, correct, and
    // property-tested with ZERO non-test callers). evaluateQuarantineForBatch
    // now folds every result's flaky classification through
    // QuarantineLogic.evaluate/apply in the same fold that already updates
    // FlakyHistory, and AffectedTestsComputed excludes a quarantined test
    // from the next RunAffectedTests selection — a deliberate, reviewed
    // correctness fix, not silent accretion. Ratchet back DOWN when this
    // file is split; never bump to paper over drift.
    // 3130 -> 3124: requested-run wiring (RunTestsRequested allocates once,
    // TestRunStartedAt, result stamping) was paid for by splitting the
    // dispatch-batch reducer out to SageFsDispatchReduction.fs. Exact size.
    // 3124 -> 3094: SseDedupKey moved to SageFsSseDedup.fs without changing
    // its public module path. Exact post-split size.
    // 3094 -> 3078: the rebuild readiness poll became one await on AwaitReady
    // (RebuildReadyWait.fs). Exact size.
    // 3078 -> 3079: the HostCrashed arm of the session display mapping.
    // 3079 -> 2270: the effect handler (EffectDeps and everything that interprets a SageFsEffect)
    // moved to SageFsEffectHandler.fs. Exact post-split size.
    // 2270 -> 2509: the reducer cases and helpers for result provenance, build confirmation, pause and
    // scope. Deliberate and reviewed; still below the 3079 this file stood at before the effect handler
    // moved out. Ratchet back DOWN when this file is split; never bump to paper over drift.
    "SageFs/SageFsApp.fs", 2509
    "SageFs.Core/AppState.fs", 2000
    // 1850 -> 1860: a one-time bump for the #82 app-output routing (the
    // WorkerAppOutput command + the kept-alive stdout reader) — a deliberate,
    // reviewed feature, not silent accretion. Ratchet back DOWN when
    // SessionManager is split; never bump to paper over drift.
    // 1860 -> 1885: a one-time bump for the fcs-onboarding-trial fix
    // (fcs-trial-a/b/c, 2026-09-22): the warmup-ready poll loop now folds
    // through WarmupSupervision.decidePoll (inactivity + absolute bounds,
    // replacing a flat elapsed check) and awaitWorkerPort's stderr/stdout
    // readers moved off the thread pool onto dedicated threads
    // (runOnDedicatedThread) after a real daemon lockup under 5 concurrent
    // session warmups. Deliberate, reviewed fixes, not silent accretion.
    // Ratchet back DOWN when this file is split; never bump to paper over
    // drift.
    // 1885 -> 1909: the earned-Ready gate. WorkerReportedReady used to
    // commit the worker's self-reported SessionStatus.Ready verbatim, even
    // when the session had asked for projects and resolved none of them
    // (loadedProjects: [], reported Ready anyway). It now classifies the
    // request against what actually resolved (ProjectResolution.fs) before
    // trusting the worker's report, and Faults with the exact request
    // named when nothing resolved — a genuine scratch session (nothing
    // asked for) is untouched. Deliberate, reviewed fix, not silent
    // accretion. Ratchet back DOWN when this file is split; never bump to
    // paper over drift.
    // 1909 -> 1913: the RestartSession command now carries a RestartPlan
    // (what to cover, and whether to rebuild) instead of a bare bool that
    // silently held both decisions. Net +4 after trimming the surrounding
    // comments. Ratchet back DOWN when the file is split; never bump to paper
    // over drift.
    // 1913 -> 1915: RestartPlan gained a THIRD case, `Migrate` — a respawn
    // that keeps a live value instead of rebuilding. The dispatch here is net
    // +1 (`Migrate` shares the no-build arm with `RespawnOnly`, because a
    // rebuild is still what an unscoped migration falls back to) and the rest
    // is the comment naming the distinction. Ratchet back DOWN when the file
    // is split; never bump to paper over drift.
    // 1915 -> 1904: the ready-transport validity helpers moved to
    // ReadyTransport.fs, net of three log lines for the failed-rebuild paths.
    // 1904 -> 1894: the worker stderr queue became StderrTail (TailBuffer.fs).
    // 1894 -> 1780: startWorkerProcess and SpawnedWorker moved to WorkerSpawn.fs
    // (about 130 lines), net of recording every rebuild's outcome on the session.
    // 1780 -> 1740: runOnDedicatedThread (a 25-line comment and its body) and
    // killWorkerPids moved to WorkerSpawn.fs, net of recording each worker's
    // reload verdict (`ReloadObserved`).
    // 1740 -> 1616: the WorkerReady decision moved to WorkerReadyCommit.fs and
    // the ready-poll watchdog and two post-ready fetches to WorkerPostReady.fs.
    // 1616 -> 1672: the supervisor alarm (the callbacks record, createWithAlarm
    // and the loop's beat stamps), net of probeWorkerHealthOnce moving to
    // WorkerPostReady.fs. Still 68 under where the WorkerReady split started.
    // 1672 -> 1676: the health probe hands the worker's reported status to the registry sync
    // (WorkerHealthProbe.syncRegistry), and a HostCrashed arm in the ready-waiter settle.
    // 1676 -> 1599: awaitWorkerPort's reading and timing moved to WorkerStartup.fs (about 180 lines), net of
    // the start-timeout handler, the ledger in the runtime and the first attempt's budget. The decision the
    // handler carries out is StartTimeoutDecision.fs.
    "SageFs.Core/SessionManager.fs", 1599 ]

let lineCount (repoRoot: string) (rel: string) : int =
  File.ReadAllLines(Path.Combine(repoRoot, rel)).Length

/// Blocking calls in test bodies starve the thread pool and make the suite time out. These freeze the current debt
/// at its present level: a new one fails, and converting a test to testTask/testAsync lowers a number.
let blockingCalls : (string * int) list =
  [ "Async.RunSynchronously", 41
    "Thread.Sleep", 37
    ".Wait(", 17
    "GetAwaiter().GetResult()", 24 ]

/// Every test source file except the two that name the patterns as string literals (which would count themselves)
/// and generated bin/obj.
let testSourceFiles (repoRoot: string) : string array =
  Directory.GetFiles(Path.Combine(repoRoot, "SageFs.Tests"), "*.fs", SearchOption.AllDirectories)
  |> Array.filter (fun p ->
    let n = p.Replace('\\', '/')
    not (n.Contains "/bin/")
    && not (n.Contains "/obj/")
    && not (n.EndsWith "ArchitectureTests.fs")
    && not (n.EndsWith "RatchetBudgets.fs"))

let countPattern (repoRoot: string) (pattern: string) : int =
  testSourceFiles repoRoot
  |> Array.sumBy (fun f -> File.ReadAllLines f |> Array.filter (fun line -> line.Contains pattern) |> Array.length)

/// What is over budget, in the words the ratchet prints. Empty when every budget holds.
let violations (repoRoot: string) : string list =
  [ for (rel, budget) in fileSize do
      let lines = lineCount repoRoot rel
      if lines > budget then
        yield sprintf "%s is %d lines, over its %d budget — split it (and ratchet the budget DOWN), never raise the budget" rel lines budget
    for (pattern, budget) in blockingCalls do
      let actual = countPattern repoRoot pattern
      if actual > budget then
        yield sprintf "'%s' now appears on %d test lines, over the %d budget — convert a test to testTask/testAsync + awaitable conditions (and ratchet the budget DOWN), never raise it" pattern actual budget ]
