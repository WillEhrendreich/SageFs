open Expecto
open System
open System.IO
open VerifyExpecto
open VerifyTests

[<EntryPoint>]
let main argv =
  let isReleaseReadiness = argv |> Array.exists (fun arg -> arg = "--release-readiness")
  match isReleaseReadiness with
  | true ->
    let errors =
      System.IO.File.ReadAllText SageFs.Tests.DefinitionOfDoneTests.matrixPath
      |> SageFs.Tests.DefinitionOfDoneTests.validateMatrix true (DateOnly.FromDateTime DateTime.UtcNow)
    match errors with
    | [] -> 0
    | failures ->
      failures |> List.iter (eprintfn "QUALITY GATE: %s")
      1
  | false ->

  // Run mutation score report if --mutation-score flag is passed
  let isMutationScore = argv |> Array.exists (fun a -> a = "--mutation-score")
  match isMutationScore with
  | true ->
    // Remove the --mutation-score / --threshold args from the argv used for
    // per-mutant filters.
    let threshold =
      let rec parse = function
        | "--threshold" :: value :: _ ->
          match Double.TryParse(value) with
          | true, v -> Some v
          | _ -> None
        | _ :: rest -> parse rest
        | [] -> None
      parse (Array.toList argv)
      |> Option.defaultValue 0.0
    let mutationTests =
      testList "Mutation Score" [
        HotReloadStateMutationTests.hotReloadMutationTests
        ResultExMutationTests.resultExMutationTests
        SageFsErrorMutationTests.sageFsErrorMutationTests
        SessionLifecycleMutationTests.sessionLifecycleMutationTests
        CoverageViewMutationTests.coverageViewMutationTests
        CoverageViewProjectMutationTests.coverageViewProjectMutationTests
      ]
    // Honest mutation accounting: each mutant is one test case that PASSES only
    // when the mutant is killed (real <> mutant). Deriving the score from the
    // run's aggregate exit code makes it binary theater — one survivor zeroes
    // the score and the threshold is inert. Instead, run each mutant case
    // individually and tally killed/survived per mutant, so the reported score
    // and threshold are measured, never guessed.
    let rec flattenTests (t: Expecto.Test) : (string * Expecto.Test) list =
      match t with
      | Expecto.TestLabel (name, inner, _) ->
        match inner with
        | Expecto.TestCase _ -> [ name, t ]
        | _ -> flattenTests inner
      | Expecto.TestCase _ -> [ "", t ]
      | Expecto.TestList (tests, _) -> tests |> List.collect flattenTests
      | Expecto.Sequenced (_, inner) -> flattenTests inner
    let mutants = flattenTests mutationTests
    let totalMutations = mutants.Length
    let outcomes =
      mutants
      |> List.map (fun (caseName, caseTest) ->
          // Run the single mutant in isolation; Expecto's ``filter-test-case``
          // is a substring match and the case names are unique.
          let exitCode = Tests.runTestsWithCLIArgs [] [| "--filter-test-case"; caseName |] caseTest
          // A mutant is killed only when ITS case passes (exit 0). Any other
          // exit code (including 2 = no test matched, the case never ran) is a
          // survivor — fail-closed: an unverified mutant cannot inflate the
          // score.
          caseName, (exitCode = 0))
    let killed = outcomes |> List.filter snd |> List.length
    let survivors =
      outcomes |> List.filter (fun (_, isKilled) -> not isKilled) |> List.map fst
    let score = (float killed / float totalMutations) * 100.0
    printfn ""
    printfn "═══════════════════════════════════════════════════════════════"
    printfn "  MUTATION SCORE REPORT"
    printfn "═══════════════════════════════════════════════════════════════"
    printfn "  Killed (passed):  %d" killed
    printfn "  Total mutations:  %d" totalMutations
    printfn "  ────────────────────────────────────────────"
    printfn "  Mutation score:    %.1f%%" score
    printfn "  Threshold:         %.1f%%" threshold
    printfn "═══════════════════════════════════════════════════════════════"
    if survivors.Length > 0 then
      printfn "✗ Surviving mutants:"
      survivors |> List.iter (printfn "    - %s")
    if survivors.Length = 0 && score >= threshold then
      printfn "✓ All %d mutants killed — mutation score %.1f%% meets threshold %.1f%%" totalMutations score threshold
      Environment.Exit 0
      0
    else
      printfn "✗ %d mutants survived — mutation score %.1f%% below threshold %.1f%%" survivors.Length score threshold
      Environment.Exit 1
      1
  | false ->

  // Run EVERY self-contained [Integration] suite — real FSI sessions, real
  // SageFs.Host spawns, Harmony detours, real daemons on reserved ports with
  // isolated SAGEFS_DATA_DIRs, the HTTP API against the samples. The set is
  // structural: every suite registered as `Integration.Host`
  // (TestInfrastructure.Integration), so a new host suite runs here by
  // construction. Suites that need a browser or VS Code are registered against
  // their own entry points (--integration-browser/-hr/-lt/-vsc below).
  let isIntegrationHost = argv |> Array.exists (fun a -> a = "--integration-host")
  match isIntegrationHost with
  | true ->
    let hostArgv = argv |> Array.filter (fun a -> a <> "--integration-host")
    // Sequenced: these suites share process-global state — the one
    // TestInfrastructure.globalActorResult FSI actor (which the reset suites
    // reset), Harmony patches, environment variables. Run in parallel, a reset
    // in one suite lands mid-eval in another (observed: "State: WarmingUp",
    // "Expected Active phase, got Initializing" in suites that pass alone).
    let hostIntegrationTests =
      testSequenced (
        testList "Integration (host)" (SageFs.Tests.TestInfrastructure.Integration.hostSuites ()))
    let result = Tests.runTestsWithCLIArgs [] hostArgv hostIntegrationTests
    Environment.Exit result
    result
  | false ->

  // Run the [Integration] Dashboard browser journeys (Playwright.NET) with the
  // daemon lifecycle owned in-process: boot an isolated daemon on reserved
  // ports, create a Ready session on the WebappDatastar sample, run the
  // journeys, tear down. CI invokes this with --integration-browser after a
  // Release build (plus `playwright install chromium` for the .NET driver).
  let isIntegrationBrowser = argv |> Array.exists (fun a -> a = "--integration-browser")
  match isIntegrationBrowser with
  | true ->
    let result = SageFs.Tests.DashboardBrowserRunner.runBrowserJourneys argv
    Environment.Exit result
    result
  | false ->

  // Run the [Integration] hot-reload dashboard browser journeys (Playwright.NET)
  // against a WebLive session on the WebAppFixture: real file save -> the SAME
  // running app serves the new value, observed from the dashboard page. CI
  // invokes this with --integration-hr after a Release build (same shape as
  // --integration-host / --integration-browser).
  let isIntegrationHr = argv |> Array.exists (fun a -> a = "--integration-hr")
  match isIntegrationHr with
  | true ->
    let result = SageFs.Tests.DashboardBrowserRunner.runHotReloadBrowserJourneys argv
    Environment.Exit result
    result
  | false ->

  // Run the [Integration] live-testing dashboard browser journeys
  // (Playwright.NET) against a session on the FromCSharp sample: enable live
  // testing through the panel -> 11 tests discovered/passing -> edit Hello.fs
  // on disk -> the panel shows the failing test -> revert -> all green again.
  // CI invokes this with --integration-lt after a Release build.
  let isIntegrationLt = argv |> Array.exists (fun a -> a = "--integration-lt")
  match isIntegrationLt with
  | true ->
    let result = SageFs.Tests.DashboardBrowserRunner.runLiveTestingBrowserJourneys argv
    Environment.Exit result
    result
  | false ->

  // The CDP-driven VS Code DoD journeys (HR-VSC-E2E, LT-VSC-E2E) that used
  // to run here via --integration-vsc were retired 2026-09-12 — see issue
  // #133 and VscodeCommandProofTests.fs's header. That file's tests are
  // registered under Integration.hostList and run via --integration-host
  // like every other real-daemon [Integration] suite; no separate CLI flag
  // or runner is needed.

  // Harness-root Verify configuration (snapshot directory, unique-prefix
  // setting, CRLF scrubber) — owned by TestInfrastructure.Snapshots, never by
  // individual snapshot test files.
  SageFs.Tests.TestInfrastructure.Snapshots.configure ()

  let includeAll =
    argv |> Array.exists (fun a -> a = "--all" || a = "--integration")

  let complianceOnly =
    argv |> Array.exists (fun a -> a = "--compliance")

  let filteredArgv =
    argv |> Array.filter (fun a -> a <> "--all" && a <> "--integration" && a <> "--compliance")

  let result =
    match complianceOnly with
    | true ->
      // Run only the compliance suite (behavioral contracts)
      Tests.runTestsWithCLIArgs [] filteredArgv SageFs.Tests.ComplianceSuite.complianceSuite
    | false ->
    match includeAll with
    | true ->
      Tests.runTestsInAssemblyWithCLIArgs [] filteredArgv
    | false ->
      // Default: exclude the registered [Integration] suites (structurally, by
      // the identity of their test bodies — see TestInfrastructure.Integration).
      // Run with --all or --integration to include them.
      let tests =
        Impl.testFromThisAssembly ()
        |> Option.defaultValue (testList "empty" [])
        |> SageFs.Tests.TestInfrastructure.Integration.excludeRegistered
        // Exclude [Benchmark]-tagged wall-clock perf tests from the fast default
        // suite — their p95/latency budgets flake under load (roast-5 §12). They
        // run on demand via --all/--benchmark, not on every default run.
        |> Test.filter
          defaultConfig.joinWith.asString
          (fun z -> not ((defaultConfig.joinWith.format z).Contains "[Benchmark]"))
      // Fail closed: an "[Integration]"-tagged test that bypassed the registry
      // would silently join the fast default run.
      match SageFs.Tests.TestInfrastructure.Integration.unregisteredTagged tests with
      | [] -> Tests.runTestsWithCLIArgs [] filteredArgv tests
      | leaked ->
        eprintfn "Integration tests are not registered — build them with Integration.hostList/hostCase or Integration.register in TestInfrastructure:"
        leaked |> List.iter (eprintfn "  %s")
        1

  // Force exit: Kestrel ConsoleLifetime and other test infrastructure may leave
  // foreground threads alive after all tests complete, preventing clean shutdown.
  Environment.Exit result
  result
