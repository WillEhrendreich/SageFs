module SageFs.Tests.ProjectLoadingTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.ProjectLoading
open SageFs.Args
open SageFs.WorkflowTypes
open SageFs.Server.DaemonMode
open SageFs.WorkerProtocol
open SageFs.Tests.SharedGenerators

[<Tests>]
let tests =
  testList "ProjectLoading" [
    testList "emptySolution" [
      test "has empty Projects" {
        emptySolution.Projects |> Expect.isEmpty "Projects should be empty"
      }

      test "has empty References" {
        emptySolution.References |> Expect.isEmpty "References should be empty"
      }

      test "has empty FsProjects" {
        emptySolution.FsProjects |> Expect.isEmpty "FsProjects should be empty"
      }

      test "has empty StartupFiles" {
        emptySolution.StartupFiles |> Expect.isEmpty "StartupFiles should be empty"
      }

      test "has empty LibPaths" {
        emptySolution.LibPaths |> Expect.isEmpty "LibPaths should be empty"
      }

      test "has empty OtherArgs" {
        emptySolution.OtherArgs |> Expect.isEmpty "OtherArgs should be empty"
      }
    ]

    testList "DaemonFlags.parse" [
      test "empty args returns defaults" {
        let flags = DaemonFlags.parse []
        flags |> Expect.equal "should equal defaults" DaemonFlags.defaults
      }

      test "--no-resume sets NoResume" {
        let flags = DaemonFlags.parse [ "--no-resume" ]
        flags.NoResume |> Expect.isTrue "NoResume should be true"
      }

      test "--prune and --no-watch sets both" {
        let flags = DaemonFlags.parse [ "--prune"; "--no-watch" ]
        flags.Prune |> Expect.isTrue "Prune should be true"
        flags.NoWatch |> Expect.isTrue "NoWatch should be true"
      }

      test "--proj and --sln are ignored for bare daemon startup" {
        let flags = DaemonFlags.parse [ "--proj"; "app.fsproj"; "--sln"; "demo.slnx" ]
        flags |> Expect.equal "legacy startup flags should be ignored" DaemonFlags.defaults
      }

      test "unknown flags are ignored" {
        let flags = DaemonFlags.parse [ "--unknown-flag" ]
        flags |> Expect.equal "should equal defaults" DaemonFlags.defaults
      }
    ]

    testList "WorkerConfig.fromEnvironmentWith" [
      let emptyEnv (_: string) = null

      test "all-empty returns defaults" {
        let wc = WorkerConfig.fromEnvironmentWith emptyEnv "s1" 5000
        wc.SessionId |> Expect.equal "session id" "s1"
        wc.HttpPort |> Expect.equal "http port" 5000
        wc.Projects |> Expect.isEmpty "projects should be empty"
        wc.IsBare |> Expect.isFalse "IsBare should be false"
      }

      test "parses SAGEFS_SESSION_PROJECTS" {
        let getEnv name =
          match name with
          | "SAGEFS_SESSION_PROJECTS" -> "a.fsproj;b.fsproj"
          | _ -> null
        let wc = WorkerConfig.fromEnvironmentWith getEnv "s2" 0
        wc.Projects |> Expect.equal "projects" [ "a.fsproj"; "b.fsproj" ]
      }

      test "parses SAGEFS_BARE_SESSION=true" {
        let getEnv name =
          match name with
          | "SAGEFS_BARE_SESSION" -> "true"
          | _ -> null
        let wc = WorkerConfig.fromEnvironmentWith getEnv "s3" 0
        wc.IsBare |> Expect.isTrue "IsBare should be true"
      }

      test "auto_open defaults to true" {
        let wc = WorkerConfig.fromEnvironmentWith emptyEnv "s4" 0
        wc.AutoOpenNamespaces |> Expect.isTrue "AutoOpenNamespaces should default to true"
      }

      test "auto_open false when SAGEFS_AUTO_OPEN_NAMESPACES=false" {
        let getEnv name =
          match name with
          | "SAGEFS_AUTO_OPEN_NAMESPACES" -> "false"
          | _ -> null
        let wc = WorkerConfig.fromEnvironmentWith getEnv "s5" 0
        wc.AutoOpenNamespaces |> Expect.isFalse "AutoOpenNamespaces should be false"
      }
    ]

    testList "ProjectLoadConfig.fromWorkerConfig" [
      test "partitions .sln/.slnx from .fsproj" {
        let wc =
          WorkerConfig.fromEnvironmentWith
            (fun name ->
              match name with
              | "SAGEFS_SESSION_PROJECTS" -> "app.fsproj;my.sln;other.slnx;lib.fsproj"
              | _ -> null)
            "s6" 0
        let plc = ProjectLoadConfig.fromWorkerConfig wc
        plc.Solutions |> Expect.equal "solutions" [ "my.sln"; "other.slnx" ]
        plc.Projects |> Expect.equal "projects" [ "app.fsproj"; "lib.fsproj" ]
      }
    ]

    testList "buildWorkerSpawnConfig" [
      test "includes session ID in args" {
        let args, _ = buildWorkerSpawnConfig "abc-123" [] false false true SessionWorkflow.Interactive
        args |> Expect.stringContains "should contain session id" "abc-123"
      }

      test "isBare=true includes SAGEFS_BARE_SESSION=1" {
        let _, envVars = buildWorkerSpawnConfig "s1" [] true false true SessionWorkflow.Interactive
        envVars
        |> List.exists (fun (k, v) -> k = "SAGEFS_BARE_SESSION" && v = "1")
        |> Expect.isTrue "should include SAGEFS_BARE_SESSION=1"
      }
    ]

    testList "resolveSiblingConfigOutput" [
      // Ionide evaluates projects with MSBuild's default Configuration (Debug),
      // so a Release-only build leaves TargetPath pointing at a missing
      // bin/Debug DLL and warmup faults with "Missing DLL". The resolver must
      // fall back to the sibling config output that actually exists.
      test "falls back Debug -> Release when only Release exists" {
        let root = Path.Combine(Path.GetTempPath(), "sagefs-projload-" + Guid.NewGuid().ToString("N"))
        let releaseDll = Path.Combine(root, "bin", "Release", "net10.0", "App.dll")
        Directory.CreateDirectory(Path.GetDirectoryName releaseDll) |> ignore
        File.WriteAllText(releaseDll, "x")
        try
          let debugPath = Path.Combine(root, "bin", "Debug", "net10.0", "App.dll")
          let result = resolveSiblingConfigOutput debugPath
          result |> Expect.equal "should resolve to the Release DLL" (Some releaseDll)
        finally
          Directory.Delete(root, true)
      }

      test "falls back Release -> Debug when only Debug exists" {
        let root = Path.Combine(Path.GetTempPath(), "sagefs-projload-" + Guid.NewGuid().ToString("N"))
        let debugDll = Path.Combine(root, "bin", "Debug", "net10.0", "App.dll")
        Directory.CreateDirectory(Path.GetDirectoryName debugDll) |> ignore
        File.WriteAllText(debugDll, "x")
        try
          let releasePath = Path.Combine(root, "bin", "Release", "net10.0", "App.dll")
          let result = resolveSiblingConfigOutput releasePath
          result |> Expect.equal "should resolve to the Debug DLL" (Some debugDll)
        finally
          Directory.Delete(root, true)
      }

      test "returns None when neither config output exists" {
        let root = Path.Combine(Path.GetTempPath(), "sagefs-projload-" + Guid.NewGuid().ToString("N"))
        try
          let debugPath = Path.Combine(root, "bin", "Debug", "net10.0", "App.dll")
          resolveSiblingConfigOutput debugPath |> Expect.isNone "no sibling should exist"
        finally
          if Directory.Exists root then Directory.Delete(root, true)
      }

      test "returns the existing path unchanged when it exists" {
        let root = Path.Combine(Path.GetTempPath(), "sagefs-projload-" + Guid.NewGuid().ToString("N"))
        let debugDll = Path.Combine(root, "bin", "Debug", "net10.0", "App.dll")
        Directory.CreateDirectory(Path.GetDirectoryName debugDll) |> ignore
        File.WriteAllText(debugDll, "x")
        try
          let result = resolveSiblingConfigOutput debugDll
          result |> Expect.equal "existing Debug DLL should be returned as-is" (Some debugDll)
        finally
          Directory.Delete(root, true)
      }

      test "handles non-bin layouts without throwing" {
        resolveSiblingConfigOutput "C:\\some\\random\\file.dll"
        |> Expect.isNone "non-bin layout should yield None"
      }
    ]

    // F6 regression: Ionide reports a Debug TargetPath, but if a STALE Debug
    // output exists while the fresh build is Release, existence-only resolution
    // loads the stale assembly. chooseFreshestConfigOutputWith must pick the
    // newest across configs. Pure — existence + write time injected.
    testList "chooseFreshestConfigOutputWith" [
      let dbg = "/proj/bin/Debug/net10.0/X.dll"
      let rel = "/proj/bin/Release/net10.0/X.dll"
      let mt (older: string) (newer: string) (p: string) =
        if p = older then DateTime(2026, 1, 1) elif p = newer then DateTime(2026, 6, 1) else DateTime(2025, 1, 1)
      let both p = p = dbg || p = rel

      test "reported Debug is stale, Release is newer -> Release" {
        chooseFreshestConfigOutputWith both (mt dbg rel) dbg
        |> Expect.equal "the fresh Release build must win over the stale Debug one" (Some rel)
      }

      test "reported Release is stale, Debug is newer -> Debug" {
        chooseFreshestConfigOutputWith both (mt rel dbg) rel
        |> Expect.equal "the newest build wins regardless of which config Ionide named" (Some dbg)
      }

      test "only the reported config exists -> that config" {
        chooseFreshestConfigOutputWith (fun p -> p = dbg) (mt dbg rel) dbg
        |> Expect.equal "with no sibling, the sole existing output is used" (Some dbg)
      }

      test "neither config output exists -> None" {
        chooseFreshestConfigOutputWith (fun _ -> false) (mt dbg rel) dbg
        |> Expect.isNone "keep the original path so the missing-DLL error stays accurate"
      }

      test "no config segment in the path -> the path itself when it exists" {
        chooseFreshestConfigOutputWith (fun p -> p = "/x/y.dll") (mt dbg rel) "/x/y.dll"
        |> Expect.equal "a path with no Debug/Release segment resolves to itself" (Some "/x/y.dll")
      }
    ]

    testList "resolveFreshestConfigOutput (real filesystem)" [
      test "WHY — a fresh Release build wins over a stale Debug output, because the REPL must run the code you built, not an old artifact" {
        let root = Path.Combine(Path.GetTempPath(), "sagefs-fresh-" + Guid.NewGuid().ToString("N"))
        let debugDll = Path.Combine(root, "bin", "Debug", "net10.0", "App.dll")
        let releaseDll = Path.Combine(root, "bin", "Release", "net10.0", "App.dll")
        Directory.CreateDirectory(Path.GetDirectoryName debugDll) |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName releaseDll) |> ignore
        File.WriteAllText(debugDll, "stale")
        File.WriteAllText(releaseDll, "fresh")
        // Make Debug provably older than Release.
        File.SetLastWriteTimeUtc(debugDll, DateTime(2026, 1, 1))
        File.SetLastWriteTimeUtc(releaseDll, DateTime(2026, 6, 1))
        try
          // Ionide would report the Debug path; the freshest across configs is Release.
          resolveFreshestConfigOutput debugDll
          |> Expect.equal "the newer Release build must be selected" (Some releaseDll)
        finally
          Directory.Delete(root, true)
      }
    ]
  ]

