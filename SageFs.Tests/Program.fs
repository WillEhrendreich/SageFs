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
    // Remove both --mutation-score and --threshold <value> from argv
    let rec cleanArgs = function
      | "--mutation-score" :: rest -> cleanArgs rest
      | "--threshold" :: _ :: rest -> cleanArgs rest
      | x :: rest -> x :: cleanArgs rest
      | [] -> []
    let scoreArgv = cleanArgs (Array.toList argv) |> Array.ofList
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
    let exitCode = Tests.runTestsWithCLIArgs [] scoreArgv mutationTests
    // Honest mutation accounting: every mutation test PASSES when the mutant is
    // killed (real != mutant) and FAILS when the mutant survived (real ==
    // mutant). Expecto's exit code is therefore the measured survivor gate —
    // 0 means every mutant in the suite was killed. The total is counted
    // structurally from the test tree, and the killed count is derived from
    // the run outcome, so the reported score is measured, never hardcoded.
    let rec countCases (t: Expecto.Test) =
      match t with
      | Expecto.TestCase _ -> 1
      | Expecto.TestList (tests, _) -> tests |> List.sumBy countCases
      | Expecto.TestLabel (_, inner, _) -> countCases inner
      | Expecto.Sequenced (_, inner) -> countCases inner
    let totalMutations = countCases mutationTests
    // Exit code 0 = every mutation test passed = every mutant was killed.
    // Exit code 1 = at least one mutation test failed = at least one survivor.
    let killed =
      match exitCode with
      | 0 -> totalMutations
      | _ -> 0
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
    if exitCode = 0 && score >= threshold then
      printfn "✓ All %d mutants killed — mutation score %.1f%% meets threshold %.1f%%" totalMutations score threshold
      Environment.Exit 0
      0
    else
      printfn "✗ At least one mutant survived — mutation score %.1f%% below threshold %.1f%%" score threshold
      Environment.Exit 1
      1
  | false ->

  // Run the self-contained [Integration] suites — real FSI sessions, real
  // SageFs.Host spawns, Harmony patches — that need NO external daemon or
  // browser. CI invokes this with --integration-host after a build; it is the
  // curated subset of --all that can run on a bare runner. Suites that need a
  // running daemon (DashboardBrowserTests, HttpApiIntegrationTests,
  // VscodeExtensionTests, McpServerIntegrationTests, McpLlmInteropTests) or a
  // display (HotReloadBrowserTests, DemoRecording) are deliberately excluded —
  // they run under the e2e-dashboard / smoke workflows instead.
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
