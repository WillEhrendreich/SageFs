module SageFs.Tests.ShadowCopyTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.ProjectLoading
open Ionide.ProjInfo.Types

/// Helper to create a minimal ProjectOptions with only TargetPath set.
let mkProjectOptions (targetPath: string) : ProjectOptions =
  { ProjectId = None
    ProjectFileName = "Test.fsproj"
    TargetFramework = "net10.0"
    SourceFiles = []
    OtherOptions = []
    ReferencedProjects = []
    PackageReferences = []
    LoadTime = DateTime.UtcNow
    TargetPath = targetPath
    TargetRefPath = None
    ProjectOutputType = ProjectOutputType.Library
    ProjectSdkInfo =
      { IsTestProject = false
        Configuration = "Debug"
        IsPackable = false
        TargetFramework = "net10.0"
        TargetFrameworkIdentifier = ".NETCoreApp"
        TargetFrameworkVersion = "v10.0"
        MSBuildAllProjects = []
        MSBuildToolsVersion = ""
        ProjectAssetsFile = ""
        RestoreSuccess = true
        Configurations = []
        TargetFrameworks = []
        RunArguments = None
        RunCommand = None
        IsPublishable = None }
    Items = []
    Properties = []
    CustomProperties = []
    AllProperties = Map.empty
    AllItems = Map.empty
    Analyzers = [] }

/// Creates a unique temp directory for test isolation.
let createTestDir () =
  let dir =
    Path.Combine(
      Path.GetTempPath(),
      sprintf "sagefs-test-%s" (Guid.NewGuid().ToString("N").[..7]))
  Directory.CreateDirectory dir |> ignore
  dir

/// Safely removes a directory if it exists.
let safeDelete dir =
  match Directory.Exists dir with
  | true -> Directory.Delete(dir, true)
  | false -> ()

