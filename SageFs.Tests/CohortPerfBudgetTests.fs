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
/// This list is NOT tagged `[Benchmark]`, so it runs in the default suite and
/// gates every run, as a `testSequenced` list: it runs after the parallel pool, alone, so the
/// rest of the suite is not computing beside it. It is also measured on the calling thread's
/// CPU time (`PerfBudget.nowMs`) and not on wall time. One case failed once at 35.9 s of wall
/// time in another agent's default run because the box was busy, while the work it timed had
/// not changed; CPU time does not count the time a thread waits for a core. What the budgets
/// claim is what the code costs: the work, in the time a core spends on it. Each budget gets
/// one test case reading a shared `Lazy` measurement (computed once per test run, on the thread
/// that first reads it, which is the thread the clock is read on). Load can only make a
/// timing slower, never faster, so each budget judges a statistic that a busy
/// box moves little: decide+project judges the median of its quietest block,
/// and the claim probe judges its BEST batch (the cheapest of hundreds of timed
/// batches). The p50 and p99 are printed beside it, then the judged number is
/// asserted against a regression bound set from the measured numbers with
/// headroom. The bound is a regression gate and not the vision's ideal. The
/// ideal for decide+project (1 ms) is NOT met, and the comment on the bounds
/// below says by how much and where the gap is. The cases always assert and
/// never skip.
///
/// A budget that cannot fail is not a budget, so the claim probe also has a
/// twin: the same probe over several times the held claims, which the same bound
/// must reject (`claimProbeTwinSlowerBy`). If a later edit loosens the bound or the
/// statistic until the twin slips under it, the twin's case fails.
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

/// The scope the fixture cohort is opened at. A v1 cohort WAS machine-wide and
/// every command below is constructed at `Machine`, so `Machine` is exactly
/// what this fixture was written on. `CohortState.empty ()` is the one that
/// hardcodes `Scope = Machine`, so this binding is what proves the fold starts
/// from a state at the SAME scope the commands address — not two literals that
/// happen to agree. These are timing budgets, not scoping tests: the scope is a
/// precondition of the fixture, not a variable.
let private scope = CohortScope.Machine

let private memberCount = 10
let private testCount = 7000

let private membersOf (count: int) : MemberId list =
  [ for i in 1 .. count -> MemberId.Minted(sprintf "member-%02d" i) ]

let private members : MemberId list = membersOf memberCount

/// A `Join` then a disjoint `File` claim for each of the given members (one claim
/// per member, under distinct paths so `AcquireClaim`'s exclusivity scan never
/// refuses one) — a cohort mid-feature, not an empty state. Mirrors the fold
/// idiom `CohortPanelTests.frameAfterWith` uses to build a `LedgerHead` with no
/// owner/ledger/IO involved. The budgets use ten members; the claim probe's twin
/// uses more, to make the same scan cost several times as much.
let private buildHeadOf (cohort: MemberId list) : LedgerHead<MemberId> =
  let joins =
    cohort |> List.map (fun m -> CohortCommand.Join(m, JoinableRole.Implementer, Some(MemberId.display m), scope))
  let acquires =
    cohort
    |> List.map (fun m ->
      CohortCommand.AcquireClaim(
        m,
        ClaimScope.File(sprintf "SageFs.Core/Features/%s.fs" (MemberId.display m)),
        sprintf "working on %s" (MemberId.display m), scope))
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

let private head = buildHeadOf members

/// The command that exercises the save-observed claim probe: a save inside
/// one member's own claim is the common case (no violation, but the scan
/// still runs in full — `ObserveSave` has no early-exit on "it's mine").
let private observeSaveCommand =
  CohortCommand.ObserveSave(members.[0], "SageFs.Core/Features/member-01.fs", scope)

// ── Measurement plumbing ───────────────────────────────────────────────

/// Per-iteration time on `PerfBudget.nowMs`, the calling thread's CPU time, so a box
/// that is busy with other work (the rest of the suite, a neighbour's build) does not
/// count against the budget: only the time this thread spent computing does. It is a
/// raw reading, not `.Elapsed` nor a fresh `Stopwatch` per iteration, both of which
/// carry allocation/object overhead that would dwarf a sub-10µs measurement. Warms the JIT
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
  let samples = Array.zeroCreate<float> iterations
  for i in 0 .. iterations - 1 do
    let t0 = PerfBudget.nowMs ()
    f ()
    let t1 = PerfBudget.nowMs ()
    samples.[i] <- t1 - t0
  Array.sortInPlace samples
  samples

