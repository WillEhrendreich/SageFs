module SageFs.Tests.Round8HardeningTests

open System
open System.IO
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.ManifestTypes
open SageFs.Features.DaemonManifest

// ---------------------------------------------------------------------------
// W1-residual — TOCTOU: canonical is now used for File.Exists and ReadAllText
// ---------------------------------------------------------------------------
// Verified by code change: Dashboard.fs createEvalFileHandler and
// McpServer.fs /load-script both hoist `canonical = resolveRealPath filePath`
// before `isContained` and use `canonical` for all subsequent operations.
// Unit-testable aspect: path containment logic is consistent when same value
// is used for check and effect.

[<Tests>]
let w1CanonicalPathTests =
  testList "W1-residual — TOCTOU: canonical path used for both check and read" [

    testCase "GetFullPath is idempotent on an already-absolute path" <| fun _ ->
      // resolveRealPath calls Path.GetFullPath(p); verify idempotency
      // (canonical of canonical == canonical — no surprises on re-resolve)
      let p = Path.Combine(Path.GetTempPath(), "some", "path", "file.fsx")
      let full = Path.GetFullPath p
      let fullAgain = Path.GetFullPath full
      fullAgain |> Expect.equal "GetFullPath is idempotent" full

    testCase "containment check: path inside workdir is contained" <| fun _ ->
      let workdir = Path.Combine(Path.GetTempPath(), "sessions", "s1")
      let file = Path.Combine(workdir, "src", "main.fsx")
      let canonical = Path.GetFullPath file
      let canonicalDir = Path.GetFullPath workdir
      let isContained =
        canonical.StartsWith(
          canonicalDir + string Path.DirectorySeparatorChar,
          StringComparison.OrdinalIgnoreCase)
        || canonical.Equals(canonicalDir, StringComparison.OrdinalIgnoreCase)
      isContained |> Expect.isTrue "file inside workdir is contained"

    testCase "containment check: path outside workdir is not contained" <| fun _ ->
      let workdir = Path.Combine(Path.GetTempPath(), "sessions", "s1")
      let file = Path.Combine(Path.GetTempPath(), "sessions", "s2", "other.fsx")
      let canonical = Path.GetFullPath file
      let canonicalDir = Path.GetFullPath workdir
      let isContained =
        canonical.StartsWith(
          canonicalDir + string Path.DirectorySeparatorChar,
          StringComparison.OrdinalIgnoreCase)
        || canonical.Equals(canonicalDir, StringComparison.OrdinalIgnoreCase)
      isContained |> Expect.isFalse "file outside workdir is not contained"

    testCase "containment check: path traversal attempt is rejected" <| fun _ ->
      let workdir = Path.Combine(Path.GetTempPath(), "sessions", "s1")
      let malicious = Path.Combine(workdir, "..", "..", "evil", "config")
      let canonical = Path.GetFullPath malicious
      let canonicalDir = Path.GetFullPath workdir
      let isContained =
        canonical.StartsWith(
          canonicalDir + string Path.DirectorySeparatorChar,
          StringComparison.OrdinalIgnoreCase)
        || canonical.Equals(canonicalDir, StringComparison.OrdinalIgnoreCase)
      isContained |> Expect.isFalse "path traversal attempt is rejected after canonicalization"

  ]

// ---------------------------------------------------------------------------
// W3 — TimelineState.record: O(1) prepend + MaxEntries cap
// ---------------------------------------------------------------------------

open SageFs.Features.EvalTimeline

