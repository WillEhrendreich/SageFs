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

/// Writes a library `ShadowDep` with a class `Dep.Base`, and a library `ShadowApp` with a class `App.Derived` that
/// inherits it, into `dir`. `ShadowApp` cannot load a type of its own without `ShadowDep`: the shape of a project whose
/// types extend or implement something a package brings (a test project's closures over Expecto's types, for one).
let writeDependentLibraries (dir: string) : string * string =
  let depPath = Path.Combine(dir, "ShadowDep.dll")
  let appPath = Path.Combine(dir, "ShadowApp.dll")
  let classAttrs =
    Mono.Cecil.TypeAttributes.Public
    ||| Mono.Cecil.TypeAttributes.Class
    ||| Mono.Cecil.TypeAttributes.BeforeFieldInit
  let write (path: string) (module_: Mono.Cecil.ModuleDefinition) =
    // The file is closed when this returns, so the next step can read it.
    using (File.Create path) (fun stream -> module_.Write stream)
    module_.Dispose()
  let dep = Mono.Cecil.ModuleDefinition.CreateModule("ShadowDep", Mono.Cecil.ModuleKind.Dll)
  dep.Types.Add(Mono.Cecil.TypeDefinition("Dep", "Base", classAttrs, dep.TypeSystem.Object))
  write depPath dep
  let app = Mono.Cecil.ModuleDefinition.CreateModule("ShadowApp", Mono.Cecil.ModuleKind.Dll)
  let depForRef = Mono.Cecil.ModuleDefinition.ReadModule depPath
  let baseRef = app.ImportReference(depForRef.GetType("Dep.Base"))
  app.Types.Add(Mono.Cecil.TypeDefinition("App", "Derived", classAttrs, baseRef))
  write appPath app
  depForRef.Dispose()
  depPath, appPath