/// A Release-only build: Ionide reports Debug paths, only Release outputs exist.
let private releaseOnlyLayout () =
  let root = Directory.CreateTempSubdirectory("sagefs-releaseonly-").FullName
  let write (parts: string list) =
    let path = Path.Combine(root :: parts |> Array.ofList)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, "x")
    path
  let appDll = write [ "App"; "bin"; "Release"; "net10.0"; "App.dll" ]
  let coreRefRelease = write [ "Core"; "obj"; "Release"; "net10.0"; "ref"; "Core.dll" ]
  let coreRefDebug = Path.Combine(root, "Core", "obj", "Debug", "net10.0", "ref", "Core.dll")
  root, appDll, coreRefRelease, coreRefDebug

let private solutionWith (targetPath: string) (otherOptions: string list) : Solution =
  { FsProjects = []
    Projects = [ { SageFs.Tests.ShadowCopyTests.mkProjectOptions targetPath with OtherOptions = otherOptions } ]
    StartupFiles = []
    References = []
    LibPaths = []
    OtherArgs = [] }

[<Tests>]
let releaseOnlyReferenceTests =
  testList "Release-only project references" [
    test "WHY — resolveSiblingConfigOutput — a referenced project's obj/Debug/<tfm>/ref assembly falls back to obj/Release, because CI builds Release only and FSI died with StopProcessingExn on the missing Debug reference" {
      let root, _, coreRefRelease, coreRefDebug = releaseOnlyLayout ()
      try
        resolveSiblingConfigOutput coreRefDebug
        |> Expect.equal "the Release reference assembly is found" (Some coreRefRelease)
      finally
        Directory.Delete(root, true)
    }

    test "WHY — solutionToFsiArgs — forwarded compiler references are resolved to outputs that exist, because a missing -r: kills FSI before any diagnostics" {
      let root, appDll, coreRefRelease, coreRefDebug = releaseOnlyLayout ()
      try
        let args = solutionToFsiArgs SageFs.Tests.TestInfrastructure.quietLogger false false (solutionWith appDll [ "-r:" + coreRefDebug ])
        args |> Array.contains ("-r:" + coreRefRelease) |> Expect.isTrue "the Release reference is passed to FSI"
        args |> Array.contains ("-r:" + coreRefDebug) |> Expect.isFalse "the missing Debug reference is never passed"
      finally
        Directory.Delete(root, true)
    }

    test "WHY — solutionToFsiArgs — a reference missing in both configurations fails naming the file, because 'StopProcessingExn' tells the user nothing" {
      let root, appDll, _, _ = releaseOnlyLayout ()
      try
        let gone = Path.Combine(root, "Gone", "obj", "Debug", "net10.0", "ref", "Gone.dll")
        let error =
          try
            solutionToFsiArgs SageFs.Tests.TestInfrastructure.quietLogger false false (solutionWith appDll [ "-r:" + gone ]) |> ignore
            None
          with ex -> Some ex.Message
        error |> Expect.isSome "a missing reference is reported before FSI starts"
        error.Value |> Expect.stringContains "the message names the missing assembly" "Gone.dll"
      finally
        Directory.Delete(root, true)
    }
  ]
