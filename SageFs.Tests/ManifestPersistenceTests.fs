module SageFs.Tests.ManifestPersistenceTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.ManifestTypes
open SageFs.Features

[<Tests>]
let manifestBinaryTests = testList "DaemonManifest binary format" [

  testCase "empty manifest roundtrips" <| fun _ ->
    let data = {
      Entries = []
      ActiveSessionId = None
      CreatedAtMs = 1709500000000L
    }
    let bytes = ManifestWriter.write data
    let result = ManifestReader.read bytes
    match result with
    | Ok loaded ->
      loaded.Entries |> Expect.isEmpty "no entries"
      loaded.ActiveSessionId |> Expect.isNone "no active session"
      loaded.CreatedAtMs |> Expect.equal "timestamp preserved" 1709500000000L
    | Error e -> failwithf "Round-trip failed: %s" e

  testCase "single alive session roundtrips" <| fun _ ->
    let created = DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero)
    let entry = {
      SessionId = "session-abc123"
      Projects = [ "MyApp.Tests.fsproj"; "MyApp.fsproj" ]
      WorkingDir = "C:\\Code\\MyApp"
      CreatedAt = created
      StoppedAt = None
    }
    let data = {
      Entries = [ entry ]
      ActiveSessionId = Some "session-abc123"
      CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    }
    let bytes = ManifestWriter.write data
    match ManifestReader.read bytes with
    | Ok loaded ->
      loaded.Entries |> Expect.hasLength "one entry" 1
      let e = loaded.Entries.[0]
      e.SessionId |> Expect.equal "session id" "session-abc123"
      e.Projects |> Expect.equal "projects" [ "MyApp.Tests.fsproj"; "MyApp.fsproj" ]
      e.WorkingDir |> Expect.equal "work dir" "C:\\Code\\MyApp"
      e.StoppedAt |> Expect.isNone "still alive"
      loaded.ActiveSessionId |> Expect.equal "active" (Some "session-abc123")
    | Error e -> failwithf "Round-trip failed: %s" e

  testCase "stopped session preserves StoppedAt" <| fun _ ->
    let created = DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero)
    let stopped = DateTimeOffset(2025, 3, 1, 13, 30, 0, TimeSpan.Zero)
    let entry = {
      SessionId = "session-stopped"
      Projects = [ "App.fsproj" ]
      WorkingDir = "/home/user/app"
      CreatedAt = created
      StoppedAt = Some stopped
    }
    let data = { Entries = [ entry ]; ActiveSessionId = None; CreatedAtMs = 0L }
    let bytes = ManifestWriter.write data
    match ManifestReader.read bytes with
    | Ok loaded ->
      let e = loaded.Entries.[0]
      e.StoppedAt |> Expect.isSome "should be stopped"
      e.StoppedAt.Value.ToUnixTimeMilliseconds()
      |> Expect.equal "stopped time ms" (stopped.ToUnixTimeMilliseconds())
    | Error e -> failwithf "Round-trip failed: %s" e

  testCase "multiple sessions roundtrip" <| fun _ ->
    let now = DateTimeOffset.UtcNow
    let entries = [
      for i in 1 .. 5 do
        {
          SessionId = sprintf "session-%d" i
          Projects = [ sprintf "Proj%d.fsproj" i ]
          WorkingDir = sprintf "C:\\Code\\Proj%d" i
          CreatedAt = now.AddMinutes(float i)
          StoppedAt =
            match i % 2 with
            | 0 -> Some (now.AddMinutes(float (i + 10)))
            | _ -> None
        }
    ]
    let data = {
      Entries = entries
      ActiveSessionId = Some "session-3"
      CreatedAtMs = now.ToUnixTimeMilliseconds()
    }
    let bytes = ManifestWriter.write data
    match ManifestReader.read bytes with
    | Ok loaded ->
      loaded.Entries |> Expect.hasLength "5 entries" 5
      loaded.ActiveSessionId |> Expect.equal "active is session-3" (Some "session-3")
      let alive = loaded.Entries |> List.filter (fun e -> e.StoppedAt.IsNone)
      alive |> Expect.hasLength "3 alive" 3
    | Error e -> failwithf "Round-trip failed: %s" e

  testCase "CRC detects corruption" <| fun _ ->
    let data = { Entries = []; ActiveSessionId = None; CreatedAtMs = 0L }
    let bytes = ManifestWriter.write data
    let corrupted = Array.copy bytes
    corrupted.[bytes.Length - 1] <- corrupted.[bytes.Length - 1] ^^^ 0xFFuy
    match ManifestReader.read corrupted with
    | Error msg ->
      (msg.Contains("CRC") || msg.Contains("mismatch"))
      |> Expect.isTrue "error mentions CRC"
    | Ok _ -> failwith "Should have detected corruption"

  testCase "invalid magic rejected" <| fun _ ->
    let bytes = Array.zeroCreate 100
    match ManifestReader.read bytes with
    | Error msg -> msg |> Expect.stringContains "mentions magic" "magic"
    | Ok _ -> failwith "Should reject invalid magic"

  testCase "file too small rejected" <| fun _ ->
    match ManifestReader.read [| 1uy; 2uy |] with
    | Error msg -> msg |> Expect.stringContains "mentions small" "too small"
    | Ok _ -> failwith "Should reject too-small file"

  testCase "roundtrip preserves fields across 100 random manifests" <| fun _ ->
    let rng = Random(42)
    for _ in 0 .. 99 do
      let count = rng.Next(0, 8)
      let entries = [
        for _ in 0 .. count - 1 do
          let projects = [ for _ in 0 .. rng.Next(1, 4) do sprintf "Proj%d.fsproj" (rng.Next(100)) ]
          {
            SessionId = sprintf "session-%d" (rng.Next(10000))
            Projects = projects
            WorkingDir = sprintf "C:\\Code\\Proj%d" (rng.Next(100))
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(int64 (rng.Next(1_000_000, 2_000_000)) * 1000L)
            StoppedAt =
              match rng.Next(2) with
              | 0 -> None
              | _ -> Some (DateTimeOffset.FromUnixTimeMilliseconds(int64 (rng.Next(1_000_000, 2_000_000)) * 1000L))
          }
      ]
      let activeId =
        match entries.Length > 0 && rng.Next(2) = 0 with
        | true -> Some entries.[rng.Next(entries.Length)].SessionId
        | false -> None
      let manifest = {
        Entries = entries
        ActiveSessionId = activeId
        CreatedAtMs = int64 (rng.Next(1_000_000, 2_000_000)) * 1000L
      }
      let bytes = ManifestWriter.write manifest
      match ManifestReader.read bytes with
      | Ok loaded ->
        loaded.Entries.Length |> Expect.equal "entry count" manifest.Entries.Length
        loaded.ActiveSessionId |> Expect.equal "active id" manifest.ActiveSessionId
        loaded.CreatedAtMs |> Expect.equal "created ms" manifest.CreatedAtMs
        List.zip loaded.Entries manifest.Entries
        |> List.iteri (fun i (l, r) ->
          l.SessionId |> Expect.equal (sprintf "sid[%d]" i) r.SessionId
          l.Projects |> Expect.equal (sprintf "proj[%d]" i) r.Projects
          l.WorkingDir |> Expect.equal (sprintf "dir[%d]" i) r.WorkingDir
          l.CreatedAt.ToUnixTimeMilliseconds()
          |> Expect.equal (sprintf "created[%d]" i) (r.CreatedAt.ToUnixTimeMilliseconds())
          match l.StoppedAt, r.StoppedAt with
          | None, None -> ()
          | Some a, Some b ->
            a.ToUnixTimeMilliseconds()
            |> Expect.equal (sprintf "stopped[%d]" i) (b.ToUnixTimeMilliseconds())
          | _ -> failwithf "StoppedAt mismatch at index %d" i)
      | Error e -> failwithf "Round-trip failed: %s" e
]

