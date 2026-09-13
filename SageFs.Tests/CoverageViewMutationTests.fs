/// ## CoverageView Mutation Tests
///
/// Proves the test suite catches mutations in `SageFs.CoverageView`,
/// `CoverageViewState`, and `Overflow` modules. These are pure projection
/// functions with no IO — the most testable code in the system.
///
/// Each case asserts EXACT equality against the correct value (not merely
/// inequality with one hand-picked wrong value) so a mutant that returns any
/// other wrong value is killed too.
module CoverageViewMutationTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

// ── Mutation Tests ─────────────────────────────────────────────────────────

let coverageViewMutationTests = testList "CoverageView mutations" [

  // ── Overflow.fromTotal ────────────────────────────────────────────────────

  testCase "WHY — fromTotal_within_when_under — total below threshold means Within" <| fun () ->
    Overflow.fromTotal 10 5
    |> Expect.equal "fromTotal 10 5 (under threshold) must be Within" Overflow.Within

  testCase "WHY — fromTotal_overflow_when_over — total above threshold means Overflow with the right hidden count" <| fun () ->
    Overflow.fromTotal 5 10
    |> Expect.equal "fromTotal 5 10 (over threshold) must be Overflow 5" (Overflow.Overflow 5)

  testCase "WHY — fromTotal_exact_threshold — total equals threshold means Within" <| fun () ->
    Overflow.fromTotal 5 5
    |> Expect.equal "fromTotal 5 5 (exact threshold) must be Within" Overflow.Within

  testCase "WHY — fromTotal_hidden_count_accuracy — hidden count must be total minus threshold" <| fun () ->
    Overflow.fromTotal 3 10
    |> Expect.equal "fromTotal 3 10 must hide exactly 7" (Overflow.Overflow 7)

  // ── CoverageViewState.fromCounts ──────────────────────────────────────────

  testCase "WHY — fromCounts_absent_when_empty — zero tests means Absent" <| fun () ->
    CoverageViewState.fromCounts 0 0 0 0 0
    |> Expect.equal "fromCounts with all zero counts must be Absent" CoverageViewState.Absent

  testCase "WHY — fromCounts_passing_when_all_pass — all passing means Passing" <| fun () ->
    CoverageViewState.fromCounts 5 0 0 0 0
    |> Expect.equal "fromCounts with only passing must be Passing" CoverageViewState.Passing

  testCase "WHY — fromCounts_failing_dominates_passing — any failure makes Failing" <| fun () ->
    CoverageViewState.fromCounts 5 1 0 0 0
    |> Expect.equal "fromCounts with a failure present must be Failing" CoverageViewState.Failing

  testCase "WHY — fromCounts_running_dominates_passing — running makes Running" <| fun () ->
    CoverageViewState.fromCounts 5 0 1 0 0
    |> Expect.equal "fromCounts with a running test must be Running" CoverageViewState.Running

  testCase "WHY — fromCounts_stale_dominates_passing — stale makes Stale" <| fun () ->
    CoverageViewState.fromCounts 5 0 0 1 0
    |> Expect.equal "fromCounts with a stale test must be Stale" CoverageViewState.Stale

  testCase "WHY — fromCounts_skipped_dominates_passing — skipped makes Skipped" <| fun () ->
    CoverageViewState.fromCounts 5 0 0 0 1
    |> Expect.equal "fromCounts with a skipped test must be Skipped" CoverageViewState.Skipped

  testCase "WHY — fromCounts_priority_failing_over_running — Failing > Running" <| fun () ->
    CoverageViewState.fromCounts 5 1 1 0 0
    |> Expect.equal "fromCounts with both failing and running must prioritize Failing" CoverageViewState.Failing

  testCase "WHY — fromCounts_priority_running_over_stale — Running > Stale" <| fun () ->
    CoverageViewState.fromCounts 5 0 1 1 0
    |> Expect.equal "fromCounts with both running and stale must prioritize Running" CoverageViewState.Running

  testCase "WHY — fromCounts_priority_stale_over_skipped — Stale > Skipped" <| fun () ->
    CoverageViewState.fromCounts 5 0 0 1 1
    |> Expect.equal "fromCounts with both stale and skipped must prioritize Stale" CoverageViewState.Stale

  // ── CoverageView.formatBadge ──────────────────────────────────────────────

  testCase "WHY — formatBadge_pass_uses_checkmark — passing tests show ✓" <| fun () ->
    CoverageView.formatBadge (CoverageBadge.Pass 3)
    |> Expect.equal "formatBadge(Pass 3) must be '✓ 3'" "✓ 3"

  testCase "WHY — formatBadge_fail_uses_cross — failing tests show ✗" <| fun () ->
    CoverageView.formatBadge (CoverageBadge.Fail 2)
    |> Expect.equal "formatBadge(Fail 2) must be '✗ 2'" "✗ 2"

  testCase "WHY — formatBadge_running_uses_spinner — running tests show ⟳" <| fun () ->
    CoverageView.formatBadge (CoverageBadge.Running 1)
    |> Expect.equal "formatBadge(Running 1) must be '⟳ 1'" "⟳ 1"

  // ── CoverageView.toInlineBadge ────────────────────────────────────────────

  testCase "WHY — toInlineBadge_empty_list — no badges means empty string" <| fun () ->
    CoverageView.toInlineBadge []
    |> Expect.equal "toInlineBadge([]) must be empty" ""

  testCase "WHY — toInlineBadge_single_pass — single badge renders without separator" <| fun () ->
    CoverageView.toInlineBadge [CoverageBadge.Pass 5]
    |> Expect.equal "toInlineBadge([Pass 5]) must be '✓ 5'" "✓ 5"

  testCase "WHY — toInlineBadge_multiple_space_separated — multiple badges use space separator" <| fun () ->
    CoverageView.toInlineBadge [CoverageBadge.Pass 3; CoverageBadge.Fail 1]
    |> Expect.equal "toInlineBadge([Pass 3; Fail 1]) must be '✓ 3 ✗ 1'" "✓ 3 ✗ 1"
]
