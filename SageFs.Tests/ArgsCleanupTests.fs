module SageFs.Tests.ArgsCleanupTests

open System.Collections.Generic
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Args
open SageFs.WorkflowTypes

// === Test Group 1: DaemonFlags parsing (pure, no IO) ===

let daemonFlagTests =
  testList "DaemonFlags parsing" [

    testCase "empty args gives defaults" <| fun () ->
      let flags = DaemonFlags.parse []
      flags |> Expect.equal "empty args should equal defaults" DaemonFlags.defaults

    testCase "parses --no-resume" <| fun () ->
      let flags = DaemonFlags.parse ["--no-resume"]
      flags.NoResume |> Expect.isTrue "should set no-resume"

    testCase "parses --prune" <| fun () ->
      let flags = DaemonFlags.parse ["--prune"]
      flags.Prune |> Expect.isTrue "should set prune"

    testCase "parses --no-watch" <| fun () ->
      let flags = DaemonFlags.parse ["--no-watch"]
      flags.NoWatch |> Expect.isTrue "should set no-watch"

    testCase "ignores legacy --proj and --sln startup flags" <| fun () ->
      let flags = DaemonFlags.parse ["--proj"; "foo.fsproj"; "--sln"; "bar.slnx"]
      flags |> Expect.equal "legacy startup flags should be ignored" DaemonFlags.defaults

    testCase "ignores unknown flags" <| fun () ->
      let flags = DaemonFlags.parse ["--garbage"]
      flags |> Expect.equal "unknown flags should be ignored" DaemonFlags.defaults

    testCase "parses multiple flags" <| fun () ->
      let flags = DaemonFlags.parse ["--no-resume"; "--no-watch"]
      flags.NoResume |> Expect.isTrue "should set no-resume"
      flags.NoWatch |> Expect.isTrue "should set no-watch"
      flags.Prune |> Expect.isFalse "prune not set"
  ]

// === Test Group 2: WorkerConfig from env (pure via dependency rejection) ===

/// Fake env reader — a dictionary lookup.
let fakeEnv (vars: (string * string) list) =
  let dict = Dictionary<string, string>()
  for (k, v) in vars do dict.[k] <- v
  fun (key: string) ->
    match dict.TryGetValue(key) with
    | true, v -> v
    | false, _ -> null

let workerConfigTests =
  testList "WorkerConfig from environment" [

    testCase "reads projects from SAGEFS_SESSION_PROJECTS" <| fun () ->
      let getEnv = fakeEnv [ WorkerConfig.envVar, "A.fsproj;B.fsproj" ]
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.Projects
      |> Expect.equal "should have two projects" ["A.fsproj"; "B.fsproj"]

    testCase "empty env var gives empty project list" <| fun () ->
      let getEnv = fakeEnv [ WorkerConfig.envVar, "" ]
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.Projects |> Expect.isEmpty "should be empty"

    testCase "missing env var gives empty project list" <| fun () ->
      let getEnv = fakeEnv []
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.Projects |> Expect.isEmpty "should be empty"

    testCase "SAGEFS_BARE_SESSION=1 sets IsBare" <| fun () ->
      let getEnv = fakeEnv [ WorkerConfig.bareEnvVar, "1" ]
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.IsBare |> Expect.isTrue "should be bare"

    testCase "SAGEFS_BARE_SESSION=true sets IsBare" <| fun () ->
      let getEnv = fakeEnv [ WorkerConfig.bareEnvVar, "true" ]
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.IsBare |> Expect.isTrue "should be bare"

    testCase "SAGEFS_NO_WATCH=1 sets NoWatch" <| fun () ->
      let getEnv = fakeEnv [ WorkerConfig.noWatchEnvVar, "1" ]
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.NoWatch |> Expect.isTrue "should disable watch"

    testCase "missing auto-open env var defaults to enabled" <| fun () ->
      let getEnv = fakeEnv []
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.AutoOpenNamespaces |> Expect.isTrue "should default to enabled"

    testCase "SAGEFS_AUTO_OPEN_NAMESPACES=0 disables auto-open" <| fun () ->
      let getEnv = fakeEnv [ WorkerConfig.autoOpenNamespacesEnvVar, "0" ]
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.AutoOpenNamespaces |> Expect.isFalse "should disable auto-open"

    testCase "session id and port pass through" <| fun () ->
      let getEnv = fakeEnv []
      let config = WorkerConfig.fromEnvironmentWith getEnv "abc123" 5050
      config.SessionId |> Expect.equal "should pass session id" "abc123"
      config.HttpPort |> Expect.equal "should pass port" 5050

    testCase "SAGEFS_DAEMON_PID parses to Some" <| fun () ->
      let getEnv = fakeEnv [ WorkerConfig.daemonPidEnvVar, "4242" ]
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.DaemonPid |> Expect.equal "should parse daemon pid" (Some 4242)

    testCase "missing SAGEFS_DAEMON_PID defaults to None" <| fun () ->
      let getEnv = fakeEnv []
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.DaemonPid |> Expect.equal "should default to None" None

    testCase "invalid SAGEFS_DAEMON_PID defaults to None" <| fun () ->
      let getEnv = fakeEnv [ WorkerConfig.daemonPidEnvVar, "not-a-pid" ]
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.DaemonPid |> Expect.equal "should default to None on garbage" None
  ]

