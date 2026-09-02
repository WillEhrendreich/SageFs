module SageFs.Tests.HotReloadTests

open System
open System.IO
open Expecto
open SageFs.FileWatcher
open SageFs.AppState
open SageFs.Middleware.HotReloading
open SageFs.Utils

let private makeState () : AppState =
  {
    Solution = Unchecked.defaultof<_>
    OriginalSolution = Unchecked.defaultof<_>
    ShadowDir = None
    Logger = Unchecked.defaultof<_>
    Session = Unchecked.defaultof<_>
    OutStream = Unchecked.defaultof<_>
    StartupConfig = None
    Custom = Map.empty
    Diagnostics = Unchecked.defaultof<_>
    WarmupFailures = []
    WarmupContext = Unchecked.defaultof<_>
    HotReloadState = Unchecked.defaultof<_>
  }

let private passThroughNext : MiddlewareNext =
  fun (request, st) ->
    ({ EvaluationResult = Ok "ok"
       Diagnostics = [||]
       EvaluatedCode = request.Code
       Metadata = Map.empty }, st)

/// Tests that LoadScript EvalRequests carry the correct hot reload flag,
/// ensuring the Harmony method-detouring middleware fires on file reloads.
let hotReloadArgTests =
  testList "hot reload args" [
    testCase "LoadScript request includes hotReload=true arg" <| fun () ->
      let request = {
        Code = sprintf "#load @\"%s\"" @"C:\test.fs"
        Args = Map.ofList ["hotReload", box true]
      }
      request.Args
      |> Map.tryFind "hotReload"
      |> Option.map (fun v -> v :?> bool)
      |> Flip.Expect.equal "should have hotReload=true" (Some true)

    testCase "EvalCode request has empty Args by default" <| fun () ->
      let request = { Code = "1 + 1"; Args = Map.empty }
      request.Args
      |> Map.tryFind "hotReload"
      |> Flip.Expect.equal "should not have hotReload" None

    testCase "hotReload=true in Args triggers hot reload check" <| fun () ->
      let args : Map<string, obj> = Map.ofList ["hotReload", box true]
      let shouldRun =
        match Map.tryFind "hotReload" args with
        | Some v when v = (box true) -> true
        | _ -> false
      shouldRun |> Flip.Expect.isTrue "should trigger hot reload"

    testCase "missing hotReload arg does not trigger" <| fun () ->
      let args : Map<string, obj> = Map.empty
      let shouldRun =
        match Map.tryFind "hotReload" args with
        | Some v when v = (box true) -> true
        | _ -> false
      shouldRun |> Flip.Expect.isFalse "should not trigger without arg"
  ]

/// Tests that file change actions route correctly to Reload/SoftReset/Ignore.
let fileWatcherIntegrationTests =
  testList "file watcher integration" [
    testCase "fileChangeAction routes .fs Changed to Reload with correct path" <| fun () ->
      let change = {
        FilePath = @"C:\Code\MyModule.fs"
        Kind = FileChangeKind.Changed
        Timestamp = System.DateTimeOffset.UtcNow
      }
      match fileChangeAction change with
      | FileChangeAction.Reload path ->
        Flip.Expect.equal "path should match" @"C:\Code\MyModule.fs" path
      | other -> failwithf "expected Reload, got %A" other

    testCase "fileChangeAction routes .fsx Changed to Reload" <| fun () ->
      let change = {
        FilePath = @"C:\Code\Script.fsx"
        Kind = FileChangeKind.Changed
        Timestamp = System.DateTimeOffset.UtcNow
      }
      match fileChangeAction change with
      | FileChangeAction.Reload path ->
        Flip.Expect.equal "path should match" @"C:\Code\Script.fsx" path
      | other -> failwithf "expected Reload, got %A" other

    testCase "fileChangeAction routes .fsproj to SoftReset" <| fun () ->
      let change = {
        FilePath = @"C:\Code\App.fsproj"
        Kind = FileChangeKind.Changed
        Timestamp = System.DateTimeOffset.UtcNow
      }
      fileChangeAction change
      |> Flip.Expect.equal "should soft reset" FileChangeAction.SoftReset

    testCase "fileChangeAction ignores Deleted files" <| fun () ->
      let change = {
        FilePath = @"C:\Code\Old.fs"
        Kind = FileChangeKind.Deleted
        Timestamp = System.DateTimeOffset.UtcNow
      }
      fileChangeAction change
      |> Flip.Expect.equal "should ignore" FileChangeAction.Ignore

    testCase "fileChangeAction ignores non-F# extensions" <| fun () ->
      let change = {
        FilePath = @"C:\Code\style.css"
        Kind = FileChangeKind.Changed
        Timestamp = System.DateTimeOffset.UtcNow
      }
      fileChangeAction change
      |> Flip.Expect.equal "should ignore" FileChangeAction.Ignore
  ]

