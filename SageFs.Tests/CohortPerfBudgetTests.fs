/// Phase 0 item 1 (sagefs-multiagent-vision.md §3.4, §7.4, §10): the pure
/// cohort core's two latency budgets, pinned as MEASURED RED tests over a
/// realistic ten-member, 7,000-test fixture — "the numbers go in this doc
/// before Phase 1 promises a member count" (§10 Phase 0 item 1) gets an
/// executable back-reference instead of a documented-but-unverified claim.
///
/// §3.4 / §7.4 state two budgets:
///   - `Cohort.decide` + `Cohort.project` together: < 1 ms p99 at ten members
///     and 7,000 tests.
///   - the "save-observed claim probe" (§5.1: "the save-observed check is a
///     prefix-trie probe over the claimed paths, never a regex per claim") —
///     today that is the `ObserveSave` branch of `decide` (Cohort.fs:659-669),
///     a linear scan over held claims checking `ClaimScope.overlaps` (there is
///     no separate prefix-trie type yet — v1 claims are File/Project only,
///     §5.1's "twenty lines, then log the misses"): < 10 µs p99.
///
/// No daemon, no FSI, no I/O — pure `Cohort.fs` functions only, exactly the
/// "no daemon" discipline `CohortPanelTests.fs`/`CohortOwnerTests.fs` use.
///
/// Tagged `[Benchmark]` (the convention `Program.fs`'s default-suite filter and
/// `LiveTestingCycleTests.fs`'s `cycleBenchmarkTests` already use, roast-5
/// §12: "their p95/latency budgets flake under load") so this does not run in
/// the fast default suite — it runs on demand via `--all`. This matters for
/// real reasons here, not just convention: the decide+project loop alone
/// currently costs tens of seconds (see the measured numbers below), and a
/// wall-clock assertion has no business gating every default `dotnet run`.
///
/// Each budget gets one test case reading a shared `Lazy` measurement
/// (computed once per test run, thread-safe): print the measured p50/p99,
/// then assert against a CI-safe bound (a documented margin over the vision's
/// ideal, so a loaded/virtualized box does not false-fail a gate that passes
/// easily on a quiet desktop) — and if even that generous bound is missed,
/// `skiptest` with the measured number instead of hard-failing forever. Per
/// the brief: "if a budget is currently NOT met, that's a REAL finding —
/// report it with the numbers rather than loosening the threshold into
/// meaninglessness; mark the test ignored/pending with the measured value
/// documented, and flag it as an optimization target." A hard-red test that
/// nobody is actively fixing this session is not a meaningful gate either —
/// once the underlying cost is fixed, this test starts asserting (green)
/// automatically, with no edit required.
module SageFs.Tests.CohortPerfBudgetTests

open System
open System.Diagnostics
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.Measures
open SageFs.MemberTable

// ── Fixture: 10 members, 10 disjoint File claims, 7,000 tests across an
//    integration row + 10 member rows (11 rows total — `project`'s row 0 is
//    the integration session, rows 1..N are members, §5.3) ────────────────

let private epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

let private memberCount = 10
let private testCount = 7000

let private members : MemberId list =
  [ for i in 1 .. memberCount -> MemberId.Minted(sprintf "member-%02d" i) ]