[<Tests>]
let manifestVersionTests = testList "DaemonManifest format version" [

  testCase "v1 format is accepted" <| fun _ ->
    let data = { Entries = []; ActiveSessionId = None; CreatedAtMs = 1L }
    let bytes = ManifestWriter.write data
    ManifestReader.read bytes |> Result.isOk |> Expect.isTrue "v1 should succeed"

  testCase "unknown format version is rejected" <| fun _ ->
    let data = { Entries = []; ActiveSessionId = None; CreatedAtMs = 1L }
    let bytes = ManifestWriter.write data
    // Patch format_version field (bytes 4-5) to v99
    let patched = Array.copy bytes
    patched.[4] <- 99uy; patched.[5] <- 0uy
    // Re-compute header CRC so we test version check, not CRC
    let forCrc = Array.copy patched
    forCrc.[36] <- 0uy; forCrc.[37] <- 0uy; forCrc.[38] <- 0uy; forCrc.[39] <- 0uy
    let crc = Crc32.computeAll forCrc
    let cb = System.BitConverter.GetBytes(crc)
    System.Array.Copy(cb, 0, patched, 36, 4)
    match ManifestReader.read patched with
    | Error msg ->
      msg |> Expect.stringContains "mentions version" "format version"
    | Ok _ -> failwith "Should reject unknown format version"

  testCase "format version 0 is rejected" <| fun _ ->
    let data = { Entries = []; ActiveSessionId = None; CreatedAtMs = 1L }
    let bytes = ManifestWriter.write data
    let patched = Array.copy bytes
    patched.[4] <- 0uy; patched.[5] <- 0uy
    let forCrc = Array.copy patched
    forCrc.[36] <- 0uy; forCrc.[37] <- 0uy; forCrc.[38] <- 0uy; forCrc.[39] <- 0uy
    let crc = Crc32.computeAll forCrc
    let cb = System.BitConverter.GetBytes(crc)
    System.Array.Copy(cb, 0, patched, 36, 4)
    match ManifestReader.read patched with
    | Error msg ->
      msg |> Expect.stringContains "mentions version" "format version"
    | Ok _ -> failwith "Should reject format version 0"

  testCase "currentFormatVersion equals 1" <| fun _ ->
    ManifestWriter.currentFormatVersion |> Expect.equal "writer version" 1us
]

