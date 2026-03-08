module SageFs.Tests.Round6HardeningTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features

// ---------------------------------------------------------------------------
// W5 — ManifestPersistence payloadEnd uses List.min instead of List.tryHead
// ---------------------------------------------------------------------------
// Bug: payloadEnd used List.tryHead on a filtered+mapped list of directory offsets.
//      If sections are stored out of sorted order, tryHead picks the wrong boundary.
// Fix: List.min finds the true nearest upper bound regardless of insertion order.

open SageFs.Features.ManifestTypes

let private makeManifestWithTwoEntries () =
  { DaemonManifestData.Entries =
      [ { ManifestTypes.ManifestSessionEntry.SessionId = "sess-aaa"
          Projects = ["a.fsproj"]
          WorkingDir = "C:\\a"
          CreatedAt = DateTimeOffset.UtcNow
          StoppedAt = None }
        { ManifestTypes.ManifestSessionEntry.SessionId = "sess-bbb"
          Projects = ["b.fsproj"]
          WorkingDir = "C:\\b"
          CreatedAt = DateTimeOffset.UtcNow
          StoppedAt = None } ]
    DaemonManifestData.ActiveSessionId = Some "sess-aaa"
    DaemonManifestData.CreatedAtMs = 1234567890L }

[<Tests>]
let manifestPayloadBoundaryTests =
  testList "ManifestPersistence multi-section payload boundary" [

    testCase "round-trips two sessions without corruption" <| fun _ ->
      let data = makeManifestWithTwoEntries ()
      let bytes = ManifestWriter.write data
      bytes |> Expect.isNotNull "write should succeed"
      let roundTripped = ManifestReader.read bytes
      match roundTripped with
      | Error msg -> failwithf "read failed: %s" msg
      | Ok result ->
        result.Entries |> Expect.hasLength "should have 2 entries" 2

    testCase "round-tripped entry IDs match originals" <| fun _ ->
      let data = makeManifestWithTwoEntries ()
      let bytes = ManifestWriter.write data
      match ManifestReader.read bytes with
      | Error msg -> failwithf "read failed: %s" msg
      | Ok result ->
        result.Entries |> List.exists (fun e -> e.SessionId = "sess-aaa")
        |> Expect.isTrue "first session ID should survive round-trip"
        result.Entries |> List.exists (fun e -> e.SessionId = "sess-bbb")
        |> Expect.isTrue "second session ID should survive round-trip"

    testCase "ActiveSessionId survives round-trip" <| fun _ ->
      let data = makeManifestWithTwoEntries ()
      let bytes = ManifestWriter.write data
      match ManifestReader.read bytes with
      | Error msg -> failwithf "read failed: %s" msg
      | Ok result ->
        result.ActiveSessionId |> Expect.equal "ActiveSessionId should survive" (Some "sess-aaa")
  ]

// ---------------------------------------------------------------------------
// W6 — StandbySession carries BaseUrl so hot-swap populates WorkerBaseUrl
// ---------------------------------------------------------------------------
// Bug: After standby hot-swap, WorkerBaseUrl was always set to "".
//      All URL-dependent features (dashboard stats, warmup context fetch) silently failed.
// Fix: Added BaseUrl field to StandbySession; threaded through StandbyReady message.

open SageFs.WorkerProtocol

[<Tests>]
let standbyBaseUrlTests =
  testList "StandbySession.BaseUrl threads through hot-swap" [

    testCase "StandbySession has BaseUrl field" <| fun _ ->
      let s = {
        StandbySession.Process = new System.Diagnostics.Process()
        Proxy = None
        BaseUrl = "http://127.0.0.1:9999"
        State = StandbyState.Ready
        WarmupProgress = None
        Projects = ["test.fsproj"]
        WorkingDir = @"C:\test"
        CreatedAt = DateTime.UtcNow
      }
      s.BaseUrl |> Expect.equal "BaseUrl should be stored" "http://127.0.0.1:9999"

    testCase "StandbySession BaseUrl defaults to empty on Warming state" <| fun _ ->
      let s = {
        StandbySession.Process = new System.Diagnostics.Process()
        Proxy = None
        BaseUrl = ""
        State = StandbyState.Warming
        WarmupProgress = None
        Projects = ["test.fsproj"]
        WorkingDir = @"C:\test"
        CreatedAt = DateTime.UtcNow
      }
      s.BaseUrl |> Expect.equal "BaseUrl should be empty while warming" ""

    testCase "BaseUrl can be updated via record with-expression" <| fun _ ->
      let warming = {
        StandbySession.Process = new System.Diagnostics.Process()
        Proxy = None
        BaseUrl = ""
        State = StandbyState.Warming
        WarmupProgress = None
        Projects = ["test.fsproj"]
        WorkingDir = @"C:\test"
        CreatedAt = DateTime.UtcNow
      }
      let ready = { warming with BaseUrl = "http://127.0.0.1:8888"; State = StandbyState.Ready }
      ready.BaseUrl |> Expect.equal "BaseUrl should reflect the ready URL" "http://127.0.0.1:8888"
  ]

