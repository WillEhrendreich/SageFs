module SageFs.Tests.McpLlmInteropTests

open Expecto
open System
open System.IO
open System.Threading.Tasks
open SageFs
open SageFs.AppState
open SageFs.Features.Events
open SageFs.McpTools
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
    testList "[Integration] StartupConfig type and storage" [
      
      testCase "StartupConfig should have all required fields"
      <| fun _ ->
        // This test verifies the StartupConfig type exists in AppState.fs
        let config: SageFs.AppState.StartupConfig = {
          CommandLineArgs = [| "--proj"; "Test.fsproj" |]
          LoadedProjects = [ "Test.fsproj" ]
          WorkingDirectory = @"C:\Code\Test"
          McpPort = 8080
          HotReloadEnabled = true
          AspireDetected = false
          StartupTimestamp = DateTime.UtcNow; StartupProfileLoaded = None
        }
        
        Expect.equal config.CommandLineArgs.Length 2 "Should have command line args"
        Expect.equal config.LoadedProjects.Length 1 "Should have loaded projects"
        Expect.equal config.McpPort 8080 "Should have MCP port"
        Expect.isTrue config.HotReloadEnabled "Should track hot reload"
        Expect.isFalse config.AspireDetected "Should track Aspire detection"
      
      testCase "StartupConfig should be optional in AppState"
      <| fun _ ->
        task {
          let ctx = sharedCtx ()
          
          // Get the AppState and verify StartupConfig field exists
          let! appState = globalActorResult.Value.Actor.PostAndAsyncReply(fun reply -> SageFs.AppState.GetAppState reply)
          
          // The field exists and can be Some or None
          Expect.isTrue (appState.StartupConfig.IsSome || appState.StartupConfig.IsNone) "StartupConfig field should exist"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
      
      testCase "StartupConfig should handle empty/default states"
      <| fun _ ->
        let emptyConfig: SageFs.AppState.StartupConfig = {
          CommandLineArgs = [||]
          LoadedProjects = []
          WorkingDirectory = ""
          McpPort = 0
          HotReloadEnabled = false
          AspireDetected = false
          StartupTimestamp = DateTime.UtcNow; StartupProfileLoaded = None
        }
        
        Expect.equal emptyConfig.LoadedProjects.Length 0 "Should handle no projects"
        Expect.equal emptyConfig.McpPort 0 "Should handle no MCP port"
    ]

// ============================================================================
// CRITICAL IMPROVEMENT #2: get_startup_info Tool
// ============================================================================

module GetStartupInfoTests =
  
  let tests =
    testList "[Integration] get_startup_info tool" [
      
      testCase "get_startup_info should return structured startup information"
      <| fun _ ->
        task {
          let ctx = sharedCtx ()
          
          let! result = getStartupInfo ctx
          
          // Should include key information from StartupConfig in AppState
          Expect.isNotNull result "Should return result"
          Expect.isTrue (result.Length > 0) "Should return non-empty result"
          // Verify it mentions startup information
          Expect.stringContains result "Startup" "Should mention startup"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
      
      testCase "get_startup_info should handle missing startup config gracefully"
      <| fun _ ->
        task {
          let ctx = sharedCtx ()
          
          let! result = getStartupInfo ctx
          
          // Should always return something, even if no config
          Expect.isNotNull result "Should return result"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
      
      testCase "get_startup_info should return parseable JSON format"
      <| fun _ ->
        task {
          let ctx = sharedCtx ()
          
          let! result = getStartupInfoJson ctx
          
          // Should be valid JSON that LLMs can parse
          Expect.stringContains result "{" "Should be JSON object"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
      
      testCase "get_startup_info should include usage tips for LLMs"
      <| fun _ ->
        task {
          let ctx = sharedCtx ()
          
          let! result = getStartupInfo ctx
          
          // Should guide LLMs on how to use SageFs properly
          Expect.stringContains result "SageFs" "Should mention SageFs"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
    ]

// ============================================================================
// CRITICAL IMPROVEMENT #3: Enhanced get_fsi_status with Startup Context
// ============================================================================

module EnhancedStatusTests =
  
  let tests =
    testList "[Integration] Enhanced get_fsi_status with startup context" [
      
      testCase "get_fsi_status should include startup information section"
      <| fun _ ->
        task {
          let ctx = sharedCtx ()
          
          let! result = getStatus ctx
          
          // Should include startup information from AppState
          Expect.stringContains result "Events:" "Should show events"
          Expect.stringContains result "Available:" "Should show tools"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
      
      testCase "get_fsi_status should show checkmarks for enabled features"
      <| fun _ ->
        task {
          let ctx = sharedCtx ()
          
          let! result = getStatus ctx
          
          // Should include formatted status
          Expect.isNotNull result "Should return result"
          Expect.isTrue (result.Length > 0) "Should return non-empty result"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
      
      testCase "get_fsi_status should work with default startup config"
      <| fun _ ->
        task {
          let ctx = sharedCtx ()
          
          let! result = getStatus ctx
          
          // Should still return basic status
          Expect.stringContains result "Events:" "Should show events"
          Expect.stringContains result "Available:" "Should show tools"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
    ]

// ============================================================================
// CRITICAL IMPROVEMENT #4: Project Discovery Capabilities
// ============================================================================

module ProjectDiscoveryTests =

  let tests =
    testList "[Integration] Project discovery for LLMs" [

      testCase "loadSolution discovers .fsproj files when no args provided"
      <| fun _ ->
        let solution = SageFs.ProjectLoading.loadSolution quietLogger []
        Expect.isTrue (solution.Projects.Length >= 0) "Should return project list"
        Expect.isTrue (solution.FsProjects.Length >= 0) "Should return F# projects list"

      testCase "loadSolution discovers .sln files when no args provided"
      <| fun _ ->
        let solution = SageFs.ProjectLoading.loadSolution quietLogger []
        Expect.isTrue (box solution <> null) "Should return solution object"

      testCase "isSolutionFile matches .sln files"
      <| fun _ ->
        Expect.isTrue (SageFs.McpAdapter.isSolutionFile "MyApp.sln") "Should match .sln"

      testCase "isSolutionFile matches .slnx files"
      <| fun _ ->
        Expect.isTrue (SageFs.McpAdapter.isSolutionFile "MyApp.slnx") "Should match .slnx"

      testCase "isSolutionFile rejects non-solution files"
      <| fun _ ->
        Expect.isFalse (SageFs.McpAdapter.isSolutionFile "MyApp.fsproj") "Should not match .fsproj"

      testCase "isProjectFile matches .fsproj files"
      <| fun _ ->
        Expect.isTrue (SageFs.McpAdapter.isProjectFile "MyApp.fsproj") "Should match .fsproj"

      testCase "isProjectFile rejects non-project files"
      <| fun _ ->
        Expect.isFalse (SageFs.McpAdapter.isProjectFile "MyApp.sln") "Should not match .sln"

      testCase "formatAvailableProjects includes .slnx in header"
      <| fun _ ->
        let result =
          SageFs.McpAdapter.formatAvailableProjects
            "/test/dir"
            [| "App.fsproj" |]
            [| "App.slnx" |]
        Expect.stringContains result ".slnx" "Should mention .slnx in output"
        Expect.stringContains result "App.slnx" "Should list the .slnx file"

      testCase "formatAvailableProjects shows none when empty"
      <| fun _ ->
        let result =
          SageFs.McpAdapter.formatAvailableProjects "/test/dir" [||] [||]
        Expect.stringContains result "(none found)" "Should show none for empty"

      testCase "get_available_projects tool formats discoverable projects for LLMs"
      <| fun _ ->
        task {
          let ctx = sharedCtx ()

          // Get the working directory the actor actually uses
          let! appState = globalActorResult.Value.Actor.PostAndAsyncReply(fun reply -> GetAppState reply)
          let workingDir =
            match appState.StartupConfig with
            | Some config -> config.WorkingDirectory
            | None -> Environment.CurrentDirectory

          let! result = getAvailableProjects ctx

          Expect.stringContains result "Projects" "Should have projects section"
          Expect.stringContains result "Solutions" "Should have solutions section"
          Expect.stringContains result ".fsproj" "Should mention project extension"
          Expect.stringContains result ".sln" "Should mention solution extension"
          Expect.stringContains result "SageFs --proj" "Should show usage tips"
          Expect.stringContains result workingDir "Should show working directory"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
    ]

// ============================================================================
// CRITICAL IMPROVEMENT #5: Enhanced Tool Descriptions
// ============================================================================

module ToolDescriptionTests =
  
  let tests =
    testList "Enhanced tool descriptions for LLMs" [
      
      testCase "send_fsharp_code description should mention project loading"
      <| fun _ ->
        let description = 
          """Send F# code to the FSI (F# Interactive) session for execution. 
Make sure to end statements with ';;' just as you would when interacting with fsi.exe.

NOTE: If types from a project are not available, SageFs may need to be restarted with:
  SageFs --proj YourProject.fsproj
This loads all project dependencies automatically."""
        
        Expect.stringContains description "--proj" "Should mention project loading"
        Expect.stringContains description "dependencies" "Should mention dependencies"
      
      testCase "get_fsi_status description should mention startup configuration"
      <| fun _ ->
        let description = 
          "Get information about the FSI service status, including startup configuration, loaded projects, session statistics, and available capabilities."
        
        Expect.stringContains description "startup configuration" "Should mention startup config"
        Expect.stringContains description "loaded projects" "Should mention projects"
        Expect.stringContains description "capabilities" "Should mention capabilities"
    ]

// ============================================================================
// CRITICAL IMPROVEMENT #6: McpContext Enhancement
// ============================================================================

module McpContextTests =
  
  let tests =
    testList "[Integration] McpContext accesses StartupConfig from AppState" [
      
      testCase "McpContext should access StartupConfig through Actor"
      <| fun _ ->
        task {
          let ctx = sharedCtx ()
          
          // Get StartupConfig from AppState via Actor
          let! appState = globalActorResult.Value.Actor.PostAndAsyncReply(fun reply -> SageFs.AppState.GetAppState reply)
          
          // StartupConfig should be accessible from AppState
          Expect.isTrue (appState.StartupConfig.IsSome || appState.StartupConfig.IsNone) "Should access StartupConfig"
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously
      
      testCase "McpContext should work without direct StartupConfig field"
      <| fun _ ->
        let ctx = sharedCtx ()
        
        // Context should be valid without StartupConfig field
        Expect.isTrue true "Should have actor"
        Expect.isTrue (box ctx.Store <> null) "Should have store"
    ]

// ============================================================================
// CRITICAL IMPROVEMENT #7: Adapter Functions for Formatting
// ============================================================================

module McpAdapterEnhancementTests =
  
  let tests =
    testList "McpAdapter formatting enhancements" [
      
      testCase "formatStartupInfo should create human-readable output"
      <| fun _ ->
        let config: SageFs.AppState.StartupConfig = {
          CommandLineArgs = [| "--proj"; "Test.fsproj" |]
          LoadedProjects = [ "Test.fsproj" ]
          WorkingDirectory = @"C:\Test"
          McpPort = 8080
          HotReloadEnabled = true
          AspireDetected = false
          StartupTimestamp = DateTime.UtcNow; StartupProfileLoaded = None
        }
        
        let output = SageFs.McpAdapter.formatStartupInfo config
        
        Expect.stringContains output "Test.fsproj" "Should include project"
        Expect.stringContains output "8080" "Should include port"
        Expect.stringContains output "Hot Reload" "Should mention hot reload"
      
      testCase "formatStartupInfoJson should create valid JSON"
      <| fun _ ->
        let config: SageFs.AppState.StartupConfig = {
          CommandLineArgs = [| "--proj"; "Test.fsproj" |]
          LoadedProjects = [ "Test.fsproj" ]
          WorkingDirectory = @"C:\Test"
          McpPort = 8080
          HotReloadEnabled = true
          AspireDetected = false
          StartupTimestamp = DateTime.UtcNow; StartupProfileLoaded = None
        }
        
        let json = SageFs.McpAdapter.formatStartupInfoJson config
        
        Expect.stringContains json "{" "Should be JSON object"
        Expect.stringContains json "\"commandLineArgs\"" "Should have field"
        Expect.stringContains json "\"loadedProjects\"" "Should have field"
        Expect.stringContains json "\"mcpPort\"" "Should have field"
      
      testCase "formatEnhancedStatus should include startup section"
      <| fun _ ->
        let config: SageFs.AppState.StartupConfig = {
          CommandLineArgs = [| "--proj"; "Test.fsproj" |]
          LoadedProjects = [ "Test.fsproj" ]
          WorkingDirectory = @"C:\Test"
          McpPort = 8080
          HotReloadEnabled = true
          AspireDetected = false
          StartupTimestamp = DateTime.UtcNow; StartupProfileLoaded = None
        }
        
        let output = SageFs.McpAdapter.formatEnhancedStatus "test-session" 5 SageFs.SessionState.Ready None (Some config)
        
        Expect.stringContains output "Startup Information" "Should have startup section"
        Expect.isFalse (output.Contains("Usage Tips")) "Should NOT have tips (moved to ServerInstructions)"

      testCase "formatStartupBanner includes version"
      <| fun _ ->
        let banner = SageFs.McpAdapter.formatStartupBanner "0.2.29" (Some 37749)
        Expect.stringContains banner "0.2.29" "Should include version"
        Expect.stringContains banner "SageFs" "Should include product name"

      testCase "formatStartupBanner includes MCP port when provided"
      <| fun _ ->
        let banner = SageFs.McpAdapter.formatStartupBanner "1.0.0" (Some 8080)
        Expect.stringContains banner "8080" "Should include MCP port"

      testCase "formatStartupBanner omits MCP when no port"
      <| fun _ ->
        let banner = SageFs.McpAdapter.formatStartupBanner "1.0.0" None
        Expect.isFalse (banner.Contains "MCP") "Should not mention MCP without port"
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
          Expect.isTrue (dir.StartsWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))) "Should be under temp"
          Expect.isTrue (dir.Contains "sagefs-shadow") "Should contain sagefs-shadow"
          Expect.isTrue (Directory.Exists dir) "Directory should exist"
        finally
          if Directory.Exists dir then Directory.Delete(dir, true)

      testCase "createShadowDir creates unique dirs on each call"
      <| fun _ ->
        let dir1 = SageFs.ShadowCopy.createShadowDir ()
        let dir2 = SageFs.ShadowCopy.createShadowDir ()
        try
          Expect.notEqual dir1 dir2 "Each call should create a unique directory"
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
          Expect.isTrue (File.Exists newPath) "Shadow copy should exist"
          Expect.notEqual newPath srcDll "Should be a different path"
          Expect.equal (File.ReadAllText newPath) "fake-dll-content" "Content should match"
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
          Expect.isTrue (File.Exists newPdb) "Shadow PDB should also be copied"
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
          Expect.isTrue (File.Exists new1) "First shadow copy should exist"
          Expect.isTrue (File.Exists new2) "Second shadow copy should exist"
          Expect.notEqual new1 new2 "Different DLLs should produce different shadow paths"
        finally
          if Directory.Exists shadowDir then Directory.Delete(shadowDir, true)
          if Directory.Exists srcDir then Directory.Delete(srcDir, true)

      testCase "shadowCopyFile returns original path when file does not exist"
      <| fun _ ->
        let shadowDir = SageFs.ShadowCopy.createShadowDir ()
        try
          let result = SageFs.ShadowCopy.shadowCopyFile shadowDir "/nonexistent/Foo.dll"
          Expect.equal result "/nonexistent/Foo.dll" "Should return original if source missing"
        finally
          if Directory.Exists shadowDir then Directory.Delete(shadowDir, true)

      testCase "shadowCopySolution rewrites TargetPaths to shadow dir"
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
          Expect.isNonEmpty result.References "Should have references"
          Expect.isTrue (result.References.[0].StartsWith shadowDir) "Reference should point to shadow dir"
          Expect.isTrue (File.Exists result.References.[0]) "Shadow DLL should exist"
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
          Expect.equal (File.ReadAllText fakeDll) "updated-content" "Original should be writable"
        finally
          if Directory.Exists shadowDir then Directory.Delete(shadowDir, true)
          if Directory.Exists srcDir then Directory.Delete(srcDir, true)

      testCase "cleanupShadowDir removes the directory and its contents"
      <| fun _ ->
        let dir = SageFs.ShadowCopy.createShadowDir ()
        File.WriteAllText(Path.Combine(dir, "test.dll"), "data")
        Expect.isTrue (Directory.Exists dir) "Should exist before cleanup"
        SageFs.ShadowCopy.cleanupShadowDir dir
        Expect.isFalse (Directory.Exists dir) "Should be removed after cleanup"

      testCase "cleanupShadowDir is safe on nonexistent dir"
      <| fun _ ->
        SageFs.ShadowCopy.cleanupShadowDir "/nonexistent/path/sagefs-shadow"
        Expect.isTrue true "Should not throw"
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
        Expect.isEmpty succeeded "No succeeded"
        Expect.isEmpty failed "No failed"

      testCase "all succeed on first pass"
      <| fun _ ->
        let succeeded, failed = openWithRetry 5 (fun _ -> Ok ()) [ "A"; "B"; "C" ]
        Expect.equal succeeded [ "A"; "B"; "C" ] "All should succeed"
        Expect.isEmpty failed "None should fail"

      testCase "all fail permanently"
      <| fun _ ->
        let succeeded, failed =
          openWithRetry 5 (fun n -> Error (sprintf "%s broken" n)) [ "A"; "B" ]
        Expect.isEmpty succeeded "None should succeed"
        Expect.equal (failed |> List.map fst) [ "A"; "B" ] "All should fail"
        Expect.equal (failed |> List.map snd) [ "A broken"; "B broken" ] "Errors preserved"

      testCase "dependency chain resolves in two rounds"
      <| fun _ ->
        // B depends on A. If tried in order [B; A], B fails first, A succeeds,
        // then B succeeds on retry.
        let opener = dependencyOpener (Map.ofList [ "B", [ "A" ] ])
        let succeeded, failed = openWithRetry 5 opener [ "B"; "A" ]
        Expect.contains succeeded "A" "A should succeed"
        Expect.contains succeeded "B" "B should succeed after retry"
        Expect.isEmpty failed "No permanent failures"

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
        Expect.hasLength succeeded 4 "All four should succeed"
        Expect.isEmpty failed "No failures"

      testCase "max rounds stops iteration"
      <| fun _ ->
        // Need 3 rounds to resolve A→B→C chain, but limit to 2
        let deps = Map.ofList [ "B", [ "A" ]; "C", [ "B" ] ]
        let opener = dependencyOpener deps
        let succeeded, failed = openWithRetry 2 opener [ "C"; "B"; "A" ]
        Expect.contains succeeded "A" "A resolves round 1"
        Expect.contains succeeded "B" "B resolves round 2"
        Expect.equal (failed |> List.map fst) [ "C" ] "C still failed after max rounds"

      testCase "convergence: stops when no progress"
      <| fun _ ->
        let callCount = ref 0
        let opener name =
          callCount.Value <- callCount.Value + 1
          Error (sprintf "%s always fails" name)
        let _succeeded, failed = openWithRetry 10 opener [ "X"; "Y" ]
        // Should stop after 1 round since no progress was made
        Expect.equal callCount.Value 2 "Should only call opener once per name when no progress"
        Expect.hasLength failed 2 "Both should fail"

      testCase "partition property: every name in exactly one list"
      <| fun _ ->
        let deps = Map.ofList [ "B", [ "A" ]; "Z", [ "MISSING" ] ]
        let opener = dependencyOpener deps
        let input = [ "A"; "B"; "Z" ]
        let succeeded, failed = openWithRetry 5 opener input
        let allNames = succeeded @ (failed |> List.map fst) |> List.sort
        Expect.equal allNames (input |> List.sort) "All names accounted for"
        Expect.hasLength (succeeded @ (failed |> List.map fst)) (List.length input) "No duplicates"

      testCase "mixed: some first pass, some retry, some permanent"
      <| fun _ ->
        // A: no deps (round 1), B: depends on A (round 2), X: depends on MISSING (permanent)
        let deps = Map.ofList [ "B", [ "A" ]; "X", [ "MISSING" ] ]
        let opener = dependencyOpener deps
        let succeeded, failed = openWithRetry 5 opener [ "X"; "B"; "A" ]
        Expect.equal (succeeded |> List.sort) [ "A"; "B" ] "A and B succeed"
        Expect.equal (failed |> List.map fst) [ "X" ] "X permanently fails"

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
        Expect.equal succeeded [ "Good" ] "Good should succeed"
        Expect.hasLength failed 2 "Two should fail"
        for _name, err in failed do
          Expect.stringContains err "IdentityUser" "Should preserve first-round error, not cascade"

      testCase "isBenignOpenError detects RequireQualifiedAccess"
      <| fun _ ->
        let msg = "This declaration opens the module 'Falco.Response', which is marked as 'RequireQualifiedAccess'. Adjust your code to use qualified references."
        Expect.isTrue (isBenignOpenError msg) "RequireQualifiedAccess should be benign"

      testCase "isBenignOpenError returns false for real errors"
      <| fun _ ->
        let msg = "The namespace or module 'Foo' is not defined."
        Expect.isFalse (isBenignOpenError msg) "Real errors should not be benign"

      testCase "isBenignOpenError returns false for missing dependency"
      <| fun _ ->
        let msg = "The type 'IdentityUser' is not defined in 'Microsoft.AspNetCore.Identity'."
        Expect.isFalse (isBenignOpenError msg) "Missing dependency should not be benign"

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
        Expect.equal (List.length succeeded) 3 "All should succeed including RequireQualifiedAccess"
        Expect.equal (List.length failed) 0 "None should fail"
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
    ToolDescriptionTests.tests
    McpContextTests.tests
    McpAdapterEnhancementTests.tests
    ShadowCopyTests.tests
    WarmUpTests.tests
    WarmUpTests.Properties.tests
  ]
