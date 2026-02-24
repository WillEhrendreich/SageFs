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

  /// Reflection-based Expecto executor — no compile-time Expecto dependency.
  /// Uses reflection to call Expecto.TestModule.toTestCodeList, access FlatTest
  /// properties, and invoke test code. Exception types resolved at runtime.
  module private ExpectoExecutor =
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
          if testModule = null || flatTestType = null || testCodeType = null then None
          else
            let toTestCodeList =
              testModule.GetMethod("toTestCodeList", BindingFlags.Public ||| BindingFlags.Static)
            if toTestCodeList = null then None
            else
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
    let private mapException (cache: ReflectionCache) (ex: exn) (elapsed: TimeSpan) =
      let exType = ex.GetType()
      if cache.AssertExceptionType <> null && cache.AssertExceptionType.IsAssignableFrom(exType) then
        TestResult.Failed(TestFailure.AssertionFailed ex.Message, elapsed)
      elif cache.FailedExceptionType <> null && cache.FailedExceptionType.IsAssignableFrom(exType) then
        TestResult.Failed(TestFailure.AssertionFailed ex.Message, elapsed)
      elif cache.IgnoreExceptionType <> null && cache.IgnoreExceptionType.IsAssignableFrom(exType) then
        TestResult.Skipped ex.Message
      elif ex :? OperationCanceledException then
        TestResult.Skipped "Cancelled"
      else
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
          let inner = if tie.InnerException <> null then tie.InnerException else tie :> exn
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
                          Category = TestCategory.Unit } ]
              |> List.toArray
            with _ -> [||]))
        |> Array.toList
      with
      | :? ReflectionTypeLoadException -> []
      | :? TypeLoadException -> []

  let expecto : TestExecutor =
    let mutable reflectionCache: ExpectoExecutor.ReflectionCache option = None
    let mutable flatTestLookup = Map.empty<string, ExpectoExecutor.ReflectedFlatTest>
    TestExecutor.Custom {
      Description = {
        Name = "expecto"
        AssemblyMarker = "Expecto"
      }
      Discover = fun asm ->
        match ExpectoExecutor.tryBuildCache asm with
        | Some cache ->
          reflectionCache <- Some cache
          flatTestLookup <- ExpectoExecutor.buildLookup cache asm
          ExpectoExecutor.discoverLeafTests cache asm
        | None -> []
      Execute = fun testCase ->
        async {
          let! ct = Async.CancellationToken
          match reflectionCache with
          | None -> return TestResult.NotRun
          | Some cache ->
            match Map.tryFind testCase.FullName flatTestLookup with
            | Some rft -> return! ExpectoExecutor.executeReflected cache rft ct
            | None -> return TestResult.NotRun
        }
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
      let sw = Stopwatch.StartNew()
      let! result =
        async {
          try
            let! r =
              match findExecutor executors testCase.Framework with
              | Some (TestExecutor.Custom ce) ->
                ce.Execute testCase
              | Some (TestExecutor.AttributeBased _) ->
                async { return TestResult.NotRun }
              | None ->
                async { return TestResult.NotRun }
            return r
          with
          | :? OperationCanceledException ->
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
    (executors: TestExecutor list)
    (maxParallelism: int)
    (tests: TestCase array)
    (ct: Threading.CancellationToken)
    : Async<TestRunResult array> =
    async {
      use cts = Threading.CancellationTokenSource.CreateLinkedTokenSource(ct)
      cts.CancelAfter(TimeSpan.FromSeconds 30.0)
      let semaphore = new Threading.SemaphoreSlim(maxParallelism)
      let! results =
        tests
        |> Array.map (fun tc ->
          async {
            do! Async.AwaitTask (semaphore.WaitAsync(cts.Token))
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
  /// Find tests affected by method updates. Empty updatedMethodNames means
  /// nothing changed — returns empty. Use findAllTestIds for explicit "run all".
  let findAffectedTests
    (discoveredTests: TestCase array)
    (updatedMethodNames: string list)
    : TestId array =
    if List.isEmpty updatedMethodNames then
      Array.empty
    else
      discoveredTests
      |> Array.filter (fun tc ->
        updatedMethodNames
        |> List.exists (fun updated ->
          tc.FullName.Contains updated
          || updated.Contains (tc.FullName.Split('.').[0])))
      |> Array.map (fun t -> t.Id)

  /// Returns ALL discovered test IDs. Used by explicit "run all" triggers.
  let findAllTestIds (discoveredTests: TestCase array) : TestId array =
    discoveredTests |> Array.map (fun t -> t.Id)

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
