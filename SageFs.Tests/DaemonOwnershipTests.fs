module SageFs.Tests.DaemonOwnershipTests

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Threading
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.DaemonOwnership

// Spans the tests below reuse. The ttl parse tests spell their input and expected span out
// instead, because the span is the thing under test there.

/// One second: the shortest ttl a test uses, and the unit idle time is measured in.
let private second = TestTimeouts.clockTick
/// A ttl long enough that a second of idle time is nowhere near it.
let private longTtl = FixtureDurations.ttlLong
/// A daemon that has been idle for a very long time.
let private longIdle = FixtureDurations.idleVeryLong
/// A ttl the caller passed explicitly, distinct from the nested-checkout default.
let private explicitTtl = FixtureDurations.ttlExplicit
/// The ttl from the reported bug: far above the client-activity cap.
let private hugeTtl = FixtureDurations.ttlHuge
/// A ttl below the client-activity cap, so the window is the ttl itself.
let private underCapTtl = FixtureDurations.ttlUnderCap

/// Phase 0 item 2 (multi-agent vision §3.1, §3.3, §10 item 2): daemon
/// ownership, TTL, the daemon-info file, and `sagefs sweep`. Reaping is by
/// recorded owner liveness ONLY — never by process name, never by PPID.

// ─── Ownership.parse / tryParseTtl ────────────────────────────────────────

/// The shape of a duration `--ttl` accepts, written out independently of the parser: digits with
/// an optional fraction, then an optional unit. This is the oracle the properties check the
/// parser against.
let private durationShape =
  Regex(@"^\s*([0-9]+\.?[0-9]*|\.[0-9]+)[ \t]*(ms|s|m|h)?\s*$", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 500 }

/// Text drawn from the characters a mistyped duration is made of, so near misses ("30min",
/// "1,5h", "-5", "5 mm") come up far more often than they would from random strings.
let private genNearDuration : Gen<string> =
  Gen.elements (List.ofSeq "0123456789.,-+ eEmsMSHxX")
  |> Gen.listOf
  |> Gen.map (fun chars -> System.String(List.toArray chars))

let private genAnyText : Gen<string> =
  ArbMap.defaults
  |> ArbMap.generate<NonNull<string>>
  |> Gen.map (fun (NonNull text) -> text)

/// Each unit a ttl can carry (a bare number is seconds) and how many milliseconds one of it is.
let private ttlUnits : (string * int64) list =
  [ "ms", 1L; "s", 1000L; "m", 60000L; "h", 3600000L; "", 1000L ]

/// One token of a command line: the ownership flags and their usual values, other people's flags, and noise.
let private genCommandLineToken : Gen<string> =
  let flags = Gen.elements [ "--owner-pid"; "--owner-start"; "--ttl"; "--prune"; "--mcp-port" ]
  let values = Gen.elements [ "7"; "4242"; "30m"; "1h"; "500ms"; "0"; "-1"; "30min"; "x" ]
  Gen.oneof [ flags; values; genAnyText ]

let private namesFlag (flag: OwnershipFlag) (error: OwnershipArgError) : bool =
  (OwnershipArgError.describe error).Contains(OwnershipFlag.toString flag)