let private percentile (sorted: float[]) (p: float) : float =
  let idx = int (ceil (p * float sorted.Length)) - 1 |> max 0 |> min (sorted.Length - 1)
  sorted.[idx]

/// How many independent blocks each measurement takes. One block can land
/// entirely inside a burst of load from other tests or other processes; five
/// blocks make it unlikely that all of them do, and the assertion reads the
/// quietest.
let private blockCount = 5

/// Runs `blockCount` blocks of `perBlock` samples (the JIT warmup is paid once,
/// in the first block, because tiered compilation only needs to be settled
/// once) and returns the sorted samples of the block with the lowest p99.
let private quietestBlock (warmup: int) (perBlock: int) (f: unit -> unit) : float[] =
  [| for b in 0 .. blockCount - 1 -> measureMs (if b = 0 then warmup else 0) perBlock f |]
  |> Array.minBy (fun block -> percentile block 0.99)

/// What a run of timed batches reduces to. A batch is `CallsPerBatch` calls timed
/// as one span and divided down to a per-call time, so the clock cost (tens of
/// nanoseconds) is shared by the whole batch and a single descheduled call is
/// diluted instead of owning the sample.
///
/// `BestBatchMs` is the cheapest batch. Another process, a GC pause or a
/// neighbour's compile can only add time to a batch, never take any away, so the
/// cheapest of hundreds of batches is the one least touched by the box, and it
/// sits close to what the code costs by itself. `TypicalBatchMs` is the median
/// batch, printed for the reader and never judged: on a shared box it tracks
/// the box.
type BatchReading =
  { BestBatchMs: float
    TypicalBatchMs: float
    Batches: int
    CallsPerBatch: int }

/// Warms `f` up, forces a clean GC generation so a warmup collection does not
/// land in the timed span, then times `batches` batches of `callsPerBatch` calls.
let measureBatches (warmup: int) (batches: int) (callsPerBatch: int) (f: unit -> unit) : BatchReading =
  for _ in 1 .. warmup do
    f ()
  GC.Collect()
  GC.WaitForPendingFinalizers()
  GC.Collect()
  let perCallMs = Array.zeroCreate<float> batches
  for i in 0 .. batches - 1 do
    let t0 = PerfBudget.nowMs ()
    for _ in 1 .. callsPerBatch do
      f ()
    let t1 = PerfBudget.nowMs ()
    perCallMs.[i] <- (t1 - t0) / float callsPerBatch
  Array.sortInPlace perCallMs
  { BestBatchMs = perCallMs.[0]
    TypicalBatchMs = percentile perCallMs 0.50
    Batches = batches
    CallsPerBatch = callsPerBatch }

/// What a budget decided about a reading, as a value the real case and its twin
/// both read: the real case demands `WithinBound`, the twin demands `OverBound`.
[<RequireQualifiedAccess>]
type BudgetVerdict =
  | WithinBound of measuredMs: float * boundMs: float
  | OverBound of measuredMs: float * boundMs: float

/// Judges the best batch against a bound.
let judgeBestBatch (boundMs: float) (reading: BatchReading) : BudgetVerdict =
  match reading.BestBatchMs <= boundMs with
  | true -> BudgetVerdict.WithinBound(reading.BestBatchMs, boundMs)
  | false -> BudgetVerdict.OverBound(reading.BestBatchMs, boundMs)

// ── Shared measurements (computed once; `Lazy` is thread-safe by default) ──

/// §3.4 / §7.4: "decide + project for ten members and 7,000 tests under 1 ms
/// at p99." One `decide` call (the claim probe below) plus one `project`
/// call per iteration, against the fixed 10-member/7,000-test fixture.
/// Sample counts here are deliberately smaller than the claim probe's: a single
/// decide+project call over this fixture costs about 5 ms (see the budget
/// comment below), and every default run pays for the whole measurement.
/// 100 samples per block puts the p99 on the second-largest sample, which is a
/// stable place to read it from. The 100 warmup calls settle tiered
/// compilation for `project`'s loops, which is the slow part to settle.
let private decideProjectWarmup = 100
let private decideProjectSamplesPerBlock = 100

