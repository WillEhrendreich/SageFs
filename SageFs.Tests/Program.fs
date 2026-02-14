open Expecto
open System
open System.IO
open VerifyExpecto
open VerifyTests

[<EntryPoint>]
let main argv =
  let configureVerify () =
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

  let filteredArgv =
    argv |> Array.filter (fun a -> a <> "--all" && a <> "--integration")

  if includeAll then
    Tests.runTestsInAssemblyWithCLIArgs [] filteredArgv
  else
    // Default: exclude [Integration] tests (FSI actor startup is ~8-10s each)
    // Run with --all or --integration to include them
    let tests =
      Impl.testFromThisAssembly ()
      |> Option.defaultValue (testList "empty" [])
      |> Test.filter
        defaultConfig.joinWith.asString
        (fun z ->
          let name = defaultConfig.joinWith.format z
          not (name.Contains "[Integration]"))
    Tests.runTestsWithCLIArgs [] filteredArgv tests
