module SageFs.Tests.ManifestPersistenceTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
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
let manifestHostileHeaderTests = testList "DaemonManifest hostile header rejection" [

  /// Rewrite a field then re-compute the whole-file CRC over the tampered
  /// bytes — the CRC gate must NOT be the defense here; a consistent hostile
  /// rewrite that inflates header counts/sizes must fail the bounds checks.
  let tamperAndFixCrc (bytes: byte[]) (offset: int) (setField: byte[] -> unit) =
    let patched = Array.copy bytes
    setField patched
    let forCrc = Array.copy patched
    forCrc.[36] <- 0uy; forCrc.[37] <- 0uy; forCrc.[38] <- 0uy; forCrc.[39] <- 0uy
    let crc = Crc32.computeAll forCrc
    let cb = System.BitConverter.GetBytes(crc)
    System.Array.Copy(cb, 0, patched, 36, 4)
    patched

  testCase "inflated section count is rejected even with recomputed CRC" <| fun _ ->
    let data = { Entries = []; ActiveSessionId = None; CreatedAtMs = 1L }
    let bytes = ManifestWriter.write data
    let patched = tamperAndFixCrc bytes 8 (fun p ->
      let cb = System.BitConverter.GetBytes(0xFFFFFFu)
      System.Array.Copy(cb, 0, p, 8, 4))
    match ManifestReader.read patched with
    | Error msg ->
      (msg.Contains("count") || msg.Contains("capacity"))
      |> Expect.isTrue "error should cite the inflated count"
    | Ok _ -> failwith "Should reject an inflated section count even with a valid CRC"

  testCase "declared total size mismatch is rejected even with recomputed CRC" <| fun _ ->
    let data = { Entries = []; ActiveSessionId = None; CreatedAtMs = 1L }
    let bytes = ManifestWriter.write data
    let patched = tamperAndFixCrc bytes 24 (fun p ->
      let cb = System.BitConverter.GetBytes(uint64 bytes.Length + 4096UL)
      System.Array.Copy(cb, 0, p, 24, 8))
    match ManifestReader.read patched with
    | Error msg ->
      (msg.Contains("total size") || msg.Contains("actual file size"))
      |> Expect.isTrue "error should cite the declared total size"
    | Ok _ -> failwith "Should reject a declared total size mismatch even with a valid CRC"

  testCase "declared session count mismatch is rejected even with recomputed CRC" <| fun _ ->
    let entry = {
      SessionId = "sess-1"
      Projects = [ "A.fsproj" ]
      WorkingDir = "C:\\A"
      CreatedAt = DateTimeOffset.UtcNow
      StoppedAt = None
    }
    let data = { Entries = [ entry ]; ActiveSessionId = None; CreatedAtMs = 1L }
    let bytes = ManifestWriter.write data
    let patched = tamperAndFixCrc bytes 32 (fun p ->
      let cb = System.BitConverter.GetBytes(99u)
      System.Array.Copy(cb, 0, p, 32, 4))
    match ManifestReader.read patched with
    | Error msg ->
      (msg.Contains("session count") || msg.Contains("parsed count"))
      |> Expect.isTrue "error should cite the declared session count"
    | Ok _ -> failwith "Should reject a declared session count mismatch even with a valid CRC"

  testCase "valid manifest still loads after the hostile-header guards" <| fun _ ->
    let entry = {
      SessionId = "sess-ok"
      Projects = [ "A.fsproj" ]
      WorkingDir = "C:\\A"
      CreatedAt = DateTimeOffset.UtcNow
      StoppedAt = None
    }
    let data = { Entries = [ entry ]; ActiveSessionId = Some "sess-ok"; CreatedAtMs = 1L }
    let bytes = ManifestWriter.write data
    match ManifestReader.read bytes with
    | Ok loaded ->
      loaded.Entries |> Expect.hasLength "one entry loads" 1
    | Error e -> failwithf "valid manifest rejected: %s" e
]

// ── Codec laws over generated manifests ──

let private genManifestText =
  ArbMap.defaults
  |> ArbMap.generate<string>
  |> Gen.map (fun s ->
    match s with
    | null -> ""
    // A lone surrogate is not valid UTF-16, so it cannot survive UTF-8.
    | s -> s |> String.filter (fun c -> not (Char.IsSurrogate c)))

let private genHexId =
  Gen.listOfLength 8 (Gen.elements ([ '0' .. '9' ] @ [ 'a' .. 'f' ]))
  |> Gen.map (fun cs -> String(List.toArray cs))

/// The format stores unix milliseconds, so instants are generated at that precision.
let private genInstant =
  gen {
    let! seconds = Gen.choose (0, Int32.MaxValue)
    let! ms = Gen.choose (0, 999)
    return DateTimeOffset.FromUnixTimeMilliseconds(int64 seconds * 1000L + int64 ms)
  }

let private genEntry (genStopped: Gen<DateTimeOffset option>) =
  gen {
    let! sid = Gen.oneof [ genHexId; genManifestText ]
    let! projectCount = Gen.choose (0, 4)
    let! projects = Gen.listOfLength projectCount genManifestText
    let! workDir = genManifestText
    let! created = genInstant
    let! stopped = genStopped
    return { SessionId = sid; Projects = projects; WorkingDir = workDir; CreatedAt = created; StoppedAt = stopped }
  }