let private decideProjectSamples : Lazy<float[]> =
  lazy
    (quietestBlock decideProjectWarmup decideProjectSamplesPerBlock (fun () ->
      match decide epoch [||] head.State observeSaveCommand with
      | Ok _ -> project head snapshots |> ignore
      | Error err -> failwithf "unexpected refusal in the decide+project perf loop: %A" err))

/// The twin of the decide+project budget: the same measurement over the same fixture with the frozen
/// pre-optimization `project` (`CohortProjectEquivalenceTests.Reference`, a `Set` per bitplane, 77-84 ms a call when the
/// budget was first written). A budget that the implementation it was written to keep out passes is not a budget. Fewer
/// samples than the real measurement: each call costs about 16 times as much.
let private decideProjectTwinWarmup = 2
let private decideProjectTwinSamplesPerBlock = 5

let private decideProjectTwinSamples : Lazy<float[]> =
  lazy
    (quietestBlock decideProjectTwinWarmup decideProjectTwinSamplesPerBlock (fun () ->
      match decide epoch [||] head.State observeSaveCommand with
      | Ok _ -> CohortProjectEquivalenceTests.Reference.project head snapshots |> ignore
      | Error err -> failwithf "unexpected refusal in the decide+project twin loop: %A" err))

/// §3.4 / §7.4: "the save-observed claim probe under 10 µs." `decide` alone,
/// isolating the `ObserveSave` linear scan over the fixture's 10 held claims
/// from `project`'s cost. A call is a few microseconds, so it is timed in
/// batches (see `BatchReading`). 800 batches of 16 calls is about 130 µs a
/// batch and about 0.1 s in all: a batch is short enough to fit between two
/// descheduling events on a busy box, and with 800 of them every one would have
/// to be hit for the best batch to rise. The warmup is large enough to get the
/// scan out of the tier-0 JIT.
let private claimProbeWarmup = 2000
let private claimProbeBatches = 800
let private claimProbeCallsPerBatch = 16

let private claimProbeOnce () : unit =
  match decide epoch [||] head.State observeSaveCommand with
  | Ok _ -> ()
  | Error err -> failwithf "unexpected refusal in the claim-probe perf loop: %A" err

let private claimProbeReading : Lazy<BatchReading> =
  lazy (measureBatches claimProbeWarmup claimProbeBatches claimProbeCallsPerBatch claimProbeOnce)

/// The twin: the same probe over `claimProbeTwinSlowerBy` times as many held
/// claims, so the linear scan costs that many times as much per save. That is the
/// size a per-claim cost reaches when a change makes each claim cost several
/// times what it did (a rescan per claim, a copy of the claim map per call). The
/// first twin tried was the real call repeated eight times in a loop. In some
/// processes that loop ran faster per call than one call alone (a batch read 3 µs
/// a call against 8 µs a call), so its best batch landed within 20% of the
/// bound. A bigger input does not have that fast mode. The scaled input cost 17 to 18 times the real probe in
/// every run (160 claims, best batch 58-60 µs against 3.3 µs, in the REPL). It
/// is measured exactly like the real probe and judged against the same bound.
let claimProbeTwinSlowerBy = 16

let private claimProbeTwinHead = buildHeadOf (membersOf (memberCount * claimProbeTwinSlowerBy))

let private claimProbeTwinOnce () : unit =
  match decide epoch [||] claimProbeTwinHead.State observeSaveCommand with
  | Ok _ -> ()
  | Error err -> failwithf "unexpected refusal in the claim-probe twin loop: %A" err

let private claimProbeTwinReading : Lazy<BatchReading> =
  lazy (measureBatches claimProbeWarmup claimProbeBatches claimProbeCallsPerBatch claimProbeTwinOnce)

// ── Budgets ─────────────────────────────────────────────────────────────

