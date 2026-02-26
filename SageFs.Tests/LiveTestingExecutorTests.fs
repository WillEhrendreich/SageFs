module SageFs.Tests.LiveTestingExecutorTests

open System.Reflection
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

let executorDescriptionTests = testList "TestExecutor.description" [
  test "extracts AttributeBased description" {
    let desc = TestExecutor.description BuiltInExecutors.xunit
    match desc with
    | ProviderDescription.AttributeBased d ->
      d.Name |> Expect.equal "should be xunit" "xunit"
    | _ -> failtest "Expected AttributeBased"
  }

  test "extracts Custom description" {
    let desc = TestExecutor.description BuiltInExecutors.expecto
    match desc with
    | ProviderDescription.Custom d ->
      d.Name |> Expect.equal "should be expecto" "expecto"
    | _ -> failtest "Expected Custom"
  }
]

let builtInDescriptionTests = testList "BuiltInExecutors.descriptions" [
  test "has 5 built-in providers" {
    BuiltInExecutors.descriptions
    |> List.length
    |> Expect.equal "should have 5 providers" 5
  }

  test "includes all framework names" {
    let names =
      BuiltInExecutors.descriptions
      |> List.map (fun d ->
        match d with
        | ProviderDescription.AttributeBased a -> a.Name
        | ProviderDescription.Custom c -> c.Name)
      |> Set.ofList
    Set.count names |> Expect.equal "should have 5 unique names" 5
  }

  test "descriptions match TestProviderDescriptions.builtInDescriptions" {
    let executorDescs = BuiltInExecutors.descriptions |> List.length
    let typeDescs = TestProviderDescriptions.builtInDescriptions |> List.length
    executorDescs |> Expect.equal "both should have same count" typeDescs
  }
]

let attributeDiscoveryTests = testList "AttributeDiscovery" [
  test "discovers Expecto [Tests] properties via custom executor" {
    let testsAsm =
      System.AppDomain.CurrentDomain.GetAssemblies()
      |> Array.tryFind (fun a -> a.GetName().Name = "SageFs.Tests")
    match testsAsm with
    | Some asm ->
      match BuiltInExecutors.expecto with
      | TestExecutor.Custom ce ->
        let discovery = ce.Discover asm
        (List.length discovery.Tests > 0)
        |> Expect.isTrue "should find at least 1 expecto test property"
      | _ -> failtest "Expected Custom"
    | None ->
      skiptest "SageFs.Tests assembly not loaded"
  }

  test "attribute discovery finds nothing in assembly without test attributes" {
    let desc = {
      Name = "xunit"
      TestAttributes = ["Fact"; "Theory"]
      AssemblyMarker = "xunit.core"
    }
    let coreAsm = typeof<TestId>.Assembly
    let discovered = AttributeDiscovery.discoverInAssembly desc TestCategory.Unit coreAsm
    List.length discovered |> Expect.equal "should find 0 tests" 0
  }
]

let reflectionExecutorTests = testList "ReflectionExecutor" [
  test "executeMethod handles parameter mismatch gracefully" {
    let mi = typeof<string>.GetMethod("IsNullOrEmpty", [| typeof<string> |])
    let result = ReflectionExecutor.executeMethod mi |> Async.RunSynchronously
    match result with
    | TestResult.Failed _ -> ()
    | TestResult.Passed _ -> ()
    | other -> failtestf "Expected Passed or Failed, got %A" other
  }
]

let discoverAllTests = testList "TestOrchestrator.discoverAll" [
  test "discovery returns RunTest closure" {
    let testsAsm =
      System.AppDomain.CurrentDomain.GetAssemblies()
      |> Array.tryFind (fun a -> a.GetName().Name = "SageFs.Tests")
    match testsAsm with
    | Some asm ->
      let result = TestOrchestrator.discoverAll BuiltInExecutors.builtIn asm
      (List.length result.Tests > 0)
      |> Expect.isTrue "should discover tests"
    | None -> skiptest "SageFs.Tests assembly not loaded"
  }
]