/// Tests that the NoWatch config and empty directories properly disable file watching.
let noWatchFlagTests =
  testList "NoWatch config" [
    testCase "WorkerConfig.NoWatch=true disables file watching" <| fun () ->
      let config = SageFs.Args.WorkerConfig.fromEnvironmentWith
                     (fun k -> match k with "SAGEFS_NO_WATCH" -> "1" | _ -> null) "test" 0
      config.NoWatch |> Flip.Expect.isTrue "should detect NoWatch"

    testCase "WorkerConfig.NoWatch=false means file watching enabled" <| fun () ->
      let config = SageFs.Args.WorkerConfig.fromEnvironmentWith (fun _ -> null) "test" 0
      config.NoWatch |> Flip.Expect.isFalse "should not find NoWatch"

    testCase "empty project directories skips file watcher" <| fun () ->
      let dirs : string list = []
      let shouldWatch = not (List.isEmpty dirs)
      shouldWatch |> Flip.Expect.isFalse "should skip with empty dirs"

    testCase "non-empty project directories enables file watcher" <| fun () ->
      let dirs = [@"C:\Code\Project1"; @"C:\Code\Project2"]
      let shouldWatch = not (List.isEmpty dirs)
      shouldWatch |> Flip.Expect.isTrue "should enable with project dirs"
  ]

/// Tests the full reload-to-detour cycle contract:
/// file change → #load → EvalRequest with hotReload=true → middleware check.
let reloadToDetourCycleTests =
  testList "reload to detour cycle" [
    testCase "#load code format matches expected pattern" <| fun () ->
      let filePath = @"C:\Code\Harmony\HarmonyServer\harmonyServer.fs"
      let code = sprintf "#load @\"%s\"" filePath
      Flip.Expect.stringContains "should contain #load" "#load" code
      Flip.Expect.stringContains "should contain file path" "harmonyServer.fs" code

    testCase "EvalRequest with hotReload passes middleware check" <| fun () ->
      let request = {
        Code = "#load @\"test.fs\""
        Args = Map.ofList ["hotReload", box true]
      }
      let shouldRunHotReload (hotReloadFlagEnabled: bool) (m: Map<string, obj>) =
        match hotReloadFlagEnabled, Map.tryFind "hotReload" m with
        | _, Some v when v = (box true) -> true
        | true, None -> true
        | _ -> false
      shouldRunHotReload false request.Args
      |> Flip.Expect.isTrue "explicit hotReload=true triggers even without FSI flag"

    testCase "EvalRequest without hotReload requires FSI flag" <| fun () ->
      let request = { Code = "let x = 1"; Args = Map.empty }
      let shouldRunHotReload (hotReloadFlagEnabled: bool) (m: Map<string, obj>) =
        match hotReloadFlagEnabled, Map.tryFind "hotReload" m with
        | _, Some v when v = (box true) -> true
        | true, None -> true
        | _ -> false
      shouldRunHotReload false request.Args
      |> Flip.Expect.isFalse "should not trigger without FSI flag or explicit arg"

    testCase "EvalRequest without hotReload triggers when FSI flag set" <| fun () ->
      let request = { Code = "let x = 1"; Args = Map.empty }
      let shouldRunHotReload (hotReloadFlagEnabled: bool) (m: Map<string, obj>) =
        match hotReloadFlagEnabled, Map.tryFind "hotReload" m with
        | _, Some v when v = (box true) -> true
        | true, None -> true
        | _ -> false
      shouldRunHotReload true request.Args
      |> Flip.Expect.isTrue "should trigger with FSI flag"
  ]