/// `pendingCleanups` is one process-wide bag and `cleanupAllPending` drains all of it,
/// so a test that puts a dir in the bag and then asserts it is still there loses to any
/// other test that drains it in between (the "still cannot be deleted stays pending"
/// case failed that way under load). Every test that reads or drains the bag is
/// `testSequenced`, which runs after the whole parallel pool, alone, so none of them
/// can overlap another or a parallel test. (It was a `testSequencedGroup`, which runs
/// INSIDE the parallel pool and only excludes its own group: measured on Expecto
/// 11.0.0, see HarmonyCanaryTests.)
let private bagTestCase (name: string) (body: unit -> unit) =
  testSequenced (testCase name body)

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

    // ─── origin sidecar (run_app's missing-dependency fix) ─────────────────
    // Bug: `run_app`'s `Assembly.LoadFrom` on a shadow copy fails with
    // FileNotFoundException for the project's first NuGet/project-reference
    // dependency, because the shadow dir (by design — see the doc comment
    // above `shadowCopyFile`) contains ONLY the entry assembly. The sidecar
    // is how `AppRunner.ManagedDependencyResolution` finds its way back to
    // the original build output directory, where every real dependency lives.

    testCase "WHY — shadowCopyFile records the original directory in a .origin sidecar" <| fun _ ->
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      let srcDir = createTestDir ()
      try
        let srcDll = Path.Combine(srcDir, "App.dll")
        File.WriteAllText(srcDll, "app-dll")
        let dest = SageFs.ShadowCopy.shadowCopyFile shadowDir srcDll
        File.Exists (SageFs.ShadowCopy.originSidecarPath dest)
        |> Expect.isTrue "a .origin sidecar must exist next to the shadow copy"
      finally
        safeDelete shadowDir
        safeDelete srcDir

    testCase "WHY — tryReadOriginDir round-trips exactly what shadowCopyFile recorded" <| fun _ ->
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      let srcDir = createTestDir ()
      try
        let srcDll = Path.Combine(srcDir, "App.dll")
        File.WriteAllText(srcDll, "app-dll")
        let dest = SageFs.ShadowCopy.shadowCopyFile shadowDir srcDll
        SageFs.ShadowCopy.tryReadOriginDir dest
        |> Expect.equal "the recorded origin is the source directory, not the shadow dir" (Some (Path.GetFullPath srcDir))
      finally
        safeDelete shadowDir
        safeDelete srcDir

    testCase "the bug itself: the origin must NOT be the shadow directory" <| fun _ ->
      // Proves the test has teeth — a broken implementation that recorded
      // (or fell back to) the SHADOW dir would defeat the whole fix, since
      // the shadow dir is exactly where the missing dependency is NOT.
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      let srcDir = createTestDir ()
      try
        let srcDll = Path.Combine(srcDir, "App.dll")
        File.WriteAllText(srcDll, "app-dll")
        let dest = SageFs.ShadowCopy.shadowCopyFile shadowDir srcDll
        SageFs.ShadowCopy.tryReadOriginDir dest
        |> Expect.notEqual "must not equal the shadow dir itself" (Some (Path.GetFullPath shadowDir))
      finally
        safeDelete shadowDir
        safeDelete srcDir

    testCase "tryReadOriginDir is None when no sidecar was ever written" <| fun _ ->
      let fakeShadowedPath = Path.Combine(Path.GetTempPath(), sprintf "sagefs-no-sidecar-%s.dll" (Guid.NewGuid().ToString("N").[..7]))
      SageFs.ShadowCopy.tryReadOriginDir fakeShadowedPath
      |> Expect.equal "no sidecar, no origin" None

    testCase "shadowCopyFile with a missing source writes no sidecar" <| fun _ ->
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      try
        let fakePath = Path.Combine(Path.GetTempPath(), "nonexistent-sidecar-42.dll")
        let result = SageFs.ShadowCopy.shadowCopyFile shadowDir fakePath
        File.Exists (SageFs.ShadowCopy.originSidecarPath result)
        |> Expect.isFalse "the early-return (no copy) path must not fabricate a sidecar"
      finally
        safeDelete shadowDir

    testCase "shadowCopySolution's rewritten TargetPath also carries an origin sidecar" <| fun _ ->
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      let srcDir = createTestDir ()
      try
        let dllPath = Path.Combine(srcDir, "Proj.dll")
        File.WriteAllText(dllPath, "proj-dll")
        let sln: Solution =
          { emptySolution with
              Projects = [ mkProjectOptions dllPath ] }
        let result = SageFs.ShadowCopy.shadowCopySolution shadowDir sln
        let shadowedTarget = (result.Projects |> List.head).TargetPath
        SageFs.ShadowCopy.tryReadOriginDir shadowedTarget
        |> Expect.equal "AppRunner must be able to find this project's original build output" (Some (Path.GetFullPath srcDir))
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
      // Throwing here fails the test; a no-op must also not create the directory.
      SageFs.ShadowCopy.cleanupShadowDir fakePath
      Directory.Exists fakePath |> Expect.isFalse "cleanup of a missing dir should leave nothing behind"

    bagTestCase "cleanupAllPending clears pendingCleanups" <| fun _ ->
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

