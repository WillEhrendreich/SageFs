/// A timeout is a decision with a name and one home, never a bare number at the call site.
/// `Timeouts` (SageFs.Core/Timeouts.fs) holds the product's, `TestTimeouts`
/// (SageFs.Tests/TestInfrastructure.fs) holds the ones a test picks on purpose, and each says
/// what the wait is for and why that long. This pins where an inline literal is still left:
/// `TimeSpan.From...` with a number, `Task.Delay n` and `Thread.Sleep n`. Each file has a budget,
/// the budgets only go DOWN, a file that is not listed has a budget of zero, and a file under its
/// budget is stale. When the table is empty there is nowhere a magic duration can hide.
module SageFs.Tests.TimeoutLiteralsTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private literalPattern =
  Regex(@"TimeSpan\.From(Seconds|Milliseconds|Minutes|Hours|Days)\s*\(?\s*[0-9]|Task\.Delay\s*\(?\s*[0-9]|Thread\.Sleep\s*\(?\s*[0-9]", RegexOptions.Compiled)

/// The one file allowed to hold the product's durations.
let private central = Set.ofList [ "SageFs.Core/Timeouts.fs" ]

/// What each file may still spell out itself. Ratchet down, never up.
let private budgets : (string * int) list =
  [ "SageFs.Core/Cohort.fs", 2
    "SageFs.Core/DaemonOwnership.fs", 2
    "SageFs.Core/ExpensiveWorkLease.fs", 9
    "SageFs.Core/Features/ObservedFrictionTypes.fs", 3
    "SageFs.Core/HostCoreAdoption.fs", 1
    "SageFs.Core/MemorySupervisor.fs", 1
    "SageFs.Core/Middleware/ValueReads.fs", 1
    "SageFs.Core/OwnerMonitor.fs", 1
    "SageFs.Core/SettingsCatalog.fs", 1
    "SageFs.Host/AppRunner.fs", 2
    "SageFs.Host/WorkerHttpTransport.fs", 2
    "SageFs.Host/WorkerMain.fs", 1
    "SageFs.Tests/AffordancesMutationTests.fs", 12
    "SageFs.Tests/AffordancesPropertyTests.fs", 1
    "SageFs.Tests/AgentActivityTrackerTests.fs", 3
    "SageFs.Tests/AppRunOrchestrationTests.fs", 4
    "SageFs.Tests/AppRunnerTests.fs", 2
    "SageFs.Tests/BatchFlusherTests.fs", 1
    "SageFs.Tests/BinaryFormatTests.fs", 8
    "SageFs.Tests/BuildPreflightBoundaryTests.fs", 1
    "SageFs.Tests/CohortDogfoodIntegrationTests.fs", 1
    "SageFs.Tests/CohortLandingGitAcceptanceTests.fs", 2
    "SageFs.Tests/CohortMcpToolsIntegrationTests.fs", 1
    "SageFs.Tests/CohortReaperTests.fs", 3
    "SageFs.Tests/CohortRetentionTests.fs", 4
    "SageFs.Tests/CoverageIntelTests.fs", 1
    "SageFs.Tests/CrossCheckoutOverlapTests.fs", 2
    "SageFs.Tests/CustomPortOwnershipTests.fs", 2
    "SageFs.Tests/DaemonHealthTests.fs", 13
    "SageFs.Tests/DaemonIdleRssTests.fs", 3
    "SageFs.Tests/DaemonIntegrationTests.fs", 1
    "SageFs.Tests/DaemonOwnershipTests.fs", 30
    "SageFs.Tests/DaemonResumeOutcomeTests.fs", 5
    "SageFs.Tests/DaemonRssReturnsToBaselineTests.fs", 2
    "SageFs.Tests/DashboardBrowserRunner.fs", 12
    "SageFs.Tests/DashboardBrowserTests.fs", 10
    "SageFs.Tests/DashboardDisconnectIndicatorBrowserTests.fs", 3
    "SageFs.Tests/DashboardFailureNarrativesTests.fs", 4
    "SageFs.Tests/DashboardHealthTests.fs", 16
    "SageFs.Tests/DevReloadTests.fs", 5
    "SageFs.Tests/DiagnosticianTests.fs", 1
    "SageFs.Tests/DiagnosticsToolTests.fs", 1
    "SageFs.Tests/DstCoverageTests.fs", 7
    "SageFs.Tests/DstOracleTests.fs", 5
    "SageFs.Tests/DstSeedCorpusTests.fs", 1
    "SageFs.Tests/ElmDaemonTests.fs", 2
    "SageFs.Tests/ElmLoopResilienceTests.fs", 4
    "SageFs.Tests/ElmLoopStateMachineTests.fs", 2
    "SageFs.Tests/EntryProbeTests.fs", 4
    "SageFs.Tests/EvalLatencyTraceTests.fs", 3
    "SageFs.Tests/EventExhaustivenessTests.fs", 2
    "SageFs.Tests/FalcoTests.fs", 1
    "SageFs.Tests/FlakyClassificationPropertyTests.fs", 2
    "SageFs.Tests/FlakyClassificationTests.fs", 5
    "SageFs.Tests/FsiHostClientTests.fs", 6
    "SageFs.Tests/GateAdmissionTests.fs", 5
    "SageFs.Tests/GcDumpCaptureTests.fs", 3
    "SageFs.Tests/HealthSignalWiringTests.fs", 2
    "SageFs.Tests/HealthWatchWiringTests.fs", 2
    "SageFs.Tests/HotReloadBrowserTests.fs", 11
    "SageFs.Tests/HotReloadStateHarness.fs", 9
    "SageFs.Tests/HotReloadStateOutcomeTests.fs", 1
    "SageFs.Tests/HttpApiIntegrationTests.fs", 23
    "SageFs.Tests/HttpOriginGuardTests.fs", 1
    "SageFs.Tests/InlinedCalleeOutcomeTests.fs", 1
    "SageFs.Tests/JsonCoreFilesTests.fs", 2
    "SageFs.Tests/LandingCacheTests.fs", 2
    "SageFs.Tests/LifecyclePropertyTests.fs", 1
    "SageFs.Tests/LiveBindingsAdaptiveTests.fs", 4
    "SageFs.Tests/LiveTestActivityTests.fs", 2
    "SageFs.Tests/LiveTestActivityWiringTests.fs", 2
    "SageFs.Tests/LiveTestWatcherAtomicSaveTests.fs", 2
    "SageFs.Tests/LiveTestWatcherScopeTests.fs", 4
    "SageFs.Tests/LiveTestingBrowserTests.fs", 2
    "SageFs.Tests/LiveTestingBudgets.fs", 3
    "SageFs.Tests/LiveTestingCoreTests.fs", 3
    "SageFs.Tests/LiveTestingCoverageTests.fs", 9
    "SageFs.Tests/LiveTestingCycleTests.fs", 48
    "SageFs.Tests/LiveTestingDecompositionTests.fs", 4
    "SageFs.Tests/LiveTestingElmTests.fs", 14
    "SageFs.Tests/LiveTestingGraphTests.fs", 20
    "SageFs.Tests/LiveTestingTypesTests.fs", 2
    "SageFs.Tests/LiveValuesPullTests.fs", 1
    "SageFs.Tests/LocalDataRetentionTests.fs", 3
    "SageFs.Tests/LocalDataSqliteTests.fs", 3
    "SageFs.Tests/McpAdapterTests.fs", 8
    "SageFs.Tests/McpAppRunOutcomeTests.fs", 6
    "SageFs.Tests/McpHardResetRebuildTests.fs", 1
    "SageFs.Tests/McpJsonWireTests.fs", 1
    "SageFs.Tests/McpRunTestsTests.fs", 3
    "SageFs.Tests/McpStdioBridgeE2ETests.fs", 2
    "SageFs.Tests/McpToolExecutionTests.fs", 1
    "SageFs.Tests/McpToolOutcomeTests.fs", 6
    "SageFs.Tests/MemorySupervisorTests.fs", 5
    "SageFs.Tests/MultiAgentCoordinationTests.fs", 4
    "SageFs.Tests/MultiClientOutcomeTests.fs", 3
    "SageFs.Tests/MultiTargetReferenceOutcomeTests.fs", 3
    "SageFs.Tests/OriginGuardOutcomeTests.fs", 1
    "SageFs.Tests/OwnerMonitorTests.fs", 7
    "SageFs.Tests/ParentMonitorTests.fs", 1
    "SageFs.Tests/PatchAnnouncerTests.fs", 3
    "SageFs.Tests/PersistenceComplianceTests.fs", 1
    "SageFs.Tests/PureModulesComprehensiveTests.fs", 13
    "SageFs.Tests/QuarantineTests.fs", 1
    "SageFs.Tests/ReflectionReadTrackingTests.fs", 5
    "SageFs.Tests/ReflectionReadsTests.fs", 1
    "SageFs.Tests/ReloadBroadcastTests.fs", 2
    "SageFs.Tests/ReloadPlanningTests.fs", 2
    "SageFs.Tests/RestartJitterTests.fs", 4
    "SageFs.Tests/RestartPolicyTests.fs", 10
    "SageFs.Tests/RunAppSaveOutcomeTests.fs", 6
    "SageFs.Tests/SageFsAppTests.fs", 13
    "SageFs.Tests/SageFsEffectHandlerTests.fs", 5
    "SageFs.Tests/SageFsIOTests.fs", 3
    "SageFs.Tests/SessionActivityTouchTests.fs", 3
    "SageFs.Tests/SessionBindingIsolationOutcomeTests.fs", 1
    "SageFs.Tests/SessionDisplayMutationTests.fs", 3
    "SageFs.Tests/SessionIsolationTests.fs", 7
    "SageFs.Tests/SessionLifecycleMutationTests.fs", 8
    "SageFs.Tests/SessionManagerSpawnFirstRestartTests.fs", 2
    "SageFs.Tests/SessionManagerSupervisorAlarmTests.fs", 3
    "SageFs.Tests/SessionOperationsMutationTests.fs", 2
    "SageFs.Tests/SessionPredicatesAndUiTests.fs", 1
    "SageFs.Tests/ShutdownLifecycleTests.fs", 7
    "SageFs.Tests/SimulationTests.fs", 16
    "SageFs.Tests/SseContractComplianceTests.fs", 4
    "SageFs.Tests/SseDedupKeyTests.fs", 4
    "SageFs.Tests/SseWriterTests.fs", 4
    "SageFs.Tests/StatusWaitTests.fs", 3
    "SageFs.Tests/StreamingProxyTests.fs", 15
    "SageFs.Tests/TestExecutionReportTests.fs", 3
    "SageFs.Tests/TestInfrastructure.fs", 4
    "SageFs.Tests/TestNarrationTests.fs", 13
    "SageFs.Tests/TestRunExplainerTests.fs", 5
    "SageFs.Tests/TestRunKeyTests.fs", 1
    "SageFs.Tests/TestSummaryCompletenessTests.fs", 1
    "SageFs.Tests/TestsPaneTests.fs", 2
    "SageFs.Tests/TimeoutsTests.fs", 16
    "SageFs.Tests/UpdateCheckTests.fs", 7
    "SageFs.Tests/VscodeCommandProofTests.fs", 7
    "SageFs.Tests/VscodeExtensionTests.fs", 8
    "SageFs.Tests/WarmupInitBoundaryTests.fs", 1
    "SageFs.Tests/WarmupSimTests.fs", 4
    "SageFs.Tests/WarmupSupervisionTests.fs", 27
    "SageFs.Tests/WatchdogMutationTests.fs", 2
    "SageFs.Tests/WatchdogTests.fs", 6
    "SageFs.Tests/WebAppHotReloadVerificationTests.fs", 12
    "SageFs.Tests/WorkerHttpGuardTests.fs", 1
    "SageFs.Tests/WorkerHttpTransportTests.fs", 1
    "SageFs.Tests/WorkerLogFileTests.fs", 1
    "SageFs.Tests/WorkerProtocolTests.fs", 2
    "SageFs.Tests/WorkerStderrCaptureTests.fs", 1
    "SageFs.Tests/WorkflowScenarioTests.fs", 1
    "SageFs.Tests/WorkflowSwitchTests.fs", 1
    "SageFs.Tests/WorkflowTransitionPropertyTests.fs", 1
    "SageFs.Tests/WorkflowTypesMutationTests.fs", 1
    "SageFs/CohortGit.fs", 3
    "SageFs/CohortLandingVerify.fs", 1
    "SageFs/DaemonLogging.fs", 1
    "SageFs/DaemonMode.fs", 13
    "SageFs/Dashboard.fs", 2
    "SageFs/EnvCheck.fs", 1
    "SageFs/JupyterTransport.fs", 2
    "SageFs/Mcp.fs", 2
    "SageFs/McpServer.fs", 1
    "SageFs/McpStdioBridge.fs", 1
    "SageFs/Program.fs", 1 ]