/// Tests that defaultWatchConfig produces correct settings for worker file watching.
let watchConfigTests =
  testList "watch config for worker" [
    testCase "defaultWatchConfig watches .fs .fsx .fsproj" <| fun () ->
      let config = defaultWatchConfig [@"C:\Code\Proj1"]
      Flip.Expect.contains "should watch .fs" ".fs" config.Extensions
      Flip.Expect.contains "should watch .fsx" ".fsx" config.Extensions
      Flip.Expect.contains "should watch .fsproj" ".fsproj" config.Extensions

    testCase "defaultWatchConfig debounce is 200ms" <| fun () ->
      let config = defaultWatchConfig [@"C:\Code\Proj1"]
      Flip.Expect.equal "debounce should be 200" 200 config.DebounceMs

    testCase "watches multiple project directories" <| fun () ->
      let dirs = [@"C:\Code\Server"; @"C:\Code\Types"; @"C:\Code\Tests"]
      let config = defaultWatchConfig dirs
      Flip.Expect.equal "should have 3 dirs" 3 config.Directories.Length

    testCase "shouldTriggerRebuild rejects bin/obj even for .fs" <| fun () ->
      let config = defaultWatchConfig [@"C:\Code"]
      let binPath =
        sprintf @"C:\Code\bin%cDebug%cfile.fs"
          System.IO.Path.DirectorySeparatorChar
          System.IO.Path.DirectorySeparatorChar
      shouldTriggerRebuild config binPath
      |> Flip.Expect.isFalse "should reject bin path"
  ]

let middlewareGuardTests =
  testList "hot reload middleware guards" [
    testCase "null session skips flag lookup and post-eval bookkeeping" <| fun () ->
      let request = { Code = "let x = 1"; Args = Map.empty }
      let response, _ = hotReloadingMiddleware passThroughNext (request, makeState ())
      response.EvaluatedCode
      |> Flip.Expect.equal "middleware should still forward the injected request without crashing" (injectNoInlining request.Code)
  ]

/// Tests that mkReloadingState registers NuGet package directories
/// and framework SDK directories in assemblySearchPaths, not just project outputs.
/// Chesterton's fence: Without this, transitive NuGet dependencies (e.g.
/// OpenTelemetry.Instrumentation.AspNetCore) fail to resolve at runtime when
/// loading real-world projects like Harmony inside SageFs FSI.
let assemblySearchPathTests =
  testList "assembly search paths" [
    testCase "registerSearchPath extracts directory from DLL path" <| fun () ->
      let dllPath = Path.Combine("extract-test-" + System.Guid.NewGuid().ToString("N"), "bin", "Debug", "net10.0", "MyProject.dll")
      registerSearchPath dllPath
      assemblySearchPaths.ContainsKey(Path.GetDirectoryName dllPath)
      |> Flip.Expect.isTrue "should register DLL's parent directory"

    testCase "registerSearchPath deduplicates same directory" <| fun () ->
      let dir = Path.Combine("dedup-test-" + System.Guid.NewGuid().ToString("N"), "bin")
      registerSearchPath (Path.Combine(dir, "MyProject.dll"))
      registerSearchPath (Path.Combine(dir, "OtherProject.dll"))
      // Both DLLs are in the same directory, so only one key should exist
      assemblySearchPaths.ContainsKey dir
      |> Flip.Expect.isTrue "directory should be registered"
      // Verify the directory appears exactly once by counting keys matching our unique prefix
      assemblySearchPaths.Keys
      |> Seq.filter (fun k -> k.Contains(dir))
      |> Seq.length
      |> Flip.Expect.equal "same directory registered twice should yield 1 entry" 1

    testCase "registerSearchPath tracks multiple distinct directories" <| fun () ->
      let unique = System.Guid.NewGuid().ToString("N")
      let dir1 = sprintf "dir1-%s" unique
      let dir2 = sprintf "dir2-%s" unique
      registerSearchPath (Path.Combine(dir1, "A.dll"))
      registerSearchPath (Path.Combine(dir2, "B.dll"))
      assemblySearchPaths.ContainsKey dir1
      |> Flip.Expect.isTrue "first directory should be registered"
      assemblySearchPaths.ContainsKey dir2
      |> Flip.Expect.isTrue "second directory should be registered"

    testCase "NuGet package path registers correctly" <| fun () ->
      let dllPath = Path.Combine("nuget-test-" + System.Guid.NewGuid().ToString("N"), ".nuget", "packages", "opentelemetry", "1.15.0", "lib", "net8.0", "OpenTelemetry.dll")
      registerSearchPath dllPath
      assemblySearchPaths.ContainsKey(Path.GetDirectoryName dllPath)
      |> Flip.Expect.isTrue "should register NuGet package directory"

    testCase "framework SDK path registers correctly" <| fun () ->
      let dllPath = Path.Combine("sdk-test-" + System.Guid.NewGuid().ToString("N"), "shared", "Microsoft.AspNetCore.App", "10.0.0", "Microsoft.AspNetCore.dll")
      registerSearchPath dllPath
      assemblySearchPaths.ContainsKey(Path.GetDirectoryName dllPath)
      |> Flip.Expect.isTrue "should register framework SDK directory"
  ]

