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
  test "has 6 built-in providers" {
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

/// A deliberately unresolvable assembly: ONE public type whose BASE TYPE lives in an
/// AssemblyNameReference that resolves nowhere. Built with Mono.Cecil rather than compiled F#, because
/// this needs a TYPE-LEVEL resolution failure (the base type, not merely a method signature) —
/// live-verified that's what makes `GetExportedTypes()` actually fail on this runtime; a method whose
/// PARAMETER type is unresolvable does not (the CLR resolves method signatures lazily, only base
/// types/interfaces are resolved eagerly to materialize the `Type`).
let private writeUnresolvableAssembly () : string =
  let workDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sagefs-unresolvable-asm-test")
  System.IO.Directory.CreateDirectory workDir |> ignore
  let dst = System.IO.Path.Combine(workDir, sprintf "Unresolvable-%s.dll" (System.Guid.NewGuid().ToString "N"))
  let asmName = Mono.Cecil.AssemblyNameDefinition("SageFsUnresolvableRegressionProbe", System.Version(1, 0, 0, 0))
  let assembly = Mono.Cecil.AssemblyDefinition.CreateAssembly(asmName, "SageFsUnresolvableRegressionProbe", Mono.Cecil.ModuleKind.Dll)
  let modul = assembly.MainModule
  let missingRef = Mono.Cecil.AssemblyNameReference("SageFs.NoSuchAssembly.ForRegressionTest", System.Version(1, 0, 0, 0))
  modul.AssemblyReferences.Add missingRef
  let missingBaseType = Mono.Cecil.TypeReference("SageFs.NoSuchNamespace", "MissingBase", modul, missingRef :> Mono.Cecil.IMetadataScope)
  let publicType =
    Mono.Cecil.TypeDefinition(
      "SageFsUnresolvableRegressionProbe",
      "PublicType",
      Mono.Cecil.TypeAttributes.Public ||| Mono.Cecil.TypeAttributes.Class,
      missingBaseType)
  modul.Types.Add publicType
  use output = new System.IO.MemoryStream()
  assembly.Write output
  System.IO.File.WriteAllBytes(dst, output.ToArray())
  dst

let attributeDiscoveryTests = testList "AttributeDiscovery" [
  test "WHY — live testing discovery — skips dynamic assemblies because FSI submissions must not crash REPL evaluation" {
    let asm =
      System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
        AssemblyName("sagefs-dynamic-discovery-probe"),
        System.Reflection.Emit.AssemblyBuilderAccess.Run)

    SageFs.Features.ReflectionDiscovery.exportedTypes asm
    |> Array.length
    |> Expect.equal "dynamic assemblies should be treated as having no discoverable test types" 0
  }

  // Regression coverage for the live-testing discovery bug that looked like "0 tests" for hours because
  // a load failure inside GetExportedTypes() was swallowed to a bare [||] with nothing logged, and — the
  // worse half — could ALSO simply propagate uncaught for a single-type failure (see
  // ReflectionDiscovery.exportedTypes's own doc comment for the live evidence). A deliberately
  // unresolvable assembly must produce a REPORTED failure, not a silent empty list and not a crash.
  test "WHY — a deliberately unresolvable assembly reports a failure through the log, not a silent empty list" {
    let dllPath = writeUnresolvableAssembly ()
    let asm = Assembly.LoadFrom dllPath

    let logged = System.Collections.Concurrent.ConcurrentBag<string>()
    let prevWarn = SageFs.Utils.Log.logWarn
    SageFs.Utils.Log.logWarn <- fun s -> logged.Add s; prevWarn s
    try
      let types = SageFs.Features.ReflectionDiscovery.exportedTypes asm
      types |> Array.length |> Expect.equal "the unresolvable type contributes no discoverable types" 0

      let relevant =
        logged
        |> Seq.filter (fun s -> s.Contains "ReflectionDiscovery" && s.Contains asm.FullName)
        |> Seq.toList
      (relevant |> List.isEmpty |> not)
      |> Expect.isTrue "a load failure must be logged, not silently swallowed to an empty array"
      relevant
      |> List.exists (fun s -> s.Contains "SageFs.NoSuchAssembly.ForRegressionTest")
      |> Expect.isTrue "the log must name the actual assembly that failed to resolve, not just say 'something went wrong'"
    finally
      SageFs.Utils.Log.logWarn <- prevWarn
  }

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

  test "discovers Expecto module let bindings exposed as getter methods" {
    let fixtureModuleName =
      typeof<ExpectoModuleBindingFixture.Marker>.DeclaringType.FullName

    let testsAsm = typeof<ExpectoModuleBindingFixture.Marker>.Assembly
    match BuiltInExecutors.expecto with
    | TestExecutor.Custom ce ->
      let discovery = ce.Discover testsAsm
      let fixtureTests =
        discovery.Tests
        |> List.filter (fun tc -> tc.FullName.Contains(sprintf "%s.tests/" fixtureModuleName))
      (List.length fixtureTests > 0)
      |> Expect.isTrue "should discover module let tests"
    | _ -> failtest "Expected Custom"
  }

  testAsync "run closure executes discovered module let binding tests" {
    let fixtureModuleName =
      typeof<ExpectoModuleBindingFixture.Marker>.DeclaringType.FullName

    let testsAsm = typeof<ExpectoModuleBindingFixture.Marker>.Assembly
    match BuiltInExecutors.expecto with
    | TestExecutor.Custom ce ->
      let discovery = ce.Discover testsAsm
      let fixtureTest =
        discovery.Tests
        |> List.tryFind (fun tc -> tc.FullName.Contains(sprintf "%s.tests/" fixtureModuleName))
      match fixtureTest with
      | None -> failtest "fixture test was not discovered"
      | Some tc ->
        let! outcome = discovery.RunTest tc
        match outcome with
        | TestResult.Passed _ -> ()
        | other -> failtestf "Expected Passed, got %A" other
    | _ -> failtest "Expected Custom"
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
  testAsync "executeMethod handles parameter mismatch gracefully" {
    let mi = typeof<string>.GetMethod("IsNullOrEmpty", [| typeof<string> |])
    let! result = ReflectionExecutor.executeMethod mi
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
  testAsync "totality: every discovered attr test is executable (not NotRun)" {
    let result = TestOrchestrator.discoverAll [ syntheticExecutor ] syntheticAsm
    for tc in result.Tests do
      let! outcome = result.RunTest tc
      match outcome with
      | TestResult.NotRun -> failtestf "Test '%s' returned NotRun — not wired!" tc.FullName
      | _ -> ()
  }

  testAsync "passing attr test returns Passed" {
    let result = TestOrchestrator.discoverAll [ syntheticExecutor ] syntheticAsm
    let passing =
      result.Tests
      |> List.find (fun tc -> tc.FullName.Contains "PassingTest")
    let! outcome = result.RunTest passing
    match outcome with
    | TestResult.Passed _ -> ()
    | other -> failtestf "Expected Passed, got %A" other
  }

  testAsync "failing attr test returns Failed" {
    let result = TestOrchestrator.discoverAll [ syntheticExecutor ] syntheticAsm
    let failing =
      result.Tests
      |> List.find (fun tc -> tc.FullName.Contains "FailingTest")
    let! outcome = result.RunTest failing
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

  testAsync "constructor DI class returns Skipped, not Failed" {
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
      let! outcome = result.RunTest tc
      match outcome with
      | TestResult.Skipped _ -> ()
      | other -> failtestf "Expected Skipped for DI class, got %A" other
    | None -> skiptest "NeedsConstructorArg test not discovered"
  }

  testAsync "mixed executors: custom + attr dispatch correctly" {
    let result =
      TestOrchestrator.discoverAll
        (syntheticExecutor :: BuiltInExecutors.builtIn)
        syntheticAsm
    // Attr tests should be executable
    let attrTests =
      result.Tests |> List.filter (fun tc -> tc.Framework = TestFramework.Unknown "SyntheticFact")
    (List.length attrTests > 0) |> Expect.isTrue "should have synthetic attr tests"
    for tc in attrTests do
      let! outcome = result.RunTest tc
      match outcome with
      | TestResult.NotRun -> failtestf "Attr test '%s' returned NotRun in mixed mode" tc.FullName
      | _ -> ()
    // Expecto (custom) tests should also be executable
    let expectoTests =
      result.Tests |> List.filter (fun tc -> tc.Framework = TestFramework.Expecto)
    match expectoTests with
    | [] -> skiptest "No expecto tests found — assembly may not expose them"
    | first :: _ ->
      let! outcome = result.RunTest first
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

  testAsync "discoverAll expands Theory rows and all are executable" {
    let result = TestOrchestrator.discoverAll [ syntheticTheoryExecutor ] theoryAsm
    let theoryTests = result.Tests |> List.filter (fun tc -> tc.FullName.Contains "ParameterisedTest")
    theoryTests
    |> List.length
    |> Expect.equal "should produce 3 test cases for ParameterisedTest" 3
    for tc in theoryTests do
      let! outcome = result.RunTest tc
      match outcome with
      | TestResult.NotRun -> failtestf "Theory test '%s' returned NotRun — not wired!" tc.FullName
      | _ -> ()
  }

  testAsync "discoverAll Theory runners pass correct args (passing test)" {
    let result = TestOrchestrator.discoverAll [ syntheticTheoryExecutor ] theoryAsm
    let theoryTests = result.Tests |> List.filter (fun tc -> tc.FullName.Contains "ParameterisedTest")
    for tc in theoryTests do
      let! outcome = result.RunTest tc
      match outcome with
      | TestResult.Passed _ -> ()
      | other -> failtestf "Expected Passed for '%s', got %A" tc.FullName other
  }
]

// --- AsyncFsCheck reflection regression tests ---

/// Simple FsCheck property fixture for testing live testing executor reflection.
let private fsCheckPropertyFixture =
  testProperty "trivial always-true property" (fun (x: int) -> x = x)

let asyncFsCheckReflectionTests = testList "AsyncFsCheck reflection" [
  test "AsyncFsCheck tag 3 tests are discovered" {
    let testsAsm =
      System.AppDomain.CurrentDomain.GetAssemblies()
      |> Array.tryFind (fun a -> a.GetName().Name = "SageFs.Tests")
    match testsAsm with
    | Some asm ->
      let tag3Count =
        match BuiltInExecutors.ExpectoExecutor.tryBuildCache asm with
        | Some cache ->
          let lookup = BuiltInExecutors.ExpectoExecutor.buildLookup cache asm
          lookup |> Map.filter (fun _ (rft: BuiltInExecutors.ExpectoExecutor.ReflectedFlatTest) -> rft.Tag = 3) |> Map.count
        | None -> 0
      (tag3Count > 0) |> Expect.isTrue "should discover AsyncFsCheck (tag 3) tests"
    | None -> skiptest "SageFs.Tests assembly not loaded"
  }

  testAsync "AsyncFsCheck tests can be executed via reflection" {
    let testsAsm =
      System.AppDomain.CurrentDomain.GetAssemblies()
      |> Array.tryFind (fun a -> a.GetName().Name = "SageFs.Tests")
    match testsAsm with
    | Some asm ->
      match BuiltInExecutors.ExpectoExecutor.tryBuildCache asm with
      | Some cache ->
        let lookup = BuiltInExecutors.ExpectoExecutor.buildLookup cache asm
        let fsCheckEntry =
          lookup |> Map.toSeq |> Seq.tryFind (fun (_, rft) -> rft.Tag = 3)
        match fsCheckEntry with
        | Some (name, rft) ->
          let! result =
            BuiltInExecutors.ExpectoExecutor.executeReflected cache rft System.Threading.CancellationToken.None
          match result with
          | TestResult.Passed _ -> ()
          | TestResult.Failed (f, _) ->
            failtestf "AsyncFsCheck test '%s' failed: %A" name f
          | other ->
            failtestf "AsyncFsCheck test '%s' unexpected result: %A" name other
        | None -> failtest "no AsyncFsCheck tests in lookup"
      | None -> failtest "could not build reflection cache"
    | None -> skiptest "SageFs.Tests assembly not loaded"
  }

  testAsync "no AsyncFsCheck tests fail with 'could not reflect property'" {
    let testsAsm =
      System.AppDomain.CurrentDomain.GetAssemblies()
      |> Array.tryFind (fun a -> a.GetName().Name = "SageFs.Tests")
    match testsAsm with
    | Some asm ->
      match BuiltInExecutors.ExpectoExecutor.tryBuildCache asm with
      | Some cache ->
        let lookup = BuiltInExecutors.ExpectoExecutor.buildLookup cache asm
        let fsCheckTests =
          lookup |> Map.toList |> List.filter (fun (_, rft) -> rft.Tag = 3)
        // Run a sample of up to 10 AsyncFsCheck tests
        let sample = fsCheckTests |> List.truncate 10
        let propertyReflectionErrors = ref 0
        for (_, rft) in sample do
          let! result =
            BuiltInExecutors.ExpectoExecutor.executeReflected cache rft System.Threading.CancellationToken.None
          match result with
          | TestResult.Failed (TestFailure.ExceptionThrown(msg, _), _)
              when msg.Contains("could not reflect") ->
            propertyReflectionErrors.Value <- propertyReflectionErrors.Value + 1
          | _ -> ()
        propertyReflectionErrors.Value
        |> Expect.equal "no AsyncFsCheck tests should fail with reflection error" 0
      | None -> failtest "could not build reflection cache"
    | None -> skiptest "SageFs.Tests assembly not loaded"
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
  asyncFsCheckReflectionTests
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