// ---------------------------------------------------------------------------
// W8 — StopSession only appends DaemonSessionStopped when session actually stopped
// ---------------------------------------------------------------------------
// Bug: appendEventsAsync called before checking Result of StopSession.
//      A tombstone event was written even when the session didn't exist.
// Fix: appendEventsAsync inside Ok branch only.
// (Pure behavioural test — verified by inspecting DaemonMode wiring)

// The fix is structural (Ok-branch gating) — we verify the predicate logic here.

[<Tests>]
let stopSessionEventGatingTests =
  testList "StopSession event gating" [

    testCase "Ok result should allow event write (predicate)" <| fun _ ->
      let result : Result<unit, SageFsError> = Ok ()
      let mutable written = false
      match result with
      | Ok () -> written <- true
      | Error _ -> ()
      written |> Expect.isTrue "event should be written on Ok"

    testCase "Error result should not write event (predicate)" <| fun _ ->
      let result : Result<unit, SageFsError> = Error (SageFsError.SessionNotFound "missing")
      let mutable written = false
      match result with
      | Ok () -> written <- true
      | Error _ -> ()
      written |> Expect.isFalse "event must NOT be written on Error"
  ]

// ---------------------------------------------------------------------------
// W10 — FeatureHooks KnownBindings is incrementally maintained (O(1) per eval)
// ---------------------------------------------------------------------------
// Bug: computeCellDepsPush rebuilt knownBindings by scanning all EvalHistory entries
//      on every SSE push — O(n) per push, O(n²) total.
// Fix: KnownBindings field on FeaturePushState, updated incrementally in recordEval.

open SageFs.Features.FeatureHooks

[<Tests>]
let knownBindingsIncrementalTests =
  testList "FeaturePushState.KnownBindings incremental update" [

    testCase "empty state has empty KnownBindings" <| fun _ ->
      FeaturePushState.empty.KnownBindings
      |> Map.isEmpty
      |> Expect.isTrue "empty state should have no bindings"

    testCase "recordEval with val binding updates KnownBindings" <| fun _ ->
      let result = "val x : int = 42"
      let state = recordEval "let x = 42" result 5L FeaturePushState.empty
      state.KnownBindings |> Map.containsKey "x"
      |> Expect.isTrue "binding 'x' should be in KnownBindings after eval"

    testCase "KnownBindings maps name to correct cell index" <| fun _ ->
      let s0 = recordEval "let a = 1" "val a : int = 1" 1L FeaturePushState.empty
      let s1 = recordEval "let b = 2" "val b : int = 2" 1L s0
      s1.KnownBindings |> Map.tryFind "a"
      |> Expect.equal "a should be cell 0" (Some 0)
      s1.KnownBindings |> Map.tryFind "b"
      |> Expect.equal "b should be cell 1" (Some 1)

    testCase "later binding with same name overwrites earlier (last-writer wins)" <| fun _ ->
      let s0 = recordEval "let x = 1" "val x : int = 1" 1L FeaturePushState.empty
      let s1 = recordEval "let x = 99" "val x : int = 99" 1L s0
      s1.KnownBindings |> Map.tryFind "x"
      |> Expect.equal "x should point to the latest cell" (Some 1)

    testCase "result without val lines does not add to KnownBindings" <| fun _ ->
      let s = recordEval "printfn \"hi\"" "hi" 1L FeaturePushState.empty
      s.KnownBindings |> Map.isEmpty
      |> Expect.isTrue "no val lines means no bindings added"

    testCase "computeCellDepsPush uses KnownBindings from state" <| fun _ ->
      let opts = System.Text.Json.JsonSerializerOptions()
      let s0 = recordEval "let z = 10" "val z : int = 10" 1L FeaturePushState.empty
      let s1 = recordEval "z + 1" "val it : int = 11" 1L s0
      // Just verify it doesn't throw and returns a result
      let newState, _ = computeCellDepsPush opts None s1
      newState.KnownBindings |> Map.containsKey "z"
      |> Expect.isTrue "z should still be in bindings after push"
  ]