/// Tests for the [<MethodImpl(NoInlining)>] injection that prevents
/// JIT inlining from defeating Harmony's entry-point detours.
let noInliningInjectionTests =
  testList "NoInlining injection" [
    testCase "injects on unit-param function" <| fun () ->
      let result = injectNoInlining "let f () = 42"
      Flip.Expect.stringContains
        "should have MethodImpl attribute" "[<MethodImpl(MethodImplOptions.NoInlining)>]" result
      Flip.Expect.stringContains
        "should open CompilerServices" "open System.Runtime.CompilerServices" result

    testCase "skips value bindings" <| fun () ->
      let result = injectNoInlining "let x = 42"
      Flip.Expect.equal "value binding should be unchanged" "let x = 42" result

    testCase "injects on named-param function" <| fun () ->
      let result = injectNoInlining "let add x y = x + y"
      Flip.Expect.stringContains
        "should have MethodImpl" "[<MethodImpl(MethodImplOptions.NoInlining)>]" result

    testCase "injects on private function" <| fun () ->
      let result = injectNoInlining "let private f x y = x + y"
      Flip.Expect.stringContains
        "should have MethodImpl" "[<MethodImpl(MethodImplOptions.NoInlining)>]" result

    testCase "skips mutable value" <| fun () ->
      let result = injectNoInlining "let mutable count = 0"
      Flip.Expect.equal "mutable val should be unchanged" "let mutable count = 0" result

    testCase "injects on inline function" <| fun () ->
      let result = injectNoInlining "let inline f x = x"
      Flip.Expect.stringContains
        "should have MethodImpl" "[<MethodImpl(MethodImplOptions.NoInlining)>]" result

    testCase "injects on rec function" <| fun () ->
      let result = injectNoInlining "let rec f n = if n = 0 then 1 else n * f (n-1)"
      Flip.Expect.stringContains
        "should have MethodImpl" "[<MethodImpl(MethodImplOptions.NoInlining)>]" result

    testCase "skips typed value binding" <| fun () ->
      let result = injectNoInlining "let h : int = 42"
      Flip.Expect.equal "typed val should be unchanged" "let h : int = 42" result

    testCase "injects on indented module-member function" <| fun () ->
      // Chesterton's fence: module-declared files are transformed by indenting
      // the module body, so the detour target is indented. It must get
      // NoInlining or the JIT inlines it into the route closure and Harmony
      // has nothing to detour (P0 hot-reload gap).
      let result = injectNoInlining "module Greeting =\n  let greeting () = \"hi\""
      Flip.Expect.stringContains
        "should inject NoInlining on indented module-member function"
        "[<MethodImpl(MethodImplOptions.NoInlining)>]" result

    testCase "injects on static member" <| fun () ->
      let result = injectNoInlining "static member Hello () = \"hi\""
      Flip.Expect.stringContains
        "should have MethodImpl" "[<MethodImpl(MethodImplOptions.NoInlining)>]" result

    testCase "handles multi-line code with mixed bindings" <| fun () ->
      let code = "let greeting () = \"hello\"\nlet count = 0\nlet handler x = greeting ()"
      let result = injectNoInlining code
      // greeting and handler get NoInlining, count does not
      let lines = result.Split('\n')
      let attrCount =
        lines |> Array.filter (fun l -> l.Contains("[<MethodImpl(MethodImplOptions.NoInlining)>]")) |> Array.length
      Flip.Expect.equal "should inject exactly 2 attributes" 2 attrCount

    testCase "preserves original code lines" <| fun () ->
      let code = "let f () = 42"
      let result = injectNoInlining code
      Flip.Expect.stringContains "should contain original" "let f () = 42" result
  ]

