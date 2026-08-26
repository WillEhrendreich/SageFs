open Expecto
open System
open System.IO
open VerifyExpecto
open VerifyTests

[<EntryPoint>]
let main argv =
  // Run BenchmarkDotNet if --benchmark flag is passed
  let isBenchmark = argv |> Array.exists (fun a -> a = "--benchmark")
  match isBenchmark with
  | true ->
    let benchArgv = argv |> Array.filter (fun a -> a <> "--benchmark")
    SageFs.Tests.Benchmarks.BenchmarkRunner.run benchArgv
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
