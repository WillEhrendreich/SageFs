module SageFs.Tests.DaemonStateTests

open System
open System.Diagnostics
open Expecto
open Expecto.Flip
open SageFs

[<Tests>]
let tests =
  testList "DaemonState" [
    testList "isProcessAlive" [
      testCase "current process is alive" <| fun _ ->
        let pid = Process.GetCurrentProcess().Id
        DaemonState.isProcessAlive pid
        |> Expect.isTrue "current process should be alive"

      testCase "bogus PID is not alive" <| fun _ ->
        DaemonState.isProcessAlive 99999999
        |> Expect.isFalse "bogus PID should not be alive"
    ]

    testList "HTTP detection" [
      testCase "read returns None when no daemon running" <| fun _ ->
        // Probe a port that should not have a daemon
        DaemonState.readOnPort 39999
        |> Expect.isNone "should return None when no daemon on port"

      testCase "requestShutdown returns false when no daemon" <| fun _ ->
        DaemonState.requestShutdown 39999
        |> Expect.isFalse "shutdown should fail when no daemon"
    ]
  ]
