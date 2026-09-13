module SageFs.Tests.Round12HardeningTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.ManifestTypes
open SageFs.Features.DaemonManifest

// ---------------------------------------------------------------------------
// W26 — ManifestLoadError DU replaces stringly-typed sentinel
// ---------------------------------------------------------------------------
// W26(R12): loadManifest now returns Result<DaemonManifestState, ManifestLoadError>
// with a structured DU instead of string comparisons. The compiler enforces
// exhaustiveness; no sentinel-stability test needed.

[<Tests>]
let w26ManifestLoadErrorDuTests =
  testList "W26(R12) — ManifestLoadError DU: structured errors from loadManifest" [

    testCase "loadManifest for missing dir returns Error NotFound (not a string)" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r12-%s" (Guid.NewGuid().ToString("N")))
      let result = DaemonPersistence.loadManifest dir
      match result with
      | Result.Error ManifestLoadError.NotFound -> ()
      | other -> failtest (sprintf "Expected Error NotFound, got %A" other)

    testCase "loadManifest for corrupt file returns Error CorruptData" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r12-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        IO.File.WriteAllBytes(IO.Path.Combine(dir, "daemon.sagefm"), Array.create 32 0xDEuy)
        let result = DaemonPersistence.loadManifest dir
        match result with
        | Result.Error (ManifestLoadError.CorruptData _) -> ()
        | other -> failtest (sprintf "Expected Error CorruptData, got %A" other)
      finally
        IO.Directory.Delete(dir, true)

    testCase "ManifestLoadError NotFound is distinguishable from IoError and CorruptData" <| fun _ ->
      // Compiler-enforced exhaustiveness — if DU gains a new case this test fails to compile.
      let classify (e: ManifestLoadError) =
        match e with
        | NotFound -> "not-found"
        | IoError _ -> "io-error"
        | CorruptData _ -> "corrupt"
      classify NotFound |> Expect.equal "NotFound classifies correctly" "not-found"
      classify (IoError "x") |> Expect.equal "IoError classifies correctly" "io-error"
      classify (CorruptData "x") |> Expect.equal "CorruptData classifies correctly" "corrupt"

    testCase "ManifestFile.load round-trips to Ok for a valid manifest" <| fun _ ->
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r12-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        // Save a valid manifest, then load it — should return Ok, not any error variant
        let state = DaemonManifestState.empty
        match DaemonPersistence.saveManifest dir state with
        | Result.Error err -> failtest (sprintf "Save failed: %s" err)
        | Ok _ ->
          match DaemonPersistence.loadManifest dir with
          | Ok _ -> ()
          | Result.Error err -> failtest (sprintf "Expected Ok after valid save, got %A" err)
      finally
        IO.Directory.Delete(dir, true)
  ]

// ---------------------------------------------------------------------------
// W23 — mergeManifestWithExisting returns Result; callers skip save on Error
// ---------------------------------------------------------------------------
// W23(R12): The function now returns Result<DaemonManifestState, string>.
// Error means "cannot read manifest; callers must NOT write to preserve history."
// The old code logged "history preserved" but still wrote active-only state.

[<Tests>]
let w23MergeResultTests =
  testList "W23(R12) — mergeManifestWithExisting returns Result; Error skips write" [

    testCase "corrupt manifest on disk causes loadManifest Error — no Ok result that would erase history" <| fun _ ->
      // Simulate the scenario: daemon has a manifest on disk that's corrupt (partial write).
      // Before W23: mergeManifest would return active-only DaemonManifestState and callers would write.
      // After W23: callers receive Error and skip the write — history is preserved (nothing written).
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r12-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        IO.File.WriteAllBytes(IO.Path.Combine(dir, "daemon.sagefm"), [| 0xFFuy; 0xFEuy; 0xFDuy |])
        let result = DaemonPersistence.loadManifest dir
        // loadManifest returns Error — the caller (periodicManifestSave) would skip saveManifest
        let isError =
          match result with
          | Result.Error _ -> true
          | Ok _ -> false
        isError |> Expect.isTrue "corrupt manifest should return Error — caller must skip write"
      finally
        IO.Directory.Delete(dir, true)
  ]

// ---------------------------------------------------------------------------
// W25 — Consistent snapshot: passed as value not thunk
// ---------------------------------------------------------------------------
// W25(R12): buildManifestState and mergeManifestWithExisting now take QuerySnapshot as a value.
// The observable property: the same snapshot value always produces the same replay state.
// Testing via DaemonPersistence (accessible from test project) for round-trip consistency.

[<Tests>]
let w25SnapshotConsistencyTests =
  testList "W25(R12) — Snapshot value consistency: deterministic for same input" [

    testCase "empty DaemonManifestState round-trips through save/load consistently" <| fun _ ->
      // Verify that a snapshot-derived state is stable: save X, load X, matches X.
      // This is the key property that passing snapshot-as-value must preserve.
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r12-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        let state = DaemonManifestState.empty
        DaemonPersistence.saveManifest dir state |> ignore
        match DaemonPersistence.loadManifest dir with
        | Ok loaded ->
          loaded.Sessions.Count |> Expect.equal "round-trip session count matches" state.Sessions.Count
          loaded.ActiveSessionId |> Expect.equal "round-trip active id matches" state.ActiveSessionId
        | Result.Error err -> failtest (sprintf "Load failed: %A" err)
      finally
        IO.Directory.Delete(dir, true)

    testCase "loading the same manifest twice returns equal results" <| fun _ ->
      // If passing a snapshot-as-value is correct, reading from disk is idempotent.
      let dir = IO.Path.Combine(IO.Path.GetTempPath(), sprintf "sagefs-r12-%s" (Guid.NewGuid().ToString("N")))
      IO.Directory.CreateDirectory(dir) |> ignore
      try
        let state = DaemonManifestState.empty
        DaemonPersistence.saveManifest dir state |> ignore
        let r1 = DaemonPersistence.loadManifest dir
        let r2 = DaemonPersistence.loadManifest dir
        match r1, r2 with
        | Ok s1, Ok s2 ->
          s1.Sessions.Count |> Expect.equal "two loads give same session count" s2.Sessions.Count
          s1.ActiveSessionId |> Expect.equal "two loads give same active id" s2.ActiveSessionId
        | _ -> failtest (sprintf "Expected both loads to succeed, got %A and %A" r1 r2)
      finally
        IO.Directory.Delete(dir, true)
  ]

[<Tests>]
let allRound12Tests =
  testList "Round 12 Hardening (R12) — W23 W25 W26" [
    w26ManifestLoadErrorDuTests
    w23MergeResultTests
    w25SnapshotConsistencyTests
  ]