// === Test Group 3: ProjectLoadConfig from WorkerConfig (pure) ===

let projectLoadConfigTests =
  testList "ProjectLoadConfig from WorkerConfig" [

    testCase "separates .sln/.slnx from .fsproj" <| fun () ->
      let wc = {
        SessionId = "x"; HttpPort = 0; IsBare = false; NoWatch = false; AutoOpenNamespaces = true
        WorkingDir = "."
        Projects = ["MyApp.fsproj"; "Solution.sln"; "Other.slnx"; "Lib.fsproj"]
        Workflow = SessionWorkflow.Interactive
        DaemonPid = None
      }
      let plc = ProjectLoadConfig.fromWorkerConfig wc
      plc.Solutions
      |> Expect.equal "solutions" ["Solution.sln"; "Other.slnx"]
      plc.Projects
      |> Expect.equal "projects" ["MyApp.fsproj"; "Lib.fsproj"]

    testCase "empty projects gives empty config" <| fun () ->
      let wc = {
        SessionId = "x"; HttpPort = 0; IsBare = false; NoWatch = false; AutoOpenNamespaces = true
        WorkingDir = "/tmp"; Projects = []
        Workflow = SessionWorkflow.Interactive
        DaemonPid = None
      }
      let plc = ProjectLoadConfig.fromWorkerConfig wc
      plc.Projects |> Expect.isEmpty "no projects"
      plc.Solutions |> Expect.isEmpty "no solutions"
      plc.WorkingDir |> Expect.equal "working dir" "/tmp"
  ]

// === Test Group 4: Worker spawn config (pure) ===