/// A delete that fails is made with a Unix permission (a directory with no write bit cannot lose its contents), and a
/// privileged process is not held back by it, so the case is registered only on a Unix box where the process is not
/// privileged. Elsewhere it does not exist; it is not skipped.
let private stuckDeleteCases : Test list =
  match OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess with
  | true -> []
  | false ->
    [ bagTestCase "WHY — sweepShadowDirsIn — a deferred dir that still cannot be deleted stays pending, because dropping it would leak it for good" <| fun _ ->
        let root = Directory.CreateTempSubdirectory("sagefs-sweep-").FullName
        let lockedParent = Directory.CreateDirectory(Path.Combine(root, "locked")).FullName
        let stuck = Directory.CreateDirectory(Path.Combine(lockedParent, "stuck")).FullName
        File.WriteAllText(Path.Combine(stuck, "f.txt"), "x")
        try
          File.SetUnixFileMode(stuck, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)
          SageFs.ShadowCopy.pendingCleanups.Add stuck
          SageFs.ShadowCopy.sweepShadowDirsIn root (fun _ -> SageFs.ShadowCopy.OwnerLiveness.Running)
          Directory.Exists stuck |> Expect.isTrue "the locked dir is still there"
          SageFs.ShadowCopy.pendingCleanups
          |> Seq.contains stuck
          |> Expect.isTrue "the failed delete stays pending for the next sweep"
        finally
          File.SetUnixFileMode(stuck, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
          // Drain our own entry so the shared bag is clean for other tests.
          SageFs.ShadowCopy.cleanupAllPending ()
          safeDelete root ]

[<Tests>]
let staleSweepTests =
  testList "ShadowCopy stale sweep" (stuckDeleteCases @ [
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

    testCase "WHY — loadShadowed — a shadow copy's types all load, because the reflection scan lost every type that extends something a package brings (3398 of SageFs.Tests' types) and reported the package as missing from the shadow copy" <| fun _ ->
      let srcDir = createTestDir ()
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      let alc = new System.Runtime.Loader.AssemblyLoadContext("sagefs-shadow-test", true)
      try
        let _, appPath = writeDependentLibraries srcDir
        // Only the project's own assembly is shadow-copied, as `shadowCopySolution` does.
        let shadowApp = SageFs.ShadowCopy.shadowCopyFile shadowDir appPath
        File.Exists (Path.Combine(shadowDir, "ShadowDep.dll"))
        |> Expect.isFalse "the dependency is not in the shadow directory: that is the situation"
        let asm = SageFs.ShadowCopy.loadShadowed alc shadowApp
        let types = asm.GetTypes()
        types
        |> Array.map (fun t -> t.FullName)
        |> Expect.contains "App.Derived loaded with its base class from the build directory" "App.Derived"
      finally
        alc.Unload()
        safeDelete shadowDir
        safeDelete srcDir

    testCase "WHY — loadShadowed — a dependency the build directory does not hold is still reported, not invented" <| fun _ ->
      let srcDir = createTestDir ()
      let shadowDir = SageFs.ShadowCopy.createShadowDir ()
      let alc = new System.Runtime.Loader.AssemblyLoadContext("sagefs-shadow-test-missing", true)
      try
        let depPath, appPath = writeDependentLibraries srcDir
        File.Delete depPath
        let shadowApp = SageFs.ShadowCopy.shadowCopyFile shadowDir appPath
        let asm = SageFs.ShadowCopy.loadShadowed alc shadowApp
        let outcome =
          try
            asm.GetTypes() |> ignore
            None
          with :? System.Reflection.ReflectionTypeLoadException as ex ->
            ex.LoaderExceptions |> Array.choose Option.ofObj |> Array.map (fun e -> e.Message) |> Some
        match outcome with
        | None -> failwith "with the dependency gone from the build directory too, the type cannot load"
        | Some messages ->
          messages
          |> String.concat "; "
          |> Expect.stringContains "the missing assembly is named" "ShadowDep"
      finally
        alc.Unload()
        safeDelete shadowDir
        safeDelete srcDir

    bagTestCase "WHY — sweepShadowDirsIn — a shadow dir whose delete was deferred is gone after the sweep and no longer pending, because cleanupAllPending was only ever called from a test so the bag grew for the daemon's whole uptime" <| fun _ ->
      let root = Directory.CreateTempSubdirectory("sagefs-sweep-").FullName
      let deferred = Directory.CreateDirectory(Path.Combine(root, "deferred")).FullName
      try
        SageFs.ShadowCopy.pendingCleanups.Add deferred
        SageFs.ShadowCopy.sweepShadowDirsIn root (fun _ -> SageFs.ShadowCopy.OwnerLiveness.Running)
        Directory.Exists deferred |> Expect.isFalse "the deferred dir is deleted by the sweep"
        SageFs.ShadowCopy.pendingCleanups
        |> Seq.contains deferred
        |> Expect.isFalse "a deleted dir is drained from the pending bag"
      finally
        safeDelete root
  ])
