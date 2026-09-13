module SageFs.Tests.Round11HardeningTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.DaemonManifest
open SageFs.Features.EvalTimeline

// ---------------------------------------------------------------------------
// W17 — mergeManifestWithExisting distinguishes "No file" from IO errors
// ---------------------------------------------------------------------------
// W26(R12): The stringly-typed "No manifest file found" sentinel has been replaced by the
// ManifestLoadError DU (NotFound | IoError | CorruptData). The sentinel-stability test
// that used to live here has been deleted — it was guarding a design smell.
// The compiler now enforces exhaustiveness on the DU, making the test redundant.

[<Tests>]
let w17ManifestErrorDistinctionTests =
  testList "W17(R11) — manifest load: IO errors distinguished from not-found" [

    testCase "ManifestReader returns error for zero-byte file" <| fun _ ->
      let result = ManifestReader.read [||]
      match result with
      | Ok _ -> failtest "Expected error for empty manifest"
      | Result.Error msg ->
        // This is a format/CRC error — it does NOT map to ManifestLoadError.NotFound.
        let isNotFoundMsg = msg = "No manifest file found"
        isNotFoundMsg |> Expect.isFalse "corrupt file error should not say 'No manifest file found'"

    testCase "loadManifest for existing but corrupt file returns CorruptData error" <| fun _ ->
      let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "sagefs-test-%s" (System.Guid.NewGuid().ToString("N")))
      System.IO.Directory.CreateDirectory(dir) |> ignore
      try
        // Write a corrupt manifest file
        let fakeManifestPath = System.IO.Path.Combine(dir, "daemon.sagefm")
        System.IO.File.WriteAllBytes(fakeManifestPath, Array.create 64 0xFFuy)
        let result = DaemonPersistence.loadManifest dir
        match result with
        | Result.Error (SageFs.Features.ManifestTypes.ManifestLoadError.CorruptData _) -> ()
        | other -> failtest (sprintf "Expected CorruptData, got %A" other)
      finally
        System.IO.Directory.Delete(dir, true)
  ]