/// Every source file with its count of inline timeout literals, except the central module.
let private actual : (string * int) list =
  [ for project in [ "SageFs"; "SageFs.Core"; "SageFs.Host"; "SageFs.Tests" ] do
      let dir = Path.Combine(repoRoot, project)
      match Directory.Exists dir with
      | false -> ()
      | true ->
        for file in Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories) do
          let relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/')
          let skipped =
            relative.Contains "/obj/" || relative.Contains "/bin/" || relative.Contains "/fixtures/" || central.Contains relative
          match skipped with
          | true -> ()
          | false ->
            match literalPattern.Matches(File.ReadAllText file).Count with
            | 0 -> ()
            | n -> yield relative, n ]

[<Tests>]
let tests =
  testList "Timeout literals" [

    testCase "WHY — no file spells out more durations than its budget, so the places a magic timeout can hide only shrink" <| fun _ ->
      let allowed = Map.ofList budgets
      let over =
        actual
        |> List.choose (fun (file, count) ->
          let budget = Map.tryFind file allowed |> Option.defaultValue 0
          match count > budget with
          | true -> Some (sprintf "%s has %d, budget %d" file count budget)
          | false -> None)
      over |> Expect.isEmpty "every file is within its budget (a new duration goes in Timeouts, or TestTimeouts for a test, with a name and a reason)"

    testCase "WHY — a budget above the file's real count is stale, so room that was already won back cannot be spent again" <| fun _ ->
      let counts = Map.ofList actual
      let stale =
        budgets
        |> List.choose (fun (file, budget) ->
          let count = Map.tryFind file counts |> Option.defaultValue 0
          match count < budget with
          | true -> Some (sprintf "%s has %d but its budget is %d: lower it" file count budget)
          | false -> None)
      stale |> Expect.isEmpty "every budget equals the file's current count"
  ]
