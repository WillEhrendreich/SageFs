module SageFs.Tests.DaemonOwnershipTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open SageFs.DaemonOwnership

/// Phase 0 item 2 (multi-agent vision §3.1, §3.3, §10 item 2): daemon
/// ownership, TTL, the daemon-info file, and `sagefs sweep`. Reaping is by
/// recorded owner liveness ONLY — never by process name, never by PPID.

// ─── OwnershipArgs.parse / tryParseTtl ────────────────────────────────────

[<Tests>]
let parseTests = testList "OwnershipArgs.parse" [

  testCase "no flags is empty" <| fun _ ->
    OwnershipArgs.parse []
    |> Expect.equal "empty" OwnershipArgs.empty

  testCase "--owner-pid sets OwnerPid" <| fun _ ->
    (OwnershipArgs.parse ["--owner-pid"; "4242"]).OwnerPid
    |> Expect.equal "owner pid" (Some 4242)

  testCase "--owner-start sets OwnerStartTicks" <| fun _ ->
    (OwnershipArgs.parse ["--owner-start"; "638000000000000000"]).OwnerStartTicks
    |> Expect.equal "owner start ticks" (Some 638000000000000000L)

  testCase "garbage --owner-pid value is ignored" <| fun _ ->
    (OwnershipArgs.parse ["--owner-pid"; "not-a-pid"]).OwnerPid
    |> Expect.equal "owner pid" None

  testCase "--ttl 30m sets a 30-minute Ttl" <| fun _ ->
    (OwnershipArgs.parse ["--ttl"; "30m"]).Ttl
    |> Expect.equal "ttl" (Some (TimeSpan.FromMinutes 30.0))

  testCase "all three together" <| fun _ ->
    let parsed = OwnershipArgs.parse ["--owner-pid"; "10"; "--owner-start"; "20"; "--ttl"; "1h"]
    parsed
    |> Expect.equal "parsed" { OwnerPid = Some 10; OwnerStartTicks = Some 20L; Ttl = Some (TimeSpan.FromHours 1.0) }

  testCase "unrelated flags are skipped without disturbing parsing" <| fun _ ->
    OwnershipArgs.parse ["--no-watch"; "--owner-pid"; "7"; "--prune"]
    |> Expect.equal "parsed" { OwnershipArgs.empty with OwnerPid = Some 7 }
]

[<Tests>]
let tryParseTtlTests = testList "OwnershipArgs.tryParseTtl" [

  testCase "milliseconds" <| fun _ ->
    OwnershipArgs.tryParseTtl "500ms"
    |> Expect.equal "500ms" (Some (TimeSpan.FromMilliseconds 500.0))

  testCase "seconds" <| fun _ ->
    OwnershipArgs.tryParseTtl "1s"
    |> Expect.equal "1s" (Some (TimeSpan.FromSeconds 1.0))

  testCase "minutes" <| fun _ ->
    OwnershipArgs.tryParseTtl "30m"
    |> Expect.equal "30m" (Some (TimeSpan.FromMinutes 30.0))

  testCase "hours" <| fun _ ->
    OwnershipArgs.tryParseTtl "2h"
    |> Expect.equal "2h" (Some (TimeSpan.FromHours 2.0))

  testCase "bare number defaults to seconds" <| fun _ ->
    OwnershipArgs.tryParseTtl "90"
    |> Expect.equal "90" (Some (TimeSpan.FromSeconds 90.0))

  testCase "garbage is None" <| fun _ ->
    OwnershipArgs.tryParseTtl "banana"
    |> Expect.equal "garbage" None

  testCase "empty is None" <| fun _ ->
    OwnershipArgs.tryParseTtl ""
    |> Expect.equal "empty" None
]

// ─── Nested-checkout default ─────────────────────────────────────────────