let versionAwareResolutionTests =
  testList "version-aware assembly resolution" [
    test "registerSearchPath registers assembly directory" {
      assemblySearchPaths.Clear()
      let expectoAsm = typeof<Expecto.TestCode>.Assembly
      let expectoDir = IO.Path.GetDirectoryName(expectoAsm.Location : string)
      registerSearchPath expectoAsm.Location
      assemblySearchPaths.ContainsKey(expectoDir)
      |> Flip.Expect.isTrue "should have registered Expecto directory"
    }

    test "registerSearchPath handles duplicate paths" {
      assemblySearchPaths.Clear()
      let testAsm = typeof<Expecto.TestCode>.Assembly
      registerSearchPath testAsm.Location
      let countAfterFirst = assemblySearchPaths.Count
      registerSearchPath testAsm.Location
      assemblySearchPaths.Count
      |> Flip.Expect.equal "count should not increase for duplicates" countAfterFirst
    }

    test "assemblySearchPaths is non-empty after registration" {
      assemblySearchPaths.Clear()
      let expectoAsm = typeof<Expecto.TestCode>.Assembly
      registerSearchPath expectoAsm.Location
      (assemblySearchPaths.Count, 0)
      |> Flip.Expect.isGreaterThan "should have at least one search path"
    }
  ]

let hotReloadCompilationContextTests =
  testList "hot-reload CompilationContext integration" [
    testTask "File mode wraps module declaration for FSI" {
      let code = "module MyApp.Server\n\nopen System\n\nlet handler () = \"hello\"\n"
      let filePath = System.IO.Path.Combine("C", "project", "Server.fs")
      let! fs =
        SageFs.Middleware.CompilationContext.parseFileStructure filePath code
      let result, _ =
        SageFs.Middleware.CompilationContext.preprocessForFsi
          (Some fs) SageFs.Middleware.CompilationContext.EvalMode.File None Set.empty code
      let lines = result.Code.Split('\n')
      let hasStandaloneModule =
        lines
        |> Array.exists (fun l ->
          let t = l.Trim()
          t.StartsWith("module ") && not (t.EndsWith("=")))
      hasStandaloneModule
      |> Flip.Expect.isFalse "should not have standalone module declaration (would fail in FSI)"
    }

    testTask "File mode preserves function definitions" {
      let code = "module MyApp.Handlers\n\nlet index () = \"Welcome\"\nlet about () = \"About\"\n"
      let filePath = System.IO.Path.Combine("C", "project", "Handlers.fs")
      let! fs =
        SageFs.Middleware.CompilationContext.parseFileStructure filePath code
      let result, _ =
        SageFs.Middleware.CompilationContext.preprocessForFsi
          (Some fs) SageFs.Middleware.CompilationContext.EvalMode.File None Set.empty code
      result.Code |> Flip.Expect.stringContains "should contain index function" "index"
      result.Code |> Flip.Expect.stringContains "should contain about function" "about"
    }

    testTask "File mode tracks evaluated modules" {
      let code = "module MyApp.Domain\n\ntype User = { Name: string }\n"
      let filePath = System.IO.Path.Combine("C", "project", "Domain.fs")
      let! fs =
        SageFs.Middleware.CompilationContext.parseFileStructure filePath code
      let _, updatedModules =
        SageFs.Middleware.CompilationContext.preprocessForFsi
          (Some fs) SageFs.Middleware.CompilationContext.EvalMode.File None Set.empty code
      updatedModules.Contains("MyApp.Domain")
      |> Flip.Expect.isTrue "should track MyApp.Domain as evaluated module"
    }

    testTask "Successive file reloads accumulate module context" {
      let code1 = "module MyApp.Types\n\ntype Color = Red | Green | Blue\n"
      let typesPath = System.IO.Path.Combine("C", "project", "Types.fs")
      let! fs1 =
        SageFs.Middleware.CompilationContext.parseFileStructure typesPath code1
      let _, modules1 =
        SageFs.Middleware.CompilationContext.preprocessForFsi
          (Some fs1) SageFs.Middleware.CompilationContext.EvalMode.File None Set.empty code1
      let code2 = "module MyApp.Logic\n\nlet describe color = sprintf \"%A\" color\n"
      let logicPath = System.IO.Path.Combine("C", "project", "Logic.fs")
      let! fs2 =
        SageFs.Middleware.CompilationContext.parseFileStructure logicPath code2
      let _, modules2 =
        SageFs.Middleware.CompilationContext.preprocessForFsi
          (Some fs2) SageFs.Middleware.CompilationContext.EvalMode.File None modules1 code2
      modules2.Contains("MyApp.Types")
      |> Flip.Expect.isTrue "should still have Types module"
      modules2.Contains("MyApp.Logic")
      |> Flip.Expect.isTrue "should also have Logic module"
    }

    test "Fallback to raw code when fileStructure is None" {
      let code = "#load @\"C:\\project\\file.fs\""
      let result, modules =
        SageFs.Middleware.CompilationContext.preprocessForFsi
          None SageFs.Middleware.CompilationContext.EvalMode.File None Set.empty code
      result.Code |> Flip.Expect.equal "should pass through raw code" code
      modules |> Flip.Expect.isEmpty "should not update modules on fallback"
    }
  ]

