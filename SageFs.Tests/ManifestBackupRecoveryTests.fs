module SageFs.Tests.ManifestBackupRecoveryTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Features.ManifestTypes
open SageFs.Features

/// Create minimal test manifest data with a unique timestamp.
let private testData (ts: int64) =
  let created = DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero)
  {
    Entries = [
      {
        SessionId = sprintf "test-session-%d" ts
        Projects = [ "TestProj.fsproj" ]
        WorkingDir = "C:\\Code\\Test"
        CreatedAt = created
        StoppedAt = None
      }
    ]
    ActiveSessionId = Some (sprintf "test-session-%d" ts)
    CreatedAtMs = ts
  }

/// Create a temp directory for a single test, returning the path.
let private withTempDir (f: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), sprintf "sagefs-bak-%s" (Guid.NewGuid().ToString("N")))
  Directory.CreateDirectory(dir) |> ignore
  try f dir
  finally
    try Directory.Delete(dir, true) with _ -> ()

let private manifestPath dir = Path.Combine(dir, "daemon.sagefm")
let private bakPath dir = Path.Combine(dir, "daemon.sagefm.bak")

[<Tests>]
let manifestBackupSaveTests = testList "Manifest Backup/Save" [

  testCase "save creates .sagefm file" <| fun _ ->
    withTempDir (fun dir ->
      let data = testData 1000L
      ManifestFile.save dir data |> Result.isOk |> Expect.isTrue "save should succeed"
      File.Exists(manifestPath dir) |> Expect.isTrue "manifest file should exist"
    )

  testCase "second save creates .sagefm.bak from previous" <| fun _ ->
    withTempDir (fun dir ->
      let data1 = testData 1001L
      let data2 = testData 1002L
      ManifestFile.save dir data1 |> ignore
      ManifestFile.save dir data2 |> ignore
      File.Exists(bakPath dir) |> Expect.isTrue ".bak should exist after second save"
    )

  testCase ".bak content matches previous .sagefm content" <| fun _ ->
    withTempDir (fun dir ->
      let data1 = testData 2001L
      let data2 = testData 2002L
      ManifestFile.save dir data1 |> ignore
      let primaryBytesBeforeSecondSave = File.ReadAllBytes(manifestPath dir)
      ManifestFile.save dir data2 |> ignore
      let bakBytes = File.ReadAllBytes(bakPath dir)
      bakBytes |> Expect.equal ".bak should be byte-identical to previous primary" primaryBytesBeforeSecondSave
    )

  testCase "first save to new directory skips .bak creation" <| fun _ ->
    withTempDir (fun dir ->
      let data = testData 3001L
      ManifestFile.save dir data |> ignore
      File.Exists(bakPath dir) |> Expect.isFalse ".bak should not exist after first save"
    )

  testCase "save with read-only .bak still succeeds (best-effort)" <| fun _ ->
    withTempDir (fun dir ->
      let data1 = testData 4001L
      let data2 = testData 4002L
      ManifestFile.save dir data1 |> ignore
      // Create a read-only .bak to simulate IO error on backup
      File.WriteAllBytes(bakPath dir, [| 0uy |])
      File.SetAttributes(bakPath dir, FileAttributes.ReadOnly)
      try
        // On Windows, File.Copy with overwrite=true can fail on read-only targets;
        // the save should still succeed because backup is best-effort
        let result = ManifestFile.save dir data2
        result |> Result.isOk |> Expect.isTrue "save should succeed even if .bak write fails"
      finally
        // Clean up read-only attribute so temp dir deletion works
        match File.Exists(bakPath dir) with
        | true -> File.SetAttributes(bakPath dir, FileAttributes.Normal)
        | false -> ()
    )
]

[<Tests>]
let manifestBackupLoadTests = testList "Manifest Backup/Load" [

  testCase "load from valid primary returns (data, Primary)" <| fun _ ->
    withTempDir (fun dir ->
      let data = testData 5001L
      ManifestFile.save dir data |> ignore
      match ManifestFile.load dir with
      | Ok (loaded, source) ->
        source |> Expect.equal "source should be Primary" ManifestSource.Primary
        loaded.CreatedAtMs |> Expect.equal "timestamp preserved" 5001L
      | Error e -> failwithf "Expected Ok, got Error: %A" e
    )

  testCase "load from corrupt primary falls back to valid backup" <| fun _ ->
    withTempDir (fun dir ->
      let data1 = testData 6001L
      let data2 = testData 6002L
      ManifestFile.save dir data1 |> ignore
      ManifestFile.save dir data2 |> ignore
      // Corrupt the primary
      File.WriteAllBytes(manifestPath dir, [| 0uy; 0uy; 0uy; 0uy |])
      match ManifestFile.load dir with
      | Ok (loaded, source) ->
        source |> Expect.equal "source should be Backup" ManifestSource.Backup
        loaded.CreatedAtMs |> Expect.equal "should recover data1 from backup" 6001L
      | Error e -> failwithf "Expected Ok from backup, got Error: %A" e
    )

  testCase "load from missing primary + valid backup returns (data, Backup)" <| fun _ ->
    withTempDir (fun dir ->
      let data1 = testData 7001L
      let data2 = testData 7002L
      ManifestFile.save dir data1 |> ignore
      ManifestFile.save dir data2 |> ignore
      // Delete primary, keep backup
      File.Delete(manifestPath dir)
      match ManifestFile.load dir with
      | Ok (loaded, source) ->
        source |> Expect.equal "source should be Backup" ManifestSource.Backup
        loaded.CreatedAtMs |> Expect.equal "should recover data1 from backup" 7001L
      | Error e -> failwithf "Expected Ok from backup, got Error: %A" e
    )

  testCase "load from missing primary + missing backup returns NotFound" <| fun _ ->
    withTempDir (fun dir ->
      match ManifestFile.load dir with
      | Error NotFound -> ()
      | other -> failwithf "Expected NotFound, got %A" other
    )

  testCase "load from corrupt primary + corrupt backup returns CorruptData" <| fun _ ->
    withTempDir (fun dir ->
      // Write corrupt data to both primary and backup
      File.WriteAllBytes(manifestPath dir, [| 0x53uy; 0x46uy; 0x4Duy; 0x31uy; 0uy; 0uy |])
      File.WriteAllBytes(bakPath dir, [| 0uy; 0uy; 0uy |])
      match ManifestFile.load dir with
      | Error (CorruptData _) -> ()
      | other -> failwithf "Expected CorruptData, got %A" other
    )

  testCase "load from corrupt primary + missing backup returns CorruptData" <| fun _ ->
    withTempDir (fun dir ->
      File.WriteAllBytes(manifestPath dir, [| 0uy; 0uy; 0uy; 0uy |])
      match ManifestFile.load dir with
      | Error (CorruptData _) -> ()
      | other -> failwithf "Expected CorruptData, got %A" other
    )
]

