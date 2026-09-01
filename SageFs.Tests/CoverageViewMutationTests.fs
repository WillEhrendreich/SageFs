/// ## CoverageView Mutation Tests
///
/// Proves the test suite catches mutations in `SageFs.CoverageView`,
/// `CoverageViewState`, and `Overflow` modules. These are pure projection
/// functions with no IO — the most testable code in the system.
module CoverageViewMutationTests

open Expecto
open SageFs.Features.LiveTesting

// ── Mutation Tests ─────────────────────────────────────────────────────────

let coverageViewMutationTests = testList "CoverageView mutations" [

  // ── Overflow.fromTotal ────────────────────────────────────────────────────

  testCase "WHY — fromTotal_within_when_under — total below threshold means Within" <| fun () ->
    let real = Overflow.fromTotal 10 5
    let mutant = Overflow.Overflow 5  // mutant: always overflow
    if real = mutant then
      failwith "Mutation survived — fromTotal returned Overflow for under-threshold"

  testCase "WHY — fromTotal_overflow_when_over — total above threshold means Overflow" <| fun () ->
    let real = Overflow.fromTotal 5 10
    let mutant = Overflow.Within  // mutant: always within
    if real = mutant then
      failwith "Mutation survived — fromTotal returned Within for over-threshold"

  testCase "WHY — fromTotal_exact_threshold — total equals threshold means Within" <| fun () ->
    let real = Overflow.fromTotal 5 5
    if real <> Overflow.Within then
      failwith "Mutation survived — fromTotal returned Overflow for exact threshold"

  testCase "WHY — fromTotal_hidden_count_accuracy — hidden count must be total minus threshold" <| fun () ->
    let real = Overflow.fromTotal 3 10
    match real with
    | Overflow.Overflow hidden when hidden = 7 -> () // correct
    | Overflow.Overflow other ->
      failwithf "Mutation survived — hidden count is %d, expected 7" other
    | _ ->
      failwith "Mutation survived — expected Overflow, got Within"

  // ── CoverageViewState.fromCounts ──────────────────────────────────────────

  testCase "WHY — fromCounts_absent_when_empty — zero tests means Absent" <| fun () ->
    let real = CoverageViewState.fromCounts 0 0 0 0 0
    if real <> CoverageViewState.Absent then
      failwith "Mutation survived — fromCounts didn't return Absent for zero tests"

  testCase "WHY — fromCounts_passing_when_all_pass — all passing means Passing" <| fun () ->
    let real = CoverageViewState.fromCounts 5 0 0 0 0
    if real <> CoverageViewState.Passing then
      failwith "Mutation survived — fromCounts didn't return Passing"

  testCase "WHY — fromCounts_failing_dominates_passing — any failure makes Failing" <| fun () ->
    let real = CoverageViewState.fromCounts 5 1 0 0 0
    if real <> CoverageViewState.Failing then
      failwith "Mutation survived — fromCounts didn't return Failing when failures present"

  testCase "WHY — fromCounts_running_dominates_passing — running makes Running" <| fun () ->
    let real = CoverageViewState.fromCounts 5 0 1 0 0
    if real <> CoverageViewState.Running then
      failwith "Mutation survived — fromCounts didn't return Running"

  testCase "WHY — fromCounts_stale_dominates_passing — stale makes Stale" <| fun () ->
    let real = CoverageViewState.fromCounts 5 0 0 1 0
    if real <> CoverageViewState.Stale then
      failwith "Mutation survived — fromCounts didn't return Stale"

  testCase "WHY — fromCounts_skipped_dominates_passing — skipped makes Skipped" <| fun () ->
    let real = CoverageViewState.fromCounts 5 0 0 0 1
    if real <> CoverageViewState.Skipped then
      failwith "Mutation survived — fromCounts didn't return Skipped"

  testCase "WHY — fromCounts_priority_failing_over_running — Failing > Running" <| fun () ->
    let real = CoverageViewState.fromCounts 5 1 1 0 0
    if real <> CoverageViewState.Failing then
      failwith "Mutation survived — fromCounts didn't prioritize Failing over Running"

  testCase "WHY — fromCounts_priority_running_over_stale — Running > Stale" <| fun () ->
    let real = CoverageViewState.fromCounts 5 0 1 1 0
    if real <> CoverageViewState.Running then
      failwith "Mutation survived — fromCounts didn't prioritize Running over Stale"

  testCase "WHY — fromCounts_priority_stale_over_skipped — Stale > Skipped" <| fun () ->
    let real = CoverageViewState.fromCounts 5 0 0 1 1
    if real <> CoverageViewState.Stale then
      failwith "Mutation survived — fromCounts didn't prioritize Stale over Skipped"

  // ── CoverageView.formatBadge ──────────────────────────────────────────────

  testCase "WHY — formatBadge_pass_uses_checkmark — passing tests show ✓" <| fun () ->
    let real = CoverageView.formatBadge (CoverageBadge.Pass 3)
    if real <> "✓ 3" then
      failwithf "Mutation survived — formatBadge(Pass) returned '%s'" real

  testCase "WHY — formatBadge_fail_uses_cross — failing tests show ✗" <| fun () ->
    let real = CoverageView.formatBadge (CoverageBadge.Fail 2)
    if real <> "✗ 2" then
      failwithf "Mutation survived — formatBadge(Fail) returned '%s'" real

  testCase "WHY — formatBadge_running_uses_spinner — running tests show ⟳" <| fun () ->
    let real = CoverageView.formatBadge (CoverageBadge.Running 1)
    if real <> "⟳ 1" then
      failwithf "Mutation survived — formatBadge(Running) returned '%s'" real

  // ── CoverageView.toInlineBadge ────────────────────────────────────────────

  testCase "WHY — toInlineBadge_empty_list — no badges means empty string" <| fun () ->
    let real = CoverageView.toInlineBadge []
    if real <> "" then
      failwithf "Mutation survived — toInlineBadge([]) returned '%s'" real

  testCase "WHY — toInlineBadge_single_pass — single badge renders without separator" <| fun () ->
    let real = CoverageView.toInlineBadge [CoverageBadge.Pass 5]
    if real <> "✓ 5" then
      failwithf "Mutation survived — toInlineBadge single returned '%s'" real

  testCase "WHY — toInlineBadge_multiple_space_separated — multiple badges use space separator" <| fun () ->
    let real = CoverageView.toInlineBadge [CoverageBadge.Pass 3; CoverageBadge.Fail 1]
    if real <> "✓ 3 ✗ 1" then
      failwithf "Mutation survived — toInlineBadge multiple returned '%s'" real
]
