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

// ---------------------------------------------------------------------------
// W1 — Path traversal: containment check (pure predicate tests)
// ---------------------------------------------------------------------------
// Bug: createEvalFileHandler reads any file path with no containment check.
//      `{"path":"C:/Users/dev/.ssh/id_rsa"}` would be served to any client.
// Fix: Path.GetFullPath canonicalization + StartsWith(workingDir + sep) check.

/// Local copy of the containment predicate (mirrors what Dashboard.fs now implements).
let private isPathContained (workingDir: string) (filePath: string) : bool =
  match String.IsNullOrWhiteSpace workingDir || String.IsNullOrWhiteSpace filePath with
  | true -> false
  | false ->
    let canonical = Path.GetFullPath filePath
    let canonicalDir = Path.GetFullPath workingDir
    canonical.StartsWith(
      canonicalDir + string Path.DirectorySeparatorChar,
      StringComparison.OrdinalIgnoreCase)
    || canonical.Equals(canonicalDir, StringComparison.OrdinalIgnoreCase)

[<Tests>]
let pathContainmentTests =
  testList "Path containment for eval-file security" [

    testCase "file inside working dir is allowed" <| fun _ ->
      let dir = Path.GetTempPath().TrimEnd([|'\\'; '/'|])
      let file = Path.Combine(dir, "test.fsx")
      isPathContained dir file |> Expect.isTrue "file inside dir should be allowed"

    testCase "file outside working dir is rejected" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "myproject")
      let file = Path.Combine(Path.GetTempPath(), "other", "secret.fsx")
      isPathContained dir file |> Expect.isFalse "file outside dir must be rejected"

    testCase "path traversal ../ is blocked after canonicalization" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "myproject")
      let file = Path.Combine(dir, "..", "secret.fsx")
      isPathContained dir file |> Expect.isFalse "../ path traversal must be blocked"

    testCase "prefix attack (proj vs proj2) is blocked" <| fun _ ->
      let base' = Path.GetTempPath().TrimEnd([|'\\'; '/'|])
      let dir = Path.Combine(base', "proj")
      let file = Path.Combine(base', "proj2", "secret.fsx")
      isPathContained dir file |> Expect.isFalse "prefix-only match must not be allowed"

    testCase "empty working dir rejects all files" <| fun _ ->
      isPathContained "" (Path.Combine(Path.GetTempPath(), "anything.fsx"))
      |> Expect.isFalse "empty working dir must reject all files"
  ]