[<Tests>]
let manifestMappingTests = testList "ManifestMapping" [
  testCase "replay state → manifest → replay state roundtrips" <| fun _ ->
    let now = DateTimeOffset.UtcNow
    let state : Replay.DaemonReplayState = {
      Sessions = Map.ofList [
        "s1", { SessionId = "s1"; Projects = ["A.fsproj"]; WorkingDir = "C:\\A"; CreatedAt = now; StoppedAt = None }
        "s2", { SessionId = "s2"; Projects = ["B.fsproj"]; WorkingDir = "C:\\B"; CreatedAt = now.AddMinutes(-5.0); StoppedAt = Some now }
      ]
      ActiveSessionId = Some "s1"
    }
    let manifest = ManifestMapping.fromReplayState state
    let roundtripped = ManifestMapping.toReplayState manifest

    roundtripped.Sessions.Count |> Expect.equal "2 sessions" 2
    roundtripped.ActiveSessionId |> Expect.equal "active" (Some "s1")
    let s1 = roundtripped.Sessions.["s1"]
    s1.Projects |> Expect.equal "s1 projects" ["A.fsproj"]
    s1.StoppedAt |> Expect.isNone "s1 alive"
    let s2 = roundtripped.Sessions.["s2"]
    s2.StoppedAt |> Expect.isSome "s2 stopped"
]