[<Tests>]
let nestedCheckoutTests = testList "isNestedCheckout / applyNestedCheckoutDefault" [

  testCase "a directory with no ancestor marker is not nested" <| fun _ ->
    isNestedCheckout (fun _ -> false) "/some/where"
    |> Expect.isFalse "no markers anywhere means not nested"

  testCase "an ancestor marker (not the dir's own) makes it nested" <| fun _ ->
    let root = Path.GetFullPath "/repo"
    let dir = Path.Combine(root, ".claude", "worktrees", "agent-x")
    let hasMarker (d: string) = String.Equals(d.TrimEnd('/'), root.TrimEnd('/'), StringComparison.Ordinal)
    isNestedCheckout hasMarker dir
    |> Expect.isTrue "an ancestor above dir has the marker"

  testCase "the directory's own marker does not count" <| fun _ ->
    let dir = Path.GetFullPath "/repo/worktree-x"
    // Only the dir itself has a marker — its parent tree does not.
    let hasMarker (d: string) = String.Equals(Path.GetFullPath d, dir, StringComparison.Ordinal)
    isNestedCheckout hasMarker dir
    |> Expect.isFalse "a checkout's own marker must not make it nested-inside-itself"

  testCase "neither flag, nested checkout -> defaults Ttl and flags DefaultedTtl" <| fun _ ->
    let effective = applyNestedCheckoutDefault true OwnershipArgs.empty
    effective.Ttl |> Expect.equal "ttl" (Some defaultTtlForNestedCheckout)
    effective.DefaultedTtl |> Expect.isTrue "should mark defaulted"

  testCase "not nested -> no default applied" <| fun _ ->
    let effective = applyNestedCheckoutDefault false OwnershipArgs.empty
    effective.Ttl |> Expect.equal "ttl" None
    effective.DefaultedTtl |> Expect.isFalse "should not mark defaulted"

  testCase "nested but --owner-pid given -> no default applied" <| fun _ ->
    let args = { OwnershipArgs.empty with OwnerPid = Some 123 }
    let effective = applyNestedCheckoutDefault true args
    effective.Ttl |> Expect.equal "ttl" None
    effective.DefaultedTtl |> Expect.isFalse "an explicit owner means no default TTL"

  testCase "nested but --ttl already given -> the explicit Ttl wins, not defaulted" <| fun _ ->
    let args = { OwnershipArgs.empty with Ttl = Some (TimeSpan.FromMinutes 5.0) }
    let effective = applyNestedCheckoutDefault true args
    effective.Ttl |> Expect.equal "ttl" (Some (TimeSpan.FromMinutes 5.0))
    effective.DefaultedTtl |> Expect.isFalse "an explicit ttl is not a default"
]

// ─── TTL idle-check ────────────────────────────────────────────────────────

[<Tests>]
let shouldSelfTerminateTests = testList "shouldSelfTerminate" [

  // "a daemon with --ttl 1s and no clients exits" (§10 item 2's RED test).
  testCase "idle past the ttl with no sessions and no clients -> true" <| fun _ ->
    let now = DateTime.UtcNow
    let ttl = TimeSpan.FromSeconds 1.0
    let lastActive = now - TimeSpan.FromSeconds 2.0
    shouldSelfTerminate now ttl lastActive false false
    |> Expect.isTrue "idle well past a 1s ttl with nobody around should self-terminate"

  testCase "not yet past the ttl -> false" <| fun _ ->
    let now = DateTime.UtcNow
    let ttl = TimeSpan.FromMinutes 30.0
    let lastActive = now - TimeSpan.FromSeconds 1.0
    shouldSelfTerminate now ttl lastActive false false
    |> Expect.isFalse "well within the ttl window"

  testCase "has live sessions -> never terminates regardless of elapsed time" <| fun _ ->
    let now = DateTime.UtcNow
    let ttl = TimeSpan.FromSeconds 1.0
    let lastActive = now - TimeSpan.FromDays 1.0
    shouldSelfTerminate now ttl lastActive true false
    |> Expect.isFalse "a live session must block self-termination"

  testCase "has clients -> never terminates regardless of elapsed time" <| fun _ ->
    let now = DateTime.UtcNow
    let ttl = TimeSpan.FromSeconds 1.0
    let lastActive = now - TimeSpan.FromDays 1.0
    shouldSelfTerminate now ttl lastActive false true
    |> Expect.isFalse "a connected client must block self-termination"

  testCase "exactly at the ttl boundary -> true (>=)" <| fun _ ->
    let now = DateTime.UtcNow
    let ttl = TimeSpan.FromSeconds 5.0
    let lastActive = now - ttl
    shouldSelfTerminate now ttl lastActive false false
    |> Expect.isTrue "the boundary itself counts as past the ttl"
]

