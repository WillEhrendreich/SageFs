/// A timeout is a decision with a name and one home, never a bare number at the call site.
/// `Timeouts` (SageFs.Core/Timeouts.fs) holds the product's, `TestTimeouts`
/// (SageFs.Tests/TestTimeouts.fs) holds the ones a test picks on purpose, and each says
/// what the wait is for and why that long. This pins where an inline literal is still left:
/// `TimeSpan.From...` with a number, `Task.Delay n`, `Thread.Sleep n`, `Async.Sleep n`, `.AddSeconds n` and
/// its kin, `WaitForExit n`, `CancelAfter n`, a `...Ms = n` or `timeout = n` binding, and a bare digit-group
/// number written with underscore groups (a millisecond count wearing no unit). Each file has a budget,
/// the budgets only go DOWN, a file that is not listed has a budget of zero, and a file under its
/// budget is stale. When the table is empty there is nowhere a magic duration can hide.
module SageFs.Tests.TimeoutLiteralsTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private literalPattern =
  Regex(@"TimeSpan\.From(Seconds|Milliseconds|Minutes|Hours|Days)\s*\(?\s*[0-9]|Task\.Delay\s*\(?\s*[0-9]|Thread\.Sleep\s*\(?\s*[0-9]|Async\.Sleep\s*\(?\s*[0-9]|\.Add(Milliseconds|Seconds|Minutes|Hours|Days)\s*\(\s*[0-9]|WaitForExit\s*\(\s*[0-9]|CancelAfter\s*\(\s*[0-9]|[A-Za-z](Ms|Millis|Seconds|Secs)\s*=\s*[0-9]|[Tt]imeout\s*=\s*[0-9]|\b[0-9]{1,3}(_000)+L?\b", RegexOptions.Compiled)

/// The files allowed to hold durations: the product's, and the ones a test picks on purpose.
let private central = Set.ofList [ "SageFs.Core/Timeouts.fs"; "SageFs.Tests/TestTimeouts.fs"; "SageFs.Tests/LiveTestingBudgets.fs" ]

