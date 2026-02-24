namespace SageFs.Features.LiveTesting

open System
open System.Diagnostics
open System.Reflection

// --- Executor Types (IO side — functions that actually run tests) ---

/// Tier 1: Attribute-based test executor.
/// Core discovers tests via tree-sitter + reflection, executor just runs them.
type AttributeTestExecutor = {
  Description: AttributeProviderDescription
  Execute: MethodInfo -> Async<TestResult>
}

/// Tier 2: Custom test executor.
/// Provider handles its own discovery (e.g., Expecto value-based tests).
type CustomTestExecutor = {
  Description: CustomProviderDescription
  Discover: Assembly -> TestCase list
  Execute: TestCase -> Async<TestResult>
}

[<RequireQualifiedAccess>]
type TestExecutor =
  | AttributeBased of AttributeTestExecutor
  | Custom of CustomTestExecutor

module TestExecutor =
  let description (executor: TestExecutor) : ProviderDescription =
    match executor with
    | TestExecutor.AttributeBased ap -> ProviderDescription.AttributeBased ap.Description
    | TestExecutor.Custom cp -> ProviderDescription.Custom cp.Description

// --- Attribute-based discovery ---

module AttributeDiscovery =

  let private hasTestAttribute (attrs: string list) (mi: MethodInfo) : bool =
    mi.GetCustomAttributes(true)
    |> Array.exists (fun attr ->
      let attrName = attr.GetType().Name
      attrs
      |> List.exists (fun testAttr ->
        attrName = testAttr || attrName = sprintf "%sAttribute" testAttr))

  let private toTestCase (framework: string) (category: TestCategory) (mi: MethodInfo) : TestCase =
    let fullName = sprintf "%s.%s" mi.DeclaringType.FullName mi.Name
    { Id = TestId.create fullName framework
      FullName = fullName
      DisplayName = mi.Name
      Origin = TestOrigin.ReflectionOnly
      Labels = []
      Framework = framework
      Category = category }

  let discoverInAssembly
    (desc: AttributeProviderDescription)
    (category: TestCategory)
    (asm: Assembly)
    : TestCase list =
    try
      asm.GetExportedTypes()
      |> Array.collect (fun t ->
        t.GetMethods(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static)
        |> Array.filter (hasTestAttribute desc.TestAttributes))
      |> Array.map (toTestCase desc.Name category)
      |> Array.toList
    with
    | :? ReflectionTypeLoadException -> []
    | :? TypeLoadException -> []

// --- Reflection-based execution ---

module ReflectionExecutor =

  let executeMethod (mi: MethodInfo) : Async<TestResult> =
    async {
      let sw = Stopwatch.StartNew()
      try
        let instance =
          if mi.IsStatic then null
          else Activator.CreateInstance(mi.DeclaringType)
        let result = mi.Invoke(instance, [||])
        match result with
        | :? Threading.Tasks.Task as task ->
          do! Async.AwaitTask task
        | _ -> ()
        sw.Stop()
        return TestResult.Passed sw.Elapsed
      with
      | :? TargetInvocationException as tie ->
        sw.Stop()
        let inner = if tie.InnerException <> null then tie.InnerException else tie :> exn
        let isAssertion =
          inner.GetType().Name.Contains("Assert")
          || inner.GetType().Name.Contains("Expect")
        if isAssertion then
          return TestResult.Failed (TestFailure.AssertionFailed inner.Message, sw.Elapsed)
        else
          return TestResult.Failed (TestFailure.ExceptionThrown (inner.Message, inner.StackTrace), sw.Elapsed)
      | ex ->
        sw.Stop()
        return TestResult.Failed (TestFailure.ExceptionThrown (ex.Message, ex.StackTrace), sw.Elapsed)
    }

// --- Built-in framework executors ---