let discoverTests = testList "TestOrchestrator.discoverTests" [
  test "discovers expecto tests from test assembly" {
    let testsAsm =
      System.AppDomain.CurrentDomain.GetAssemblies()
      |> Array.tryFind (fun a -> a.GetName().Name = "SageFs.Tests")
    match testsAsm with
    | Some asm ->
      let result = TestOrchestrator.discoverAll BuiltInExecutors.builtIn asm
      (List.length result.Tests > 0)
      |> Expect.isTrue "should discover tests"
    | None ->
      skiptest "SageFs.Tests assembly not loaded"
  }

  test "all discovered tests have framework=expecto" {
    let testsAsm =
      System.AppDomain.CurrentDomain.GetAssemblies()
      |> Array.tryFind (fun a -> a.GetName().Name = "SageFs.Tests")
    match testsAsm with
    | Some asm ->
      let result = TestOrchestrator.discoverAll BuiltInExecutors.builtIn asm
      result.Tests
      |> List.iter (fun tc ->
        tc.Framework |> Expect.equal "framework should be expecto" "expecto")
    | None ->
      skiptest "SageFs.Tests assembly not loaded"
  }
]

[<Tests>]
let allExecutorTests = testList "Provider Executors" [
  executorDescriptionTests
  builtInDescriptionTests
  attributeDiscoveryTests
  reflectionExecutorTests
  discoverAllTests
  discoverTests
]

// --- LiveTestingHook tests ---

let getTestAsm () =
  System.AppDomain.CurrentDomain.GetAssemblies()
  |> Array.tryFind (fun a -> a.GetName().Name = "SageFs.Tests")

let detectProvidersTests = testList "LiveTestingHook.detectProviders" [
  test "detects Expecto provider for SageFs.Tests assembly" {
    match getTestAsm () with
    | Some asm ->
      let providers = LiveTestingHook.detectProviders BuiltInExecutors.builtIn asm
      providers
      |> List.exists (fun p ->
        match p with
        | ProviderDescription.Custom c -> c.Name = "expecto"
        | _ -> false)
      |> Expect.isTrue "should detect expecto provider"
    | None -> skiptest "SageFs.Tests assembly not loaded"
  }

  test "does not detect xunit provider for SageFs.Tests assembly" {
    match getTestAsm () with
    | Some asm ->
      let providers = LiveTestingHook.detectProviders BuiltInExecutors.builtIn asm
      providers
      |> List.exists (fun p ->
        match p with
        | ProviderDescription.AttributeBased a -> a.Name = "xunit"
        | _ -> false)
      |> Expect.isFalse "should not detect xunit provider"
    | None -> skiptest "SageFs.Tests assembly not loaded"
  }

  test "returns empty for assembly with no test frameworks" {
    let coreAsm = typeof<TestCase>.Assembly
    let providers = LiveTestingHook.detectProviders BuiltInExecutors.builtIn coreAsm
    providers
    |> List.length
    |> Expect.equal "should detect no providers" 0
  }
]

let hookDiscoverTestsTests = testList "LiveTestingHook.discoverTests" [
  test "discovers Expecto tests in SageFs.Tests assembly" {
    match getTestAsm () with
    | Some asm ->
      let result = LiveTestingHook.discoverTests BuiltInExecutors.builtIn asm
      Expect.isTrue "should discover multiple tests" (result.Tests.Length > 0)
    | None -> skiptest "SageFs.Tests assembly not loaded"
  }

  test "all discovered tests have framework = expecto" {
    match getTestAsm () with
    | Some asm ->
      let result = LiveTestingHook.discoverTests BuiltInExecutors.builtIn asm
      result.Tests
      |> List.forall (fun t -> t.Framework = "expecto")
      |> Expect.isTrue "all tests should be expecto framework"
    | None -> skiptest "SageFs.Tests assembly not loaded"
  }

  test "discovers no tests in assembly with no test frameworks" {
    let coreAsm = typeof<TestCase>.Assembly
    let result = LiveTestingHook.discoverTests BuiltInExecutors.builtIn coreAsm
    result.Tests
    |> List.length
    |> Expect.equal "should discover no tests" 0
  }
]