[<Tests>]
let manifestBackupRoundtripTests = testList "Manifest Backup/Roundtrip" [

  testCase "save then load returns original data with Primary source" <| fun _ ->
    withTempDir (fun dir ->
      let data = testData 8001L
      ManifestFile.save dir data |> ignore
      match ManifestFile.load dir with
      | Ok (loaded, source) ->
        source |> Expect.equal "source" ManifestSource.Primary
        loaded.CreatedAtMs |> Expect.equal "timestamp" 8001L
        loaded.Entries |> Expect.hasLength "one entry" 1
        loaded.Entries.[0].SessionId |> Expect.equal "session id" "test-session-8001"
      | Error e -> failwithf "Load failed: %A" e
    )

  testCase "save twice then corrupt primary → load recovers first save from backup" <| fun _ ->
    withTempDir (fun dir ->
      let data1 = testData 9001L
      let data2 = testData 9002L
      ManifestFile.save dir data1 |> ignore
      ManifestFile.save dir data2 |> ignore
      // .bak now holds data1, primary holds data2
      // Corrupt primary → should fall back to .bak (data1)
      let bytes = File.ReadAllBytes(manifestPath dir)
      let corrupted = Array.copy bytes
      corrupted.[bytes.Length - 1] <- corrupted.[bytes.Length - 1] ^^^ 0xFFuy
      File.WriteAllBytes(manifestPath dir, corrupted)
      match ManifestFile.load dir with
      | Ok (loaded, source) ->
        source |> Expect.equal "source should be Backup" ManifestSource.Backup
        loaded.CreatedAtMs |> Expect.equal "should be data1 timestamp" 9001L
      | Error e -> failwithf "Expected backup recovery, got Error: %A" e
    )

  testCase "multiple saves maintain only 1 backup generation" <| fun _ ->
    withTempDir (fun dir ->
      let data1 = testData 10001L
      let data2 = testData 10002L
      let data3 = testData 10003L
      ManifestFile.save dir data1 |> ignore
      ManifestFile.save dir data2 |> ignore
      ManifestFile.save dir data3 |> ignore
      // .bak should be data2 (the previous save), not data1
      let bakBytes = File.ReadAllBytes(bakPath dir)
      match ManifestReader.read bakBytes with
      | Ok bakData ->
        bakData.CreatedAtMs |> Expect.equal ".bak should be data2" 10002L
      | Error e -> failwithf "Failed to read .bak: %s" e
      // Verify no .bak.bak or other multi-generation files
      let bakBakPath = bakPath dir + ".bak"
      File.Exists(bakBakPath) |> Expect.isFalse "no multi-generation backups"
    )
]

[<Tests>]
let manifestBackupCrcTests = testList "Manifest Backup/CRC" [

  testCase "backup file passes CRC validation" <| fun _ ->
    withTempDir (fun dir ->
      let data1 = testData 11001L
      let data2 = testData 11002L
      ManifestFile.save dir data1 |> ignore
      ManifestFile.save dir data2 |> ignore
      let bakBytes = File.ReadAllBytes(bakPath dir)
      match ManifestReader.read bakBytes with
      | Ok bakData ->
        bakData.CreatedAtMs |> Expect.equal "backup data readable" 11001L
      | Error e -> failwithf "Backup CRC validation failed: %s" e
    )

  testCase "corrupting 1 byte in primary triggers backup fallback" <| fun _ ->
    withTempDir (fun dir ->
      let data1 = testData 12001L
      let data2 = testData 12002L
      ManifestFile.save dir data1 |> ignore
      ManifestFile.save dir data2 |> ignore
      // Flip one byte in the primary
      let bytes = File.ReadAllBytes(manifestPath dir)
      let corrupted = Array.copy bytes
      corrupted.[40] <- corrupted.[40] ^^^ 0x01uy
      File.WriteAllBytes(manifestPath dir, corrupted)
      match ManifestFile.load dir with
      | Ok (loaded, source) ->
        source |> Expect.equal "should fall back to Backup" ManifestSource.Backup
        loaded.CreatedAtMs |> Expect.equal "should be data1" 12001L
      | Error e -> failwithf "Expected backup fallback, got Error: %A" e
    )
]