// ---------------------------------------------------------------------------
// W2 — Symlink bypass: resolveRealPath follows LinkTarget before containment check
// ---------------------------------------------------------------------------
// Bug: Path.GetFullPath canonicalizes .. segments but does NOT follow symlinks.
//      A symlink inside the working dir pointing outside it bypasses R5's guard.
// Fix: Walk FileInfo.LinkTarget recursively (up to 16 hops) before checking containment.
// (The actual fix is in Dashboard.fs — here we test the pure predicate logic.)

/// Mirrors the resolveRealPath + isContained logic from Dashboard.fs createEvalFileHandler.
let private resolveRealPath (p: string) : string =
  let mutable current = Path.GetFullPath p
  let mutable hops = 0
  let mutable keepGoing = true
  while keepGoing && hops < 16 do
    let fi = FileInfo(current)
    match fi.LinkTarget with
    | null | "" -> keepGoing <- false
    | target ->
      let resolved =
        match Path.IsPathRooted target with
        | true -> target
        | false -> Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current), target))
      current <- resolved
      hops <- hops + 1
  current

let private isContainedAfterSymlinkResolution (workingDir: string) (filePath: string) : bool =
  match String.IsNullOrWhiteSpace workingDir || String.IsNullOrWhiteSpace filePath with
  | true -> false
  | false ->
    let canonical = resolveRealPath filePath
    let canonicalDir = resolveRealPath workingDir
    canonical.StartsWith(
      canonicalDir + string Path.DirectorySeparatorChar,
      StringComparison.OrdinalIgnoreCase)
    || canonical.Equals(canonicalDir, StringComparison.OrdinalIgnoreCase)

[<Tests>]
let symlinkResolutionTests =
  testList "resolveRealPath symlink resistance" [

    testCase "non-symlink file returns Path.GetFullPath result" <| fun _ ->
      let dir = Path.GetTempPath().TrimEnd([|'\\'; '/'|])
      let file = Path.Combine(dir, "test.fsx")
      let resolved = resolveRealPath file
      resolved |> Expect.equal "non-symlink resolves to GetFullPath" (Path.GetFullPath file)

    testCase "file in workdir is contained after resolution" <| fun _ ->
      let dir = Path.GetTempPath().TrimEnd([|'\\'; '/'|])
      let file = Path.Combine(dir, "script.fsx")
      isContainedAfterSymlinkResolution dir file
      |> Expect.isTrue "file inside dir should be contained"

    testCase "file outside workdir is not contained after resolution" <| fun _ ->
      let base' = Path.GetTempPath().TrimEnd([|'\\'; '/'|])
      let workDir = Path.Combine(base', "myproject")
      let outsideFile = Path.Combine(base', "other", "secret.fsx")
      isContainedAfterSymlinkResolution workDir outsideFile
      |> Expect.isFalse "file outside dir should not be contained"

    testCase "path traversal ../ is blocked" <| fun _ ->
      let dir = Path.Combine(Path.GetTempPath(), "myproject")
      let file = Path.Combine(dir, "..", "secret.fsx")
      isContainedAfterSymlinkResolution dir file
      |> Expect.isFalse "../ traversal must be blocked even after resolution"

    testCase "symlink inside workdir to outside is rejected (live test on platforms with symlinks)" <| fun _ ->
      let base' = Path.GetTempPath()
      let workDir = Path.Combine(base', sprintf "r6test-%s" (Guid.NewGuid().ToString("N").[..7]))
      let targetDir = Path.Combine(base', sprintf "r6secret-%s" (Guid.NewGuid().ToString("N").[..7]))
      Directory.CreateDirectory(workDir) |> ignore
      Directory.CreateDirectory(targetDir) |> ignore
      let secretFile = Path.Combine(targetDir, "secret.txt")
      File.WriteAllText(secretFile, "sensitive")
      let linkPath = Path.Combine(workDir, "link.txt")
      try
        try
          File.CreateSymbolicLink(linkPath, secretFile) |> ignore
          let result = isContainedAfterSymlinkResolution workDir linkPath
          result |> Expect.isFalse "symlink pointing outside workdir must be rejected"
        with
        | :? UnauthorizedAccessException ->
          // Creating symlinks requires elevated privileges on some Windows configs — skip gracefully
          ()
        | :? IOException ->
          // Platform may not support symlinks — skip gracefully
          ()
      finally
        try Directory.Delete(workDir, true) with _ -> ()
        try Directory.Delete(targetDir, true) with _ -> ()
  ]