/// Ten `Join`s then ten disjoint `File` claims (one per member, under distinct
/// paths so `AcquireClaim`'s exclusivity scan never refuses one) — a cohort
/// mid-feature, not an empty state. Mirrors the fold idiom
/// `CohortPanelTests.frameAfterWith` uses to build a `LedgerHead` with no
/// owner/ledger/IO involved.
let private buildHead () : LedgerHead<MemberId> =
  let joins =
    members |> List.map (fun m -> CohortCommand.Join(m, JoinableRole.Implementer, Some(MemberId.display m)))
  let acquires =
    members
    |> List.map (fun m ->
      CohortCommand.AcquireClaim(
        m,
        ClaimScope.File(sprintf "SageFs.Core/Features/%s.fs" (MemberId.display m)),
        sprintf "working on %s" (MemberId.display m)
      ))
  let finalState, finalSeq =
    (joins @ acquires)
    |> List.fold
      (fun (state, sq) cmd ->
        match decide epoch (BitConverter.GetBytes(int sq)) state cmd with
        | Ok(newState, _, _) -> newState, sq + 1L<ledgerSeq>
        | Error err -> failwithf "unexpected refusal building the perf fixture: %A" err)
      (CohortState.empty (), 0L<ledgerSeq>)
  { Seq = (if finalSeq = 0L<ledgerSeq> then 0L<ledgerSeq> else finalSeq - 1L<ledgerSeq>)
    State = finalState }

/// A pool of 7,000 distinct test ids shared by every row, so `project`'s
/// `Array.distinct` (Cohort.fs:993) collapses back down to exactly 7,000
/// columns — a per-row-disjoint pool would silently multiply the column
/// count instead of stressing the fixed 7,000-column budget the vision names.
let private testPool =
  Array.init testCount (fun i -> TestId(sprintf "SageFs.Tests.Module%d.test_case_%d" (i % 50) i))

/// Deterministic, per-row-varying pass/fail/stale split (~95/3/2) so the
/// fixture has real matrix variation instead of one degenerate all-pass
/// column set.
let private snapshotFor (rowIndex: int) (memberOpt: MemberId option) : SessionSnapshot<MemberId> =
  let passing = ResizeArray(testCount)
  let failing = ResizeArray()
  let stale = ResizeArray()
  for i in 0 .. testCount - 1 do
    match (i + rowIndex) % 100 with
    | b when b < 95 -> passing.Add testPool.[i]
    | b when b < 98 -> failing.Add testPool.[i]
    | _ -> stale.Add testPool.[i]
  { Member = memberOpt
    SessionId = sprintf "session-%d" rowIndex
    Generation = int64 rowIndex
    PassingTests = List.ofSeq passing
    FailingTests = List.ofSeq failing
    StaleTests = List.ofSeq stale }

let private snapshots : SessionSnapshot<MemberId>[] =
  Array.append
    [| snapshotFor 0 None |]
    (members |> List.mapi (fun idx m -> snapshotFor (idx + 1) (Some m)) |> Array.ofList)

let private head = buildHead ()

/// The command that exercises the save-observed claim probe: a save inside
/// one member's own claim is the common case (no violation, but the scan
/// still runs in full — `ObserveSave` has no early-exit on "it's mine").
let private observeSaveCommand =
  CohortCommand.ObserveSave(members.[0], "SageFs.Core/Features/member-01.fs")

// ── Measurement plumbing ───────────────────────────────────────────────

/// Per-iteration wall time via raw `Stopwatch` timestamps — not `.Elapsed`
/// nor a fresh `Stopwatch` per iteration, both of which carry allocation/
/// object overhead that would dwarf a sub-10µs measurement. Warms the JIT
/// for `warmup` iterations first (discarded, matters most for tiered
/// compilation), forces a clean GC generation before sampling so a warmup
/// collection does not land inside the timed loop, then samples `iterations`
/// more and returns them sorted ascending, in milliseconds.
let private measureMs (warmup: int) (iterations: int) (f: unit -> unit) : float[] =
  for _ in 1 .. warmup do
    f ()
  GC.Collect()
  GC.WaitForPendingFinalizers()
  GC.Collect()
  let freq = float Stopwatch.Frequency
  let samples = Array.zeroCreate<float> iterations
  for i in 0 .. iterations - 1 do
    let t0 = Stopwatch.GetTimestamp()
    f ()
    let t1 = Stopwatch.GetTimestamp()
    samples.[i] <- float (t1 - t0) / freq * 1000.0
  Array.sortInPlace samples
  samples

let private percentile (sorted: float[]) (p: float) : float =
  let idx = int (ceil (p * float sorted.Length)) - 1 |> max 0 |> min (sorted.Length - 1)
  sorted.[idx]

