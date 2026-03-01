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

/// Pure result from discovery: tests + how to run them.
/// The RunTest closure captures cache/lookup from the SAME discovery call.
/// No mutable ref needed — this IS the execution capability.
type DiscoveryResult = {
  Tests: TestCase list
  RunTest: TestCase -> Async<TestResult>
}

/// Tier 2: Custom test executor.
/// Provider handles its own discovery (e.g., Expecto value-based tests).
/// Discover returns DiscoveryResult — both tests AND execution capability.
type CustomTestExecutor = {
  Description: CustomProviderDescription
  Discover: Assembly -> DiscoveryResult
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

  let hasTestAttribute (attrs: string list) (mi: MethodInfo) : bool =
    mi.GetCustomAttributes(true)
    |> Array.exists (fun attr ->
      let attrName = attr.GetType().Name
      attrs
      |> List.exists (fun testAttr ->
        attrName = testAttr || attrName = sprintf "%sAttribute" testAttr))

  let toTestCase (framework: string) (category: TestCategory) (mi: MethodInfo) : TestCase =
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
          match mi.IsStatic with
          | true -> null
          | false -> Activator.CreateInstance(mi.DeclaringType)
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
        let inner =
          match tie.InnerException <> null with
          | true -> tie.InnerException
          | false -> tie :> exn
        let isAssertion =
          inner.GetType().Name.Contains("Assert")
          || inner.GetType().Name.Contains("Expect")
        match isAssertion with
        | true ->
          return TestResult.Failed (TestFailure.AssertionFailed inner.Message, sw.Elapsed)
        | false ->
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

  /// Reflection-based Expecto executor — no compile-time Expecto dependency.
  /// Uses reflection to call Expecto.TestModule.toTestCodeList, access FlatTest
  /// properties, and invoke test code. Exception types resolved at runtime.
  module ExpectoExecutor =
    open System.Threading

    /// Cached reflection handles for Expecto types (resolved once per assembly).
    type ReflectionCache = {
      ToTestCodeList: MethodInfo
      FlatTestNameProp: PropertyInfo
      FlatTestTestProp: PropertyInfo
      TestCodeTagProp: PropertyInfo
      AssertExceptionType: System.Type
      FailedExceptionType: System.Type
      IgnoreExceptionType: System.Type
    }

    /// Per-leaf-test: the boxed TestCode DU value + its tag for dispatch.
    type ReflectedFlatTest = {
      TestCodeObj: obj
      Tag: int
    }

    /// Try to build reflection cache from an assembly that references Expecto.
    let tryBuildCache (asm: Assembly) : ReflectionCache option =
      try
        let expectoRef =
          asm.GetReferencedAssemblies()
          |> Array.tryFind (fun a -> a.Name = "Expecto")
        match expectoRef with
        | None -> None
        | Some asmName ->
          let expAsm = Assembly.Load(asmName)
          let testModule = expAsm.GetType("Expecto.TestModule")
          let flatTestType = expAsm.GetType("Expecto.FlatTest")
          let testCodeType = expAsm.GetType("Expecto.TestCode")
          match testModule = null || flatTestType = null || testCodeType = null with
          | true -> None
          | false ->
            let toTestCodeList =
              testModule.GetMethod("toTestCodeList", BindingFlags.Public ||| BindingFlags.Static)
            match toTestCodeList = null with
            | true -> None
            | false ->
              Some {
                ToTestCodeList = toTestCodeList
                FlatTestNameProp = flatTestType.GetProperty("name")
                FlatTestTestProp = flatTestType.GetProperty("test")
                TestCodeTagProp = testCodeType.GetProperty("Tag")
                AssertExceptionType = expAsm.GetType("Expecto.AssertException")
                FailedExceptionType = expAsm.GetType("Expecto.FailedException")
                IgnoreExceptionType = expAsm.GetType("Expecto.IgnoreException")
              }
      with _ -> None

    /// Map an exception to TestResult using reflection-resolved Expecto types.
    let mapException (cache: ReflectionCache) (ex: exn) (elapsed: TimeSpan) =
      let exType = ex.GetType()
      match cache.AssertExceptionType <> null && cache.AssertExceptionType.IsAssignableFrom(exType) with
      | true ->
        TestResult.Failed(TestFailure.AssertionFailed ex.Message, elapsed)
      | false ->
        match cache.FailedExceptionType <> null && cache.FailedExceptionType.IsAssignableFrom(exType) with
        | true ->
          TestResult.Failed(TestFailure.AssertionFailed ex.Message, elapsed)
        | false ->
          match cache.IgnoreExceptionType <> null && cache.IgnoreExceptionType.IsAssignableFrom(exType) with
          | true ->
            TestResult.Skipped ex.Message
          | false ->
            match ex :? OperationCanceledException with
            | true ->
              TestResult.Skipped "Cancelled"
            | false ->
              TestResult.Failed(
                TestFailure.ExceptionThrown(
                  ex.Message,
                  ex.StackTrace |> Option.ofObj |> Option.defaultValue ""),
                elapsed)

    /// Execute a reflected test code via reflection.
    /// Tag 0=Sync (stest), 1=SyncWithCancel (stest), 2=Async (atest), 3+=skip.
    let executeReflected
      (cache: ReflectionCache)
      (rft: ReflectedFlatTest)
      (ct: CancellationToken)
      : Async<TestResult> =
      async {
        let sw = Stopwatch.StartNew()
        try
          match rft.Tag with
          | 0 -> // Sync: stest is FSharpFunc<unit, unit>
            ct.ThrowIfCancellationRequested()
            let stestProp = rft.TestCodeObj.GetType().GetProperty("stest")
            let syncFn = stestProp.GetValue(rft.TestCodeObj)
            let invokeMethod = syncFn.GetType().GetMethod("Invoke", [|typeof<unit>|])
            invokeMethod.Invoke(syncFn, [|box ()|]) |> ignore
          | 1 -> // SyncWithCancel: stest is FSharpFunc<CancellationToken, unit>
            let stestProp = rft.TestCodeObj.GetType().GetProperty("stest")
            let cancelFn = stestProp.GetValue(rft.TestCodeObj)
            let invokeMethod =
              cancelFn.GetType().GetMethod("Invoke", [|typeof<CancellationToken>|])
            invokeMethod.Invoke(cancelFn, [|box ct|]) |> ignore
          | 2 -> // Async: atest is FSharpAsync<unit>
            let atestProp = rft.TestCodeObj.GetType().GetProperty("atest")
            let asyncComp = atestProp.GetValue(rft.TestCodeObj)
            let runSyncMethod =
              typeof<Async>.GetMethods()
              |> Array.find (fun m ->
                m.Name = "RunSynchronously" && m.GetParameters().Length = 3)
            let genericMethod = runSyncMethod.MakeGenericMethod([|typeof<unit>|])
            genericMethod.Invoke(
              null,
              [| asyncComp
                 box (None: int option)
                 box (Some ct: CancellationToken option) |]) |> ignore
          | _ -> // AsyncFsCheck or unknown — skip
            ct.ThrowIfCancellationRequested()
          sw.Stop()
          return TestResult.Passed sw.Elapsed
        with
        | :? TargetInvocationException as tie ->
          sw.Stop()
          let inner =
            match tie.InnerException <> null with
            | true -> tie.InnerException
            | false -> tie :> exn
          return mapException cache inner sw.Elapsed
        | :? OperationCanceledException ->
          return TestResult.Skipped "Cancelled"
        | ex ->
          sw.Stop()
          return mapException cache ex sw.Elapsed
      }

    /// Build a lookup from FullName → ReflectedFlatTest for leaf-level execution.
    let buildLookup (cache: ReflectionCache) (asm: Assembly) : Map<string, ReflectedFlatTest> =
      try
        asm.GetExportedTypes()
        |> Array.collect (fun t ->
          t.GetProperties(BindingFlags.Public ||| BindingFlags.Static)
          |> Array.filter (fun (pi: PropertyInfo) ->
            pi.GetCustomAttributes(true)
            |> Array.exists (fun attr -> attr.GetType().Name = "TestsAttribute"))
          |> Array.collect (fun (pi: PropertyInfo) ->
            try
              let testValue = pi.GetValue(null)
              let propertyFullName = sprintf "%s.%s" t.FullName pi.Name
              let flatTests = cache.ToTestCodeList.Invoke(null, [|testValue|])
              let enumerable = flatTests :?> System.Collections.IEnumerable
              [ for ft in enumerable do
                  let name = cache.FlatTestNameProp.GetValue(ft) :?> string list
                  let testCode = cache.FlatTestTestProp.GetValue(ft)
                  let tag = cache.TestCodeTagProp.GetValue(testCode) :?> int
                  let testPath = name |> String.concat "/"
                  let fullName = sprintf "%s/%s" propertyFullName testPath
                  yield fullName, { TestCodeObj = testCode; Tag = tag } ]
              |> List.toArray
            with _ -> [||]))
        |> Map.ofArray
      with _ -> Map.empty

    /// Discover individual leaf-level tests from all [<Tests>] properties.
    let discoverLeafTests (cache: ReflectionCache) (asm: Assembly) : TestCase list =
      try
        asm.GetExportedTypes()
        |> Array.collect (fun t ->
          t.GetProperties(BindingFlags.Public ||| BindingFlags.Static)
          |> Array.filter (fun (pi: PropertyInfo) ->
            pi.GetCustomAttributes(true)
            |> Array.exists (fun attr -> attr.GetType().Name = "TestsAttribute"))
          |> Array.collect (fun (pi: PropertyInfo) ->
            try
              let testValue = pi.GetValue(null)
              let propertyFullName = sprintf "%s.%s" t.FullName pi.Name
              let flatTests = cache.ToTestCodeList.Invoke(null, [|testValue|])
              let enumerable = flatTests :?> System.Collections.IEnumerable
              [ for ft in enumerable do
                  let name = cache.FlatTestNameProp.GetValue(ft) :?> string list
                  let testPath = name |> String.concat "/"
                  let fullName = sprintf "%s/%s" propertyFullName testPath
                  let displayName = name |> List.last
                  yield { Id = TestId.create fullName "expecto"
                          FullName = fullName
                          DisplayName = displayName
                          Origin = TestOrigin.ReflectionOnly
                          Labels = []
                          Framework = "expecto"
                          Category = CategoryDetection.categorize [] fullName "expecto" [||] } ]
              |> List.toArray
            with _ -> [||]))
        |> Array.toList
      with
      | :? ReflectionTypeLoadException -> []
      | :? TypeLoadException -> []

  let expecto : TestExecutor =
    TestExecutor.Custom {
      Description = {
        Name = "expecto"
        AssemblyMarker = "Expecto"
      }
      Discover = fun asm ->
        match ExpectoExecutor.tryBuildCache asm with
        | Some cache ->
          let lookup = ExpectoExecutor.buildLookup cache asm
          let tests = ExpectoExecutor.discoverLeafTests cache asm
          { Tests = tests
            RunTest = fun testCase ->
              async {
                let! ct = Async.CancellationToken
                match Map.tryFind testCase.FullName lookup with
                | Some rft -> return! ExpectoExecutor.executeReflected cache rft ct
                | None -> return TestResult.NotRun
              } }
        | None ->
          { Tests = []; RunTest = fun _ -> async { return TestResult.NotRun } }
    }

  let builtIn = [ xunit; nunit; mstest; tunit; expecto ]

  let descriptions : ProviderDescription list =
    builtIn |> List.map TestExecutor.description

// --- Test orchestration ---

module TestOrchestrator =

  /// Discovery result with composed RunTest from all executors
  type DiscoverAllResult = {
    Tests: TestCase list
    RunTest: TestCase -> Async<TestResult>
  }

  let discoverAll
    (executors: TestExecutor list)
    (asm: Assembly)
    : DiscoverAllResult =
    let customResults =
      executors
      |> List.choose (fun executor ->
        match executor with
        | TestExecutor.Custom ce ->
          let dr = ce.Discover asm
          Some (ce.Description.Name, dr)
        | _ -> None)
    let attrTests =
      executors
      |> List.collect (fun executor ->
        match executor with
        | TestExecutor.AttributeBased ae ->
          AttributeDiscovery.discoverInAssembly ae.Description TestCategory.Unit asm
        | _ -> [])
    let allTests =
      (customResults |> List.collect (fun (_, dr) -> dr.Tests))
      @ attrTests
    let runTestByFramework =
      customResults
      |> List.map (fun (fw, dr) -> fw, dr.RunTest)
      |> Map.ofList
    { Tests = allTests
      RunTest = fun testCase ->
        match Map.tryFind testCase.Framework runTestByFramework with
        | Some runTest -> runTest testCase
        | None -> async { return TestResult.NotRun } }

  let executeOne
    (runTest: TestCase -> Async<TestResult>)
    (testCase: TestCase)
    : Async<TestRunResult> =
    async {
      let sw = Stopwatch.StartNew()
      let perTestTimeout = TimeSpan.FromSeconds 5.0
      let! result =
        async {
          try
            let testTask = Async.StartAsTask(runTest testCase)
            let timeoutTask = Threading.Tasks.Task.Delay(perTestTimeout)
            let! winner = Threading.Tasks.Task.WhenAny(testTask, timeoutTask) |> Async.AwaitTask
            match Object.ReferenceEquals(winner, timeoutTask) with
            | true ->
              sw.Stop()
              return TestResult.Skipped (sprintf "Timed out after %gs" perTestTimeout.TotalSeconds)
            | false ->
              return! testTask |> Async.AwaitTask
          with
          | :? OperationCanceledException ->
            sw.Stop()
            return TestResult.Skipped "Cancelled"
          | :? System.AggregateException as ae when (ae.InnerException :? OperationCanceledException) ->
            sw.Stop()
            return TestResult.Skipped "Cancelled"
          | ex ->
            sw.Stop()
            return TestResult.Failed(
              TestFailure.ExceptionThrown(
                ex.Message,
                ex.StackTrace |> Option.ofObj |> Option.defaultValue ""),
              sw.Elapsed)
        }
      return {
        TestId = testCase.Id
        TestName = testCase.DisplayName
        Result = result
        Timestamp = DateTimeOffset.UtcNow
      }
    }

  let executeFiltered
    (runTest: TestCase -> Async<TestResult>)
    (onResult: TestRunResult -> unit)
    (maxParallelism: int)
    (tests: TestCase array)
    (ct: Threading.CancellationToken)
    : Async<unit> =
    async {
      // Process in chunks to avoid scheduling all tests to ThreadPool at once
      let chunkSize = maxParallelism
      for chunkStart in 0 .. chunkSize .. tests.Length - 1 do
        ct.ThrowIfCancellationRequested()
        let chunkEnd = min (chunkStart + chunkSize) tests.Length
        let chunk = tests.[chunkStart .. chunkEnd - 1]
        let! _ =
          chunk
          |> Array.map (fun tc ->
            async {
              let! result = executeOne runTest tc
              onResult result
            })
          |> Async.Parallel
        ()
    }

// --- Hot-reload integration hook ---

/// Pure data returned by the live testing hook after a hot reload.
/// The Elm loop dispatches this as events (ProvidersDetected, TestsDiscovered, etc.)
/// RunTest is the composed execution function from all discoverers.
type LiveTestHookResult = {
  DetectedProviders: ProviderDescription list
  DiscoveredTests: TestCase array
  AffectedTestIds: TestId array
  RunTest: TestCase -> Async<TestResult>
}

module LiveTestHookResult =
  let noOp : TestCase -> Async<TestResult> =
    fun _ -> async { return TestResult.NotRun }
  let empty = {
    DetectedProviders = []
    DiscoveredTests = [||]
    AffectedTestIds = [||]
    RunTest = noOp
  }

/// Serializable subset of LiveTestHookResult for cross-process transport.
/// RunTest is a closure and cannot cross process boundaries.
type LiveTestHookResultDto = {
  DetectedProviders: ProviderDescription list
  DiscoveredTests: TestCase array
  AffectedTestIds: TestId array
}

module LiveTestHookResultDto =
  let fromResult (r: LiveTestHookResult) : LiveTestHookResultDto =
    { DetectedProviders = r.DetectedProviders
      DiscoveredTests = r.DiscoveredTests
      AffectedTestIds = r.AffectedTestIds }

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
        match referencedNames.Contains ae.Description.AssemblyMarker with
        | true -> Some (ProviderDescription.AttributeBased ae.Description)
        | false -> None
      | TestExecutor.Custom ce ->
        match referencedNames.Contains ce.Description.AssemblyMarker with
        | true -> Some (ProviderDescription.Custom ce.Description)
        | false -> None)

  /// Discover all tests in an assembly using matching executors.
  /// Returns tests and a composed RunTest function.
  let discoverTests
    (executors: TestExecutor list)
    (asm: Assembly)
    : TestOrchestrator.DiscoverAllResult =
    let referencedNames =
      try
        asm.GetReferencedAssemblies()
        |> Array.map (fun a -> a.Name)
        |> Set.ofArray
      with _ -> Set.empty
    let matchingExecutors =
      executors
      |> List.filter (fun executor ->
        match executor with
        | TestExecutor.AttributeBased ae ->
          referencedNames.Contains ae.Description.AssemblyMarker
        | TestExecutor.Custom ce ->
          referencedNames.Contains ce.Description.AssemblyMarker)
    TestOrchestrator.discoverAll matchingExecutors asm

  /// Returns ALL discovered test IDs. Used by explicit "run all" triggers
  /// and as conservative fallback when no specific match is found.
  let findAllTestIds (discoveredTests: TestCase array) : TestId array =
    discoveredTests |> Array.map (fun t -> t.Id)

  /// Find which discovered tests are affected by updated method names.
  /// Simple name matching — FCS-based matching comes in Phase 4.
  /// Empty updatedMethodNames means nothing changed — returns empty.
  /// Conservative fallback: when methods changed but none match by name,
  /// run ALL discovered tests rather than silently skipping them.
  let findAffectedTests
    (discoveredTests: TestCase array)
    (updatedMethodNames: string list)
    : TestId array =
    match List.isEmpty updatedMethodNames with
    | true -> Array.empty
    | false ->
      let matched =
        discoveredTests
        |> Array.filter (fun tc ->
          updatedMethodNames
          |> List.exists (fun updated ->
            tc.FullName.Contains updated
            || updated.Contains (tc.FullName.Split('.').[0])))
        |> Array.map (fun t -> t.Id)
      // Conservative fallback: if nothing matched, run everything.
      // Better to run extra tests than silently miss affected ones.
      match Array.isEmpty matched with
      | true -> findAllTestIds discoveredTests
      | false -> matched

  /// Main hook: given executors and a freshly loaded assembly,
  /// produce the full result for the Elm loop.
  let afterReload
    (executors: TestExecutor list)
    (asm: Assembly)
    (updatedMethodNames: string list)
    : LiveTestHookResult =
    let providers = detectProviders executors asm
    let discovery = discoverTests executors asm
    let tests = discovery.Tests |> Array.ofList
    let affected = findAffectedTests tests updatedMethodNames
    { DetectedProviders = providers
      DiscoveredTests = tests
      AffectedTestIds = affected
      RunTest = discovery.RunTest }

