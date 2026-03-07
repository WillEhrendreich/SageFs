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
      d.Name |> Expect.equal "should be xunit" TestFramework.XUnit
    | _ -> failtest "Expected AttributeBased"
  }

  test "extracts Custom description" {
    let desc = TestExecutor.description BuiltInExecutors.expecto
    match desc with
    | ProviderDescription.Custom d ->
      d.Name |> Expect.equal "should be expecto" TestFramework.Expecto
    | _ -> failtest "Expected Custom"
  }
]

let builtInDescriptionTests = testList "BuiltInExecutors.descriptions" [
  test "has 5 built-in providers" {
    BuiltInExecutors.descriptions
    |> List.length
    |> Expect.equal "should have 6 providers" 6
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
      Name = TestFramework.XUnit
      TestAttributes = ["Fact"; "Theory"]
      AssemblyMarker = "xunit.core"
    }
    let coreAsm = typeof<TestId>.Assembly
    let discovered = AttributeDiscovery.discoverInAssembly desc [] TestCategory.Unit coreAsm
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
        tc.Framework |> Expect.equal "framework should be expecto" TestFramework.Expecto)
    | None ->
      skiptest "SageFs.Tests assembly not loaded"
  }
]

// --- Issue #32 regression tests: AttributeBased executors wired into RunTest ---

/// Synthetic attribute matching xunit's Fact pattern
[<System.AttributeUsage(System.AttributeTargets.Method)>]
type SyntheticFactAttribute() = inherit System.Attribute()

/// Synthetic attribute matching xunit's Theory pattern
[<System.AttributeUsage(System.AttributeTargets.Method)>]
type SyntheticTheoryAttribute() = inherit System.Attribute()

/// Synthetic attribute matching xunit's InlineData pattern
[<System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)>]
type SyntheticInlineDataAttribute(data: obj array) =
  inherit System.Attribute()
  new(a: obj) = SyntheticInlineDataAttribute([| a |])
  new(a: obj, b: obj) = SyntheticInlineDataAttribute([| a; b |])
  member _.Data : obj array = data

/// Test class with a passing and a failing method
type SyntheticTestClass() =
  [<SyntheticFact>]
  member _.PassingTest() = ()

  [<SyntheticFact>]
  member _.FailingTest() = failwith "intentional failure"

/// Test class that requires constructor DI (no default ctor)
type NeedsConstructorArg(value: string) =
  [<SyntheticFact>]
  member _.SomeTest() = ()

/// Test class with Theory+InlineData methods (for Issue #38 regression)
type SyntheticTheoryClass() =
  [<SyntheticTheory>]
  [<SyntheticInlineData(1)>]
  [<SyntheticInlineData(2)>]
  [<SyntheticInlineData(3)>]
  member _.ParameterisedTest(x: int) = ()

  [<SyntheticTheory>]
  [<SyntheticInlineData(1, "a")>]
  [<SyntheticInlineData(2, "b")>]
  member _.MultiArgTest(n: int, s: string) = ()

  [<SyntheticTheory>]
  member _.TheoryWithNoData() = ()

let private syntheticExecutor : TestExecutor =
  let desc = {
    Name = TestFramework.Unknown "SyntheticFact"
    TestAttributes = [ "SyntheticFactAttribute" ]
    AssemblyMarker = "SageFs.Tests"
  }
  TestExecutor.AttributeBased {
    Description = desc
    Execute = ReflectionExecutor.executeMethod
    TheoryAttributes = []
  }

let private syntheticAsm = typeof<SyntheticTestClass>.Assembly

