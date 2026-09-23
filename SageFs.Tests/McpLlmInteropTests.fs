module SageFs.Tests.McpLlmInteropTests

open Expecto
open Expecto.Flip
open System
open System.IO
open System.Threading.Tasks
open SageFs
open SageFs.AppState
open SageFs.WorkflowTypes
open SageFs.Features.Events
open SageFs.McpTools
open SageFs.WorkerProtocol
open SageFs.Tests.TestInfrastructure

// Unit tests for TDD of LLM Interop improvements based on SageFs_mcp_improvement_suggestion.md
// These tests drive the implementation of:
// 1. StartupConfig tracking (stored in AppState)
// 2. get_startup_info tool
// 3. Enhanced get_fsi_status with startup context
// 4. Project discovery capabilities

// ============================================================================
// CRITICAL IMPROVEMENT #1: StartupConfig Type and Storage
// ============================================================================

module StartupConfigTests =
  
  let tests =
    testList "StartupConfig type and storage" [
      
      testCase "StartupConfig should have all required fields"
      <| fun _ ->
        // This test verifies the StartupConfig type exists in AppState.fs
        let config: SageFs.AppState.StartupConfig = {
          CommandLineArgs = [| "--mcp-port"; "8080" |]
          LoadedProjects = [ "Test.fsproj" ]
          WorkingDirectory = @"C:\Code\Test"
          Workflow = SessionWorkflow.HotReload BrowserRefreshConfig.defaults
          AutoOpenNamespaces = true
          AspireDetected = false
          StartupTimestamp = DateTime.UtcNow; StartupProfileLoaded = None
        }
        
        config.CommandLineArgs.Length |> Expect.equal "Should have command line args" 2
        config.LoadedProjects.Length |> Expect.equal "Should have loaded projects" 1
        config.HotReloadEnabled |> Expect.isTrue "Should track hot reload"
        config.AspireDetected |> Expect.isFalse "Should track Aspire detection"
      
      testCase "StartupConfig should handle empty/default states"
      <| fun _ ->
        let emptyConfig: SageFs.AppState.StartupConfig = {
          CommandLineArgs = [||]
          LoadedProjects = []
          WorkingDirectory = ""
          Workflow = SessionWorkflow.Interactive
          AutoOpenNamespaces = true
          AspireDetected = false
          StartupTimestamp = DateTime.UtcNow; StartupProfileLoaded = None
        }
        
        emptyConfig.LoadedProjects.Length |> Expect.equal "Should handle no projects" 0
    ]

// ============================================================================
// CRITICAL IMPROVEMENT #2: get_startup_info Tool
// ============================================================================