let workerSpawnConfigTests =
  testList "worker spawn config" [

    testCase "single project sets env var" <| fun () ->
      let args, envVars = buildWorkerSpawnConfig "sess1" ["MyApp.fsproj"] false false true SessionWorkflow.Interactive
      args |> Expect.stringContains "should have session id" "sess1"
      (args.Contains "--proj")
      |> Expect.isFalse "no --proj in args"
      (args.Contains "--session-id")
      |> Expect.isFalse "host takes positional args, not --session-id"
      args |> Expect.stringEnds "host takes positional httpPort (0 = ephemeral)" "0"
      envVars
      |> List.tryFind (fun (k, _) -> k = WorkerConfig.envVar)
      |> Option.map snd
      |> Expect.equal "projects env" (Some "MyApp.fsproj")

    testCase "hostExePath resolves relative to the daemon base dir" <| fun () ->
      // Platform-agnostic: the expected path is built with Path.Combine so
      // separators match whatever hostExePath produced on this OS.
      let daemonDir = Path.Combine("daemon", "bin")
      let path = hostExePath daemonDir
      let expected = Path.Combine("daemon", "bin", "host", "SageFs.Host.exe")
      Expect.equal "host exe path" expected path

    testCase "sets SAGEFS_DAEMON_PID to the spawning process id" <| fun () ->
      let _, envVars = buildWorkerSpawnConfig "s" [] false false true SessionWorkflow.Interactive
      envVars
      |> List.tryFind (fun (k, _) -> k = WorkerConfig.daemonPidEnvVar)
      |> Option.map snd
      |> Expect.equal "daemon pid env" (Some (string System.Environment.ProcessId))

    testCase "multiple projects semicolon-separated" <| fun () ->
      let _, envVars = buildWorkerSpawnConfig "s" ["A.fsproj"; "B.sln"] false false true SessionWorkflow.Interactive
      envVars
      |> List.tryFind (fun (k, _) -> k = WorkerConfig.envVar)
      |> Option.map snd
      |> Expect.equal "projects" (Some "A.fsproj;B.sln")

    testCase "bare session sets SAGEFS_BARE_SESSION" <| fun () ->
      let _, envVars = buildWorkerSpawnConfig "s" [] true false true SessionWorkflow.Interactive
      envVars
      |> List.exists (fun (k, v) -> k = WorkerConfig.bareEnvVar && v = "1")
      |> Expect.isTrue "bare env var set"

    testCase "no-watch sets SAGEFS_NO_WATCH" <| fun () ->
      let _, envVars = buildWorkerSpawnConfig "s" [] false true true SessionWorkflow.Interactive
      envVars
      |> List.exists (fun (k, v) -> k = WorkerConfig.noWatchEnvVar && v = "1")
      |> Expect.isTrue "no-watch env var set"

    testCase "auto-open disabled sets SAGEFS_AUTO_OPEN_NAMESPACES" <| fun () ->
      let _, envVars = buildWorkerSpawnConfig "s" [] false false false SessionWorkflow.Interactive
      envVars
      |> List.exists (fun (k, v) -> k = WorkerConfig.autoOpenNamespacesEnvVar && v = "0")
      |> Expect.isTrue "auto-open env var set"

    testCase "no bare/no-watch omits those env vars" <| fun () ->
      let _, envVars = buildWorkerSpawnConfig "s" ["A.fsproj"] false false true SessionWorkflow.Interactive
      envVars
      |> List.exists (fun (k, _) -> k = WorkerConfig.bareEnvVar)
      |> Expect.isFalse "no bare env var"
      envVars
      |> List.exists (fun (k, _) -> k = WorkerConfig.noWatchEnvVar)
      |> Expect.isFalse "no no-watch env var"
      envVars
      |> List.exists (fun (k, _) -> k = WorkerConfig.autoOpenNamespacesEnvVar)
      |> Expect.isFalse "no auto-open env var"
  ]

let hotReloadWorkerConfigTests =
  testList "WorkerConfig hot-reload env var" [

    testCase "SAGEFS_HOT_RELOAD=1 enables hot-reload" <| fun () ->
      let getEnv = fakeEnv [ WorkerConfig.hotReloadEnvVar, "1" ]
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.HotReloadEnabled |> Expect.isTrue "should enable hot-reload"

    testCase "SAGEFS_HOT_RELOAD=true enables hot-reload" <| fun () ->
      let getEnv = fakeEnv [ WorkerConfig.hotReloadEnvVar, "true" ]
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.HotReloadEnabled |> Expect.isTrue "should enable hot-reload"

    testCase "missing SAGEFS_HOT_RELOAD defaults to disabled" <| fun () ->
      let getEnv = fakeEnv []
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.HotReloadEnabled |> Expect.isFalse "should default to disabled"

    testCase "SAGEFS_HOT_RELOAD=0 disables hot-reload" <| fun () ->
      let getEnv = fakeEnv [ WorkerConfig.hotReloadEnvVar, "0" ]
      let config = WorkerConfig.fromEnvironmentWith getEnv "test-id" 0
      config.HotReloadEnabled |> Expect.isFalse "should be disabled"
  ]

let hotReloadSpawnConfigTests =
  testList "worker spawn config hot-reload" [

    testCase "WebLive workflow sets SAGEFS_HOT_RELOAD env var" <| fun () ->
      let _, envVars = buildWorkerSpawnConfig "s" [] false false true (SessionWorkflow.WebLive BrowserRefreshConfig.defaults)
      envVars
      |> List.exists (fun (k, v) -> k = WorkerConfig.hotReloadEnvVar && v = "1")
      |> Expect.isTrue "hot-reload env var set"

    testCase "Interactive workflow omits SAGEFS_HOT_RELOAD env var" <| fun () ->
      let _, envVars = buildWorkerSpawnConfig "s" [] false false true SessionWorkflow.Interactive
      envVars
      |> List.exists (fun (k, _) -> k = WorkerConfig.hotReloadEnvVar)
      |> Expect.isFalse "no hot-reload env var"
  ]

[<Tests>]
let allArgsCleanupTests =
  testList "Args cleanup" [
    daemonFlagTests
    workerConfigTests
    projectLoadConfigTests
    workerSpawnConfigTests
    hotReloadWorkerConfigTests
    hotReloadSpawnConfigTests
  ]