let findAffectedTestsTests = testList "LiveTestingHook.findAffectedTests" [
  test "returns empty when no updated methods specified" {
    let tests = [|
      { Id = TestId.create "Mod.test1" "expecto"
        FullName = "Mod.test1"; DisplayName = "test1"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = "expecto"; Category = TestCategory.Unit }
      { Id = TestId.create "Mod.test2" "expecto"
        FullName = "Mod.test2"; DisplayName = "test2"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = "expecto"; Category = TestCategory.Unit }
    |]
    let affected = LiveTestingHook.findAffectedTests tests []
    affected
    |> Array.length
    |> Expect.equal "no tests affected when no methods changed" 0
  }

  test "filters to matching tests when updated methods specified" {
    let tests = [|
      { Id = TestId.create "MyModule.test1" "expecto"
        FullName = "MyModule.test1"; DisplayName = "test1"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = "expecto"; Category = TestCategory.Unit }
      { Id = TestId.create "OtherModule.test2" "expecto"
        FullName = "OtherModule.test2"; DisplayName = "test2"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = "expecto"; Category = TestCategory.Unit }
    |]
    let affected = LiveTestingHook.findAffectedTests tests ["MyModule.helper"]
    affected
    |> Array.length
    |> Expect.equal "only affected test matched" 1
  }

  test "falls back to all tests when no tests match updated methods" {
    let tests = [|
      { Id = TestId.create "MyModule.test1" "expecto"
        FullName = "MyModule.test1"; DisplayName = "test1"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = "expecto"; Category = TestCategory.Unit }
    |]
    // Conservative fallback: non-empty methods but no match → run everything
    let affected = LiveTestingHook.findAffectedTests tests ["UnrelatedModule.func"]
    affected
    |> Array.length
    |> Expect.equal "falls back to all tests" 1
  }
]

let afterReloadTests = testList "LiveTestingHook.afterReload" [
  test "afterReload produces complete result for SageFs.Tests assembly" {
    match getTestAsm () with
    | Some asm ->
      let result = LiveTestingHook.afterReload BuiltInExecutors.builtIn asm []
      Expect.isTrue "should detect providers" (not (List.isEmpty result.DetectedProviders))
      Expect.isTrue "should discover tests" (result.DiscoveredTests.Length > 0)
      Expect.isTrue "affected = 0 when no methods changed"
        (result.AffectedTestIds.Length = 0)
    | None -> skiptest "SageFs.Tests assembly not loaded"
  }

  test "afterReload returns empty for assembly with no test frameworks" {
    let coreAsm = typeof<TestCase>.Assembly
    let result = LiveTestingHook.afterReload BuiltInExecutors.builtIn coreAsm []
    result.DetectedProviders
    |> List.length
    |> Expect.equal "no providers" 0
    result.DiscoveredTests
    |> Array.length
    |> Expect.equal "no tests" 0
    result.AffectedTestIds
    |> Array.length
    |> Expect.equal "no affected" 0
  }

  test "detected providers match discovered test frameworks" {
    match getTestAsm () with
    | Some asm ->
      let result = LiveTestingHook.afterReload BuiltInExecutors.builtIn asm []
      let providerNames =
        result.DetectedProviders
        |> List.map (fun p ->
          match p with
          | ProviderDescription.AttributeBased a -> a.Name
          | ProviderDescription.Custom c -> c.Name)
        |> Set.ofList
      let testFrameworks =
        result.DiscoveredTests
        |> Array.map (fun t -> t.Framework)
        |> Set.ofArray
      testFrameworks
      |> Set.forall (fun fw -> providerNames.Contains fw)
      |> Expect.isTrue "all test frameworks should have detected providers"
    | None -> skiptest "SageFs.Tests assembly not loaded"
  }
]

[<Tests>]
let allHookTests = testList "LiveTestingHook" [
  detectProvidersTests
  hookDiscoverTestsTests
  findAffectedTestsTests
  afterReloadTests
]