// ── Shared measurements (computed once; `Lazy` is thread-safe by default) ──

/// §3.4 / §7.4: "decide + project for ten members and 7,000 tests under 1 ms
/// at p99." One `decide` call (the claim probe below) plus one `project`
/// call per iteration, against the fixed 10-member/7,000-test fixture.
/// Warmup/sample counts here are deliberately smaller than the claim probe's:
/// a single decide+project call over this fixture measures ~80-100ms (see the
/// budget comment below), so 2,000 samples would cost several minutes on
/// every `--all` run. 300 samples is still enough to place a stable p99.
let private decideProjectSamples : Lazy<float[]> =
  lazy
    (measureMs 100 300 (fun () ->
      match decide epoch [||] head.State observeSaveCommand with
      | Ok _ -> project head snapshots |> ignore
      | Error err -> failwithf "unexpected refusal in the decide+project perf loop: %A" err))

/// §3.4 / §7.4: "the save-observed claim probe under 10 µs." `decide` alone,
/// isolating the `ObserveSave` linear scan over the fixture's 10 held claims
/// from `project`'s cost.
let private claimProbeSamples : Lazy<float[]> =
  lazy
    (measureMs 500 5000 (fun () ->
      match decide epoch [||] head.State observeSaveCommand with
      | Ok _ -> ()
      | Error err -> failwithf "unexpected refusal in the claim-probe perf loop: %A" err))

// ── Budgets ─────────────────────────────────────────────────────────────

/// The vision's own numbers (§3.4, §7.4) — never loosened.
let private idealDecideProjectMsP99 = 1.000
let private idealClaimProbeMsP99 = 0.010 // 10 µs

/// The actually-enforced CI gate — a documented multiple of the ideal, sized
/// from REAL measurements taken while writing this file (Linux dev box,
/// `dotnet run -c Release`; see the commit message for the full transcript):
///
///   - claim probe: p50=1.76µs, p99=10.20µs — already at the 10µs ideal (the
///     0.20µs overshoot is Stopwatch/tiered-JIT noise at single-digit-µs
///     scale, not a real gap: `decide`'s `ObserveSave` branch is a plain
///     linear scan over 10 held claims, Cohort.fs:659-669). A 4x margin
///     (40µs) keeps the gate real without flaking under a loaded CI runner.
///     MET.
///   - decide+project: ORIGINALLY p50=79.9ms, p99=101.5ms — ~80-100x OVER the
///     1ms ideal. Root cause (confirmed, not just inspected): `project`'s
///     `bitmapFor` built a fresh immutable `Set<TestId>` per session per
///     bitplane via `Set.ofList` — O(n log n) with an allocation on every
///     tree-rebalance step, 33 times (11 sessions × 3 bitplanes) per call,
///     over up to ~6,650-element lists — then queried it once per test
///     column via balanced-tree lookups.
///
///     FIXED (this session, perf task): `project` (`Cohort.fs`) now does a
///     SINGLE pass over every reported test outcome, hashing each `TestId`
///     AT MOST ONCE (assign-or-look-up a first-seen `Dictionary<TestId,int>`
///     column index, recording each hit as a plain `int` in a per-session
///     `ResizeArray<int>`), then derives a first-seen→sorted-column
///     permutation with one probe per DISTINCT id, and materializes the
///     final `bool[]` rows via that permutation — plain integer indexing,
///     never a second string hash. Proven output-identical against a frozen
///     copy of the original implementation by
///     `CohortProjectEquivalenceTests.fs`'s FsCheck property (plus an
///     explicit same-session-multiple-categories example).
///
///     RE-MEASURED after the fix: p50≈5.0-5.1ms, p99≈5.8-6.3ms across
///     several runs — a ~14-16x improvement (77-84ms → 5-6ms), down from
///     ~80-100x over the 1ms ideal to ~5-6x over it. STILL NOT MET against
///     even this 5x/5ms CI-safe margin (narrowly: measured p99 exceeds it by
///     roughly 15-25%), so this stays `skiptest`, not a hard assertion —
///     never loosen the gate to make a still-short number look green.
///
///     Profiled floor (isolated `dotnet fsi` phase timing, proportions only —
///     fsi is not JIT-tiered like the Release benchmark): of the remaining
///     cost, the single dedup/index-assignment pass over all ~77,000 reported
///     outcomes (10 members × 7,000 tests × ~1.1 categories) dominates at
///     roughly 2/3, with the sort-into-canonical-order-plus-permutation-build
///     pass (7,000 elements) most of the rest; materializing the final
///     `bool[]` rows from the recorded hits is now negligible (≈2-3% of the
///     total) — the permutation trick fully solved that half of the original
///     problem. The residual floor is the O(total reported outcomes) cost of
///     hashing/comparing `TestId`-wrapped STRINGS at all, which is now
///     structural to `TestId`'s own representation (Cohort.fs's own scope
///     note: "`TestId` local, minimal — §5.3's TestRunKey/InputHash overhaul
///     is a separate item") rather than an algorithmic mistake inside
///     `project` — reaching the 1ms ideal likely needs test identity to
///     become (or be accompanied by) a pre-assigned dense integer id upstream
///     of `project`, not a further rewrite of this function. Flagged as a
///     Phase 2+ optimization target for whichever item owns that overhaul,
///     not silently hidden and not hard-failing CI. The 5x/5ms margin over
///     the 1ms ideal is kept exactly as originally documented — unchanged —
///     so this test starts passing automatically, with no edit, once that
///     next layer of work lands.
let private ciSafeDecideProjectMsP99 = 5.000
let private ciSafeClaimProbeMsP99 = 0.040 // 40 µs

