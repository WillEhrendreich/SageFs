module SageFs.Tests.WatchdogRunnerSeedTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server

let private crashedAt = DateTime(2026, 3, 1, 12, 0, 0)

let private restartDelayFor (seed: RestartPolicy.JitterSeed) : TimeSpan =
  let state = Watchdog.emptyState crashedAt |> Watchdog.recordStart 1234 crashedAt
  match Watchdog.decide Watchdog.defaultConfig seed state Watchdog.DaemonStatus.NotRunning (crashedAt.AddSeconds 60.0) with
  | Watchdog.Action.RestartDaemon delay, _ -> delay
  | other, _ -> failtestf "expected RestartDaemon, got %A" other

[<Tests>]
let tests =
  testList "WatchdogRunner jitter seed" [

    testCase "WHY — the same directory, port and crash time always give the same seed, so a replay is exact" <| fun _ ->
      WatchdogRunner.jitterSeed "/work/a" 37749 crashedAt
      |> Expect.equal "stable" (WatchdogRunner.jitterSeed "/work/a" 37749 crashedAt)

    testCase "WHY — daemons in different directories get different seeds for the same crash" <| fun _ ->
      WatchdogRunner.jitterSeed "/work/a" 37749 crashedAt
      |> Expect.notEqual "directory is part of the identity" (WatchdogRunner.jitterSeed "/work/b" 37749 crashedAt)

    testCase "WHY — daemons on different ports get different seeds for the same crash" <| fun _ ->
      WatchdogRunner.jitterSeed "/work/a" 37749 crashedAt
      |> Expect.notEqual "port is part of the identity" (WatchdogRunner.jitterSeed "/work/a" 37751 crashedAt)

    testCase "WHY — per-directory daemons that die together do not all respawn at the same instant" <| fun _ ->
      let delays =
        [ 1 .. 20 ]
        |> List.map (fun n -> restartDelayFor (WatchdogRunner.jitterSeed (sprintf "/home/will/Work/repo%d" n) 37749 crashedAt))
      Expect.isGreaterThan "the respawns are spread out" (delays |> List.distinct |> List.length, 15)

    testCase "WHY — the daemon port is read from --mcp-port in the daemon args" <| fun _ ->
      WatchdogRunner.daemonPort [ "--mcp-port"; "4242"; "--foo" ]
      |> Expect.equal "explicit port wins" 4242

    testCase "WHY — without --mcp-port the daemon port is the configured default" <| fun _ ->
      WatchdogRunner.daemonPort [ "--foo" ]
      |> Expect.equal "falls back to the env or default port" SageFsConfig.McpPortFromEnv

    testCase "WHY — a --mcp-port with no readable number falls back to the configured default" <| fun _ ->
      WatchdogRunner.daemonPort [ "--mcp-port"; "nope" ]
      |> Expect.equal "unparseable falls back" SageFsConfig.McpPortFromEnv
  ]