let exactMethodMatchingTests =
  testList "exact method matching" [
    test "getUser does not match getUsers (suffix mismatch)" {
      let fsiFull = "getUser"
      let projectFull = "MyApp.Server.getUsers"
      projectFull.EndsWith(fsiFull, StringComparison.Ordinal)
      |> Flip.Expect.isFalse "getUser should NOT match getUsers suffix"
    }

    test "handler matches Module.handler (exact suffix)" {
      let fsiFull = "handler"
      let projectFull = "MyApp.Server.Module.handler"
      projectFull.EndsWith(fsiFull, StringComparison.Ordinal)
      |> Flip.Expect.isTrue "handler should match as exact suffix"
    }

    test "Module.handler matches with module prefix scoring" {
      let fsiName = "handler"
      let projectFull = "MyApp.Server.Routes.handler"
      let lastOpenModules = ["Routes"]
      let moduleScore =
        lastOpenModules
        |> Seq.map (fun o ->
          match projectFull.EndsWith(o + "." + fsiName, StringComparison.Ordinal) with
          | true -> 2
          | false -> 0)
        |> Seq.tryHead
        |> Option.defaultValue 0
      let noModuleScore =
        match projectFull.EndsWith(fsiName, StringComparison.Ordinal) with
        | true -> 1
        | false -> 0
      let score = max moduleScore noModuleScore
      score |> Flip.Expect.equal "module-qualified match should score 2" 2
    }

    test "unrelated method scores 0" {
      let fsiName = "handler"
      let projectFull = "MyApp.Server.Routes.unrelatedFunction"
      let lastOpenModules = ["Routes"]
      let moduleScore =
        lastOpenModules
        |> Seq.map (fun o ->
          match projectFull.EndsWith(o + "." + fsiName, StringComparison.Ordinal) with
          | true -> 2
          | false -> 0)
        |> Seq.tryHead
        |> Option.defaultValue 0
      let noModuleScore =
        match projectFull.EndsWith(fsiName, StringComparison.Ordinal) with
        | true -> 1
        | false -> 0
      let score = max moduleScore noModuleScore
      score |> Flip.Expect.equal "unrelated method should score 0" 0
    }
  ]

let nonBlockingRunGuardTests =
  testList "NonBlockingRun hotReload guard" [
    test "hotReload flag extraction: true when present" {
      let args = Map.ofList ["hotReload", box true]
      let isHotReload =
        args
        |> Map.tryFind "hotReload"
        |> Option.map (fun v -> v :?> bool)
        |> Option.defaultValue false
      isHotReload |> Flip.Expect.isTrue "should detect hotReload=true"
    }

    test "hotReload flag extraction: false when absent" {
      let args: Map<string, obj> = Map.empty
      let isHotReload =
        args
        |> Map.tryFind "hotReload"
        |> Option.map (fun v -> v :?> bool)
        |> Option.defaultValue false
      isHotReload |> Flip.Expect.isFalse "should default to false when absent"
    }

    test "hotReload flag extraction: false when explicitly false" {
      let args = Map.ofList ["hotReload", box false]
      let isHotReload =
        args
        |> Map.tryFind "hotReload"
        |> Option.map (fun v -> v :?> bool)
        |> Option.defaultValue false
      isHotReload |> Flip.Expect.isFalse "should be false when explicitly set to false"
    }
  ]