[<Tests>]
let parseTests = testList "Ownership.parse" [

  testCase "no flags is Unowned" <| fun _ ->
    Ownership.parse []
    |> Expect.equal "unowned" (Result.Ok Ownership.Unowned)

  testCase "--owner-pid alone is an unfenced owner" <| fun _ ->
    Ownership.parse ["--owner-pid"; "4242"]
    |> Expect.equal "owner pid" (Result.Ok (Ownership.OwnedByPid { Pid = 4242; Fence = OwnerFence.Unfenced }))

  testCase "--owner-pid with --owner-start is a fenced owner, in either order" <| fun _ ->
    let expected = Result.Ok (Ownership.OwnedByPid { Pid = 4242; Fence = OwnerFence.StartedAtTicks 638000000000000000L })
    Ownership.parse ["--owner-pid"; "4242"; "--owner-start"; "638000000000000000"]
    |> Expect.equal "pid then start" expected
    Ownership.parse ["--owner-start"; "638000000000000000"; "--owner-pid"; "4242"]
    |> Expect.equal "start then pid" expected

  testCase "--ttl 30m is an explicit 30-minute ttl" <| fun _ ->
    Ownership.parse ["--ttl"; "30m"]
    |> Expect.equal "ttl" (Result.Ok (Ownership.ExpiresAfter (FixtureDurations.parsedMinutes, TtlSource.Explicit)))

  testCase "all three together" <| fun _ ->
    Ownership.parse ["--owner-pid"; "10"; "--owner-start"; "20"; "--ttl"; "1h"]
    |> Expect.equal "parsed"
         (Result.Ok (Ownership.OwnedAndExpiring ({ Pid = 10; Fence = OwnerFence.StartedAtTicks 20L }, FixtureDurations.parsedHour)))

  testCase "unrelated flags are skipped without disturbing parsing" <| fun _ ->
    Ownership.parse ["--no-watch"; "--owner-pid"; "7"; "--prune"]
    |> Expect.equal "parsed" (Result.Ok (Ownership.OwnedByPid { Pid = 7; Fence = OwnerFence.Unfenced }))

  testCase "a repeated flag takes its last value" <| fun _ ->
    Ownership.parse ["--ttl"; "10m"; "--ttl"; "30m"]
    |> Expect.equal "the later --ttl wins" (Result.Ok (Ownership.ExpiresAfter (FixtureDurations.parsedMinutes, TtlSource.Explicit)))

  // Contract change: these used to be silently ignored, which left a daemon unowned and
  // immortal. They are now errors that name the flag (R-DOMAIN-1).

  testCase "a garbage --owner-pid is an Error naming --owner-pid, not an unowned daemon" <| fun _ ->
    Ownership.parse ["--owner-pid"; "not-a-pid"]
    |> Expect.equal "refused" (Result.Error [ OwnershipArgError.InvalidValue (OwnershipFlag.OwnerPid, "not-a-pid") ])

  testCase "--ttl 30min is an Error naming --ttl and showing --ttl 30m" <| fun _ ->
    match Ownership.parse ["--ttl"; "30min"] with
    | Result.Error [ error ] ->
      error |> Expect.equal "the bad value" (OwnershipArgError.InvalidValue (OwnershipFlag.Ttl, "30min"))
      OwnershipArgError.describe error |> Expect.stringContains "names the flag" "--ttl"
      OwnershipArgError.describe error |> Expect.stringContains "shows a valid one" "--ttl 30m"
    | other -> failtestf "expected one error, got %A" other

  testCase "a ttl that is negative, not finite or too large is an Error, and never throws" <| fun _ ->
    for raw in [ "-5"; "-1m"; "Infinity"; "NaN"; "1e3"; "99999999999h"; "1,5"; "ms"; "." ] do
      Ownership.parse ["--ttl"; raw]
      |> Expect.equal (sprintf "--ttl %s" raw) (Result.Error [ OwnershipArgError.InvalidValue (OwnershipFlag.Ttl, raw) ])

  testCase "--owner-pid must be a whole number above zero" <| fun _ ->
    for raw in [ "12x"; "0"; "-4"; "+4"; "4.5"; "99999999999"; "" ] do
      Ownership.parse ["--owner-pid"; raw]
      |> Expect.equal (sprintf "--owner-pid %s" raw) (Result.Error [ OwnershipArgError.InvalidValue (OwnershipFlag.OwnerPid, raw) ])

  testCase "--owner-start with no --owner-pid is an Error, not an owner-less fence" <| fun _ ->
    Ownership.parse ["--owner-start"; "123"]
    |> Expect.equal "refused" (Result.Error [ OwnershipArgError.OwnerStartWithoutOwnerPid ])

  testCase "a flag with no value is an Error, at the end of the line and before another flag" <| fun _ ->
    Ownership.parse ["--ttl"]
    |> Expect.equal "last argument" (Result.Error [ OwnershipArgError.MissingValue OwnershipFlag.Ttl ])
    Ownership.parse ["--ttl"; "--owner-pid"; "5"]
    |> Expect.equal "next argument is a flag" (Result.Error [ OwnershipArgError.MissingValue OwnershipFlag.Ttl ])

  testCase "every problem is reported, in command-line order" <| fun _ ->
    Ownership.parse ["--ttl"; "30min"; "--owner-pid"; "x"; "--owner-start"; "5"]
    |> Expect.equal "all of them"
         (Result.Error
            [ OwnershipArgError.InvalidValue (OwnershipFlag.Ttl, "30min")
              OwnershipArgError.InvalidValue (OwnershipFlag.OwnerPid, "x")
              OwnershipArgError.OwnerStartWithoutOwnerPid ])

  testCase "a bad repeat is still an Error even when an earlier one was fine" <| fun _ ->
    Ownership.parse ["--ttl"; "10m"; "--ttl"; "30min"]
    |> Expect.equal "refused" (Result.Error [ OwnershipArgError.InvalidValue (OwnershipFlag.Ttl, "30min") ])

  testCase "every error names its flag and says what a valid value looks like" <| fun _ ->
    for flag in OwnershipFlag.all do
      for error in [ OwnershipArgError.MissingValue flag; OwnershipArgError.InvalidValue (flag, "wrong") ] do
        let text = OwnershipArgError.describe error
        text |> Expect.stringContains "names the flag" (OwnershipFlag.toString flag)
        text |> Expect.stringContains "gives an example" (OwnershipFlag.example flag)
    OwnershipArgError.describe OwnershipArgError.OwnerStartWithoutOwnerPid
    |> Expect.stringContains "names the missing flag" "--owner-pid"

  testPropertyWithConfig cfg "any text that is not a duration is an Error naming --ttl" <|
    Prop.forAll (Arb.fromGen (Gen.oneof [ genNearDuration; genAnyText ])) (fun raw ->
      match Ownership.parse ["--ttl"; raw], durationShape.IsMatch raw with
      | Result.Ok _, false -> false
      | Result.Error errors, false ->
        errors |> List.exists (namesFlag OwnershipFlag.Ttl)
      | _, true -> true)

  testPropertyWithConfig cfg "a duration written as a number and a unit parses to exactly that long" <|
    Prop.forAll (Arb.fromGen (Gen.zip (Gen.choose (0, 100000)) (Gen.elements ttlUnits))) (fun (n, (suffix, millisPerUnit)) ->
      let expected = Result.Ok (Ownership.ExpiresAfter (TimeSpan.FromMilliseconds (float (int64 n * millisPerUnit)), TtlSource.Explicit))
      let actual = Ownership.parse [ "--ttl"; sprintf "%d%s" n suffix ]
      actual = expected)

  testPropertyWithConfig cfg "every Ok is a legal ownership: a real pid, a non-negative explicit ttl, never the nested default" <|
    Prop.forAll (Arb.fromGen (Gen.listOf genCommandLineToken)) (fun args ->
      match Ownership.parse args with
      | Result.Error errors -> not (List.isEmpty errors)
      | Result.Ok ownership ->
        let ownerIsLegal =
          match Ownership.owner ownership with
          | None -> true
          | Some owner ->
            owner.Pid > 0
            && (match owner.Fence with
                | OwnerFence.Unfenced -> true
                | OwnerFence.StartedAtTicks ticks -> ticks > 0L)
        let ttlIsLegal =
          match ownership with
          | Ownership.ExpiresAfter (ttl, source) -> ttl >= TimeSpan.Zero && source = TtlSource.Explicit
          | Ownership.OwnedAndExpiring (_, ttl) -> ttl >= TimeSpan.Zero
          | Ownership.Unowned
          | Ownership.OwnedByPid _ -> true
        ownerIsLegal && ttlIsLegal)
]

