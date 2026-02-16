module SageFs.Tests.DaemonStateTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Server
open SageFs

[<Tests>]
let tests =
  let withTempPath f =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-test-%s" (Guid.NewGuid().ToString("N").[..7]))
    let path = Path.Combine(dir, "daemon.json")
    try f path
    finally
      try Directory.Delete(dir, true) with _ -> ()

  let sampleInfo pid = {
    Pid = pid
    Port = 37749
    StartedAt = DateTime(2026, 2, 13, 12, 0, 0, DateTimeKind.Utc)
    WorkingDirectory = @"C:\Code\Repos\SageFs"
    Version = "0.2.44"
  }

  testList "DaemonState" [
    testList "serialization" [
      testCase "round-trip preserves all fields" <| fun _ ->
        let info = sampleInfo 1234
        let json = DaemonState.serializeInfo info
        let rt = DaemonState.deserializeInfo json
        rt |> Expect.equal "round-trip" info

      testCase "uses camelCase keys" <| fun _ ->
        let json = DaemonState.serializeInfo (sampleInfo 1)
        json |> Expect.stringContains "has pid" "\"pid\""
        json |> Expect.stringContains "has port" "\"port\""
        json |> Expect.stringContains "has startedAt" "\"startedAt\""
        json |> Expect.stringContains "has workingDirectory" "\"workingDirectory\""
        json |> Expect.stringContains "has version" "\"version\""

      testCase "json is indented" <| fun _ ->
        let json = DaemonState.serializeInfo (sampleInfo 1)
        json |> Expect.stringContains "is multiline" "\n"
    ]

    testList "isProcessAlive" [
      testCase "current process is alive" <| fun _ ->
        let pid = Process.GetCurrentProcess().Id
        DaemonState.isProcessAlive pid
        |> Expect.isTrue "current process should be alive"

      testCase "bogus PID is not alive" <| fun _ ->
        DaemonState.isProcessAlive 99999999
        |> Expect.isFalse "bogus PID should not be alive"
    ]

    testList "file operations" [
      testCase "write creates file" <| fun _ ->
        withTempPath <| fun path ->
          DaemonState.writeToPath path (sampleInfo 1234)
          File.Exists path
          |> Expect.isTrue "file should exist"

      testCase "write then read returns info when PID alive" <| fun _ ->
        withTempPath <| fun path ->
          let pid = Process.GetCurrentProcess().Id
          let info = sampleInfo pid
          DaemonState.writeToPath path info
          let result = DaemonState.readFromPath path
          result |> Expect.isSome "should read back"
          result.Value |> Expect.equal "matches written" info

      testCase "read returns None when PID dead" <| fun _ ->
        withTempPath <| fun path ->
          DaemonState.writeToPath path (sampleInfo 99999999)
          let result = DaemonState.readFromPath path
          result |> Expect.isNone "dead PID should return None"

      testCase "read clears stale file when PID dead" <| fun _ ->
        withTempPath <| fun path ->
          DaemonState.writeToPath path (sampleInfo 99999999)
          DaemonState.readFromPath path |> ignore
          File.Exists path
          |> Expect.isFalse "stale file should be cleaned up"

      testCase "read returns None for missing file" <| fun _ ->
        withTempPath <| fun path ->
          DaemonState.readFromPath path
          |> Expect.isNone "missing file should return None"

      testCase "read returns None for corrupt JSON" <| fun _ ->
        withTempPath <| fun path ->
          let dir = Path.GetDirectoryName path
          Directory.CreateDirectory(dir) |> ignore
          File.WriteAllText(path, "not json at all {{{")
          DaemonState.readFromPath path
          |> Expect.isNone "corrupt JSON should return None"

      testCase "read clears corrupt file" <| fun _ ->
        withTempPath <| fun path ->
          let dir = Path.GetDirectoryName path
          Directory.CreateDirectory(dir) |> ignore
          File.WriteAllText(path, "not json at all {{{")
          DaemonState.readFromPath path |> ignore
          File.Exists path
          |> Expect.isFalse "corrupt file should be cleaned up"

      testCase "clear removes file" <| fun _ ->
        withTempPath <| fun path ->
          DaemonState.writeToPath path (sampleInfo 1234)
          DaemonState.clearPath path
          File.Exists path
          |> Expect.isFalse "file should be removed"

      testCase "clear on missing file does not throw" <| fun _ ->
        withTempPath <| fun path ->
          DaemonState.clearPath path

      testCase "write overwrites existing file" <| fun _ ->
        withTempPath <| fun path ->
          let pid = Process.GetCurrentProcess().Id
          DaemonState.writeToPath path (sampleInfo pid)
          let updated = { sampleInfo pid with Port = 12345 }
          DaemonState.writeToPath path updated
          let result = DaemonState.readFromPath path
          result |> Expect.isSome "should read back"
          result.Value.Port |> Expect.equal "updated port" 12345
    ]
  ]
