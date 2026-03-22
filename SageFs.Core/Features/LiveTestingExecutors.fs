namespace SageFs.Features.LiveTesting

open System
open System.Diagnostics
open System.Reflection
open SageFs
open SageFs.Utils

// --- Executor Types (IO side — functions that actually run tests) ---

/// Tier 1: Attribute-based test executor.
/// Core discovers tests via tree-sitter + reflection, executor just runs them.
type AttributeTestExecutor = {
  Description: AttributeProviderDescription
  Execute: MethodInfo -> Async<TestResult>
  /// Attribute names (without the "Attribute" suffix) that mark parameterised /
  /// theory tests. Methods with one of these attributes AND with InlineData
  /// counterparts are expanded into one TestCase per data row.
  /// For xunit this is ["Theory"]; for frameworks without data-driven expansion, [].
  TheoryAttributes: string list
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

// --- Reflection-based execution ---
// Defined before AttributeDiscovery so discoverWithRunner can reference executeMethodWithArgs.

module ReflectionExecutor =

  let private invokeWith (mi: MethodInfo) (args: obj array) : Async<TestResult> =
    async {
      let sw = Stopwatch.StartNew()
      try
        let instance =
          match mi.IsStatic with
          | true -> null
          | false -> Activator.CreateInstance(mi.DeclaringType)
        let result = mi.Invoke(instance, args)
        match result with
        | :? Threading.Tasks.Task as task ->
          do! Async.AwaitTask task
        | result when result <> null ->
          let t = result.GetType()
          match t.Name.StartsWith("Property") with
          | true ->
            // FsCheck.Xunit Property<_> — invoke FsCheck.Check.QuickThrowOnFailure via reflection
            let fsCheckAssembly =
              System.AppDomain.CurrentDomain.GetAssemblies()
              |> Array.tryFind (fun a -> a.GetName().Name = "FsCheck")
            match fsCheckAssembly with
            | None -> ()
            | Some asm ->
              let checkType = asm.GetType("FsCheck.Check")
              match checkType with
              | null -> ()
              | checkT ->
                let methods = checkT.GetMethods() |> Array.filter (fun m -> m.Name = "QuickThrowOnFailure")
                let genericArg = t.GetGenericArguments() |> Array.tryHead |> Option.defaultValue typeof<unit>
                let method = methods |> Array.tryFind (fun m -> m.GetParameters().Length = 1)
                match method with
                | None -> ()
                | Some m ->
                  let gm = m.MakeGenericMethod([| genericArg |])
                  gm.Invoke(null, [| result |]) |> ignore
          | false -> ()
        | _ -> ()
        sw.Stop()
        return TestResult.Passed sw.Elapsed
      with
      | :? MissingMethodException ->
        sw.Stop()
        return TestResult.Skipped "Constructor injection not yet supported"
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

  let executeMethod (mi: MethodInfo) : Async<TestResult> = invokeWith mi [||]

  /// Runs a [Theory] test method with the given [InlineData] arguments.
  let executeMethodWithArgs (mi: MethodInfo) (args: obj array) : Async<TestResult> = invokeWith mi args

// --- Attribute-based discovery ---

module AttributeDiscovery =

  let hasTestAttribute (attrs: string list) (mi: MethodInfo) : bool =
    mi.GetCustomAttributes(true)
    |> Array.exists (fun attr ->
      let attrName = attr.GetType().Name
      attrs
      |> List.exists (fun testAttr ->
        attrName = testAttr || attrName = sprintf "%sAttribute" testAttr))

  let toTestCase (framework: TestFramework) (category: TestCategory) (mi: MethodInfo) : TestCase =
    let fullName = sprintf "%s.%s" mi.DeclaringType.FullName mi.Name
    { Id = TestId.create fullName framework
      FullName = fullName
      DisplayName = mi.Name
      Origin = TestOrigin.ReflectionOnly
      Labels = []
      Framework = framework
      Category = category }

  /// Returns true if the method carries an annotation from the given theory attribute names.
  /// Checks both bare name and "Attribute"-suffixed form (e.g. "Theory" matches "TheoryAttribute").
  let private isTheoryMethod (theoryAttrNames: string list) (mi: MethodInfo) : bool =
    match theoryAttrNames with
    | [] -> false
    | names ->
      mi.GetCustomAttributes(true)
      |> Array.exists (fun attr ->
        let name = attr.GetType().Name
        names |> List.exists (fun ta -> name = ta || name = sprintf "%sAttribute" ta))

  /// Extracts the data rows from every InlineData attribute on the method.
  /// Matches attributes by "InlineDataAttribute" suffix convention (covers all xunit variants).
  /// Returns an empty array when there are no InlineData attributes (e.g. MemberData).
  let private getInlineDataRows (mi: MethodInfo) : obj array array =
    mi.GetCustomAttributes(true)
    |> Array.choose (fun attr ->
      if attr.GetType().Name.EndsWith("InlineDataAttribute") then
        match attr.GetType().GetProperty("Data") with
        | null -> None
        | prop ->
          match prop.GetValue(attr) with
          | :? (obj array) as data -> Some data
          | _ -> None
      else None)

  /// Creates a TestCase for a single Theory data row. The full name and display
  /// name include the stringified arguments so each row gets a unique identity.
  let private toTheoryTestCase (framework: TestFramework) (category: TestCategory) (mi: MethodInfo) (args: obj array) : TestCase =
    let argsStr = args |> Array.map (fun a -> if a = null then "null" else string a) |> String.concat ", "
    let fullName = sprintf "%s.%s(%s)" mi.DeclaringType.FullName mi.Name argsStr
    { Id = TestId.create fullName framework
      FullName = fullName
      DisplayName = sprintf "%s(%s)" mi.Name argsStr
      Origin = TestOrigin.ReflectionOnly
      Labels = []
      Framework = framework
      Category = category }

  /// Expands a single MethodInfo into one or more TestCases.
  /// Theory methods with InlineData rows produce N cases (one per row);
  /// all other methods (including Theory without InlineData) produce one case.
  let toTestCases (framework: TestFramework) (category: TestCategory) (theoryAttrNames: string list) (mi: MethodInfo) : TestCase list =
    match isTheoryMethod theoryAttrNames mi with
    | false -> [ toTestCase framework category mi ]
    | true ->
      match getInlineDataRows mi with
      | [||] -> [ toTestCase framework category mi ]
      | rows -> rows |> Array.map (toTheoryTestCase framework category mi) |> Array.toList

  let discoverInAssembly
    (desc: AttributeProviderDescription)
    (theoryAttrNames: string list)
    (category: TestCategory)
    (asm: Assembly)
    : TestCase list =
    try
      asm.GetExportedTypes()
      |> Array.collect (fun t ->
        t.GetMethods(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static)
        |> Array.filter (hasTestAttribute desc.TestAttributes))
      |> Array.collect (fun mi -> toTestCases desc.Name category theoryAttrNames mi |> List.toArray)
      |> Array.toList
    with
    | :? ReflectionTypeLoadException -> []
    | :? TypeLoadException -> []

  /// Discover tests with their runner closures retained for execution wiring.
  /// Theory+InlineData methods are expanded: each data row becomes a separate
  /// (TestCase * runner) pair whose runner invokes the method with that row's args.
  /// Theory expansion is triggered by attributes listed in ae.TheoryAttributes.
  /// categoryFn is called per-method to derive the correct TestCategory (e.g. Property vs Unit).
  let discoverWithRunner
    (ae: AttributeTestExecutor)
    (categoryFn: MethodInfo -> TestCategory)
    (asm: Assembly)
    : (TestCase * Async<TestResult>) list =
    try
      asm.GetExportedTypes()
      |> Array.collect (fun t ->
        t.GetMethods(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static)
        |> Array.filter (hasTestAttribute ae.Description.TestAttributes)
        |> Array.collect (fun mi ->
          let category = categoryFn mi
          match isTheoryMethod ae.TheoryAttributes mi with
          | false ->
            let tc = toTestCase ae.Description.Name category mi
            let runner = ae.Execute mi
            [| (tc, runner) |]
          | true ->
            match getInlineDataRows mi with
            | [||] ->
              // Theory without InlineData (e.g. MemberData) — one case, no args
              let tc = toTestCase ae.Description.Name category mi
              let runner = ae.Execute mi
              [| (tc, runner) |]
            | rows ->
              rows |> Array.map (fun args ->
                let tc = toTheoryTestCase ae.Description.Name category mi args
                let runner = ReflectionExecutor.executeMethodWithArgs mi args
                (tc, runner))))
      |> Array.toList
    with
    | :? ReflectionTypeLoadException -> []
    | :? TypeLoadException -> []

// --- Built-in framework executors ---

module BuiltInExecutors =

  let xunit : TestExecutor =
    TestExecutor.AttributeBased {
      Description = {
        Name = TestFramework.XUnit
        TestAttributes = ["Fact"; "Theory"; "Property"]
        AssemblyMarker = "xunit.core"
      }
      Execute = ReflectionExecutor.executeMethod
      TheoryAttributes = ["Theory"]
    }

  let xunitV3 : TestExecutor =
    TestExecutor.AttributeBased {
      Description = {
        Name = TestFramework.XUnit
        TestAttributes = ["Fact"; "Theory"; "Property"]
        AssemblyMarker = "xunit.v3.core"
      }
      Execute = ReflectionExecutor.executeMethod
      TheoryAttributes = ["Theory"]
    }

  let nunit : TestExecutor =
    TestExecutor.AttributeBased {
      Description = {
        Name = TestFramework.NUnit
        TestAttributes = ["Test"]
        AssemblyMarker = "nunit.framework"
      }
      Execute = ReflectionExecutor.executeMethod
      TheoryAttributes = []
    }

  let mstest : TestExecutor =
    TestExecutor.AttributeBased {
      Description = {
        Name = TestFramework.MSTest
        TestAttributes = ["TestMethod"]
        AssemblyMarker = "Microsoft.VisualStudio.TestPlatform.TestFramework"
      }
      Execute = ReflectionExecutor.executeMethod
      TheoryAttributes = []
    }

  let tunit : TestExecutor =
    TestExecutor.AttributeBased {
      Description = {
        Name = TestFramework.TUnit
        TestAttributes = ["Test"]
        AssemblyMarker = "TUnit.Core"
      }
      Execute = ReflectionExecutor.executeMethod
      TheoryAttributes = []
    }

  /// Reflection-based Expecto executor — no compile-time Expecto dependency.
  /// Uses reflection to call Expecto.TestModule.toTestCodeList, access FlatTest
  /// properties, and invoke test code. Exception types resolved at runtime.
  module ExpectoExecutor =
    open System.Threading

    /// Cached reflection handles for Expecto types (resolved once per assembly).
    type ReflectionCache = {
      ToTestCodeList: MethodInfo
      TestType: System.Type
      FlatTestNameProp: PropertyInfo
      FlatTestTestProp: PropertyInfo
      TestCodeTagProp: PropertyInfo
      AssertExceptionType: System.Type
      FailedExceptionType: System.Type
      IgnoreExceptionType: System.Type
      /// FsCheckConfig.defaultConfig — used when testConfig is None for AsyncFsCheck.
      FsCheckDefaultConfig: obj option
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
          let testType = expAsm.GetType("Expecto.Test")
          let flatTestType = expAsm.GetType("Expecto.FlatTest")
          let testCodeType = expAsm.GetType("Expecto.TestCode")
          match testModule = null || testType = null || flatTestType = null || testCodeType = null with
          | true -> None
          | false ->
            let toTestCodeList =
              testModule.GetMethod("toTestCodeList", BindingFlags.Public ||| BindingFlags.Static)
            match toTestCodeList = null with
            | true -> None
            | false ->
              let fsCheckDefaultConfig =
                try
                  let fscType = expAsm.GetType("Expecto.FsCheckConfig")
                  match fscType <> null with
                  | true ->
                    let defaultProp = fscType.GetProperty("defaultConfig", BindingFlags.Public ||| BindingFlags.Static)
                    match defaultProp <> null with
                    | true -> Some (defaultProp.GetValue(null))
                    | false -> None
                  | false -> None
                with _ -> None
              Some {
                ToTestCodeList = toTestCodeList
                TestType = testType
                FlatTestNameProp = flatTestType.GetProperty("name")
                FlatTestTestProp = flatTestType.GetProperty("test")
                TestCodeTagProp = testCodeType.GetProperty("Tag")
                AssertExceptionType = expAsm.GetType("Expecto.AssertException")
                FailedExceptionType = expAsm.GetType("Expecto.FailedException")
                IgnoreExceptionType = expAsm.GetType("Expecto.IgnoreException")
                FsCheckDefaultConfig = fsCheckDefaultConfig
              }
      with ex ->
        Log.warn "[LiveTesting] Expecto reflection cache build failed for %s: %s\n%s" asm.FullName ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        Instrumentation.liveTestingAssemblyLoadErrors.Add(1L)
        None

    type private ExpectoTestBinding = {
      Name: string
      ReadValue: unit -> obj
    }

    let private normalizeBindingName (memberName: string) =
      match memberName.StartsWith("get_", StringComparison.Ordinal) with
      | true -> memberName.Substring("get_".Length)
      | false -> memberName

    let private getTestBindings (cache: ReflectionCache) (t: Type) =
      let propertyBindings =
        t.GetProperties(BindingFlags.Public ||| BindingFlags.Static)
        |> Array.filter (fun (pi: PropertyInfo) ->
          pi.GetCustomAttributes(true)
          |> Array.exists (fun attr -> attr.GetType().Name = "TestsAttribute"))
        |> Array.map (fun (pi: PropertyInfo) ->
          { Name = pi.Name
            ReadValue = fun () -> pi.GetValue(null) })

      let propertyNames =
        propertyBindings
        |> Array.map (fun binding -> binding.Name)
        |> Set.ofArray

      let getterBindings =
        t.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
        |> Array.choose (fun (mi: MethodInfo) ->
          let bindingName = normalizeBindingName mi.Name
          match mi.IsSpecialName
                && mi.Name.StartsWith("get_", StringComparison.Ordinal)
                && mi.GetParameters().Length = 0
                && mi.ReturnType = cache.TestType
                && not (Set.contains bindingName propertyNames) with
          | true ->
            Some {
              Name = bindingName
              ReadValue = fun () -> mi.Invoke(null, [||])
            }
          | false -> None)

      Array.append propertyBindings getterBindings

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
    /// Tag 0=Sync (stest), 1=SyncWithCancel (stest), 2=Async (atest),
    /// 3=AsyncFsCheck (testConfig, stressConfig, test).
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
          | 3 -> // AsyncFsCheck: testConfig * stressConfig * test
            // Expecto.TestCode.AsyncFsCheck fields:
            //   testConfig  : FsCheckConfig option
            //   stressConfig: FsCheckConfig option
            //   test        : FsCheckConfig -> Async<unit>
            ct.ThrowIfCancellationRequested()
            let testObjType = rft.TestCodeObj.GetType()
            // Resolve the FsCheck config to use: prefer testConfig if Some, else default
            let config =
              match testObjType.GetProperty("testConfig") with
              | null -> cache.FsCheckDefaultConfig
              | tcProp ->
                let tcVal = tcProp.GetValue(rft.TestCodeObj)
                // testConfig is FSharpOption<FsCheckConfig> — extract via Value if Some
                match tcVal with
                | null -> cache.FsCheckDefaultConfig
                | opt ->
                  let optType = opt.GetType()
                  match optType.GetProperty("Value") with
                  | null -> cache.FsCheckDefaultConfig
                  | valProp ->
                    try Some (valProp.GetValue(opt))
                    with :? TargetInvocationException -> cache.FsCheckDefaultConfig
            // Resolve the test function: FsCheckConfig -> Async<unit>
            let testFn =
              match testObjType.GetProperty("test") with
              | null ->
                raise (System.InvalidOperationException(
                  "AsyncFsCheck: could not reflect 'test' field — " +
                  "expected a property named 'test' on the TestCode.AsyncFsCheck DU case."))
              | p -> p.GetValue(rft.TestCodeObj)
            // Invoke test(config) to get Async<unit>
            match config with
            | None ->
              raise (System.InvalidOperationException(
                "AsyncFsCheck: no FsCheckConfig available — " +
                "neither testConfig nor FsCheckConfig.defaultConfig could be resolved."))
            | Some cfg ->
              let invokeMethod = testFn.GetType().GetMethod("Invoke")
              let asyncUnit = invokeMethod.Invoke(testFn, [| cfg |])
              // Run the Async<unit> synchronously
              let runSyncMethod =
                typeof<Async>.GetMethods()
                |> Array.find (fun m ->
                  m.Name = "RunSynchronously" && m.GetParameters().Length = 3)
              let genericMethod = runSyncMethod.MakeGenericMethod([|typeof<unit>|])
              genericMethod.Invoke(
                null,
                [| asyncUnit
                   box (None: int option)
                   box (Some ct: CancellationToken option) |]) |> ignore
          | _ -> // unknown tag — skip
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
          getTestBindings cache t
          |> Array.collect (fun binding ->
            try
              let testValue = binding.ReadValue ()
              let propertyFullName = sprintf "%s.%s" t.FullName binding.Name
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
             with ex ->
              Log.warn "[LiveTesting] buildLookup binding %s.%s failed: %s\n%s" t.FullName binding.Name ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
              [||]))
        |> Map.ofArray
      with ex ->
        Log.warn "[LiveTesting] buildLookup assembly scan failed for %s: %s\n%s" asm.FullName ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
        Instrumentation.liveTestingAssemblyLoadErrors.Add(1L)
        Map.empty

    /// Discover individual leaf-level tests from all public Expecto test bindings.
    let discoverLeafTests (cache: ReflectionCache) (asm: Assembly) : TestCase list =
      try
        asm.GetExportedTypes()
        |> Array.collect (fun t ->
          getTestBindings cache t
          |> Array.collect (fun binding ->
            try
              let testValue = binding.ReadValue ()
              let propertyFullName = sprintf "%s.%s" t.FullName binding.Name
              let flatTests = cache.ToTestCodeList.Invoke(null, [|testValue|])
              let enumerable = flatTests :?> System.Collections.IEnumerable
              [ for ft in enumerable do
                  let name = cache.FlatTestNameProp.GetValue(ft) :?> string list
                  let testPath = name |> String.concat "/"
                  let fullName = sprintf "%s/%s" propertyFullName testPath
                  let displayName = name |> List.last
                  yield { Id = TestId.create fullName TestFramework.Expecto
                          FullName = fullName
                          DisplayName = displayName
                          Origin = TestOrigin.ReflectionOnly
                          Labels = []
                          Framework = TestFramework.Expecto
                          Category = CategoryDetection.categorize [] fullName TestFramework.Expecto [||] None } ]
              |> List.toArray
            with ex ->
              Log.warn "[LiveTesting] discoverLeafTests binding %s.%s failed: %s\n%s" t.FullName binding.Name ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
              [||]))
        |> Array.toList
      with
      | :? ReflectionTypeLoadException -> []
      | :? TypeLoadException -> []

  let expecto : TestExecutor =
    TestExecutor.Custom {
      Description = {
        Name = TestFramework.Expecto
        AssemblyMarker = "Expecto"
      }
      Discover = fun asm ->
        let sw = Stopwatch.StartNew()
        let result =
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
        sw.Stop()
        Instrumentation.liveTestingDiscoveryMs.Record(sw.Elapsed.TotalMilliseconds)
        result
    }

  let builtIn = [ xunit; xunitV3; nunit; mstest; tunit; expecto ]

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
    // Custom executors: discovery + execution bundled together
    let customResults =
      executors
      |> List.choose (fun executor ->
        match executor with
        | TestExecutor.Custom ce ->
          let dr = ce.Discover asm
          Some (ce.Description.Name, dr)
        | _ -> None)

    // Attribute-based: discover with runner closures retained.
    // Each MethodInfo is captured via partial application — never stored on TestCase.
    // [<Property>] (FsCheck.Xunit) is assigned TestCategory.Property; all others get Unit.
    let attrMethodCategory (mi: MethodInfo) : TestCategory =
      let isProperty =
        mi.GetCustomAttributes(true)
        |> Array.exists (fun attr ->
          let n = attr.GetType().Name
          n = "PropertyAttribute" || n = "Property")
      match isProperty with
      | true -> TestCategory.Property
      | false -> TestCategory.Unit
    let attrDiscoveries =
      executors
      |> List.collect (fun executor ->
        match executor with
        | TestExecutor.AttributeBased ae ->
          AttributeDiscovery.discoverWithRunner ae attrMethodCategory asm
        | _ -> [])

    let attrTests = attrDiscoveries |> List.map fst
    // Keyed by TestId — each attr test needs its own runner closure
    let attrRunMap =
      attrDiscoveries
      |> List.map (fun (tc, runner) -> TestId.value tc.Id, runner)
      |> Map.ofList

    let allTests =
      (customResults |> List.collect (fun (_, dr) -> dr.Tests))
      @ attrTests

    // Custom executors keyed by framework (their RunTest has internal dispatch)
    let customRunMap =
      customResults
      |> List.map (fun (fw, dr) -> fw, dr.RunTest)
      |> Map.ofList

    { Tests = allTests
      RunTest = fun testCase ->
        match Map.tryFind testCase.Framework customRunMap with
        | Some runTest -> runTest testCase
        | None ->
          match Map.tryFind (TestId.value testCase.Id) attrRunMap with
          | Some runner -> runner
          | None -> async { return TestResult.NotRun } }

  /// Thread-safe stdout capture: a single TextWriter installed on Console.Out
  /// that routes writes to the current thread's capture StringWriter (if any).
  /// Falls through to the original writer when no capture is active.
  [<Sealed>]
  type private ThreadLocalCapture(original: IO.TextWriter) =
    inherit IO.TextWriter()
    let captures = new Threading.ThreadLocal<IO.StringWriter option>(fun () -> None)
    member _.SetCapture(sw: IO.StringWriter) = captures.Value <- Some sw
    member _.ClearCapture() =
      let sw = captures.Value
      captures.Value <- None
      sw
    override _.Encoding = original.Encoding
    override _.Write(c: char) =
      match captures.Value with
      | Some sw -> sw.Write(c)
      | None -> original.Write(c)
    override _.Write(s: string) =
      match captures.Value with
      | Some sw -> sw.Write(s)
      | None -> original.Write(s)
    override _.WriteLine(s: string) =
      match captures.Value with
      | Some sw -> sw.WriteLine(s)
      | None -> original.WriteLine(s)
    override _.Flush() = original.Flush()

  let private threadCapture =
    let tc = new ThreadLocalCapture(Console.Out)
    Console.SetOut(tc)
    tc

  let executeOne
    (runTest: TestCase -> Async<TestResult>)
    (testCase: TestCase)
    : Async<TestRunResult> =
    async {
      let activity = LiveTestingInstrumentation.activitySource.StartActivity("live_testing.test.execute")
      match isNull activity with
      | false ->
        activity.SetTag("test.id", testCase.Id) |> ignore
        activity.SetTag("test.name", testCase.DisplayName) |> ignore
        activity.SetTag("test.framework", testCase.Framework) |> ignore
      | true -> ()
      let sw = Stopwatch.StartNew()
      let perTestTimeout = Timeouts.perTestDefault()
      let stdoutCapture = new IO.StringWriter()
      // Install per-thread capture — no global SetOut calls needed
      threadCapture.SetCapture(stdoutCapture)
      let! result =
        async {
          try
            use cts = new Threading.CancellationTokenSource()
            let testTask = Async.StartAsTask(runTest testCase, cancellationToken = cts.Token)
            let timeoutTask = Threading.Tasks.Task.Delay(perTestTimeout)
            let! winner = Threading.Tasks.Task.WhenAny(testTask, timeoutTask) |> Async.AwaitTask
            match Object.ReferenceEquals(winner, timeoutTask) with
            | true ->
              cts.Cancel()
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
      threadCapture.ClearCapture() |> ignore
      sw.Stop()
      let durationMs = sw.Elapsed.TotalMilliseconds
      LiveTestingInstrumentation.perTestDurationMs.Record(durationMs)
      LiveTestingInstrumentation.testsCompleted.Add(1L)
      let resultKind =
        match result with
        | TestResult.Passed _ ->
          LiveTestingInstrumentation.testsPassed.Add(1L)
          "passed"
        | TestResult.Failed _ ->
          LiveTestingInstrumentation.testsFailed.Add(1L)
          "failed"
        | TestResult.Skipped msg when msg.StartsWith("Timed out") ->
          LiveTestingInstrumentation.testsTimedOut.Add(1L)
          "timed_out"
        | TestResult.Skipped _ -> "skipped"
        | TestResult.NotRun -> "not_run"
      match isNull activity with
      | false ->
        activity.SetTag("test.result", resultKind) |> ignore
        activity.SetTag("test.duration_ms", durationMs) |> ignore
        match result with
        | TestResult.Failed (failure, _) ->
          activity.SetTag("error", true) |> ignore
          activity.SetTag("error.message", sprintf "%A" failure) |> ignore
          activity.SetStatus(ActivityStatusCode.Error, sprintf "%A" failure) |> ignore
        | _ -> ()
        activity.Stop()
        activity.Dispose()
      | true -> ()
      let captured = stdoutCapture.ToString()
      let output =
        match String.IsNullOrWhiteSpace captured with
        | true -> None
        | false -> Some (captured.TrimEnd())
      return {
        TestId = testCase.Id
        TestName = testCase.DisplayName
        Result = result
        Timestamp = DateTimeOffset.UtcNow
        Output = output
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
      use globalCts = Threading.CancellationTokenSource.CreateLinkedTokenSource(ct)
      globalCts.CancelAfter(Timeouts.globalTestRun())
      let totalSw = Stopwatch.StartNew()
      let totalChunks = (tests.Length + maxParallelism - 1) / maxParallelism
      let mutable chunkIndex = 0
      // Process in chunks to avoid scheduling all tests to ThreadPool at once
      let chunkSize = maxParallelism
      for chunkStart in 0 .. chunkSize .. tests.Length - 1 do
        globalCts.Token.ThrowIfCancellationRequested()
        let chunkEnd = min (chunkStart + chunkSize) tests.Length
        let chunk = tests.[chunkStart .. chunkEnd - 1]
        let chunkActivity = LiveTestingInstrumentation.activitySource.StartActivity("live_testing.chunk")
        match isNull chunkActivity with
        | false ->
          chunkActivity.SetTag("chunk.index", chunkIndex) |> ignore
          chunkActivity.SetTag("chunk.size", chunk.Length) |> ignore
          chunkActivity.SetTag("chunk.total", totalChunks) |> ignore
        | true -> ()
        let chunkSw = Stopwatch.StartNew()
        let! _ =
          chunk
          |> Array.map (fun tc ->
            async {
              let! result = executeOne runTest tc
              onResult result
            })
          |> Async.Parallel
        chunkSw.Stop()
        LiveTestingInstrumentation.chunkDurationMs.Record(chunkSw.Elapsed.TotalMilliseconds)
        LiveTestingInstrumentation.chunksCompleted.Add(1L)
        match isNull chunkActivity with
        | false ->
          chunkActivity.SetTag("chunk.duration_ms", chunkSw.Elapsed.TotalMilliseconds) |> ignore
          chunkActivity.Stop()
          chunkActivity.Dispose()
        | true -> ()
        chunkIndex <- chunkIndex + 1
      totalSw.Stop()
      LiveTestingInstrumentation.executionHistogram.Record(totalSw.Elapsed.TotalMilliseconds)
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
      with ex ->
        Log.warn "[LiveTesting] GetReferencedAssemblies failed for %s: %s" (asm.GetName().Name) ex.Message
        Set.empty
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
      with ex ->
        Log.warn "[LiveTesting] GetReferencedAssemblies failed for %s: %s" (asm.GetName().Name) ex.Message
        Set.empty
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
      match Array.isEmpty matched with
      | true ->
        LiveTestingInstrumentation.depGraphFallbackTotal.Add(1L)
        findAllTestIds discoveredTests
      | false ->
        LiveTestingInstrumentation.depGraphMatchTotal.Add(1L)
        matched

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

type RebuildCancellationRegistry() =
  let gate = obj()
  let currentBySession = System.Collections.Generic.Dictionary<string, int64>()
  let tokensByIdentity =
    System.Collections.Generic.Dictionary<string * int64, System.Threading.CancellationTokenSource>()

  let sessionKey targetSession =
    targetSession |> Option.defaultValue ""

  member _.start(targetSession: string option, generation: int64) =
    lock gate (fun () ->
      let key = sessionKey targetSession
      match currentBySession.TryGetValue key with
      | true, previousGeneration ->
          match tokensByIdentity.TryGetValue((key, previousGeneration)) with
          | true, previousCts ->
              previousCts.Cancel()
          | _ ->
              ()
      | _ ->
          ()

      let fresh = new System.Threading.CancellationTokenSource()
      currentBySession.[key] <- generation
      tokensByIdentity.[(key, generation)] <- fresh
      fresh.Token)

  member _.cancel(targetSession: string option, generation: int64) =
    lock gate (fun () ->
      let key = sessionKey targetSession
      match tokensByIdentity.TryGetValue((key, generation)) with
      | true, cts ->
          cts.Cancel()
          true
      | _ ->
          false)

  member _.complete(targetSession: string option, generation: int64) =
    lock gate (fun () ->
      let key = sessionKey targetSession
      match tokensByIdentity.TryGetValue((key, generation)) with
      | true, cts ->
          tokensByIdentity.Remove((key, generation)) |> ignore
          match currentBySession.TryGetValue key with
          | true, currentGeneration when currentGeneration = generation ->
              currentBySession.Remove key |> ignore
          | _ ->
              ()
          cts.Dispose()
      | _ ->
          ())

  member _.dispose() =
    lock gate (fun () ->
      for KeyValue(_, cts) in tokensByIdentity do
        cts.Cancel()
        cts.Dispose()
      tokensByIdentity.Clear()
      currentBySession.Clear())

  interface System.IDisposable with
    member this.Dispose() = this.dispose()

/// Manages cancellation tokens for each test cycle stage.
type TestCycleCancellation = {
  Discovery: CancellationChain
  TreeSitter: CancellationChain
  Fcs: CancellationChain
  TestRun: CancellationChain
  Rebuild: RebuildCancellationRegistry
}

module TestCycleCancellation =
  let create () = {
    Discovery = new CancellationChain()
    TreeSitter = new CancellationChain()
    Fcs = new CancellationChain()
    TestRun = new CancellationChain()
    Rebuild = new RebuildCancellationRegistry()
  }

  /// Cancel previous work and get a fresh token for the specified effect.
  let tokenForEffect (effect: TestCycleEffect) (pc: TestCycleCancellation) : System.Threading.CancellationToken =
    match effect with
    | TestCycleEffect.RequestInitialDiscovery -> pc.Discovery.next()
    | TestCycleEffect.ParseTreeSitter _ -> pc.TreeSitter.next()
    | TestCycleEffect.RequestFcsTypeCheck _ -> pc.Fcs.next()
    | TestCycleEffect.RunAffectedTests _ -> pc.TestRun.next()
    | TestCycleEffect.CancelRebuild (sessionId, generation) ->
        pc.Rebuild.cancel(sessionId, generation) |> ignore
        System.Threading.CancellationToken.None
    | TestCycleEffect.RequestRebuild (generation, req) ->
        pc.Rebuild.start(req.SessionId, generation)
    | TestCycleEffect.RegisterFileWatcher _ -> System.Threading.CancellationToken.None
    | TestCycleEffect.DisposeFileWatcher _ -> System.Threading.CancellationToken.None

  let dispose (pc: TestCycleCancellation) =
    pc.Discovery.dispose()
    pc.TreeSitter.dispose()
    pc.Fcs.dispose()
    pc.TestRun.dispose()
    pc.Rebuild.dispose()