[<Tests>]
let tryParseTtlTests = testList "Ownership.tryParseTtl" [

  testCase "milliseconds" <| fun _ ->
    Ownership.tryParseTtl "500ms"
    |> Expect.equal "500ms" (Some FixtureDurations.parsedMillis)

  testCase "seconds" <| fun _ ->
    Ownership.tryParseTtl "1s"
    |> Expect.equal "1s" (Some TestTimeouts.clockTick)

  testCase "minutes" <| fun _ ->
    Ownership.tryParseTtl "30m"
    |> Expect.equal "30m" (Some FixtureDurations.parsedMinutes)

  testCase "hours" <| fun _ ->
    Ownership.tryParseTtl "2h"
    |> Expect.equal "2h" (Some FixtureDurations.parsedHours)

  testCase "bare number defaults to seconds" <| fun _ ->
    Ownership.tryParseTtl "90"
    |> Expect.equal "90" (Some FixtureDurations.parsedBare)

  testCase "garbage is None" <| fun _ ->
    Ownership.tryParseTtl "banana"
    |> Expect.equal "garbage" None

  testCase "empty is None" <| fun _ ->
    Ownership.tryParseTtl ""
    |> Expect.equal "empty" None
]

/// What `sagefs` does with the flags, one layer up from the parser.
[<Tests>]
let startupResolutionTests = testList "Program.resolveOwnership" [

  testCase "a bad --ttl is refused with a message naming --ttl, so the daemon never starts" <| fun _ ->
    match Program.resolveOwnership [| "--ttl"; "30min" |] (Path.GetTempPath()) with
    | Result.Error errors ->
      Program.describeOwnershipRefusal errors |> Expect.stringContains "names the flag" "--ttl 30min"
      Program.startupRefusedExitCode |> Expect.notEqual "a refusal is not success" 0
    | Result.Ok ownership -> failtestf "a daemon with --ttl 30min must not start, got %A" ownership

  testCase "a good --ttl is passed through as an explicit ttl, never the nested default" <| fun _ ->
    Program.resolveOwnership [| "--ttl"; "30m" |] (Path.GetTempPath())
    |> Expect.equal "explicit" (Result.Ok (Ownership.ExpiresAfter (FixtureDurations.parsedMinutes, TtlSource.Explicit)))
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

  testCase "neither flag, nested checkout -> defaults the ttl and says it was the default" <| fun _ ->
    applyNestedCheckoutDefault true Ownership.Unowned
    |> Expect.equal "defaulted" (Ownership.ExpiresAfter (defaultTtlForNestedCheckout, TtlSource.NestedCheckoutDefault))

  testCase "not nested -> no default applied" <| fun _ ->
    applyNestedCheckoutDefault false Ownership.Unowned
    |> Expect.equal "still unowned" Ownership.Unowned

  testCase "nested but --owner-pid given -> no default applied" <| fun _ ->
    let owned = Ownership.OwnedByPid { Pid = 123; Fence = OwnerFence.Unfenced }
    applyNestedCheckoutDefault true owned
    |> Expect.equal "an explicit owner means no default ttl" owned

  testCase "nested but --ttl already given -> the explicit ttl wins and stays Explicit" <| fun _ ->
    let explicit = Ownership.ExpiresAfter (explicitTtl, TtlSource.Explicit)
    applyNestedCheckoutDefault true explicit
    |> Expect.equal "an explicit ttl is not a default" explicit
]