// ─── ttlClientActivityWindow ───────────────────────────────────────────────
// Bug: the daemon's TTL idle-check used to pass `ttl` itself as the
// "is a client still around" freshness window (SageFs/DaemonMode.fs's
// ttlCallback), so a single MCP call stayed "fresh" for a full extra `ttl`
// and kept re-stamping the idle clock on every tick it did — a daemon with
// ANY past activity effectively never went idle. These tests pin the fixed
// window: a small constant, never the raw ttl.

[<Tests>]
let ttlClientActivityWindowTests = testList "ttlClientActivityWindow" [

  testCase "WHY — a long ttl (60m, the reported bug's exact value) is capped at 2 minutes, not reused whole" <| fun _ ->
    ttlClientActivityWindow (TimeSpan.FromMinutes 60.0)
    |> Expect.equal "capped, never the raw (bug-reproducing) ttl" (TimeSpan.FromMinutes 2.0)

  testCase "WHY — a short ttl (30s) is never inflated past the ttl itself" <| fun _ ->
    ttlClientActivityWindow (TimeSpan.FromSeconds 30.0)
    |> Expect.equal "capped at the ttl, not the 2-minute default" (TimeSpan.FromSeconds 30.0)

  testCase "WHY — the window can never exceed the ttl it serves" <| fun _ ->
    [ TimeSpan.FromSeconds 1.0; TimeSpan.FromMinutes 1.0; TimeSpan.FromMinutes 2.0
      TimeSpan.FromMinutes 5.0; TimeSpan.FromHours 1.0; TimeSpan.FromHours 24.0 ]
    |> List.forall (fun ttl -> ttlClientActivityWindow ttl <= ttl)
    |> Expect.isTrue "window <= ttl for every ttl, however large"

  testCase "the bug itself: the raw ttl would NOT equal the fixed window for a large ttl" <| fun _ ->
    // Proves this test has teeth: the pre-fix code (window = ttl) would fail
    // this exact assertion for any ttl above the 2-minute cap.
    ttlClientActivityWindow (TimeSpan.FromMinutes 60.0)
    |> Expect.notEqual "a broken 'window = ttl' implementation would equal 60m here" (TimeSpan.FromMinutes 60.0)
]

// ─── isUsableSessionStatus ──────────────────────────────────────────────────
// Bug: the daemon's TTL idle-check counted ANY registered session as "live"
// (SessionManager.QuerySnapshot.allSessions |> List.isEmpty |> not), including
// a `Faulted` tombstone SessionManager deliberately keeps registered after a
// worker crashes and exhausts its restart budget. One permanently-dead
// session then blocked a TTL-governed daemon from EVER going idle.

[<Tests>]
let isUsableSessionStatusTests = testList "isUsableSessionStatus" [

  testCase "WHY — a Faulted session is NOT usable (the exact bug: a dead tombstone must not block TTL)" <| fun _ ->
    SageFs.WorkerProtocol.SessionLifecycleStatus.Faulted (Some "worker exited")
    |> isUsableSessionStatus
    |> Expect.isFalse "a faulted, permanently-dead session must not count as live"

  testCase "WHY — a Faulted session with no reason is also NOT usable" <| fun _ ->
    SageFs.WorkerProtocol.SessionLifecycleStatus.Faulted None
    |> isUsableSessionStatus
    |> Expect.isFalse "faulted is faulted regardless of whether a reason was recorded"

  testCase "a Stopped session is NOT usable" <| fun _ ->
    SageFs.WorkerProtocol.SessionLifecycleStatus.Stopped
    |> isUsableSessionStatus
    |> Expect.isFalse "a stopped session is not live"

  testCase "a Ready session IS usable" <| fun _ ->
    SageFs.WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1; Port = Some 1234 }
    |> isUsableSessionStatus
    |> Expect.isTrue "a ready worker is genuinely live"

  testCase "a Starting session IS usable" <| fun _ ->
    SageFs.WorkerProtocol.SessionLifecycleStatus.Starting { Pid = 1; Port = None }
    |> isUsableSessionStatus
    |> Expect.isTrue "a session still starting up is not yet dead"

  testCase "an Evaluating session IS usable" <| fun _ ->
    SageFs.WorkerProtocol.SessionLifecycleStatus.Evaluating { Pid = 1; Port = Some 1234 }
    |> isUsableSessionStatus
    |> Expect.isTrue "actively evaluating is definitely live"

  testCase "a Restarting session IS usable" <| fun _ ->
    SageFs.WorkerProtocol.SessionLifecycleStatus.Restarting (Some 1)
    |> isUsableSessionStatus
    |> Expect.isTrue "a restart in flight is not a dead end"
]

