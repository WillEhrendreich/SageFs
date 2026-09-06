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
  // Run BenchmarkDotNet if --benchmark flag is passed
  let isBenchmark = argv |> Array.exists (fun a -> a = "--benchmark")
  match isBenchmark with
  | true ->
    let benchArgv = argv |> Array.filter (fun a -> a <> "--benchmark")
    SageFs.Tests.Benchmarks.BenchmarkRunner.run benchArgv
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

  // Run the self-contained [Integration] suites — real FSI sessions, real
  // SageFs.Host spawns, Harmony patches — that need NO external daemon or
  // browser. CI invokes this with --integration-host after a build; it is the
  // curated subset of --all that can run on a bare runner. Suites that need a
  // running daemon or a display are deliberately excluded — the dashboard
  // browser journeys run under --integration-browser (DashboardBrowserRunner
  // owns the daemon lifecycle), and the remaining daemon/display suites run
  // under the smoke workflow.
  let isIntegrationHost = argv |> Array.exists (fun a -> a = "--integration-host")
  match isIntegrationHost with
  | true ->
    let hostArgv = argv |> Array.filter (fun a -> a <> "--integration-host")
    let hostIntegrationTests =
      testList "Integration (host)" [
        SageFs.Tests.WebAppHotReloadVerificationTests.webAppHotReloadVerificationTests
        SageFs.Tests.EvalCancellationTests.evalCancellationTests
        SageFs.Tests.EvalActorResilienceTests.evalActorResilienceTests
        SageFs.Tests.HarmonyCanaryTests.allTests
        SageFs.Tests.MethodPatcherTests.tests
        SageFs.Tests.FsiCrossSubmissionTests.allTests
        SageFs.Tests.DaemonStateChangeContractTests.daemonStateChangeContractTests
      ]
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

  // Run the [Integration] VS Code DoD journeys (HR-VSC-E2E, LT-VSC-E2E):
  // REAL VS Code + the SageFs extension against a real daemon with a session
  // on the FromCSharp sample, asserting real client state (hot-reload tree,
  // live-testing status bar). CI invokes this with --integration-vsc after a
  // Release build + installing the extension VSIX into the test profile.
  let isIntegrationVsc = argv |> Array.exists (fun a -> a = "--integration-vsc")
  match isIntegrationVsc with
  | true ->
    let result = SageFs.Tests.DashboardBrowserRunner.runVscodeDoDJourneys argv
    Environment.Exit result
    result
  | false ->

  let configureVerify () =
    VerifierSettings.DisableRequireUniquePrefix()
    // Global line-ending scrub: normalize CRLF to LF on BOTH the received and
    // verified content so snapshot comparisons are immune to git autocrlf /
    // editor line-ending differences. Verified files are committed with LF;
    // on Windows checkouts they may be materialized as CRLF — without this,
    // every text snapshot fails with a spurious line-ending mismatch.
    VerifierSettings.AddScrubber(fun builder ->
      // Verify passes the content via StringBuilder; normalize \r\n → \n.
      builder.Replace("\r\n", "\n") |> ignore)
    let isCI =
      not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
      || not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
      || not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD")))

    let snapshotsDir =
      if isCI then
        let assemblyDir =
          Path.GetDirectoryName(
            Reflection.Assembly.GetExecutingAssembly().Location
          )
        Path.Combine(assemblyDir, "snapshots")
      else
        Path.Combine(__SOURCE_DIRECTORY__, "snapshots")

    if not (Directory.Exists snapshotsDir) then
      Directory.CreateDirectory snapshotsDir |> ignore

    Verifier.DerivePathInfo(fun _ _ typeName methodName ->
      PathInfo(directory = snapshotsDir, typeName = typeName, methodName = methodName))

  configureVerify ()

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
      // Default: exclude [Integration] and [Benchmark] tests.
      // Run with --all or --integration to include them.
      let tests =
        Impl.testFromThisAssembly ()
        |> Option.defaultValue (testList "empty" [])
        |> Test.filter
          defaultConfig.joinWith.asString
          (fun z ->
            let name = defaultConfig.joinWith.format z
            not (name.Contains "[Integration]")
            && not (name.Contains "[Benchmark]"))
      Tests.runTestsWithCLIArgs [] filteredArgv tests

  // Force exit: Kestrel ConsoleLifetime and other test infrastructure may leave
  // foreground threads alive after all tests complete, preventing clean shutdown.
  Environment.Exit result
  result