/// The vision's own numbers (§3.4, §7.4). They are printed beside every
/// measurement so the gap stays visible, and they are not what is asserted.
let private idealDecideProjectMsP99 = 1.000
let private idealClaimProbeMsP99 = 0.010 // 10 µs

/// The actually-enforced regression gate, set from REAL measurements (Linux
/// dev box, Release build) with headroom for a loaded box. Each case asserts
/// the quietest block's p99 against its bound and never skips:
///
///   - claim probe: MET at the 10µs ideal when run alone (`decide`'s
///     `ObserveSave` branch is a plain linear scan over 10 held claims). The
///     numbers it was first written from (p50=1.76µs, p99=10.20µs) are no
///     longer what it reads: the probe alone reads p50 7.3-8.0µs and p99
///     8-11µs in Release (2026-10-25, a shared box). Its bound and
///     the statistic it is judged on are explained at
///     `regressionBoundClaimProbeMsBestBatch`.
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
///     ~80-100x over the 1ms ideal to ~5-6x over it. The 1ms IDEAL IS UNMET.
///     The gate below is a regression bound, not a claim that the ideal is met.
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
///     of `project`, not a further rewrite of this function. That is the
///     Phase 2+ item that owns the identity overhaul, and the gap to the
///     ideal stays open until it lands.
///
///     On a box with load average 8 the quietest-block p99 read 10.5-12.3ms
///     over three runs (p50 5.5-6.7ms), against 5.8-6.3ms on a quiet one.
///
///     The bound for decide+project is 25ms, on the MEDIAN of the quietest block, not on p99. A p99 over
///     100 samples is the second-slowest call, and with the other 13,000 tests of the default suite running beside
///     it that call is a statement about the box: the 2026-10-19 default run read p99=27.2ms where its p50 was
///     13.7ms. The median holds the cost of the work. 25ms is about 4x the quiet median (5.5-6.7ms) and 1.8x the
///     busiest one seen, and under a third of the 79.9ms median the `Set`-per-bitplane implementation cost, so a
///     return to that implementation (or any 4x regression) fails it.
let private regressionBoundDecideProjectMsP50 = 25.000

/// The claim probe bound is 25µs on the BEST batch, where it used to be 40µs on
/// the p99 of the quietest block. The p99 bound sat on the typical value: a full
/// default run, with 13,000 other tests allocating and collecting beside it,
/// read p99=40.0µs, then 45.5µs, against 40µs (p50 12.8µs and 19.2µs, where the
/// probe alone has a p50 of 7.3-8.0µs and a p99 of 8.3-11.4µs). A p99 over 1,000
/// samples is the tenth slowest call, and on a shared box that call is a
/// statement about the box.
///
/// What the numbers are, all Release, 800 batches of 16 calls, 2026-10-25 on the
/// shared box (load average 31-37 before any load of ours):
///   - probe alone, best batch:        6.8-8.7µs  (typical batch 8.4-10.6µs)
///   - probe with 32 niced busy loops: 9.9-12.8µs (typical batch 13.4-15.1µs)
///   - twin (16x the held claims):     47-51µs alone, 73-82µs with the loops
/// The old statistic on the same kind of runs: p99 8-11µs alone and 16-22µs with
/// the loops.
///
/// The bound is sized for the SLOWEST the probe runs, because the code does not run
/// at one speed. The same call reads 3.3µs in the REPL's long-lived worker and
/// 7-9µs in the test process, and in some test processes a repeat loop around it
/// dropped to 3µs a call part way through. That is what the JIT's tiering looks
/// like (it promotes a method after a quiet 100 ms, and a suite that keeps
/// compiling new tests keeps pushing that back), so the number a run reads
/// depends on how far the process has got. 25µs is the point between the two
/// sides: 1.95x the worst loaded best batch, so a neighbour's build does not fail
/// it, and 1.9x under the cheapest twin, so a scan that does 3.4x the work of the
/// first-tier probe still fails. A regression smaller than that, in a process
/// that has tiered up, passes; the twin pins the size that cannot.
let private regressionBoundClaimProbeMsBestBatch = 0.025 // 25 µs