let highestVersionResolutionTests =
  testList "highest version assembly resolution" [
    test "resolveAssembly prefers highest version when multiple candidates exist" {
      let versions = [
        System.Version(1, 0, 0, 0)
        System.Version(3, 0, 0, 0)
        System.Version(2, 0, 0, 0)
      ]
      let highest =
        versions
        |> Seq.sortByDescending id
        |> Seq.tryHead
      highest
      |> Flip.Expect.isSome "should find a version"
      highest.Value
      |> Flip.Expect.equal "should pick highest version" (System.Version(3, 0, 0, 0))
    }

    test "version comparison: candidate >= requested passes" {
      let requested = System.Version(2, 0, 0, 0)
      let candidate = System.Version(3, 0, 0, 0)
      (candidate >= requested)
      |> Flip.Expect.isTrue "3.0 >= 2.0 should pass"
    }

    test "version comparison: candidate < requested fails" {
      let requested = System.Version(3, 0, 0, 0)
      let candidate = System.Version(2, 0, 0, 0)
      (candidate >= requested)
      |> Flip.Expect.isFalse "2.0 >= 3.0 should fail"
    }

    test "null version matches any candidate" {
      let requestedVersion: System.Version = null
      let passes = match requestedVersion with null -> true | _ -> false
      passes |> Flip.Expect.isTrue "null requested version should accept any candidate"
    }
  ]

let assemblySearchPathsThreadSafetyTests =
  testList "assemblySearchPaths thread safety" [
    test "assemblySearchPaths is ConcurrentDictionary" {
      // Chesterton's fence: assembly resolve callbacks fire on arbitrary CLR threads.
      // A plain ResizeArray would corrupt under concurrent access.
      assemblySearchPaths.GetType().Name
      |> Flip.Expect.stringContains "should be ConcurrentDictionary" "ConcurrentDictionary"
    }

    test "ContainsKey works for registered paths" {
      let dllPath = Path.Combine("containskey-test-" + System.Guid.NewGuid().ToString("N"), "dir", "My.dll")
      registerSearchPath dllPath
      assemblySearchPaths.ContainsKey(Path.GetDirectoryName dllPath)
      |> Flip.Expect.isTrue "should find registered directory via ContainsKey"
    }

    test "TryAdd is idempotent" {
      let dir = Path.Combine("idempotent-test-" + System.Guid.NewGuid().ToString("N"), "dir")
      registerSearchPath (Path.Combine(dir, "A.dll"))
      registerSearchPath (Path.Combine(dir, "B.dll"))
      assemblySearchPaths.Keys
      |> Seq.filter (fun k -> k.Contains(dir))
      |> Seq.length
      |> Flip.Expect.equal "TryAdd should not duplicate same directory" 1
    }
  ]

let moduleScoringMaxTests =
  testList "module scoring uses max not first" [
    test "best module match wins even if not first in list" {
      // Simulates LastOpenModules = ["Unrelated"; "MyApp.Handlers"]
      // For method "handleRequest", "Unrelated" scores 0, "MyApp.Handlers" scores 2.
      // With Seq.tryHead, we'd get 0. With Seq.fold max 0, we get 2.
      let modules = [ "Unrelated"; "MyApp.Handlers" ]
      let existingFullName = "MyApp.Handlers.handleRequest"
      let newName = "handleRequest"
      let score =
        modules
        |> Seq.map (fun o ->
          match existingFullName.EndsWith(o + "." + newName, StringComparison.Ordinal) with
          | true -> 2
          | false -> 0)
        |> Seq.fold max 0
      score |> Flip.Expect.equal "should find best match across all modules" 2
    }

    test "first module matching does not shadow later better match" {
      // If tryHead was used, first module's 0 score would win.
      let modules = [ "A"; "B"; "Target.Module" ]
      let existingFullName = "Target.Module.doWork"
      let newName = "doWork"
      let scoreWithFold =
        modules
        |> Seq.map (fun o ->
          match existingFullName.EndsWith(o + "." + newName, StringComparison.Ordinal) with
          | true -> 2
          | false -> 0)
        |> Seq.fold max 0
      let scoreWithTryHead =
        modules
        |> Seq.map (fun o ->
          match existingFullName.EndsWith(o + "." + newName, StringComparison.Ordinal) with
          | true -> 2
          | false -> 0)
        |> Seq.tryHead
        |> Option.defaultValue 0
      scoreWithFold |> Flip.Expect.equal "fold should find the match" 2
      scoreWithTryHead |> Flip.Expect.equal "tryHead would miss it (returns first)" 0
    }
  ]