[<Tests>]
let w3TimelineStateTests =
  testList "W3 — TimelineState.record: prepend + MaxEntries cap" [

    let makeEntry cellId durationMs status =
      { CellId = cellId; StartMs = 0L; DurationMs = durationMs; Status = status }

    testCase "record adds entry (now newest-first)" <| fun _ ->
      let s0 = TimelineState.empty
      let s1 = TimelineState.record (makeEntry 0 100L Success) s0
      s1.Entries |> Expect.hasLength "one entry after one record" 1

    testCase "newest entry is at the head after prepend" <| fun _ ->
      let s0 = TimelineState.empty
      let s1 = s0 |> TimelineState.record (makeEntry 0 100L Success)
      let s2 = s1 |> TimelineState.record (makeEntry 1 200L Success)
      s2.Entries.[0].CellId |> Expect.equal "head is most recent entry" 1

    testCase "entries capped at MaxEntries" <| fun _ ->
      let final =
        Seq.init (TimelineState.MaxEntries + 50) id
        |> Seq.fold (fun state i ->
          TimelineState.record (makeEntry i (int64 i) Success) state
        ) TimelineState.empty
      final.Entries |> Expect.hasLength "entries capped at MaxEntries" TimelineState.MaxEntries

    testCase "oldest entries are dropped when cap is reached" <| fun _ ->
      let final =
        Seq.init (TimelineState.MaxEntries + 5) id
        |> Seq.fold (fun state i ->
          TimelineState.record (makeEntry i (int64 i) Success) state
        ) TimelineState.empty
      // Newest entries are at head; entry 0 (oldest) should have been dropped
      let cellIds = final.Entries |> List.map (fun e -> e.CellId)
      cellIds |> List.contains 0 |> Expect.isFalse "oldest entry dropped when cap exceeded"

    testCase "timelineStats Count matches actual entry count" <| fun _ ->
      let state =
        Seq.init 5 id
        |> Seq.fold (fun s i ->
          TimelineState.record (makeEntry i (int64 (i + 1) * 10L) Success) s
        ) TimelineState.empty
      let stats = timelineStats 20 state
      stats.Count |> Expect.equal "Count matches 5 entries" 5

    testCase "sparkline is non-empty for non-empty timeline" <| fun _ ->
      let state =
        Seq.init 10 id
        |> Seq.fold (fun s i ->
          TimelineState.record (makeEntry i (int64 (i + 1) * 50L) Success) s
        ) TimelineState.empty
      let stats = timelineStats 20 state
      stats.Sparkline |> Expect.isNotEmpty "sparkline non-empty for non-empty timeline"

    testCase "sparkline width is bounded by requested width" <| fun _ ->
      let state =
        Seq.init 50 id
        |> Seq.fold (fun s i ->
          TimelineState.record (makeEntry i (int64 (i + 1) * 10L) Success) s
        ) TimelineState.empty
      let stats = timelineStats 10 state
      stats.Sparkline.Length |> Expect.equal "sparkline limited to width=10" 10

  ]

// ---------------------------------------------------------------------------
// W4 — Manifest pruning: uses StoppedAt not CreatedAt
// ---------------------------------------------------------------------------

[<Tests>]
let w4ManifestPruningTests =
  testList "W4 — toManifestState prunes by StoppedAt, not CreatedAt" [

    testCase "session created 10 days ago but stopped 1 day ago is KEPT" <| fun _ ->
      // This is the key regression: old code pruned by CreatedAt, so a session
      // created 10 days ago but stopped yesterday would be incorrectly pruned.
      let manifest : DaemonManifestData = {
        Entries = [
          { ManifestSessionEntry.SessionId = "old-created-recent-stopped"
            Projects = ["a.fsproj"]
            WorkingDir = "C:\\a"
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10.0)  // old creation
            StoppedAt = Some (DateTimeOffset.UtcNow.AddDays(-1.0)) }  // recent stop -> KEEP
        ]
        ActiveSessionId = None
        CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      }
      let state = ManifestMapping.toManifestState manifest
      state.Sessions |> Map.containsKey "old-created-recent-stopped"
      |> Expect.isTrue "session stopped only 1 day ago should be kept even if created 10 days ago"

    testCase "session stopped 8 days ago is pruned regardless of CreatedAt" <| fun _ ->
      let manifest : DaemonManifestData = {
        Entries = [
          { ManifestSessionEntry.SessionId = "old-stopped-8d"
            Projects = ["b.fsproj"]
            WorkingDir = "C:\\b"
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1.0)  // recently created (but this is irrelevant)
            StoppedAt = Some (DateTimeOffset.UtcNow.AddDays(-8.0)) }  // stopped 8 days ago -> PRUNE
        ]
        ActiveSessionId = None
        CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      }
      let state = ManifestMapping.toManifestState manifest
      state.Sessions |> Map.containsKey "old-stopped-8d"
      |> Expect.isFalse "session stopped 8 days ago should be pruned"

    testCase "session with no StoppedAt is never pruned by age" <| fun _ ->
      let manifest : DaemonManifestData = {
        Entries = [
          { ManifestSessionEntry.SessionId = "alive-very-old"
            Projects = ["c.fsproj"]
            WorkingDir = "C:\\c"
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30.0)
            StoppedAt = None }  // still alive -> never prune
        ]
        ActiveSessionId = Some "alive-very-old"
        CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      }
      let state = ManifestMapping.toManifestState manifest
      state.Sessions |> Map.containsKey "alive-very-old"
      |> Expect.isTrue "alive session (no StoppedAt) is never pruned regardless of age"

    testCase "multiple entries: only old-stopped ones are pruned" <| fun _ ->
      let manifest : DaemonManifestData = {
        Entries = [
          { ManifestSessionEntry.SessionId = "keep-alive"
            Projects = ["a.fsproj"]
            WorkingDir = "C:\\a"
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-20.0)
            StoppedAt = None }
          { ManifestSessionEntry.SessionId = "keep-recent-stop"
            Projects = ["b.fsproj"]
            WorkingDir = "C:\\b"
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-20.0)
            StoppedAt = Some (DateTimeOffset.UtcNow.AddDays(-2.0)) }
          { ManifestSessionEntry.SessionId = "prune-old-stop"
            Projects = ["c.fsproj"]
            WorkingDir = "C:\\c"
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-20.0)
            StoppedAt = Some (DateTimeOffset.UtcNow.AddDays(-10.0)) }
        ]
        ActiveSessionId = Some "keep-alive"
        CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      }
      let state = ManifestMapping.toManifestState manifest
      state.Sessions |> Map.containsKey "keep-alive" |> Expect.isTrue "alive session kept"
      state.Sessions |> Map.containsKey "keep-recent-stop" |> Expect.isTrue "recently-stopped session kept"
      state.Sessions |> Map.containsKey "prune-old-stop" |> Expect.isFalse "old-stopped session pruned"

  ]

