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
// W8 — O(n²) EvalHistory in Replay.applyEvent
// ---------------------------------------------------------------------------
// Bug: `state.EvalHistory @ [record]` copies the entire list on every append.
// Fix: prepend with `record :: state.EvalHistory`; reverse at display read sites.

open SageFs.Features.Replay

let private makeEvalEvent code : SageFsEvent =
  EvalCompleted
    {| Code = code
       Result = "val it : int = 42"
       TypeSignature = None
       Duration = TimeSpan.FromMilliseconds(10.0) |}

[<Tests>]
let evalHistoryOrderTests =
  testList "Replay.SessionReplayState EvalHistory" [

    testCase "single eval appears in EvalHistory" <| fun _ ->
      let ts = DateTimeOffset.UtcNow
      let state = SessionReplayState.replayStream [ ts, makeEvalEvent "1 + 1" ]
      state.EvalHistory |> Expect.hasLength "should have one entry" 1

    testCase "multiple evals all appear in EvalHistory" <| fun _ ->
      let ts = DateTimeOffset.UtcNow
      let events = [
        ts.AddSeconds(-2.0), makeEvalEvent "eval1"
        ts.AddSeconds(-1.0), makeEvalEvent "eval2"
        ts,                  makeEvalEvent "eval3"
      ]
      let state = SessionReplayState.replayStream events
      state.EvalHistory |> Expect.hasLength "should have 3 entries" 3

    testCase "EvalHistory contains all eval codes" <| fun _ ->
      let ts = DateTimeOffset.UtcNow
      let events = [
        ts.AddSeconds(-2.0), makeEvalEvent "first"
        ts.AddSeconds(-1.0), makeEvalEvent "second"
        ts,                  makeEvalEvent "third"
      ]
      let state = SessionReplayState.replayStream events
      state.EvalHistory |> List.exists (fun r -> r.Code = "first")
      |> Expect.isTrue "history should include the first eval"
      state.EvalHistory |> List.exists (fun r -> r.Code = "third")
      |> Expect.isTrue "history should include the third eval"

    testCase "exportAsFsx presents evals in chronological order (oldest first)" <| fun _ ->
      let ts = DateTimeOffset.UtcNow
      let events = [
        ts.AddSeconds(-2.0), makeEvalEvent "// eval-first"
        ts.AddSeconds(-1.0), makeEvalEvent "// eval-second"
        ts,                  makeEvalEvent "// eval-third"
      ]
      let state = SessionReplayState.replayStream events
      let exported = SessionReplayState.exportAsFsx state
      let firstPos = exported.IndexOf("// eval-first", StringComparison.Ordinal)
      let thirdPos = exported.IndexOf("// eval-third", StringComparison.Ordinal)
      (firstPos, thirdPos) |> Expect.isLessThan "first eval should appear before third in export"
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