[<Tests>]
let tests =
  testList "ShadowCopy" [

    testCase "createShadowDir creates a directory with correct prefix" <| fun _ ->
      let dir = SageFs.ShadowCopy.createShadowDir ()
      try
        Directory.Exists dir |> Expect.isTrue "directory should exist"
        Path.GetFileName dir
        |> fun name -> name.StartsWith "sagefs-shadow-"
        |> Expect.isTrue "name should start with sagefs-shadow-"
      finally
        safeDelete dir

    testCase "createShadowDir creates unique dirs" <| fun _ ->
      let dir1 = SageFs.ShadowCopy.createShadowDir ()
      let dir2 = SageFs.ShadowCopy.createShadowDir ()
      try
        dir1 |> Expect.notEqual "two calls should return different paths" dir2
      finally
        safeDelete dir1
        safeDelete dir2

    testCase "shadowCopyFile copies DLL" <| fun _ ->
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      let srcDir = createTestDir ()
      try
        let srcDll = Path.Combine(srcDir, "Test.dll")
        File.WriteAllBytes(srcDll, [| 0xDEuy; 0xADuy |])
        let dest = SageFs.ShadowCopy.shadowCopyFile shadowDir srcDll
        File.Exists dest |> Expect.isTrue "shadow DLL should exist"
        File.ReadAllBytes dest
        |> Expect.equal "content should match" [| 0xDEuy; 0xADuy |]
      finally
        safeDelete shadowDir
        safeDelete srcDir

    testCase "shadowCopyFile copies companion PDB" <| fun _ ->
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      let srcDir = createTestDir ()
      try
        let srcDll = Path.Combine(srcDir, "WithPdb.dll")
        let srcPdb = Path.Combine(srcDir, "WithPdb.pdb")
        File.WriteAllText(srcDll, "dll-data")
        File.WriteAllText(srcPdb, "pdb-data")
        let dest = SageFs.ShadowCopy.shadowCopyFile shadowDir srcDll
        let destPdb = Path.ChangeExtension(dest, ".pdb")
        File.Exists destPdb |> Expect.isTrue "companion PDB should be copied"
        File.ReadAllText destPdb
        |> Expect.equal "PDB content should match" "pdb-data"
      finally
        safeDelete shadowDir
        safeDelete srcDir

    testCase "shadowCopyFile returns original path when source doesn't exist" <| fun _ ->
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      try
        let fakePath = Path.Combine(Path.GetTempPath(), "nonexistent-42.dll")
        let result = SageFs.ShadowCopy.shadowCopyFile shadowDir fakePath
        result |> Expect.equal "should return original path" fakePath
      finally
        safeDelete shadowDir

    testCase "shadowCopyFile without PDB only copies DLL" <| fun _ ->
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      let srcDir = createTestDir ()
      try
        let srcDll = Path.Combine(srcDir, "NoPdb.dll")
        File.WriteAllText(srcDll, "dll-only")
        let dest = SageFs.ShadowCopy.shadowCopyFile shadowDir srcDll
        File.Exists dest |> Expect.isTrue "DLL should be copied"
        Path.ChangeExtension(dest, ".pdb")
        |> File.Exists
        |> Expect.isFalse "PDB should NOT exist when source has no PDB"
      finally
        safeDelete shadowDir
        safeDelete srcDir

    testCase "shadowCopySolution rewrites TargetPath" <| fun _ ->
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      let srcDir = createTestDir ()
      try
        let dllPath = Path.Combine(srcDir, "Proj.dll")
        File.WriteAllText(dllPath, "proj-dll")
        let sln: Solution =
          { emptySolution with
              Projects = [ mkProjectOptions dllPath ] }
        let result = SageFs.ShadowCopy.shadowCopySolution shadowDir sln
        result.Projects
        |> List.head
        |> fun p -> p.TargetPath.StartsWith shadowDir
        |> Expect.isTrue "TargetPath should point to shadow dir"
        result.Projects
        |> List.head
        |> fun p -> File.Exists p.TargetPath
        |> Expect.isTrue "shadow-copied project DLL should exist"
      finally
        safeDelete shadowDir
        safeDelete srcDir

    testCase "shadowCopySolution keeps References in place" <| fun _ ->
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      let srcDir = createTestDir ()
      try
        let refDll = Path.Combine(srcDir, "Ref.dll")
        File.WriteAllText(refDll, "ref-content")
        let sln: Solution =
          { emptySolution with References = [ refDll ] }
        let result = SageFs.ShadowCopy.shadowCopySolution shadowDir sln
        result.References
        |> List.head
        |> fun r -> r = refDll
        |> Expect.isTrue "References should stay in place (shadowing them breaks FSI #load transitive resolution)"
      finally
        safeDelete shadowDir
        safeDelete srcDir

    testCase "shadowCopySolution preserves emptySolution" <| fun _ ->
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      try
        let result = SageFs.ShadowCopy.shadowCopySolution shadowDir emptySolution
        result.Projects |> Expect.isEmpty "Projects should be empty"
        result.References |> Expect.isEmpty "References should be empty"
        result.StartupFiles |> Expect.isEmpty "StartupFiles should be empty"
        result.OtherArgs |> Expect.isEmpty "OtherArgs should be empty"
      finally
        safeDelete shadowDir

    testCase "cleanupShadowDir removes directory" <| fun _ ->
      let dir = SageFs.ShadowCopy.createShadowDir ()
      File.WriteAllText(Path.Combine(dir, "test.dll"), "data")
      Directory.Exists dir |> Expect.isTrue "should exist before cleanup"
      SageFs.ShadowCopy.cleanupShadowDir dir
      Directory.Exists dir |> Expect.isFalse "should be gone after cleanup"

    testCase "cleanupShadowDir on nonexistent dir is no-op" <| fun _ ->
      let fakePath =
        Path.Combine(
          Path.GetTempPath(),
          sprintf "sagefs-shadow-nonexist-%s" (Guid.NewGuid().ToString("N").[..7]))
      SageFs.ShadowCopy.cleanupShadowDir fakePath
      // reaching here without exception is the assertion
      true |> Expect.isTrue "should not throw on nonexistent dir"

    testCase "cleanupAllPending clears pendingCleanups" <| fun _ ->
      let dir1 = createTestDir ()
      let dir2 = createTestDir ()
      try
        SageFs.ShadowCopy.pendingCleanups.Add dir1
        SageFs.ShadowCopy.pendingCleanups.Add dir2
        SageFs.ShadowCopy.cleanupAllPending ()
        Directory.Exists dir1 |> Expect.isFalse "dir1 should be removed"
        Directory.Exists dir2 |> Expect.isFalse "dir2 should be removed"
      finally
        safeDelete dir1
        safeDelete dir2
  ]