let issue32RegressionTests = testList "Issue #32: attr executors wired into RunTest" [
  test "totality: every discovered attr test is executable (not NotRun)" {
    let result = TestOrchestrator.discoverAll [ syntheticExecutor ] syntheticAsm
    result.Tests
    |> List.iter (fun tc ->
      let outcome = result.RunTest tc |> Async.RunSynchronously
      match outcome with
      | TestResult.NotRun -> failtestf "Test '%s' returned NotRun — not wired!" tc.FullName
      | _ -> ())
  }

  test "passing attr test returns Passed" {
    let result = TestOrchestrator.discoverAll [ syntheticExecutor ] syntheticAsm
    let passing =
      result.Tests
      |> List.find (fun tc -> tc.FullName.Contains "PassingTest")
    let outcome = result.RunTest passing |> Async.RunSynchronously
    match outcome with
    | TestResult.Passed _ -> ()
    | other -> failtestf "Expected Passed, got %A" other
  }

  test "failing attr test returns Failed" {
    let result = TestOrchestrator.discoverAll [ syntheticExecutor ] syntheticAsm
    let failing =
      result.Tests
      |> List.find (fun tc -> tc.FullName.Contains "FailingTest")
    let outcome = result.RunTest failing |> Async.RunSynchronously
    match outcome with
    | TestResult.Failed _ -> ()
    | other -> failtestf "Expected Failed, got %A" other
  }

  test "all discovered test IDs are unique" {
    let result = TestOrchestrator.discoverAll [ syntheticExecutor ] syntheticAsm
    let ids = result.Tests |> List.map (fun tc -> TestId.value tc.Id)
    let uniqueIds = ids |> Set.ofList
    Set.count uniqueIds |> Expect.equal "all IDs should be unique" (List.length ids)
  }

  test "constructor DI class returns Skipped, not Failed" {
    let diExecutor : TestExecutor =
      let desc = {
        Name = TestFramework.Unknown "SyntheticDI"
        TestAttributes = [ "SyntheticFactAttribute" ]
        AssemblyMarker = "SageFs.Tests"
      }
      TestExecutor.AttributeBased {
        Description = desc
        Execute = ReflectionExecutor.executeMethod
        TheoryAttributes = []
      }
    let result = TestOrchestrator.discoverAll [ diExecutor ] syntheticAsm
    let diTest =
      result.Tests
      |> List.tryFind (fun tc -> tc.FullName.Contains "NeedsConstructorArg")
    match diTest with
    | Some tc ->
      let outcome = result.RunTest tc |> Async.RunSynchronously
      match outcome with
      | TestResult.Skipped _ -> ()
      | other -> failtestf "Expected Skipped for DI class, got %A" other
    | None -> skiptest "NeedsConstructorArg test not discovered"
  }

  test "mixed executors: custom + attr dispatch correctly" {
    let result =
      TestOrchestrator.discoverAll
        (syntheticExecutor :: BuiltInExecutors.builtIn)
        syntheticAsm
    // Attr tests should be executable
    let attrTests =
      result.Tests |> List.filter (fun tc -> tc.Framework = TestFramework.Unknown "SyntheticFact")
    (List.length attrTests > 0) |> Expect.isTrue "should have synthetic attr tests"
    attrTests
    |> List.iter (fun tc ->
      let outcome = result.RunTest tc |> Async.RunSynchronously
      match outcome with
      | TestResult.NotRun -> failtestf "Attr test '%s' returned NotRun in mixed mode" tc.FullName
      | _ -> ())
    // Expecto (custom) tests should also be executable
    let expectoTests =
      result.Tests |> List.filter (fun tc -> tc.Framework = TestFramework.Expecto)
    match expectoTests with
    | [] -> skiptest "No expecto tests found — assembly may not expose them"
    | first :: _ ->
      let outcome = result.RunTest first |> Async.RunSynchronously
      match outcome with
      | TestResult.NotRun -> failtestf "Expecto test '%s' returned NotRun" first.FullName
      | _ -> ()
  }
]

// --- Issue #38 regression tests: Theory+InlineData counts each row as a test ---

let private syntheticTheoryExecutor : TestExecutor =
  let desc = {
    Name = TestFramework.XUnit
    TestAttributes = [ "SyntheticTheory"; "SyntheticFact" ]
    AssemblyMarker = "SageFs.Tests"
  }
  TestExecutor.AttributeBased {
    Description = desc
    Execute = ReflectionExecutor.executeMethod
    TheoryAttributes = [ "SyntheticTheory" ]
  }

let private theoryAsm = typeof<SyntheticTheoryClass>.Assembly