let private genManifest =
  gen {
    let! count = Gen.choose (0, 8)
    let! entries = Gen.listOfLength count (genEntry (Gen.oneof [ Gen.constant None; genInstant |> Gen.map Some ]))
    let! active = Gen.oneof [ Gen.constant None; genHexId |> Gen.map Some ]
    let! createdAtMs = ArbMap.defaults |> ArbMap.generate<int64>
    return { Entries = entries; ActiveSessionId = active; CreatedAtMs = createdAtMs }
  }

let private isError (r: Result<'a, string>) =
  match r with
  | Error _ -> true
  | Ok _ -> false

[<Tests>]
let manifestCodecPropertyTests =
  let config = { FsCheckConfig.defaultConfig with maxTest = 200 }
  testList "DaemonManifest codec laws" [
    testPropertyWithConfig config
      "WHY — ManifestReader.read — decoding what ManifestWriter.write encoded gives back the same manifest because daemon.sagefm is the only record of which sessions to resume"
    <| Prop.forAll (Arb.fromGen genManifest) (fun manifest ->
      ManifestReader.read (ManifestWriter.write manifest) = Ok manifest)

    testPropertyWithConfig config
      "WHY — ManifestReader.read — every truncation of a manifest is an Error, never a shorter manifest, because a torn write must not silently forget sessions"
    <| Prop.forAll
         (Arb.fromGen (gen {
            let! manifest = genManifest
            let bytes = ManifestWriter.write manifest
            let! cut = Gen.choose (0, bytes.Length - 1)
            return bytes, cut }))
         (fun (bytes, cut) -> isError (ManifestReader.read bytes.[0 .. cut - 1]))

    testPropertyWithConfig config
      "WHY — ManifestReader.read — any single flipped bit is an Error, never a different manifest, because disk corruption must not resurrect or drop sessions"
    <| Prop.forAll
         (Arb.fromGen (gen {
            let! manifest = genManifest
            let bytes = ManifestWriter.write manifest
            let! position = Gen.choose (0, bytes.Length - 1)
            let! bit = Gen.choose (0, 7)
            return bytes, position, bit }))
         (fun (bytes, position, bit) ->
           let flipped = Array.copy bytes
           flipped.[position] <- flipped.[position] ^^^ (1uy <<< bit)
           isError (ManifestReader.read flipped))

    testPropertyWithConfig config
      "WHY — ManifestReader.read — arbitrary bytes behind a valid magic are an Error, not an exception, because daemon startup must survive any file it finds"
    <| Prop.forAll
         (Arb.fromGen (ArbMap.defaults |> ArbMap.generate<byte[]>))
         (fun tail ->
           let tail = match tail with null -> [||] | t -> t
           isError (ManifestReader.read (Array.append [| 0x53uy; 0x46uy; 0x4Duy; 0x31uy; 1uy; 0uy; 1uy; 0uy |] tail)))

    testPropertyWithConfig config
      "WHY — ManifestMapping — a manifest state saved and loaded back is the same state because a daemon restart must resume exactly the sessions it had"
    <| Prop.forAll
         (Arb.fromGen (gen {
            // Resume forgets sessions stopped over 7 days ago, so these stopped recently.
            let genRecent =
              Gen.choose (0, 6 * 24 * 60)
              |> Gen.map (fun minutes ->
                let now = DateTimeOffset.UtcNow
                Some (DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds() - int64 minutes * 60_000L)))
            let! count = Gen.choose (0, 8)
            let! entries = Gen.listOfLength count (genEntry (Gen.oneof [ Gen.constant None; genRecent ]))
            let! active = Gen.oneof [ Gen.constant None; genHexId |> Gen.map Some ]
            let sessions =
              entries
              |> List.map (fun e ->
                let record : DaemonManifest.DaemonSessionRecord =
                  { SessionId = e.SessionId
                    Projects = e.Projects
                    WorkingDir = e.WorkingDir
                    CreatedAt = e.CreatedAt
                    StoppedAt = e.StoppedAt }
                e.SessionId, record)
              |> Map.ofList
            let state : DaemonManifest.DaemonManifestState = { Sessions = sessions; ActiveSessionId = active }
            return state }))
         (fun state ->
           state
           |> ManifestMapping.fromManifestState
           |> ManifestWriter.write
           |> ManifestReader.read
           |> Result.map ManifestMapping.toManifestState = Ok state)
  ]

[<Tests>]
let manifestMappingTests = testList "ManifestMapping" [
  testCase "manifest state → manifest → manifest state roundtrips" <| fun _ ->
    let now = DateTimeOffset.UtcNow
    let state : Features.DaemonManifest.DaemonManifestState = {
      Sessions = Map.ofList [
        "s1", { SessionId = "s1"; Projects = ["A.fsproj"]; WorkingDir = "C:\\A"; CreatedAt = now; StoppedAt = None }
        "s2", { SessionId = "s2"; Projects = ["B.fsproj"]; WorkingDir = "C:\\B"; CreatedAt = now.AddMinutes(-5.0); StoppedAt = Some now }
      ]
      ActiveSessionId = Some "s1"
    }
    let manifest = ManifestMapping.fromManifestState state
    let roundtripped = ManifestMapping.toManifestState manifest

    roundtripped.Sessions.Count |> Expect.equal "2 sessions" 2
    roundtripped.ActiveSessionId |> Expect.equal "active" (Some "s1")
    let s1 = roundtripped.Sessions.["s1"]
    s1.Projects |> Expect.equal "s1 projects" ["A.fsproj"]
    s1.StoppedAt |> Expect.isNone "s1 alive"
    let s2 = roundtripped.Sessions.["s2"]
    s2.StoppedAt |> Expect.isSome "s2 stopped"
]