// ─── DaemonInfoFile ────────────────────────────────────────────────────────

[<Tests>]
let daemonInfoFileTests = testList "DaemonInfoFile" [

  testCase "write then tryRead roundtrips" <| fun _ ->
    let dir = Path.Combine(Path.GetTempPath(), "sagefs-ownership-test", Guid.NewGuid().ToString("N"))
    try
      let info : DaemonInfoFile =
        { Pid = 4242
          StartTime = DateTime.UtcNow
          OwnerPid = Some 99
          OwnerStart = Some 12345L
          McpPort = 37749
          DashboardPort = 37750
          DataDir = dir }
      DaemonInfoFile.write dir info
      match DaemonInfoFile.tryRead dir with
      | None -> failtest "expected to read back the written daemon-info file"
      | Some read ->
        read.Pid |> Expect.equal "pid" info.Pid
        read.OwnerPid |> Expect.equal "owner pid" info.OwnerPid
        read.OwnerStart |> Expect.equal "owner start" info.OwnerStart
        read.McpPort |> Expect.equal "mcp port" info.McpPort
    finally
      try Directory.Delete(dir, true) with _ -> ()

  testCase "tryRead on a missing file is None" <| fun _ ->
    let dir = Path.Combine(Path.GetTempPath(), "sagefs-ownership-test", Guid.NewGuid().ToString("N"))
    DaemonInfoFile.tryRead dir
    |> Expect.equal "missing" None

  testCase "delete removes the file and is idempotent" <| fun _ ->
    let dir = Path.Combine(Path.GetTempPath(), "sagefs-ownership-test", Guid.NewGuid().ToString("N"))
    try
      let info : DaemonInfoFile =
        { Pid = 1; StartTime = DateTime.UtcNow; OwnerPid = None; OwnerStart = None
          McpPort = 1; DashboardPort = 2; DataDir = dir }
      DaemonInfoFile.write dir info
      DaemonInfoFile.delete dir
      DaemonInfoFile.tryRead dir |> Expect.equal "gone" None
      // Second delete must not throw.
      DaemonInfoFile.delete dir
    finally
      try Directory.Delete(dir, true) with _ -> ()
]

// ─── sweep ──────────────────────────────────────────────────────────────

let private mkInfo ownerPid ownerStart : DaemonInfoFile =
  { Pid = 1
    StartTime = DateTime.UtcNow
    OwnerPid = ownerPid
    OwnerStart = ownerStart
    McpPort = 1
    DashboardPort = 2
    DataDir = "/tmp/does-not-matter" }

[<Tests>]
let decideSweepTests = testList "decideSweep" [

  // "sweep never kills a daemon whose owner is alive" (§10 item 2's RED test).
  testCase "owner alive -> Leave" <| fun _ ->
    decideSweep (fun _ _ -> true) (mkInfo (Some 42) None)
    |> function
       | SweepVerdict.Leave _ -> ()
       | SweepVerdict.Reap _ -> failtest "must never reap a daemon whose owner is alive"

  testCase "owner dead -> Reap" <| fun _ ->
    decideSweep (fun _ _ -> false) (mkInfo (Some 42) None)
    |> function
       | SweepVerdict.Reap _ -> ()
       | SweepVerdict.Leave _ -> failtest "a daemon whose recorded owner is gone should be reapable"

  testCase "no recorded owner -> Leave, regardless of the liveness function" <| fun _ ->
    decideSweep (fun _ _ -> failwith "must not even be called") (mkInfo None None)
    |> function
       | SweepVerdict.Leave _ -> ()
       | SweepVerdict.Reap _ -> failtest "a daemon with no recorded owner (TTL-only or interactive) must never be touched"
]