// ─── TTL idle-check ────────────────────────────────────────────────────────

[<Tests>]
let shouldSelfTerminateTests = testList "shouldSelfTerminate" [

  // "a daemon with --ttl 1s and no clients exits" (§10 item 2's RED test).
  testCase "idle past the ttl with no sessions and no clients -> true" <| fun _ ->
    let now = DateTime.UtcNow
    let ttl = second
    let lastActive = now - (second + second)
    shouldSelfTerminate now ttl lastActive false false
    |> Expect.isTrue "idle well past a 1s ttl with nobody around should self-terminate"

  testCase "not yet past the ttl -> false" <| fun _ ->
    let now = DateTime.UtcNow
    let ttl = longTtl
    let lastActive = now - second
    shouldSelfTerminate now ttl lastActive false false
    |> Expect.isFalse "well within the ttl window"

  testCase "has live sessions -> never terminates regardless of elapsed time" <| fun _ ->
    let now = DateTime.UtcNow
    let ttl = second
    let lastActive = now - longIdle
    shouldSelfTerminate now ttl lastActive true false
    |> Expect.isFalse "a live session must block self-termination"

  testCase "has clients -> never terminates regardless of elapsed time" <| fun _ ->
    let now = DateTime.UtcNow
    let ttl = second
    let lastActive = now - longIdle
    shouldSelfTerminate now ttl lastActive false true
    |> Expect.isFalse "a connected client must block self-termination"

  testCase "exactly at the ttl boundary -> true (>=)" <| fun _ ->
    let now = DateTime.UtcNow
    let ttl = FixtureDurations.ttlBoundary
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
    ttlClientActivityWindow hugeTtl
    |> Expect.equal "capped, never the raw (bug-reproducing) ttl" SageFs.Timeouts.agentActivityFresh

  testCase "WHY — a short ttl (30s) is never inflated past the ttl itself" <| fun _ ->
    ttlClientActivityWindow underCapTtl
    |> Expect.equal "capped at the ttl, not the 2-minute default" underCapTtl

  testCase "WHY — the window can never exceed the ttl it serves" <| fun _ ->
    FixtureDurations.ttlsAcrossTheRange
    |> List.forall (fun ttl -> ttlClientActivityWindow ttl <= ttl)
    |> Expect.isTrue "window <= ttl for every ttl, however large"

  testCase "the bug itself: the raw ttl would NOT equal the fixed window for a large ttl" <| fun _ ->
    // Proves this test has teeth: the pre-fix code (window = ttl) would fail
    // this exact assertion for any ttl above the 2-minute cap.
    ttlClientActivityWindow hugeTtl
    |> Expect.notEqual "a broken 'window = ttl' implementation would equal 60m here" hugeTtl
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
    SageFs.WorkerProtocol.SessionLifecycleStatus.Faulted (SageFs.WorkerProtocol.FaultReason.Reported "worker exited")
    |> isUsableSessionStatus
    |> Expect.isFalse "a faulted, permanently-dead session must not count as live"

  testCase "WHY — a Faulted session with no reason is also NOT usable" <| fun _ ->
    SageFs.WorkerProtocol.SessionLifecycleStatus.Faulted (SageFs.WorkerProtocol.FaultReason.Unexplained SageFs.WorkerProtocol.FaultOrigin.NotRecorded)
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
    SageFs.WorkerProtocol.SessionLifecycleStatus.Restarting (SageFs.WorkerProtocol.PreviousWorker.Was 1)
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
// Ownership, the same mechanism proven generically in
// OwnerMonitorTests.fs — this test exercises it framed at the daemon layer.

[<Tests>]
let daemonOwnerWatchdogTests = testList "daemon owner watchdog (OwnerMonitor.run over Ownership)" [

  testTask "a daemon whose owner dies exits within the poll interval" {
    let cts = new CancellationTokenSource()
    try
      let daemonOwner =
        match Ownership.parse [ "--owner-pid"; "999999" ] |> Result.map (applyNestedCheckoutDefault false) with
        | Result.Ok ownership -> Ownership.owner ownership |> Option.get
        | Result.Error errors -> failtestf "--owner-pid 999999 must parse, got %A" errors
      let owner : SageFs.OwnerMonitor.Owner =
        { Pid = daemonOwner.Pid; StartTimeTicks = OwnerFence.toTicks daemonOwner.Fence }
      let monitor = SageFs.OwnerMonitor.run (fun _ -> None) owner cts ignore
      let running = monitor |> Async.StartAsTask
      let! _ = Tasks.Task.WhenAny(running :> Tasks.Task, Tasks.Task.Delay TestTimeouts.monitorNotice)
      cts.IsCancellationRequested
      |> Expect.isTrue "the daemon's own cts should be cancelled once its owner is gone"
    finally
      cts.Dispose()
  }
]