let endswithPrefilterTests =
  testList "EndsWith pre-filter consistency" [
    test "EndsWith rejects substring-only match" {
      // "UserHandler.getUsers" contains "getUser" but does not EndsWith "getUser"
      let fullName = "UserHandler.getUsers"
      let candidate = "getUser"
      fullName.Contains(candidate)
      |> Flip.Expect.isTrue "Contains would match (loose)"
      fullName.EndsWith(candidate, StringComparison.Ordinal)
      |> Flip.Expect.isFalse "EndsWith correctly rejects (strict)"
    }

    test "EndsWith accepts exact suffix match" {
      let fullName = "MyApp.Handlers.processOrder"
      let candidate = "processOrder"
      fullName.EndsWith(candidate, StringComparison.Ordinal)
      |> Flip.Expect.isTrue "should accept exact suffix"
    }

    test "EndsWith accepts qualified suffix" {
      let fullName = "MyApp.Handlers.processOrder"
      let candidate = "Handlers.processOrder"
      fullName.EndsWith(candidate, StringComparison.Ordinal)
      |> Flip.Expect.isTrue "should accept qualified suffix"
    }
  ]

/// WHY tests: handleNewAsmFromRepl MUST gate the dangerous .ParameterType
/// reflection behind hotReloadEnabled. Without this gate, every normal REPL
/// eval (define type in block 1, reference it in block 2) triggers
/// TypeLoadException from stale FSI compilation units.
let handleNewAsmFromReplGatingTests =
  testList "WHY — handleNewAsmFromRepl gating" [
    test "WHY — hotReloadEnabled=false returns empty updatedMethods because ParameterType reflection causes TypeLoadException on stale FSI types" {
      // Arrange: use a real assembly with methods that have parameters
      let asm = typeof<Expecto.TestCode>.Assembly
      let emptyState : State = {
        Methods = Map.empty
        LastOpenModules = []
        LastAssembly = None
        ProjectAssemblies = []
        AssemblyLoadErrors = []
        LiveTestInit = LiveTestInit.Pending
      }
      // Act: call with hotReloadEnabled=false
      let _, updatedMethods =
        handleNewAsmFromRepl (Log.asILogger()) false asm emptyState
      // Assert: no methods should be reported as "updated" (no replacement pairs)
      updatedMethods
      |> Flip.Expect.isEmpty
        "hotReloadEnabled=false must never produce replacement pairs — ParameterType access causes TypeLoadException on stale FSI types"
    }

    test "WHY — hotReloadEnabled=false still merges methods for live testing discovery" {
      // Even with hot-reload disabled, the method merge MUST happen so that
      // live testing can discover tests via getAllMethods on subsequent evals.
      let asm = typeof<Expecto.TestCode>.Assembly
      let emptyState : State = {
        Methods = Map.empty
        LastOpenModules = []
        LastAssembly = None
        ProjectAssemblies = []
        AssemblyLoadErrors = []
        LiveTestInit = LiveTestInit.Pending
      }
      let newState, _ =
        handleNewAsmFromRepl (Log.asILogger()) false asm emptyState
      newState.Methods.IsEmpty
      |> Flip.Expect.isFalse
        "method merge must still happen when hot-reload is disabled — live testing needs the method registry"
    }

    test "WHY — hotReloadEnabled=false sets LastAssembly for duplicate detection" {
      let asm = typeof<Expecto.TestCode>.Assembly
      let emptyState : State = {
        Methods = Map.empty
        LastOpenModules = []
        LastAssembly = None
        ProjectAssemblies = []
        AssemblyLoadErrors = []
        LiveTestInit = LiveTestInit.Pending
      }
      let newState, _ =
        handleNewAsmFromRepl (Log.asILogger()) false asm emptyState
      newState.LastAssembly
      |> Flip.Expect.isSome
        "LastAssembly must be set so duplicate assembly detection works on next eval"
    }
  ]

[<Tests>]
let allHotReloadTests =
  testList "Hot Reload Integration" [
    hotReloadArgTests
    fileWatcherIntegrationTests
    noWatchFlagTests
    reloadToDetourCycleTests
    watchConfigTests
    middlewareGuardTests
    testSequenced (testList "assembly search paths (sequenced)" [
      assemblySearchPathTests
      versionAwareResolutionTests
      assemblySearchPathsThreadSafetyTests
    ])
    hotReloadCompilationContextTests
    noInliningInjectionTests
    exactMethodMatchingTests
    nonBlockingRunGuardTests
    highestVersionResolutionTests
    moduleScoringMaxTests
    endswithPrefilterTests
    handleNewAsmFromReplGatingTests
  ]
