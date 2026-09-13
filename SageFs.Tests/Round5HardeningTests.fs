module SageFs.Tests.Round5HardeningTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.ManifestTypes
open SageFs.Features.Events

// ---------------------------------------------------------------------------
// W4 — ManifestPersistence header overflow guard (SILENT corruption)
// ---------------------------------------------------------------------------
// Bug: When ActiveSessionId is long enough to push ms.Position past headerSize=64,
//      padLen = headerSize - pos becomes negative. The `match padLen > 0` guard
//      silently skips the pad write, leaving a corrupt but CRC-passing manifest.
// Fix: invalidOp when padLen < 0.
// RED TEST: write with a very long session ID should throw InvalidOperationException.

let private makeManifestData activeId =
  { DaemonManifestData.Entries = []
    DaemonManifestData.ActiveSessionId = Some activeId
    DaemonManifestData.CreatedAtMs = 0L }

[<Tests>]
let manifestOverflowTests =
  testList "ManifestWriter header overflow guard" [

    testCase "very long session ID (>36 bytes) throws InvalidOperationException" <| fun _ ->
      // header = 64 bytes; fixed fields before ActiveSessionId use ~40 bytes.
      // a 40-char ID pushes pos > 64, making padLen < 0.
      let longId = String.replicate 40 "a"
      let mutable thrownEx: exn = null
      try ManifestWriter.write (makeManifestData longId) |> ignore
      with e -> thrownEx <- e
      thrownEx |> Expect.isNotNull "should throw for overlong ActiveSessionId"
      (thrownEx :? InvalidOperationException)
      |> Expect.isTrue
        (sprintf "expected InvalidOperationException, got %s"
          (if isNull thrownEx then "null" else thrownEx.GetType().Name))

    testCase "normal session ID (8 chars) writes without error" <| fun _ ->
      ManifestWriter.write (makeManifestData "abc12345")
      |> Expect.isNotNull "short ID should produce bytes"

    testCase "None ActiveSessionId writes without error" <| fun _ ->
      ManifestWriter.write DaemonManifestData.empty
      |> Expect.isNotNull "None ActiveSessionId should produce bytes"
  ]