module BuiltInExecutors =

  let xunit : TestExecutor =
    TestExecutor.AttributeBased {
      Description = {
        Name = "xunit"
        TestAttributes = ["Fact"; "Theory"]
        AssemblyMarker = "xunit.core"
      }
      Execute = ReflectionExecutor.executeMethod
    }

  let nunit : TestExecutor =
    TestExecutor.AttributeBased {
      Description = {
        Name = "nunit"
        TestAttributes = ["Test"; "TestCase"; "TestCaseSource"]
        AssemblyMarker = "nunit.framework"
      }
      Execute = ReflectionExecutor.executeMethod
    }

  let mstest : TestExecutor =
    TestExecutor.AttributeBased {
      Description = {
        Name = "mstest"
        TestAttributes = ["TestMethod"; "DataTestMethod"]
        AssemblyMarker = "Microsoft.VisualStudio.TestPlatform.TestFramework"
      }
      Execute = ReflectionExecutor.executeMethod
    }

  let tunit : TestExecutor =
    TestExecutor.AttributeBased {
      Description = {
        Name = "tunit"
        TestAttributes = ["Test"]
        AssemblyMarker = "TUnit.Core"
      }
      Execute = ReflectionExecutor.executeMethod
    }

  let expecto : TestExecutor =
    TestExecutor.Custom {
      Description = {
        Name = "expecto"
        AssemblyMarker = "Expecto"
      }
      Discover = fun asm ->
        try
          asm.GetExportedTypes()
          |> Array.collect (fun t ->
            t.GetProperties(BindingFlags.Public ||| BindingFlags.Static)
            |> Array.filter (fun pi ->
              pi.GetCustomAttributes(true)
              |> Array.exists (fun attr ->
                attr.GetType().Name = "TestsAttribute"))
            |> Array.map (fun pi ->
              let fullName = sprintf "%s.%s" t.FullName pi.Name
              { Id = TestId.create fullName "expecto"
                FullName = fullName
                DisplayName = pi.Name
                Origin = TestOrigin.ReflectionOnly
                Labels = []
                Framework = "expecto"
                Category = TestCategory.Unit }))
          |> Array.toList
        with
        | :? ReflectionTypeLoadException -> []
        | :? TypeLoadException -> []
      Execute = fun _testCase ->
        async { return TestResult.NotRun }
    }

  let builtIn = [ xunit; nunit; mstest; tunit; expecto ]

  let descriptions : ProviderDescription list =
    builtIn |> List.map TestExecutor.description

// --- Test orchestration ---

module TestOrchestrator =

  let discoverTests
    (executors: TestExecutor list)
    (asm: Assembly)
    : TestCase list =
    executors
    |> List.collect (fun executor ->
      match executor with
      | TestExecutor.AttributeBased ae ->
        AttributeDiscovery.discoverInAssembly ae.Description TestCategory.Unit asm
      | TestExecutor.Custom ce ->
        ce.Discover asm)

  let findExecutor (executors: TestExecutor list) (framework: string) : TestExecutor option =
    executors
    |> List.tryFind (fun e ->
      match e with
      | TestExecutor.AttributeBased ae -> ae.Description.Name = framework
      | TestExecutor.Custom ce -> ce.Description.Name = framework)

  let executeOne
    (executors: TestExecutor list)
    (testCase: TestCase)
    : Async<TestRunResult> =
    async {
      let! result =
        match findExecutor executors testCase.Framework with
        | Some (TestExecutor.Custom ce) ->
          ce.Execute testCase
        | Some (TestExecutor.AttributeBased _) ->
          async { return TestResult.NotRun }
        | None ->
          async { return TestResult.NotRun }
      return {
        TestId = testCase.Id
        TestName = testCase.DisplayName
        Result = result
        Timestamp = DateTimeOffset.UtcNow
      }
    }

  let executeFiltered
    (executors: TestExecutor list)
    (maxParallelism: int)
    (tests: TestCase array)
    : Async<TestRunResult array> =
    async {
      let semaphore = new Threading.SemaphoreSlim(maxParallelism)
      let! results =
        tests
        |> Array.map (fun tc ->
          async {
            do! Async.AwaitTask (semaphore.WaitAsync())
            try
              return! executeOne executors tc
            finally
              semaphore.Release() |> ignore
          })
        |> Async.Parallel
      return results
    }

// --- Hot-reload integration hook ---