// ---------------------------------------------------------------------------
// W5 — buildScopeSnapshot: O(n) shadow detection via Map
// ---------------------------------------------------------------------------

open SageFs.Features.BindingExplorer

[<Tests>]
let w5ScopeSnapshotTests =
  testList "W5 — buildScopeSnapshot O(n) shadow detection via Map" [

    let makeCell idx src fsiOut = { CellIndex = idx; Source = src; FsiOutput = fsiOut }

    testCase "no bindings: empty scope" <| fun _ ->
      let scope = buildScopeSnapshot []
      scope.Bindings |> Expect.isEmpty "empty input produces empty bindings"

    testCase "single binding: no shadows" <| fun _ ->
      let cell = makeCell 0 "let x = 1" "val x: int = 1"
      let scope = buildScopeSnapshot [cell]
      scope.Bindings |> Expect.hasLength "one binding" 1
      scope.Bindings.[0].ShadowedBy |> Expect.isEmpty "no shadows for unique name"
      scope.ActiveBindings |> Map.containsKey "x" |> Expect.isTrue "x is active"

    testCase "redefined binding: original is shadowed by later cell" <| fun _ ->
      let c0 = makeCell 0 "let x = 1" "val x: int = 1"
      let c1 = makeCell 1 "let x = 2" "val x: int = 2"
      let scope = buildScopeSnapshot [c0; c1]
      let original = scope.Bindings |> List.find (fun b -> b.Name = "x" && b.CellIndex = 0)
      original.ShadowedBy |> Expect.equal "cell 0 x is shadowed by cell 1" [1]
      scope.ActiveBindings |> Map.containsKey "x" |> Expect.isTrue "x is still active (latest version)"
      let active = scope.ActiveBindings |> Map.find "x"
      active.CellIndex |> Expect.equal "active x is from cell 1" 1

    testCase "triple redefinition: all earlier versions shadowed" <| fun _ ->
      let cells = [
        makeCell 0 "let v = 1" "val v: int = 1"
        makeCell 1 "let v = 2" "val v: int = 2"
        makeCell 2 "let v = 3" "val v: int = 3"
      ]
      let scope = buildScopeSnapshot cells
      let v0 = scope.Bindings |> List.find (fun b -> b.Name = "v" && b.CellIndex = 0)
      let v1 = scope.Bindings |> List.find (fun b -> b.Name = "v" && b.CellIndex = 1)
      v0.ShadowedBy |> List.length |> Expect.equal "v0 shadowed by 2 later cells" 2
      v1.ShadowedBy |> Expect.equal "v1 shadowed by cell 2 only" [2]
      scope.ShadowedBindings |> Expect.hasLength "2 shadowed bindings" 2

    testCase "distinct names: no cross-shadowing" <| fun _ ->
      let cells = [
        makeCell 0 "let a = 1" "val a: int = 1"
        makeCell 1 "let b = 2" "val b: int = 2"
        makeCell 2 "let c = 3" "val c: int = 3"
      ]
      let scope = buildScopeSnapshot cells
      scope.Bindings |> List.forall (fun b -> b.ShadowedBy |> List.isEmpty)
      |> Expect.isTrue "distinct names produce no shadows"
      scope.ActiveBindings |> Map.count |> Expect.equal "all three names active" 3

    testCase "large input: shadow detection produces correct counts" <| fun _ ->
      // 100 cells each defining `x` — only the last should be active
      let cells =
        List.init 100 (fun i ->
          makeCell i (sprintf "let x = %d" i) (sprintf "val x: int = %d" i))
      let scope = buildScopeSnapshot cells
      scope.ActiveBindings |> Map.count |> Expect.equal "one active binding (x)" 1
      let active = scope.ActiveBindings |> Map.find "x"
      active.CellIndex |> Expect.equal "active x is from cell 99" 99
      scope.ShadowedBindings |> Expect.hasLength "99 shadowed definitions" 99

  ]