let issue38RegressionTests = testList "Issue #38: Theory+InlineData rows counted per row" [
  test "Theory method with 3 InlineData rows produces 3 TestCases" {
    let result = TestOrchestrator.discoverAll [ syntheticTheoryExecutor ] theoryAsm
    let parameterised = result.Tests |> List.filter (fun tc -> tc.DisplayName.StartsWith "ParameterisedTest(")
    parameterised
    |> List.length
    |> Expect.equal "should produce 3 test cases from 3 InlineData rows" 3
  }

  test "Theory method with 2 InlineData rows produces 2 TestCases" {
    let result = TestOrchestrator.discoverAll [ syntheticTheoryExecutor ] theoryAsm
    let multiArg = result.Tests |> List.filter (fun tc -> tc.DisplayName.StartsWith "MultiArgTest(")
    multiArg
    |> List.length
    |> Expect.equal "should produce 2 test cases from 2 InlineData rows" 2
  }

  test "Theory method without InlineData produces 1 TestCase" {
    let result = TestOrchestrator.discoverAll [ syntheticTheoryExecutor ] theoryAsm
    let noData = result.Tests |> List.filter (fun tc -> tc.FullName.Contains "TheoryWithNoData")
    noData
    |> List.length
    |> Expect.equal "should produce 1 test case when no InlineData" 1
  }

  test "Theory TestCase display names include args" {
    let result = TestOrchestrator.discoverAll [ syntheticTheoryExecutor ] theoryAsm
    let parameterised = result.Tests |> List.filter (fun tc -> tc.DisplayName.StartsWith "ParameterisedTest(")
    let displayNames = parameterised |> List.map (fun tc -> tc.DisplayName) |> Set.ofList
    Set.contains "ParameterisedTest(1)" displayNames |> Expect.isTrue "should include ParameterisedTest(1)"
    Set.contains "ParameterisedTest(2)" displayNames |> Expect.isTrue "should include ParameterisedTest(2)"
    Set.contains "ParameterisedTest(3)" displayNames |> Expect.isTrue "should include ParameterisedTest(3)"
  }

  test "Theory TestCase IDs are unique per row" {
    let result = TestOrchestrator.discoverAll [ syntheticTheoryExecutor ] theoryAsm
    let parameterised = result.Tests |> List.filter (fun tc -> tc.DisplayName.StartsWith "ParameterisedTest(")
    let ids = parameterised |> List.map (fun tc -> TestId.value tc.Id) |> Set.ofList
    ids |> Set.count |> Expect.equal "all row IDs should be unique" 3
  }

  test "discoverAll expands Theory rows and all are executable" {
    let result = TestOrchestrator.discoverAll [ syntheticTheoryExecutor ] theoryAsm
    let theoryTests = result.Tests |> List.filter (fun tc -> tc.FullName.Contains "ParameterisedTest")
    theoryTests
    |> List.length
    |> Expect.equal "should produce 3 test cases for ParameterisedTest" 3
    theoryTests
    |> List.iter (fun tc ->
      let outcome = result.RunTest tc |> Async.RunSynchronously
      match outcome with
      | TestResult.NotRun -> failtestf "Theory test '%s' returned NotRun — not wired!" tc.FullName
      | _ -> ())
  }

  test "discoverAll Theory runners pass correct args (passing test)" {
    let result = TestOrchestrator.discoverAll [ syntheticTheoryExecutor ] theoryAsm
    let theoryTests = result.Tests |> List.filter (fun tc -> tc.FullName.Contains "ParameterisedTest")
    theoryTests
    |> List.iter (fun tc ->
      let outcome = result.RunTest tc |> Async.RunSynchronously
      match outcome with
      | TestResult.Passed _ -> ()
      | other -> failtestf "Expected Passed for '%s', got %A" tc.FullName other)
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
  issue32RegressionTests
  issue38RegressionTests
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
        | ProviderDescription.Custom c -> c.Name = TestFramework.Expecto
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
        | ProviderDescription.AttributeBased a -> a.Name = TestFramework.XUnit
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
      |> List.forall (fun t -> t.Framework = TestFramework.Expecto)
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
      { Id = TestId.create "Mod.test1" TestFramework.Expecto
        FullName = "Mod.test1"; DisplayName = "test1"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = TestFramework.Expecto; Category = TestCategory.Unit }
      { Id = TestId.create "Mod.test2" TestFramework.Expecto
        FullName = "Mod.test2"; DisplayName = "test2"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]
    let affected = LiveTestingHook.findAffectedTests tests []
    affected
    |> Array.length
    |> Expect.equal "no tests affected when no methods changed" 0
  }

  test "filters to matching tests when updated methods specified" {
    let tests = [|
      { Id = TestId.create "MyModule.test1" TestFramework.Expecto
        FullName = "MyModule.test1"; DisplayName = "test1"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = TestFramework.Expecto; Category = TestCategory.Unit }
      { Id = TestId.create "OtherModule.test2" TestFramework.Expecto
        FullName = "OtherModule.test2"; DisplayName = "test2"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = TestFramework.Expecto; Category = TestCategory.Unit }
    |]
    let affected = LiveTestingHook.findAffectedTests tests ["MyModule.helper"]
    affected
    |> Array.length
    |> Expect.equal "only affected test matched" 1
  }

  test "falls back to all tests when no tests match updated methods" {
    let tests = [|
      { Id = TestId.create "MyModule.test1" TestFramework.Expecto
        FullName = "MyModule.test1"; DisplayName = "test1"
        Origin = TestOrigin.ReflectionOnly; Labels = []
        Framework = TestFramework.Expecto; Category = TestCategory.Unit }
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
