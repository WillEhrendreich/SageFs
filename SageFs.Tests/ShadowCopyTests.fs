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

let private liveOnly (alive: Set<int>) (pid: int) =
  match alive.Contains pid with
  | true -> SageFs.ShadowCopy.OwnerLiveness.Running
  | false -> SageFs.ShadowCopy.OwnerLiveness.Gone

[<Tests>]
let staleSweepTests =
  testList "ShadowCopy stale sweep" [
    testCase "WHY — ShadowDirOwner — a shadow dir records the process that owns it, because only its owner may decide it is garbage" <| fun _ ->
      let dir = SageFs.ShadowCopy.createShadowDir ()
      try
        SageFs.ShadowCopy.ShadowDirOwner.ofDirName dir
        |> Expect.equal "created dirs are owned by this process" (SageFs.ShadowCopy.ShadowDirOwner.OwnedBy Environment.ProcessId)
      finally
        safeDelete dir

    testCase "WHY — ShadowDirOwner — a legacy name without an owner is UnknownOwner, because its owner can never be proven dead" <| fun _ ->
      SageFs.ShadowCopy.ShadowDirOwner.ofDirName "/tmp/sagefs-shadow-20c3008c"
      |> Expect.equal "legacy dirs have no owner" SageFs.ShadowCopy.ShadowDirOwner.UnknownOwner

    testProperty "WHY — staleShadowDirs — only dirs whose owner is provably gone are swept, because deleting a live worker's shadow copy breaks every compile in that session" <|
      fun (owners: (FsCheck.NonNegativeInt * bool) list) (legacyCount: byte) ->
        let owned =
          owners |> List.mapi (fun i (pid, alive) -> sprintf "/tmp/sagefs-shadow-%d-%08x" (pid.Get + 1) i, pid.Get + 1, alive)
        let alivePids = owned |> List.choose (fun (_, pid, alive) -> match alive with | true -> Some pid | false -> None) |> Set.ofList
        let legacy = [ for i in 1 .. int legacyCount % 5 -> sprintf "/tmp/sagefs-shadow-%08x" i ]
        let swept =
          SageFs.ShadowCopy.staleShadowDirs (liveOnly alivePids) ((owned |> List.map (fun (d, _, _) -> d)) @ legacy)
          |> Set.ofList
        let expected =
          owned
          |> List.choose (fun (d, pid, _) -> match alivePids.Contains pid with | true -> None | false -> Some d)
          |> Set.ofList
        swept = expected

    testCase "WHY — staleShadowDirs — an owner whose liveness cannot be determined keeps its dir, because a sweep must fail closed" <| fun _ ->
      SageFs.ShadowCopy.staleShadowDirs (fun _ -> SageFs.ShadowCopy.OwnerLiveness.Unknown) [ "/tmp/sagefs-shadow-4242-0000abcd" ]
      |> Expect.isEmpty "unknown liveness is never swept"

    testCase "WHY — cleanupStaleDirsIn — another running process's shadow dir survives a sweep, because one session's hard reset deleted every other session's shadow copy" <| fun _ ->
      let root = Directory.CreateTempSubdirectory("sagefs-sweep-").FullName
      try
        let live = Directory.CreateDirectory(Path.Combine(root, sprintf "sagefs-shadow-%d-aaaaaaaa" Environment.ProcessId)).FullName
        let gone = Directory.CreateDirectory(Path.Combine(root, "sagefs-shadow-999999999-bbbbbbbb")).FullName
        let legacy = Directory.CreateDirectory(Path.Combine(root, "sagefs-shadow-cccccccc")).FullName
        SageFs.ShadowCopy.cleanupStaleDirsIn root (fun pid ->
          match pid = Environment.ProcessId with
          | true -> SageFs.ShadowCopy.OwnerLiveness.Running
          | false -> SageFs.ShadowCopy.OwnerLiveness.Gone)
        Directory.Exists live |> Expect.isTrue "a live owner's shadow dir is kept"
        Directory.Exists legacy |> Expect.isTrue "an ownerless legacy dir is kept"
        Directory.Exists gone |> Expect.isFalse "a dead owner's shadow dir is swept"
      finally
        safeDelete root
  ]