/// Pure data returned by the live testing hook after a hot reload.
/// The Elm loop dispatches this as events (ProvidersDetected, TestsDiscovered, etc.)
type LiveTestHookResult = {
  DetectedProviders: ProviderDescription list
  DiscoveredTests: TestCase array
  AffectedTestIds: TestId array
}

module LiveTestHookResult =
  let empty = {
    DetectedProviders = []
    DiscoveredTests = [||]
    AffectedTestIds = [||]
  }

module LiveTestingHook =

  /// Detect which providers apply to an assembly by checking referenced assemblies.
  let detectProviders
    (executors: TestExecutor list)
    (asm: Assembly)
    : ProviderDescription list =
    let referencedNames =
      try
        asm.GetReferencedAssemblies()
        |> Array.map (fun a -> a.Name)
        |> Set.ofArray
      with _ -> Set.empty
    executors
    |> List.choose (fun executor ->
      match executor with
      | TestExecutor.AttributeBased ae ->
        if referencedNames.Contains ae.Description.AssemblyMarker
        then Some (ProviderDescription.AttributeBased ae.Description)
        else None
      | TestExecutor.Custom ce ->
        if referencedNames.Contains ce.Description.AssemblyMarker
        then Some (ProviderDescription.Custom ce.Description)
        else None)

  /// Discover all tests in an assembly using matching executors.
  let discoverTests
    (executors: TestExecutor list)
    (asm: Assembly)
    : TestCase array =
    let referencedNames =
      try
        asm.GetReferencedAssemblies()
        |> Array.map (fun a -> a.Name)
        |> Set.ofArray
      with _ -> Set.empty
    executors
    |> List.collect (fun executor ->
      match executor with
      | TestExecutor.AttributeBased ae ->
        if referencedNames.Contains ae.Description.AssemblyMarker
        then AttributeDiscovery.discoverInAssembly ae.Description TestCategory.Unit asm
        else []
      | TestExecutor.Custom ce ->
        if referencedNames.Contains ce.Description.AssemblyMarker
        then ce.Discover asm
        else [])
    |> Array.ofList

  /// Find which discovered tests are affected by updated method names.
  /// Simple name matching — FCS-based matching comes in Phase 4.
  let findAffectedTests
    (discoveredTests: TestCase array)
    (updatedMethodNames: string list)
    : TestId array =
    if List.isEmpty updatedMethodNames then
      discoveredTests |> Array.map (fun t -> t.Id)
    else
      discoveredTests
      |> Array.filter (fun tc ->
        updatedMethodNames
        |> List.exists (fun updated ->
          tc.FullName.Contains updated
          || updated.Contains (tc.FullName.Split('.').[0])))
      |> Array.map (fun t -> t.Id)

  /// Main hook: given executors and a freshly loaded assembly,
  /// produce the full result for the Elm loop.
  let afterReload
    (executors: TestExecutor list)
    (asm: Assembly)
    (updatedMethodNames: string list)
    : LiveTestHookResult =
    let providers = detectProviders executors asm
    let tests = discoverTests executors asm
    let affected = findAffectedTests tests updatedMethodNames
    { DetectedProviders = providers
      DiscoveredTests = tests
      AffectedTestIds = affected }

// --- Cancellation chaining for stale work ---

/// Manages CancellationTokenSource chaining for stale work cancellation.
/// Each `next()` cancels the previous CTS and returns a fresh one.
type CancellationChain() =
  let mutable current: System.Threading.CancellationTokenSource option = None

  member _.next() =
    match current with
    | Some cts ->
      cts.Cancel()
      cts.Dispose()
    | None -> ()
    let fresh = new System.Threading.CancellationTokenSource()
    current <- Some fresh
    fresh.Token

  member _.currentToken =
    match current with
    | Some cts -> cts.Token
    | None -> System.Threading.CancellationToken.None

  member _.dispose() =
    match current with
    | Some cts ->
      cts.Cancel()
      cts.Dispose()
      current <- None
    | None -> ()

  interface IDisposable with
    member this.Dispose() = this.dispose()