[<Tests>]
let sweepIntegrationTests = testList "sweep (registry enumeration)" [

  testCase "sweep finds the primary daemon-info and every spawned registration" <| fun _ ->
    let realDir = Path.Combine(Path.GetTempPath(), "sagefs-ownership-test", Guid.NewGuid().ToString("N"))
    try
      let primary = mkInfo (Some 1) None |> fun i -> { i with Pid = 100 }
      DaemonInfoFile.write realDir primary
      let spawnedAlive = mkInfo (Some 2) None |> fun i -> { i with Pid = 200 }
      let spawnedDead = mkInfo (Some 3) None |> fun i -> { i with Pid = 300 }
      registerSpawned realDir spawnedAlive
      registerSpawned realDir spawnedDead
      let isOwnerAlive pid _ = pid <> 3
      let results = sweep isOwnerAlive realDir
      let verdictFor pid =
        results |> List.tryFind (fun (_, info, _) -> info.Pid = pid) |> Option.map (fun (_, _, v) -> v)
      match verdictFor 100 with
      | Some (SweepVerdict.Leave _) -> ()
      | other -> failtestf "primary (owner alive) expected Leave, got %A" other
      match verdictFor 200 with
      | Some (SweepVerdict.Leave _) -> ()
      | other -> failtestf "spawned-alive expected Leave, got %A" other
      match verdictFor 300 with
      | Some (SweepVerdict.Reap _) -> ()
      | other -> failtestf "spawned-dead expected Reap, got %A" other
    finally
      try Directory.Delete(realDir, true) with _ -> ()

  testCase "sweep on an empty registry finds nothing" <| fun _ ->
    let realDir = Path.Combine(Path.GetTempPath(), "sagefs-ownership-test", Guid.NewGuid().ToString("N"))
    sweep (fun _ _ -> true) realDir
    |> Expect.isEmpty "no daemon-info anywhere"

  testCase "unregisterSpawned removes exactly its own entry" <| fun _ ->
    let realDir = Path.Combine(Path.GetTempPath(), "sagefs-ownership-test", Guid.NewGuid().ToString("N"))
    try
      let a = mkInfo (Some 1) None |> fun i -> { i with Pid = 400 }
      let b = mkInfo (Some 1) None |> fun i -> { i with Pid = 401 }
      registerSpawned realDir a
      registerSpawned realDir b
      unregisterSpawned realDir 400
      let remaining = sweep (fun _ _ -> true) realDir |> List.map (fun (_, info, _) -> info.Pid) |> Set.ofList
      remaining |> Expect.equal "only 401 left" (Set.ofList [401])
    finally
      try Directory.Delete(realDir, true) with _ -> ()
]

// ─── Daemon-side owner watchdog exits within the poll interval ──────────
// (§10 item 2's "a daemon whose owner dies exits within the poll interval").
// The daemon uses exactly SageFs.OwnerMonitor.run with an Owner built from
// EffectiveOwnership, the same mechanism proven generically in
// OwnerMonitorTests.fs — this test exercises it framed at the daemon layer.

[<Tests>]
let daemonOwnerWatchdogTests = testList "daemon owner watchdog (OwnerMonitor.run over EffectiveOwnership)" [

  testTask "a daemon whose owner dies exits within the poll interval" {
    let cts = new CancellationTokenSource()
    try
      let effective = applyNestedCheckoutDefault false { OwnershipArgs.empty with OwnerPid = Some 999999 }
      let owner : SageFs.OwnerMonitor.Owner =
        { Pid = effective.OwnerPid |> Option.get; StartTimeTicks = effective.OwnerStartTicks }
      let monitor = SageFs.OwnerMonitor.run (fun _ -> None) owner cts ignore
      let running = monitor |> Async.StartAsTask
      let! _ = Tasks.Task.WhenAny(running :> Tasks.Task, Tasks.Task.Delay(SageFs.OwnerMonitor.pollIntervalMs * 3))
      cts.IsCancellationRequested
      |> Expect.isTrue "the daemon's own cts should be cancelled once its owner is gone"
    finally
      cts.Dispose()
  }
]
