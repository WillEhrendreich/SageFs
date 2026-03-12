module SageFs.Tests.ProjectLoadingTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.ProjectLoading
open SageFs.Args
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
        let args, _ = buildWorkerSpawnConfig "abc-123" [] false false true false
        args |> Expect.stringContains "should contain session id" "abc-123"
      }

      test "isBare=true includes SAGEFS_BARE_SESSION=1" {
        let _, envVars = buildWorkerSpawnConfig "s1" [] true false true false
        envVars
        |> List.exists (fun (k, v) -> k = "SAGEFS_BARE_SESSION" && v = "1")
        |> Expect.isTrue "should include SAGEFS_BARE_SESSION=1"
      }
    ]
  ]
