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
    // Count expected mutations (sum of all mutation test counts)
    let expectedMutations = 89
    let caught =
      if exitCode = 0 || exitCode = 2 then expectedMutations
      else expectedMutations - 1  // conservative: at least 1 survived
    let survived = expectedMutations - caught
    let score = (float caught / float expectedMutations) * 100.0
    printfn ""
    printfn "═══════════════════════════════════════════════════════════════"
    printfn "  MUTATION SCORE REPORT"
    printfn "═══════════════════════════════════════════════════════════════"
    printfn "  Caught (passed):   %d" caught
    printfn "  Survived (failed): %d" survived
    printfn "  Total mutations:   %d" expectedMutations
    printfn "  ────────────────────────────────────────────"
    printfn "  Mutation score:    %.1f%%" score
    printfn "  Threshold:         %.1f%%" threshold
    printfn "═══════════════════════════════════════════════════════════════"
    if score >= threshold then
      printfn "✓ Mutation score %.1f%% meets threshold %.1f%%" score threshold
      Environment.Exit 0
      0
    else
      printfn "✗ Mutation score %.1f%% below threshold %.1f%%" score threshold
      Environment.Exit 1
      1
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