// --- Cancellation chaining for stale work ---

/// Manages CancellationTokenSource chaining for stale work cancellation.
/// Each `next()` cancels the previous CTS and returns a fresh one.
type CancellationChain() =
  let mutable current: System.Threading.CancellationTokenSource option = None

  member _.next() =
    match current with
    | Some cts ->
      cts.Cancel()
      // Don't dispose here — mid-flight async code may still reference the token.
      // Let GC collect cancelled CTS instances. dispose() handles orderly shutdown.
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

/// Manages cancellation tokens for each pipeline stage.
type PipelineCancellation = {
  TreeSitter: CancellationChain
  Fcs: CancellationChain
  TestRun: CancellationChain
}

module PipelineCancellation =
  let create () = {
    TreeSitter = new CancellationChain()
    Fcs = new CancellationChain()
    TestRun = new CancellationChain()
  }

  /// Cancel previous work and get a fresh token for the specified effect.
  let tokenForEffect (effect: PipelineEffect) (pc: PipelineCancellation) : System.Threading.CancellationToken =
    match effect with
    | PipelineEffect.ParseTreeSitter _ -> pc.TreeSitter.next()
    | PipelineEffect.RequestFcsTypeCheck _ -> pc.Fcs.next()
    | PipelineEffect.RunAffectedTests _ -> pc.TestRun.next()

  let dispose (pc: PipelineCancellation) =
    pc.TreeSitter.dispose()
    pc.Fcs.dispose()
    pc.TestRun.dispose()