/// What each file may still spell out itself. Ratchet down, never up.
let private budgets : (string * int) list =
  [ "SageFs.Core/WorkflowTypes.fs", 1
    "SageFs.Tests/AffordancesMutationTests.fs", 6
    "SageFs.Tests/AffordancesPropertyTests.fs", 2
    "SageFs.Tests/AgentActivityTrackerTests.fs", 3
    "SageFs.Tests/AppRunOrchestrationTests.fs", 3
    "SageFs.Tests/BatchFlusherPropertyTests.fs", 2
    "SageFs.Tests/BatchFlusherTests.fs", 1
    "SageFs.Tests/BinaryFormatTests.fs", 34
    "SageFs.Tests/CohortDogfoodIntegrationTests.fs", 1
    "SageFs.Tests/CohortFastForwardFailedTests.fs", 1
    "SageFs.Tests/CohortGitTests.fs", 1
    "SageFs.Tests/CohortLandingGitAcceptanceTests.fs", 1
    "SageFs.Tests/CohortLandingLoopTests.fs", 1
    "SageFs.Tests/CohortLandingVerifyTests.fs", 2
    "SageFs.Tests/CohortLedgerExportTests.fs", 1
    "SageFs.Tests/CohortLedgerSqliteTests.fs", 1
    "SageFs.Tests/CohortMatrixRenderTests.fs", 1
    "SageFs.Tests/CohortPanelTests.fs", 3
    "SageFs.Tests/CohortProjectEquivalenceTests.fs", 1
    "SageFs.Tests/CohortReaperTests.fs", 1
    "SageFs.Tests/CohortRetentionTests.fs", 3
    "SageFs.Tests/CoverageIntelTests.fs", 1
    "SageFs.Tests/CrossCheckoutOverlapTests.fs", 1
    "SageFs.Tests/CustomPortOwnershipTests.fs", 1
    "SageFs.Tests/DaemonHealthTests.fs", 13
    "SageFs.Tests/DaemonIdleRssTests.fs", 1
    "SageFs.Tests/DaemonIntegrationTests.fs", 8
    "SageFs.Tests/DaemonOwnershipTests.fs", 21
    "SageFs.Tests/DaemonResumeOutcomeTests.fs", 2
    "SageFs.Tests/DaemonRssReturnsToBaselineTests.fs", 2
    "SageFs.Tests/DashboardBrowserRunner.fs", 12
    "SageFs.Tests/DashboardBrowserTests.fs", 88
    "SageFs.Tests/DashboardDiagnosticsTests.fs", 6
    "SageFs.Tests/DashboardDisconnectIndicatorBrowserTests.fs", 12
    "SageFs.Tests/DashboardFailureNarrativesTests.fs", 4
    "SageFs.Tests/DashboardFilmstripTests.fs", 6
    "SageFs.Tests/DashboardHealthTests.fs", 5
    "SageFs.Tests/DashboardHealthVerdictRenderingTests.fs", 11
    "SageFs.Tests/DashboardPanelVisibilityTests.fs", 3
    "SageFs.Tests/DashboardParsingTests.fs", 1
    "SageFs.Tests/DashboardSnapshotTests.fs", 28
    "SageFs.Tests/DashboardSparklineTests.fs", 25
    "SageFs.Tests/DashboardTestIdTests.fs", 3
    "SageFs.Tests/DevReloadTests.fs", 3
    "SageFs.Tests/DiagnosticianTests.fs", 2
    "SageFs.Tests/DogfoodReplTests.fs", 2
    "SageFs.Tests/DstOracleTests.fs", 1
    "SageFs.Tests/ElmLoopResilienceTests.fs", 2
    "SageFs.Tests/ElmLoopStateMachineTests.fs", 2
    "SageFs.Tests/EvalLatencyTraceTests.fs", 6
    "SageFs.Tests/EvalResultSummaryTests.fs", 6
    "SageFs.Tests/EvalStoreTests.fs", 8
    "SageFs.Tests/EvalTimelineTests.fs", 1
    "SageFs.Tests/EventExhaustivenessTests.fs", 3
    "SageFs.Tests/FeatureHookTests.fs", 1
    "SageFs.Tests/FileWatcherTests.fs", 1
    "SageFs.Tests/FlakyClassificationPropertyTests.fs", 1
    "SageFs.Tests/FlakyClassificationTests.fs", 4
    "SageFs.Tests/FrictionClassificationTests.fs", 3
    "SageFs.Tests/FsiHostClientTests.fs", 2
    "SageFs.Tests/FsiOutputParserTests.fs", 8
    "SageFs.Tests/FsiSessionContractTests.fs", 1
    "SageFs.Tests/GateAdmissionTests.fs", 3
    "SageFs.Tests/GcDumpCaptureTests.fs", 9
    "SageFs.Tests/HealthAnomalyTests.fs", 5
    "SageFs.Tests/HealthWatchWiringTests.fs", 2
    "SageFs.Tests/HolderTests.fs", 6
    "SageFs.Tests/HostCoreAdoptionMarkerTests.fs", 1
    "SageFs.Tests/HostCoreAdoptionOrphanSweepTests.fs", 2
    "SageFs.Tests/HotReloadBrowserTests.fs", 17
    "SageFs.Tests/HotReloadStateHarness.fs", 1
    "SageFs.Tests/HttpApiIntegrationTests.fs", 2
    "SageFs.Tests/JsonCoreFilesTests.fs", 1
    "SageFs.Tests/LandingCacheTests.fs", 1
    "SageFs.Tests/LeaseSimTests.fs", 1
    "SageFs.Tests/LiteralEditTests.fs", 3
    "SageFs.Tests/LiveTestWatcherScopeTests.fs", 2
    "SageFs.Tests/LiveTestingBrowserTests.fs", 8
    "SageFs.Tests/LiveTestingCoverageTests.fs", 2
    "SageFs.Tests/LiveTestingTypesTests.fs", 1
    "SageFs.Tests/LocalDataSqliteTests.fs", 1
    "SageFs.Tests/ManifestOwnerTests.fs", 2
    "SageFs.Tests/McpAdapterTests.fs", 4
    "SageFs.Tests/McpStdioBridgeE2ETests.fs", 2
    "SageFs.Tests/McpStdioBridgeSimTests.fs", 1
    "SageFs.Tests/MeasureTests.fs", 2
    "SageFs.Tests/MemoryShedSimTests.fs", 12
    "SageFs.Tests/MemorySupervisorTests.fs", 18
    "SageFs.Tests/MultiAgentCoordinationTests.fs", 1
    "SageFs.Tests/ObservedFrictionResetThrashTests.fs", 1
    "SageFs.Tests/ObservedFrictionResolutionTests.fs", 1
    "SageFs.Tests/OriginGuardOutcomeTests.fs", 1
    "SageFs.Tests/OwnerMonitorTests.fs", 18
    "SageFs.Tests/ParentMonitorTests.fs", 1
    "SageFs.Tests/PatchAnnouncerTests.fs", 1
    "SageFs.Tests/PeriodicManifestSaveDstTests.fs", 2
    "SageFs.Tests/PersistenceComplianceTests.fs", 1
    "SageFs.Tests/ProcessEnvironmentTests.fs", 1
    "SageFs.Tests/Program.fs", 2
    "SageFs.Tests/PureModulesComprehensiveTests.fs", 5
    "SageFs.Tests/ReflectionReadTrackingTests.fs", 1
    "SageFs.Tests/ReloadBroadcastTests.fs", 2
    "SageFs.Tests/ReloadPlanningTests.fs", 6
    "SageFs.Tests/ResilientActorTests.fs", 4
    "SageFs.Tests/RestartJitterTests.fs", 1
    "SageFs.Tests/RestartPolicyTests.fs", 4
    "SageFs.Tests/RetryPolicyTests.fs", 7
    "SageFs.Tests/Round7HardeningTests.fs", 2
    "SageFs.Tests/SafeDirectoryWalkTests.fs", 1
    "SageFs.Tests/SageFsConfigTests.fs", 1
    "SageFs.Tests/SageFsIOTests.fs", 7
    "SageFs.Tests/SessionCreationTests.fs", 1
    "SageFs.Tests/SessionLifecycleMutationTests.fs", 10
    "SageFs.Tests/SessionManagerAdmissionTests.fs", 2
    "SageFs.Tests/SessionPredicatesAndUiTests.fs", 1
    "SageFs.Tests/ShutdownLifecycleTests.fs", 1
    "SageFs.Tests/SimulationTests.fs", 1
    "SageFs.Tests/StreamingProxyTests.fs", 4
    "SageFs.Tests/SyntaxHighlightTests.fs", 1
    "SageFs.Tests/TimeoutsTests.fs", 10
    "SageFs.Tests/TweakSimDstTests.fs", 1
    "SageFs.Tests/VscodeCommandProofTests.fs", 2
    "SageFs.Tests/VscodeExtensionTests.fs", 13
    "SageFs.Tests/WorkerLogFileTests.fs", 1
    "SageFs.Tests/WorkflowSwitchTests.fs", 1
    "SageFs.Tests/WorkflowTransitionPropertyTests.fs", 1
    "SageFs.Tests/WorkflowTypesMutationTests.fs", 1 ]

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
