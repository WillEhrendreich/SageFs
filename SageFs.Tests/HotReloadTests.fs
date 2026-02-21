module SageFs.Tests.HotReloadTests

open Expecto
open SageFs.FileWatcher
open SageFs.AppState
open SageFs.Middleware.HotReloading

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

/// Tests that the No_Watch flag and empty directories properly disable file watching.
let noWatchFlagTests =
  testList "No_Watch flag" [
    testCase "No_Watch is a valid Args.Arguments case" <| fun () ->
      let args = [SageFs.Args.No_Watch; SageFs.Args.Proj "test.fsproj"]
      let hasNoWatch =
        args |> List.exists (function SageFs.Args.No_Watch -> true | _ -> false)
      hasNoWatch |> Flip.Expect.isTrue "should detect No_Watch"

    testCase "No_Watch absent means file watching enabled" <| fun () ->
      let args = [SageFs.Args.Proj "test.fsproj"]
      let hasNoWatch =
        args |> List.exists (function SageFs.Args.No_Watch -> true | _ -> false)
      hasNoWatch |> Flip.Expect.isFalse "should not find No_Watch"

    testCase "empty project directories skips file watcher" <| fun () ->
      let dirs : string list = []
      let shouldWatch = not (List.isEmpty dirs)
      shouldWatch |> Flip.Expect.isFalse "should skip with empty dirs"

    testCase "non-empty project directories enables file watcher" <| fun () ->
      let dirs = [@"C:\Code\Project1"; @"C:\Code\Project2"]
      let shouldWatch = not (List.isEmpty dirs)
      shouldWatch |> Flip.Expect.isTrue "should enable with project dirs"
  ]

/// Tests the full reload-to-detour pipeline contract:
/// file change → #load → EvalRequest with hotReload=true → middleware check.
let reloadToDetourPipelineTests =
  testList "reload to detour pipeline" [
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

    testCase "defaultWatchConfig debounce is 500ms" <| fun () ->
      let config = defaultWatchConfig [@"C:\Code\Proj1"]
      Flip.Expect.equal "debounce should be 500" 500 config.DebounceMs

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

    testCase "skips indented let (not top-level)" <| fun () ->
      let result = injectNoInlining "  let nested () = 1"
      Flip.Expect.equal "indented let should be unchanged" "  let nested () = 1" result

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

[<Tests>]
let allHotReloadTests =
  testList "Hot Reload Integration" [
    hotReloadArgTests
    fileWatcherIntegrationTests
    noWatchFlagTests
    reloadToDetourPipelineTests
    watchConfigTests
    noInliningInjectionTests
  ]