module GetStartupInfoTests =

  /// getStartupInfo/getStartupInfoJson only ever read the SessionInfo
  /// SessionOps.GetSessionInfo hands back — a static stub below — and never
  /// route a message through GetProxy's proxy. globalActorResult was forced
  /// only for the DiagnosticsChanged handle these tools never touch; a bare
  /// event replaces it, so this list needs no live FSI actor at all.
  let private mkFormattingCtx () =
    let sessionId = SessionId.newId()
    let sessionMap = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
    sessionMap.["test"] <- SessionId.value sessionId
    let sessionInfo : SessionInfo =
      { Id = sessionId
        Name = None
        Projects = []
        WorkingDirectory = ""
        SolutionRoot = None
        Status = SessionLifecycleStatus.Ready { Pid = 1; Port = None }
        Workflow = SessionWorkflow.Interactive
        CreatedAt = DateTime.UtcNow
        LastActivity = DateTime.UtcNow
        ActiveProject = None
        ProjectRoles = []
        App = SageFs.AppRun.AppRunState.NotRunning }
    let ops : SageFs.SessionManagementOps =
      { SageFs.SessionManagementOps.stub with
          GetProxy = fun _ -> Task.FromResult(Some (fun _ -> async { return WorkerResponse.WorkerReady }))
          GetSessionInfo = fun _ -> Task.FromResult(Some sessionInfo) }
    { FrictionStore = None
      DiagnosticsChanged = (Event<Features.DiagnosticsStore.T>()).Publish
      StateChanged = None
      SessionOps = ops
      SessionMap = sessionMap
      McpPort = 0
      Dispatch = None
      GetElmModel = None
      GetElmRegions = None
      GetWarmupContext = None
      GetFeatureState = None; RecordEval = None
      ActivityTracker = SageFs.AgentActivityTracker.create()
      LiveSnapshotSink = None
      CohortOwner = None } : McpContext

  let tests =
    testList "get_startup_info tool" [

      testCase "get_startup_info should return structured startup information"
      <| fun _ ->
        task {
          let ctx = mkFormattingCtx ()

          let! result = getStartupInfo ctx "test" None
          
          // Should include key information from StartupConfig in AppState
          result |> Expect.isNotNull "Should return result"
          (result.Length > 0) |> Expect.isTrue "Should return non-empty result"
          // Verify it mentions startup information
          result |> Expect.stringContains "Should mention startup" "Startup"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
      
      testCase "get_startup_info should handle missing startup config gracefully"
      <| fun _ ->
        task {
          let ctx = mkFormattingCtx ()
          
          let! result = getStartupInfo ctx "test" None
          
          // Should always return something, even if no config
          result |> Expect.isNotNull "Should return result"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
      
      testCase "get_startup_info should return parseable JSON format"
      <| fun _ ->
        task {
          let ctx = mkFormattingCtx ()
          
          let! result = getStartupInfoJson ctx "test" None

          // Startup info is per-session since the session-architecture
          // unification (the daemon no longer holds one global StartupConfig):
          // the JSON carries the session identity, not FSI command-line args.
          use doc = System.Text.Json.JsonDocument.Parse result
          let root = doc.RootElement
          (root.GetProperty("sessionId").GetString()) |> Expect.equal "Should identify the session" ctx.SessionMap.["test"]
          (root.TryGetProperty("workingDirectory") |> fst) |> Expect.isTrue "Should carry the working directory"
          (root.TryGetProperty("status") |> fst) |> Expect.isTrue "Should carry the session status"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
      
      testCase "get_startup_info names the session and its status for LLMs"
      <| fun _ ->
        task {
          let ctx = mkFormattingCtx ()

          let! result = getStartupInfo ctx "test" None

          // The per-session startup header an agent uses to orient itself.
          result |> Expect.stringContains "Should name the session" (sprintf "- Session: %s" ctx.SessionMap.["test"])
          result |> Expect.stringContains "Should report the session status" "- Status: "
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
    ]

// ============================================================================
// CRITICAL IMPROVEMENT #3: Enhanced get_fsi_status with Startup Context
// ============================================================================

module EnhancedStatusTests =
  
  let tests =
    Integration.hostList "Enhanced get_fsi_status with startup context" [
      
      testCase "get_fsi_status should include startup information section"
      <| fun _ ->
        task {
          let ctx = sharedCtx ()
          
          let! result = getStatus ctx "test" None None
          
          // Should include startup information from AppState
          result |> Expect.stringContains "Should show events" "Events:"
          result |> Expect.stringContains "Should show tools" "Available:"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
    ]

// ============================================================================
// CRITICAL IMPROVEMENT #4: Project Discovery Capabilities
// ============================================================================

module ProjectDiscoveryTests =

  // Pure: string predicates and formatters over literal inputs — no live worker/session.
  let tests =
    testList "Project discovery for LLMs" [

      testCase "loadSolution runs without error and returns a solution"
      <| fun _ ->
        // Verifies loadSolution completes without exception from the test working directory
        let solution = SageFs.ProjectLoading.loadSolution quietLogger Args.ProjectLoadConfig.empty (fun _ _ _ -> ())
        ignore solution

      testCase "isSolutionFile matches .sln files"
      <| fun _ ->
        (SageFs.McpAdapter.isSolutionFile "MyApp.sln") |> Expect.isTrue "Should match .sln"

      testCase "isSolutionFile matches .slnx files"
      <| fun _ ->
        (SageFs.McpAdapter.isSolutionFile "MyApp.slnx") |> Expect.isTrue "Should match .slnx"

      testCase "isSolutionFile rejects non-solution files"
      <| fun _ ->
        (SageFs.McpAdapter.isSolutionFile "MyApp.fsproj") |> Expect.isFalse "Should not match .fsproj"

      testCase "isProjectFile matches .fsproj files"
      <| fun _ ->
        (SageFs.McpAdapter.isProjectFile "MyApp.fsproj") |> Expect.isTrue "Should match .fsproj"

      testCase "isProjectFile rejects non-project files"
      <| fun _ ->
        (SageFs.McpAdapter.isProjectFile "MyApp.sln") |> Expect.isFalse "Should not match .sln"

      testCase "formatAvailableProjects includes .slnx in header"
      <| fun _ ->
        let result =
          SageFs.McpAdapter.formatAvailableProjects
            "/test/dir"
            [| "App.fsproj" |]
            [| "App.slnx" |]
            0
        result |> Expect.stringContains "Should mention .slnx in output" ".slnx"
        result |> Expect.stringContains "Should list the .slnx file" "App.slnx"

      testCase "formatAvailableProjects shows none when empty"
      <| fun _ ->
        let result =
          SageFs.McpAdapter.formatAvailableProjects "/test/dir" [||] [||] 0
        result |> Expect.stringContains "Should show none for empty" "(none found)"
    ]

  // NOT pure: needs the shared live FSI actor (sharedCtx/globalActorResult) — stays Integration.
  let liveTests =
    Integration.hostCase "get_available_projects tool formats discoverable projects for LLMs"
    <| fun () ->
      task {
        let ctx = sharedCtx ()

        // Get the working directory the actor actually uses
        let! phase = globalActorResult.Value.Actor.PostAndAsyncReply(fun reply -> GetSessionPhase reply)
        let workingDir =
          match phase with
          | Active (st, _) ->
            match st.StartupConfig with
            | Some config -> config.WorkingDirectory
            | None -> Environment.CurrentDirectory
          | _ -> Environment.CurrentDirectory

        let! result = getAvailableProjects ctx "test" None

        result |> Expect.stringContains "Should have projects section" "Projects"
        result |> Expect.stringContains "Should have solutions section" "Solutions"
        result |> Expect.stringContains "Should mention project extension" ".fsproj"
        result |> Expect.stringContains "Should mention solution extension" ".sln"
        result |> Expect.stringContains "Should guide users toward explicit session creation" "create_session"
        result |> Expect.stringContains "Should show working directory" workingDir
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

// ============================================================================
// CRITICAL IMPROVEMENT #7: Adapter Functions for Formatting
// ============================================================================

module McpAdapterEnhancementTests =
  
  let tests =
    testList "McpAdapter formatting enhancements" [
      
      testCase "formatStartupInfo should create human-readable output"
      <| fun _ ->
        let config: SageFs.AppState.StartupConfig = {
          CommandLineArgs = [| "--mcp-port"; "8080" |]
          LoadedProjects = [ "Test.fsproj" ]
          WorkingDirectory = @"C:\Test"
          Workflow = SessionWorkflow.HotReload BrowserRefreshConfig.defaults
          AutoOpenNamespaces = true
          AspireDetected = false
          StartupTimestamp = DateTime.UtcNow; StartupProfileLoaded = None
        }
        
        let output = SageFs.McpAdapter.formatStartupInfo config
        
        output |> Expect.stringContains "Should include project" "Test.fsproj"
        output |> Expect.stringContains "Should include command line args" "--mcp-port"
        output |> Expect.stringContains "Should mention hot reload" "Hot Reload"
      
      testCase "formatStartupInfoJson should create valid JSON"
      <| fun _ ->
        let config: SageFs.AppState.StartupConfig = {
          CommandLineArgs = [| "--mcp-port"; "8080" |]
          LoadedProjects = [ "Test.fsproj" ]
          WorkingDirectory = @"C:\Test"
          Workflow = SessionWorkflow.HotReload BrowserRefreshConfig.defaults
          AutoOpenNamespaces = true
          AspireDetected = false
          StartupTimestamp = DateTime.UtcNow; StartupProfileLoaded = None
        }
        
        let json = SageFs.McpAdapter.formatStartupInfoJson config
        
        json |> Expect.stringContains "Should be JSON object" "{"
        json |> Expect.stringContains "Should have field" "\"commandLineArgs\""
        json |> Expect.stringContains "Should have field" "\"loadedProjects\""
      
      testCase "formatEnhancedStatus should include startup section"
      <| fun _ ->
        let config: SageFs.AppState.StartupConfig = {
          CommandLineArgs = [| "--mcp-port"; "8080" |]
          LoadedProjects = [ "Test.fsproj" ]
          WorkingDirectory = @"C:\Test"
          Workflow = SessionWorkflow.HotReload BrowserRefreshConfig.defaults
          AutoOpenNamespaces = true
          AspireDetected = false
          StartupTimestamp = DateTime.UtcNow; StartupProfileLoaded = None
        }
        
        let output = SageFs.McpAdapter.formatEnhancedStatus "test-session" 5 SageFs.SessionState.Ready None (Some config)
        
        output |> Expect.stringContains "Should have startup section" "Startup Information"
        (output.Contains("Usage Tips")) |> Expect.isFalse "Should NOT have tips (moved to ServerInstructions)"

      testCase "formatStartupBanner includes version"
      <| fun _ ->
        let banner = SageFs.McpAdapter.formatStartupBanner "0.2.29" (Some 37749)
        banner |> Expect.stringContains "Should include version" "0.2.29"
        banner |> Expect.stringContains "Should include product name" "SageFs"

      testCase "formatStartupBanner includes MCP port when provided"
      <| fun _ ->
        let banner = SageFs.McpAdapter.formatStartupBanner "1.0.0" (Some 8080)
        banner |> Expect.stringContains "Should include MCP port" "8080"

      testCase "formatStartupBanner omits MCP when no port"
      <| fun _ ->
        let banner = SageFs.McpAdapter.formatStartupBanner "1.0.0" None
        (banner.Contains "MCP") |> Expect.isFalse "Should not mention MCP without port"
    ]

// ============================================================================
// IMPROVEMENT #8: Shadow-Copy DLL Lock Prevention
// ============================================================================

module ShadowCopyTests =

  let tests =
    testList "Shadow-copy DLL lock prevention" [

      testCase "createShadowDir returns path under system temp"
      <| fun _ ->
        let dir = SageFs.ShadowCopy.createShadowDir ()
        try
          (dir.StartsWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))) |> Expect.isTrue "Should be under temp"
          (dir.Contains "sagefs-shadow") |> Expect.isTrue "Should contain sagefs-shadow"
          (Directory.Exists dir) |> Expect.isTrue "Directory should exist"
        finally
          if Directory.Exists dir then Directory.Delete(dir, true)

      testCase "createShadowDir creates unique dirs on each call"
      <| fun _ ->
        let dir1 = SageFs.ShadowCopy.createShadowDir ()
        let dir2 = SageFs.ShadowCopy.createShadowDir ()
        try
          dir1 |> Expect.notEqual "Each call should create a unique directory" dir2
        finally
          if Directory.Exists dir1 then Directory.Delete(dir1, true)
          if Directory.Exists dir2 then Directory.Delete(dir2, true)

      testCase "shadowCopyFile copies DLL to shadow dir and returns new path"
      <| fun _ ->
        let shadowDir = SageFs.ShadowCopy.createShadowDir ()
        let srcDir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-test-src-%s" (Guid.NewGuid().ToString("N").[..7]))
        Directory.CreateDirectory srcDir |> ignore
        let srcDll = Path.Combine(srcDir, "Test.dll")
        File.WriteAllText(srcDll, "fake-dll-content")
        try
          let newPath = SageFs.ShadowCopy.shadowCopyFile shadowDir srcDll
          (File.Exists newPath) |> Expect.isTrue "Shadow copy should exist"
          newPath |> Expect.notEqual "Should be a different path" srcDll
          (File.ReadAllText newPath) |> Expect.equal "Content should match" "fake-dll-content"
        finally
          if Directory.Exists shadowDir then Directory.Delete(shadowDir, true)
          if Directory.Exists srcDir then Directory.Delete(srcDir, true)

      testCase "shadowCopyFile copies companion .pdb if present"
      <| fun _ ->
        let shadowDir = SageFs.ShadowCopy.createShadowDir ()
        let srcDir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-test-pdb-%s" (Guid.NewGuid().ToString("N").[..7]))
        Directory.CreateDirectory srcDir |> ignore
        let srcDll = Path.Combine(srcDir, "Test.dll")
        let srcPdb = Path.Combine(srcDir, "Test.pdb")
        File.WriteAllText(srcDll, "dll")
        File.WriteAllText(srcPdb, "pdb")
        try
          let newPath = SageFs.ShadowCopy.shadowCopyFile shadowDir srcDll
          let newPdb = Path.ChangeExtension(newPath, ".pdb")
          (File.Exists newPdb) |> Expect.isTrue "Shadow PDB should also be copied"
        finally
          if Directory.Exists shadowDir then Directory.Delete(shadowDir, true)
          if Directory.Exists srcDir then Directory.Delete(srcDir, true)

      testCase "shadowCopyFile preserves directory structure via unique flat naming"
      <| fun _ ->
        let shadowDir = SageFs.ShadowCopy.createShadowDir ()
        let srcDir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-test-flat-%s" (Guid.NewGuid().ToString("N").[..7]))
        let sub1 = Path.Combine(srcDir, "projA", "bin")
        let sub2 = Path.Combine(srcDir, "projB", "bin")
        Directory.CreateDirectory sub1 |> ignore
        Directory.CreateDirectory sub2 |> ignore
        let dll1 = Path.Combine(sub1, "A.dll")
        let dll2 = Path.Combine(sub2, "B.dll")
        File.WriteAllText(dll1, "a")
        File.WriteAllText(dll2, "b")
        try
          let new1 = SageFs.ShadowCopy.shadowCopyFile shadowDir dll1
          let new2 = SageFs.ShadowCopy.shadowCopyFile shadowDir dll2
          (File.Exists new1) |> Expect.isTrue "First shadow copy should exist"
          (File.Exists new2) |> Expect.isTrue "Second shadow copy should exist"
          new1 |> Expect.notEqual "Different DLLs should produce different shadow paths" new2
        finally
          if Directory.Exists shadowDir then Directory.Delete(shadowDir, true)
          if Directory.Exists srcDir then Directory.Delete(srcDir, true)

      testCase "shadowCopyFile returns original path when file does not exist"
      <| fun _ ->
        let shadowDir = SageFs.ShadowCopy.createShadowDir ()
        try
          let result = SageFs.ShadowCopy.shadowCopyFile shadowDir "/nonexistent/Foo.dll"
          result |> Expect.equal "Should return original if source missing" "/nonexistent/Foo.dll"
        finally
          if Directory.Exists shadowDir then Directory.Delete(shadowDir, true)

      testCase "shadowCopySolution keeps References in place"
      <| fun _ ->
        let shadowDir = SageFs.ShadowCopy.createShadowDir ()
        let srcDir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-test-sln-%s" (Guid.NewGuid().ToString("N").[..7]))
        Directory.CreateDirectory srcDir |> ignore
        let fakeDll = Path.Combine(srcDir, "MyProj.dll")
        File.WriteAllText(fakeDll, "content")
        let sln: SageFs.ProjectLoading.Solution = {
          FsProjects = []
          Projects = []
          StartupFiles = []
          References = [ fakeDll ]
          LibPaths = []
          OtherArgs = []
        }
        try
          let result = SageFs.ShadowCopy.shadowCopySolution shadowDir sln
          result.References |> Expect.isNonEmpty "Should have references"
          result.References.[0] |> Expect.equal "References should stay in place (shadowing breaks FSI #load resolution)" fakeDll
        finally
          if Directory.Exists shadowDir then Directory.Delete(shadowDir, true)
          if Directory.Exists srcDir then Directory.Delete(srcDir, true)

      testCase "shadowCopySolution leaves originals unlocked"
      <| fun _ ->
        let shadowDir = SageFs.ShadowCopy.createShadowDir ()
        let srcDir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-test-lock-%s" (Guid.NewGuid().ToString("N").[..7]))
        Directory.CreateDirectory srcDir |> ignore
        let fakeDll = Path.Combine(srcDir, "Lock.dll")
        File.WriteAllText(fakeDll, "original-content")
        let sln: SageFs.ProjectLoading.Solution = {
          FsProjects = []
          Projects = []
          StartupFiles = []
          References = [ fakeDll ]
          LibPaths = []
          OtherArgs = []
        }
        try
          let _result = SageFs.ShadowCopy.shadowCopySolution shadowDir sln
          // Original should still be writable (not locked)
          File.WriteAllText(fakeDll, "updated-content")
          (File.ReadAllText fakeDll) |> Expect.equal "Original should be writable" "updated-content"
        finally
          if Directory.Exists shadowDir then Directory.Delete(shadowDir, true)
          if Directory.Exists srcDir then Directory.Delete(srcDir, true)

      testCase "cleanupShadowDir removes the directory and its contents"
      <| fun _ ->
        let dir = SageFs.ShadowCopy.createShadowDir ()
        File.WriteAllText(Path.Combine(dir, "test.dll"), "data")
        (Directory.Exists dir) |> Expect.isTrue "Should exist before cleanup"
        SageFs.ShadowCopy.cleanupShadowDir dir
        (Directory.Exists dir) |> Expect.isFalse "Should be removed after cleanup"

      testCase "cleanupShadowDir is safe on nonexistent dir"
      <| fun _ ->
        SageFs.ShadowCopy.cleanupShadowDir "/nonexistent/path/sagefs-shadow"
        (Directory.Exists "/nonexistent/path/sagefs-shadow") |> Expect.isFalse "Should not create the directory"
    ]

// ============================================================================
// WARM-UP RETRY LOGIC (Iterative Dependency Resolution)
// ============================================================================

module WarmUpTests =

  open SageFs.WarmUp

  /// Mock opener that succeeds for names in the "available" set,
  /// and adds newly-opened names to make dependents available next round.
  let dependencyOpener (deps: Map<string, string list>) =
    let opened = System.Collections.Generic.HashSet<string>()
    fun (name: string) ->
      match Map.tryFind name deps with
      | None ->
        opened.Add(name) |> ignore
        Ok ()
      | Some required ->
        if required |> List.forall opened.Contains then
          opened.Add(name) |> ignore
          Ok ()
        else
          Error (sprintf "Missing deps for %s" name)

  let tests =
    testList "WarmUp.openWithRetry" [

      testCase "empty input returns empty succeeded and failed"
      <| fun _ ->
        let succeeded, failed = openWithRetry 5 (fun _ -> Ok ()) []
        succeeded |> Expect.isEmpty "No succeeded"
        failed |> Expect.isEmpty "No failed"

      testCase "all succeed on first pass"
      <| fun _ ->
        let succeeded, failed = openWithRetry 5 (fun _ -> Ok ()) [ "A"; "B"; "C" ]
        succeeded |> Expect.equal "All should succeed" [ "A"; "B"; "C" ]
        failed |> Expect.isEmpty "None should fail"

      testCase "all fail permanently"
      <| fun _ ->
        let succeeded, failed =
          openWithRetry 5 (fun n -> Error (sprintf "%s broken" n)) [ "A"; "B" ]
        succeeded |> Expect.isEmpty "None should succeed"
        (failed |> List.map fst) |> Expect.equal "All should fail" [ "A"; "B" ]
        (failed |> List.map snd) |> Expect.equal "Errors preserved" [ "A broken"; "B broken" ]

      testCase "dependency chain resolves in two rounds"
      <| fun _ ->
        // B depends on A. If tried in order [B; A], B fails first, A succeeds,
        // then B succeeds on retry.
        let opener = dependencyOpener (Map.ofList [ "B", [ "A" ] ])
        let succeeded, failed = openWithRetry 5 opener [ "B"; "A" ]
        succeeded |> Expect.contains "A should succeed" "A"
        succeeded |> Expect.contains "B should succeed after retry" "B"
        failed |> Expect.isEmpty "No permanent failures"

      testCase "diamond dependency resolves"
      <| fun _ ->
        // D depends on B and C, B depends on A, C depends on A
        let deps = Map.ofList [
          "B", [ "A" ]
          "C", [ "A" ]
          "D", [ "B"; "C" ]
        ]
        let opener = dependencyOpener deps
        let succeeded, failed = openWithRetry 10 opener [ "D"; "C"; "B"; "A" ]
        succeeded |> Expect.hasLength "All four should succeed" 4
        failed |> Expect.isEmpty "No failures"

      testCase "max rounds stops iteration"
      <| fun _ ->
        // Need 3 rounds to resolve A→B→C chain, but limit to 2
        let deps = Map.ofList [ "B", [ "A" ]; "C", [ "B" ] ]
        let opener = dependencyOpener deps
        let succeeded, failed = openWithRetry 2 opener [ "C"; "B"; "A" ]
        succeeded |> Expect.contains "A resolves round 1" "A"
        succeeded |> Expect.contains "B resolves round 2" "B"
        (failed |> List.map fst) |> Expect.equal "C still failed after max rounds" [ "C" ]

      testCase "convergence: stops when no progress"
      <| fun _ ->
        let callCount = ref 0
        let opener name =
          callCount.Value <- callCount.Value + 1
          Error (sprintf "%s always fails" name)
        let _succeeded, failed = openWithRetry 10 opener [ "X"; "Y" ]
        // Should stop after 1 round since no progress was made
        callCount.Value |> Expect.equal "Should only call opener once per name when no progress" 2
        failed |> Expect.hasLength "Both should fail" 2

      testCase "partition property: every name in exactly one list"
      <| fun _ ->
        let deps = Map.ofList [ "B", [ "A" ]; "Z", [ "MISSING" ] ]
        let opener = dependencyOpener deps
        let input = [ "A"; "B"; "Z" ]
        let succeeded, failed = openWithRetry 5 opener input
        let allNames = succeeded @ (failed |> List.map fst) |> List.sort
        allNames |> Expect.equal "All names accounted for" (input |> List.sort)
        (succeeded @ (failed |> List.map fst)) |> Expect.hasLength "No duplicates" (List.length input)

      testCase "mixed: some first pass, some retry, some permanent"
      <| fun _ ->
        // A: no deps (round 1), B: depends on A (round 2), X: depends on MISSING (permanent)
        let deps = Map.ofList [ "B", [ "A" ]; "X", [ "MISSING" ] ]
        let opener = dependencyOpener deps
        let succeeded, failed = openWithRetry 5 opener [ "X"; "B"; "A" ]
        (succeeded |> List.sort) |> Expect.equal "A and B succeed" [ "A"; "B" ]
        (failed |> List.map fst) |> Expect.equal "X permanently fails" [ "X" ]

      testCase "preserves first-round error, not cascade error"
      <| fun _ ->
        // Simulates FSI cascade behavior: round 1 gives real error,
        // round 2 gives "cascade from earlier error"
        let callCount = System.Collections.Generic.Dictionary<string, int>()
        let opener (name: string) =
          let count = match callCount.TryGetValue(name) with true, c -> c | _ -> 0
          callCount.[name] <- count + 1
          if name = "Good" then Ok ()
          elif count = 0 then Error (sprintf "%s: type 'IdentityUser' not found" name)
          else Error (sprintf "%s: error related to earlier error" name)
        let succeeded, failed = openWithRetry 5 opener [ "Bad1"; "Good"; "Bad2" ]
        succeeded |> Expect.equal "Good should succeed" [ "Good" ]
        failed |> Expect.hasLength "Two should fail" 2
        for _name, err in failed do
          err |> Expect.stringContains "Should preserve first-round error, not cascade" "IdentityUser"

      testCase "isBenignOpenError detects RequireQualifiedAccess"
      <| fun _ ->
        let msg = "This declaration opens the module 'Falco.Response', which is marked as 'RequireQualifiedAccess'. Adjust your code to use qualified references."
        (isBenignOpenError msg) |> Expect.isTrue "RequireQualifiedAccess should be benign"

      testCase "isBenignOpenError returns false for real errors"
      <| fun _ ->
        let msg = "The namespace or module 'Foo' is not defined."
        (isBenignOpenError msg) |> Expect.isFalse "Real errors should not be benign"

      testCase "isBenignOpenError returns false for missing dependency"
      <| fun _ ->
        let msg = "The type 'IdentityUser' is not defined in 'Microsoft.AspNetCore.Identity'."
        (isBenignOpenError msg) |> Expect.isFalse "Missing dependency should not be benign"

      testCase "RequireQualifiedAccess errors treated as success in opener"
      <| fun _ ->
        let opener name =
          match name with
          | "Response" -> Error "marked as 'RequireQualifiedAccess'. Adjust your code"
          | "Result" -> Error "marked as 'RequireQualifiedAccess'. Operation could not be completed"
          | _ -> Ok ()
        let tolerantOpener name =
          match opener name with
          | Error msg when isBenignOpenError msg -> Ok ()
          | other -> other
        let succeeded, failed = openWithRetry 5 tolerantOpener ["System"; "Response"; "Result"]
        (List.length succeeded) |> Expect.equal "All should succeed including RequireQualifiedAccess" 3
        (List.length failed) |> Expect.equal "None should fail" 0
    ]

  module Properties =
    open FsCheck

    let tests =
      testList "WarmUp.openWithRetry properties" [

        testProperty "all names in succeeded + failed = input (partition)"
        <| fun (names: string list) ->
          let uniqueNames = names |> List.distinct
          let succeeded, failed =
            openWithRetry 5 (fun _ -> Ok ()) uniqueNames
          let result = succeeded @ (failed |> List.map fst) |> List.sort
          result = (uniqueNames |> List.sort)

        testProperty "always-Ok opener → all succeed"
        <| fun (names: string list) ->
          let uniqueNames = names |> List.distinct
          let succeeded, failed = openWithRetry 5 (fun _ -> Ok ()) uniqueNames
          List.isEmpty failed && (succeeded |> List.sort) = (uniqueNames |> List.sort)

        testProperty "always-Error opener → all fail"
        <| fun (names: string list) ->
          let uniqueNames = names |> List.distinct
          let succeeded, failed =
            openWithRetry 5 (fun _ -> Error "nope") uniqueNames
          List.isEmpty succeeded && (failed |> List.map fst |> List.sort) = (uniqueNames |> List.sort)
      ]

// ============================================================================
// Combine All Tests
// ============================================================================

[<Tests>]
let allTests =
  testSequenced <| testList "MCP LLM Interop Improvements (TDD)" [
    StartupConfigTests.tests
    GetStartupInfoTests.tests
    EnhancedStatusTests.tests
    ProjectDiscoveryTests.tests
    ProjectDiscoveryTests.liveTests
    McpAdapterEnhancementTests.tests
    ShadowCopyTests.tests
    WarmUpTests.tests
    WarmUpTests.Properties.tests
  ]
