module SageFs.Tests.WorkerMainTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

let sampleTestCase id fullName displayName =
  { Id = TestId.TestId id
    FullName = fullName
    DisplayName = displayName
    Origin = TestOrigin.ReflectionOnly
    Labels = []
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit }

let expectoProvider =
  ProviderDescription.AttributeBased
    { Name = TestFramework.Expecto
      TestAttributes = [ "Tests" ]
      AssemblyMarker = "Expecto" }

[<Tests>]
let workerMainTests =
  testList "WorkerMain" [
    testCase "initial discovery aggregation removes duplicate discovered tests by TestId"
    <| fun _ ->
      let duplicate =
        sampleTestCase "dup" "Sample.Tests.add infers int" "add infers int"
      let unique =
        sampleTestCase "unique" "Sample.Tests.subtract infers int" "subtract infers int"

      let first =
        { LiveTestHookResult.empty with
            DetectedProviders = [ expectoProvider ]
            DiscoveredTests = [| duplicate |] }

      let second =
        { LiveTestHookResult.empty with
            DetectedProviders = [ expectoProvider ]
            DiscoveredTests = [| duplicate; unique |] }

      let tests, providers =
        SageFs.Server.WorkerMain.mergeInitialDiscoveryResults [| first; second |]

      tests.Length |> Expect.equal "should collapse duplicate test identities" 2
      providers.Length |> Expect.equal "should keep one provider entry" 1
      tests |> Array.map (fun test -> test.Id) |> Array.distinct |> Array.length
      |> Expect.equal "should keep unique test ids only" 2

    testCase "WHY — worker quarantine preserves Mono.Cecil because coverage instrumentation executes before project dependency resolution" <| fun _ ->
      [ "Mono.Cecil.dll"
        "Mono.Cecil.Rocks.dll"
        "Mono.Cecil.Mdb.dll"
        "Mono.Cecil.Pdb.dll" ]
      |> List.iter (fun assemblyName ->
        SageFs.Server.WorkerMain.shouldQuarantineAssembly assemblyName
        |> Expect.isFalse (sprintf "%s must remain in the worker probing root" assemblyName))

    testCase "WHY — worker quarantine still isolates project-owned collisions because protecting runtime dependencies must not disable isolation" <| fun _ ->
      SageFs.Server.WorkerMain.shouldQuarantineAssembly "Falco.dll"
      |> Expect.isTrue "unrelated project collision should remain quarantinable"

      SageFs.Server.WorkerMain.shouldQuarantineAssembly "fr-FR.resources.dll"
      |> Expect.isFalse "satellite resources should never be quarantined"
  ]