/// Prints the measured best and typical batch beside the ideal and the bound.
let private printReading (label: string) (idealMs: float) (boundMs: float) (reading: BatchReading) : unit =
  printfn
    "[perf] %s: best batch=%.4fms typical batch=%.4fms over %d batches of %d calls (ideal %.4fms p99, regression bound %.4fms)"
    label reading.BestBatchMs reading.TypicalBatchMs reading.Batches reading.CallsPerBatch idealMs boundMs

/// Prints the measured p50/p99 beside the ideal and the bound, and asserts the
/// p99 is within the bound. The failure message carries the multiple by which
/// the bound was missed.
let private assertWithinBound (label: string) (idealMs: float) (boundMs: float) (statistic: string * float) (samples: float[]) : unit =
  let p50 = percentile samples 0.50
  let p99 = percentile samples 0.99
  printfn "[perf] %s: p50=%.4fms p99=%.4fms (ideal %.4fms, regression bound %.4fms)" label p50 p99 idealMs boundMs
  let name, judged = statistic
  (judged, boundMs)
  |> Expect.isLessThanOrEqual
    (sprintf
      "%s: quietest-block p50=%.4fms p99=%.4fms, judged on %s=%.4fms, should be within the regression bound %.4fms (the §3.4 ideal is %.4fms and is unmet for decide+project)"
      label p50 p99 name judged boundMs idealMs)

[<Tests>]
let cohortPerfBudgetTests =
  testSequenced <| testList "Cohort perf budgets (sagefs-multiagent-vision.md §3.4, §7.4)" [

    testCase "decide + project: ten members, 7,000 tests" <| fun _ ->
      let samples = decideProjectSamples.Value
      assertWithinBound "decide+project (10 members, 7000 tests)" idealDecideProjectMsP99 regressionBoundDecideProjectMsP50 ("p50", percentile samples 0.50) samples

    testCase "WHY — the decide+project budget has teeth: the pre-optimization project over the same fixture is over the bound" <| fun _ ->
      let samples = decideProjectTwinSamples.Value
      let median = percentile samples 0.50
      printfn "[perf] decide+project twin (pre-optimization project): p50=%.4fms (regression bound %.4fms)" median regressionBoundDecideProjectMsP50
      (median, regressionBoundDecideProjectMsP50)
      |> Expect.isGreaterThan
        (sprintf "WHY — the frozen implementation's median is %.4fms and the bound is %.4fms. A bound that passes the implementation it exists to keep out cannot catch a regression: tighten the bound or change what is judged." median regressionBoundDecideProjectMsP50)

    testCase "the save-observed claim probe (decide's ObserveSave branch)" <| fun _ ->
      let reading = claimProbeReading.Value
      printReading "claim probe (10 held claims)" idealClaimProbeMsP99 regressionBoundClaimProbeMsBestBatch reading
      match judgeBestBatch regressionBoundClaimProbeMsBestBatch reading with
      | BudgetVerdict.WithinBound _ -> ()
      | BudgetVerdict.OverBound(measuredMs, boundMs) ->
        failtestf
          "WHY — the claim probe's best batch is %.4fms against a regression bound of %.4fms (typical batch %.4fms, the §3.4 ideal is %.4fms p99). The best batch is the one a busy box moves least, so a number over the bound means the probe itself does more work."
          measuredMs boundMs reading.TypicalBatchMs idealClaimProbeMsP99

    testCase "WHY — the claim probe budget has teeth: the same probe over several times the held claims is over the bound" <| fun _ ->
      let reading = claimProbeTwinReading.Value
      printReading (sprintf "claim probe twin (%dx the held claims)" claimProbeTwinSlowerBy) idealClaimProbeMsP99 regressionBoundClaimProbeMsBestBatch reading
      match judgeBestBatch regressionBoundClaimProbeMsBestBatch reading with
      | BudgetVerdict.OverBound _ -> ()
      | BudgetVerdict.WithinBound(measuredMs, boundMs) ->
        failtestf
          "WHY — the twin scans %dx the held claims and its best batch is %.4fms, under the %.4fms bound. A bound that passes %dx the work cannot catch a regression: tighten the bound or change what is judged."
          claimProbeTwinSlowerBy measuredMs boundMs claimProbeTwinSlowerBy
  ]