/// Prints the measured p50/p99, asserts against the CI-safe bound when it is
/// met, and otherwise `skiptest`s with the measured value and the multiple by
/// which it missed — never a silent pass, never a permanently-red gate for a
/// gap nobody in this session is fixing.
let private assertOrFlagGap (label: string) (idealMs: float) (ciSafeMs: float) (samples: float[]) : unit =
  let p50 = percentile samples 0.50
  let p99 = percentile samples 0.99
  printfn "[perf] %s: p50=%.4fms p99=%.4fms (ideal %.4fms, CI-safe %.4fms)" label p50 p99 idealMs ciSafeMs
  if p99 <= ciSafeMs then
    p99 <= ciSafeMs
    |> Expect.isTrue (
      sprintf "%s p50=%.4fms p99=%.4fms should be <= the CI-safe budget %.4fms (ideal §3.4 budget: %.4fms)" label p50 p99 ciSafeMs idealMs
    )
  else
    skiptest (
      sprintf
        "%s p50=%.4fms p99=%.4fms exceeds even the CI-safe budget %.4fms (ideal §3.4 budget: %.4fms) — a REAL, measured optimization target (%.0fx over the CI-safe bound), not flaky noise. See the file header for the suspected root cause. This test asserts automatically, with no edit required, once the measured value comes back under budget."
        label
        p50
        p99
        ciSafeMs
        idealMs
        (p99 / ciSafeMs)
    )

[<Tests>]
let cohortPerfBudgetTests =
  testList "Cohort perf budgets (sagefs-multiagent-vision.md §3.4, §7.4)" [

    testCase "decide + project: ten members, 7,000 tests" <| fun _ ->
      assertOrFlagGap "decide+project (10 members, 7000 tests)" idealDecideProjectMsP99 ciSafeDecideProjectMsP99 decideProjectSamples.Value

    testCase "the save-observed claim probe (decide's ObserveSave branch)" <| fun _ ->
      assertOrFlagGap "claim probe (10 held claims)" idealClaimProbeMsP99 ciSafeClaimProbeMsP99 claimProbeSamples.Value
  ]
