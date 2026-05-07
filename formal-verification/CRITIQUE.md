# Proof Utility Critique

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

## Last Updated

- **Date**: 2026-05-07 09:15 UTC
- **Commit**: `f5b7f4b`

---

## Overall Assessment

The SageFs formal verification project has 177 theorems proved across 11 Lean 4 files,
zero `sorry`, stdlib-only (no Mathlib). The project has progressed from individual-
module invariants to system-level composition proofs. `Composition.lean` proves the
end-to-end evaluation gate connecting `SessionLifecycle` and `Affordances`.
`PhaseTransition.lean` formalises the session state-machine transition relation with
safety invariants confirming key design decisions (eval failure → Ready, not Faulted).
The major gap from the previous critique ("No cross-file composition theorems") is now
closed. Next priorities are the conference paper, additional correspondence tests, and
a deeper EvalPipeline model. No implementation bugs have been found.

---

## Proved Theorems

| Theorem | File | Level | Bug-catching potential | Notes |
|---------|------|-------|----------------------|-------|
| `resMap_id` | `ResultEx.lean` | Low | Low | Functor identity — tautological by definition |
| `resMap_comp` | `ResultEx.lean` | Mid | Medium | Composition law — could catch wrong functor wiring |
| `resBind_left_id` | `ResultEx.lean` | Low | Low | Monad unit law |
| `resBind_right_id` | `ResultEx.lean` | Low | Low | Monad unit law |
| `resBind_assoc` | `ResultEx.lean` | Mid | Medium | Associativity — catches bind ordering bugs |
| `resMap_eq_bind` | `ResultEx.lean` | Low | Low | Definitional equivalence |
| `toOption_ofOption_id` | `ResultEx.lean` | Low | Low | Round-trip for option/error conversion |
| `ofOption_toOption_ok` | `ResultEx.lean` | Low | Low | Partial round-trip |
| `resSequence_nil` | `ResultEx.lean` | Low | Low | Base case |
| `resSequence_single_ok/error` | `ResultEx.lean` | Low | Low | Base cases |
| `resSequence_length` | `ResultEx.lean` | Mid | Medium | Key structural: sequence preserves list length |
| `resPartition_length` | `ResultEx.lean` | Mid | Medium | All elements accounted for after partition |
| `isOk_iff` | `ResultEx.lean` | Low | Low | Definitional unfolding |
| `isOk_isError_complement` | `ResultEx.lean` | Mid | Medium | Complementarity — catches states where both or neither hold |
| `resZip_ok_ok` / `resZip_error_left` | `ResultEx.lean` | Low | Low | Structural case analysis |
| `rtDecide_giveup_non_retryable` | `RetryPolicy.lean` | Mid | High | Correct: non-retryable errors always give up |
| `rtDecide_retry_when_available` | `RetryPolicy.lean` | Mid | High | Correct: retry fires when retries remain |
| `rtDecide_giveup_exhausted` | `RetryPolicy.lean` | Mid | High | Correct: give up when exhausted |
| `baseDelay_zero` | `RetryPolicy.lean` | Low | Low | Base case |
| `baseDelay_formula` | `RetryPolicy.lean` | Low | Medium | Delay arithmetic — catches off-by-one in backoff |
| `baseDelay_mono` | `RetryPolicy.lean` | Mid | High | Monotone delay growth — key safety property |
| `baseDelay_pos` | `RetryPolicy.lean` | Low | Medium | Positive delay for non-zero base |
| `rtDecide_zero_retries` | `RetryPolicy.lean` | Mid | High | Edge case: zero retries always gives up |
| `shouldRetry_false_of_ge` | `RetryPolicy.lean` | Low | Medium | Exhaustion boundary |
| `shouldRetry_true_of_lt` | `RetryPolicy.lean` | Low | Medium | Available-retry boundary |
| `shouldRetry_pred` | `RetryPolicy.lean` | Low | Low | Predecessor bound |
| `rtDecide_delay_formula` | `RetryPolicy.lean` | Mid | High | Delay formula under retry conditions |
| `rtDecide_first_retry_delay` | `RetryPolicy.lean` | Mid | Medium | First retry special case |
| `create_wellFormed` | `RingBuffer.lean` | Mid | Medium | Invariant established on creation |
| `push_wellFormed` | `RingBuffer.lean` | High | High | Invariant preserved through push — safety-critical |
| `create_capacity` | `RingBuffer.lean` | Low | Low | Capacity fixed at creation |
| `push_capacity` | `RingBuffer.lean` | Low | Low | Push preserves capacity |
| `clear_capacity` | `RingBuffer.lean` | Low | Low | Clear preserves capacity |
| `create_count_zero` / `clear_count` | `RingBuffer.lean` | Low | Low | State resets |
| `push_count` | `RingBuffer.lean` | Mid | High | Count update formula (including eviction) |
| `count_le_capacity` | `RingBuffer.lean` | Mid | High | Key invariant: count never exceeds capacity |
| `push_total` | `RingBuffer.lean` | Mid | Medium | Total monotonically increments |
| `clear_total` ⚠️ | `RingBuffer.lean` | — | — | **INCORRECT** — see D3 in CORRESPONDENCE.md; Lean model diverges from F# |
| `evictedCount_nonneg` | `RingBuffer.lean` | Mid | Medium | Eviction accounting is non-negative |
| `tryGet_none_of_ge` | `RingBuffer.lean` | Mid | High | Out-of-range access returns None |
| `toList_length` | `RingBuffer.lean` | High | High | toList has exactly `count` elements — non-trivial |
| `push_tryGet_zero` | `RingBuffer.lean` | High | High | Most-recently-pushed is at age 0 — key correctness |
| `push_aging` | `RingBuffer.lean` | High | High | Prior items age correctly — non-trivial modular arithmetic |
| `availableTools_nonempty` | `Affordances.lean` | Mid | High | At least one tool always available |
| `get_fsi_status_always` | `Affordances.lean` | Mid | High | Core FSI tool always available |
| `send_fsharp_code_iff_ready` | `Affordances.lean` | High | High | Code evaluation requires Ready state |
| `cancel_eval_iff_ready_or_evaluating` | `Affordances.lean` | High | High | Cancel availability policy |
| `hard_reset_iff_ready_or_faulted` | `Affordances.lean` | High | High | Reset gating — safety property |
| `hard_reset_not_during_eval` | `Affordances.lean` | High | High | No reset while evaluating |
| `create_session_not_during_eval` | `Affordances.lean` | High | High | No new session while busy |
| `availableTools_nodup` | `Affordances.lean` | Mid | Medium | Tool list has no duplicates |
| `checkToolAvailability_iff_mem` | `Affordances.lean` | Mid | Medium | Availability check ↔ membership |
| `send_code_denied_unless_ready` | `Affordances.lean` | High | High | Access control — security-critical |
| `hard_reset_denied_when_evaluating` | `Affordances.lean` | High | High | Access control |
| `epReturn_stages_empty` | `EvalPipeline.lean` | Low | Low | Base case |
| `epReturn_result_ok` | `EvalPipeline.lean` | Low | Low | Pure return |
| `epSucceeded_iff_ok` | `EvalPipeline.lean` | Mid | Medium | Success ↔ ok result |
| `epBind_ok_stages` | `EvalPipeline.lean` | Mid | Medium | Stage concatenation on success |
| `epBind_ok_result` | `EvalPipeline.lean` | Mid | Medium | Result threading |
| `epBind_error_stages` | `EvalPipeline.lean` | Mid | Medium | Error path stage capture |
| `epBind_error_stages_length` | `EvalPipeline.lean` | Mid | Medium | Stage count on error |
| `epBind_error_result` | `EvalPipeline.lean` | Mid | Medium | Error propagation |
| `epBind_error_not_succeeded` | `EvalPipeline.lean` | Mid | High | Error path never succeeds |
| `two_step_first_fails_one_stage` | `EvalPipeline.lean` | Mid | High | Concrete trace for failure scenario |
| `two_step_first_ok_stages` | `EvalPipeline.lean` | Mid | High | Concrete trace for success scenario |
| `watch_makes_watched` | `HotReloadState.lean` | High | High | Postcondition of watch — key |
| `watch_idempotent` | `HotReloadState.lean` | High | High | Duplicate watch has no effect |
| `watch_preserves_other` | `HotReloadState.lean` | High | High | Isolation: watch doesn't unwatch others |
| `unwatch_not_watched` | `HotReloadState.lean` | High | High | Postcondition of unwatch |
| `unwatch_preserves_other` | `HotReloadState.lean` | High | High | Isolation: unwatch doesn't disturb others |
| `toggle_involution` | `HotReloadState.lean` | High | High | Double-toggle is identity — elegant |
| `watchMany_makes_watched` | `HotReloadState.lean` | High | High | Batch watch correctness |
| `unwatchAll_clears` | `HotReloadState.lean` | High | High | Clear postcondition |
| `watchAll_makes_watched` | `HotReloadState.lean` | High | High | Batch replace correctness |
| `watchedCount_watch_new` | `HotReloadState.lean` | Mid | High | Count increments for new paths |
| `watchedCount_watch_existing` | `HotReloadState.lean` | Mid | Medium | Count stable for existing paths |
| `unwatchByDirectory_removes` | `HotReloadState.lean` | High | High | Directory-scoped unwatch correctness |
| `watchByDirectory_adds` | `HotReloadState.lean` | High | High | Directory-scoped watch correctness |
| `watchedInDirectory_spec` | `HotReloadState.lean` | High | High | Membership spec for directory queries |
| `toState_initializing` / `toState_active_*` / `toState_faulted` | `SessionLifecycle.lean` | Low | Low | Definitional unfolding |
| `toState_never_uninitialized` | `SessionLifecycle.lean` | **High** | **High** | 🌟 STRUCTURAL FINDING: Uninitialized is unreachable |
| `tryAppState_some_iff_active` | `SessionLifecycle.lean` | High | High | Active state detection |
| `ready_iff_active_idle` | `SessionLifecycle.lean` | High | High | Ready ↔ Active(Idle) |
| `evaluating_iff_active_evaluating` | `SessionLifecycle.lean` | High | High | Evaluating state detection |
| `faulted_iff` / `warming_up_iff` | `SessionLifecycle.lean` | Mid | Medium | State classification |
| `State.label_injective` | `SessionLifecycle.lean` | Mid | Medium | Labels uniquely identify states |
| `lookupOr_empty` / `lookupOr_hit` / `lookupOr_miss` | `Theme.lean` | Low | Low | Primitive correctness |
| `withOverrides_empty_id` | `Theme.lean` | Mid | Medium | Identity property |
| `withOverrides_idempotent_single` | `Theme.lean` | Mid | Medium | Idempotency |
| `withOverrides_*_preserves_*` | `Theme.lean` | Mid | Medium | Field isolation (×18 fields) |
| `defaults_hex_lengths_*` | `Theme.lean` | Low | Low | `decide` proof — hardcoded constants |
| `defaults_fg_colors_nonempty` | `Theme.lean` | Low | Low | Trivial `decide` |
| `withOverrides_unrelated_key_*` | `Theme.lean` | Mid | Medium | Cross-field isolation |
| `withOverrides_stack_*` | `Theme.lean` | Mid | Medium | Stack composition |

---

## Gaps and Recommendations

Priority order (highest first):

### 1. Conference paper (Task 11) — **HIGH**

The project has 177 theorems, 11 files, system-level composition proofs, and
correspondence tests. A conference paper summarising the methodology, findings, and
lessons learned is the next high-impact deliverable.

### 2. Runnable correspondence tests (Task 8) — **HIGH**

The `RingBuffer` now has 50 passing correspondence tests (Task 8, Route B). However,
`HotReloadState`, `SessionLifecycle`, `Affordances`, and `Theme` still lack runnable
cross-tests. `HotReloadState` is the highest-value next target due to its non-trivial
stateful behaviour.

### 3. CI for Lean proofs (Task 9) — ✅ **DONE**

`lean-ci.yml` exists and is active. Every PR touching `formal-verification/lean/**`
triggers `lake build` automatically. The correspondence-test workflow
`fv-correspondence-tests.yml` also runs on test and source changes.

### 4. Session phase transition proofs — ✅ **DONE** (`PhaseTransition.lean`)

`PhaseTransition.lean` defines the `validTransition` inductive relation with all
8 transition cases and proves all key safety invariants. The gap is closed.

### 5. Cross-file composition theorems — ✅ **DONE** (`Composition.lean`)

`Composition.lean` proves the evaluation gate end-to-end with `send_fsharp_code_iff_ready_phase`
and 11 additional composition theorems. The gap is closed.

### 6. EvalPipeline: actual evaluation correctness — **MEDIUM**

The `EvalPipeline` theorems prove structural properties of the computation-
expression trace model (stage concatenation, error propagation), but nothing
about the *correctness* of what F# Interactive actually evaluates. The model
abstracts away the FSI session entirely. A more valuable next target would be
the error-classification logic inside the pipeline.

### 7. Theme field-isolation theorems — **LOW**

The 18 `withOverrides_*_preserves_*` theorems each verify that overriding one
specific field preserves one other specific field. This is a correct but weak
coverage strategy. A stronger property would prove that `withOverrides [(k, v)] base`
agrees with `base` on *all* fields except the one named `k`.

---

## Concerns

### ✅ D3 resolved: `clear_total` now correctly preserves `total`

CORRESPONDENCE.md §RingBuffer §D3 documented a previous divergence where `rbClear`
reset `total` to 0. The current `rbClear` definition preserves `total` (`total :=
buf.total`) and `clear_total` correctly proves `(rbClear default buf).total =
buf.total`. Resolved.

### ⚠️ `defaults_hex_lengths` theorems are low-value constant checks

The three `defaults_hex_lengths_*` theorems in `Theme.lean` are proved by
`decide` over hardcoded string literals. They verify nothing structural — if
the defaults change, the theorems simply need to be re-run. They provide weak
assurance and could be replaced by a single theorem of the form
`∀ color ∈ allDefaultColors, color.length = 7`.

### ⚠️ EvalPipeline model does not cover the real evaluation path

The `EPTrace` model is a clean abstraction of computation-expression tracing,
but the actual F# `EvalPipeline` involves: FSI code submission, output parsing,
error classification, and state updates. The Lean proofs touch only the
pure-functional trace structure — a significant portion of the pipeline's
correctness is unmodelled.

### ⚠️ PhaseTransition concurrency not modelled

`PhaseTransition.lean` does not model the async mailbox processor in `AppState.fs`.
The `cancelEval` message is collapsed into the existing transitions rather than
modelled separately. Proofs hold for the sequential state-transition abstraction,
but not under concurrent reset/cancel racing with evaluation.

---

## Positive Findings

### 🌟 `toState_never_uninitialized` — structural invariant confirmed

`SessionLifecycle.lean` proves that `toState` can never return
`SessionState.Uninitialized` for any value of `Phase α`. This confirms a key
design invariant: `Uninitialized` is a sentinel for "not yet initialised" and
is intentionally absent from the normal session lifecycle. This would catch a
regression if a developer added a new `Phase` variant that could project to
`Uninitialized`.

### 🌟 `send_fsharp_code_iff_ready_phase` — system-level evaluation gate proved end-to-end

`Composition.lean` proves that code submission is available *if and only if* the
session phase is `Active·Idle`, unifying `SessionLifecycle` and `Affordances` in
a single cross-file theorem. This is the project's first multi-module composition
proof and confirms the evaluation gate is correctly specified at the system level.

### 🌟 `eval_cannot_fault_directly` — key safety invariant confirmed at transition level

`PhaseTransition.lean` proves that evaluation failure always returns to `Ready`,
never directly to `Faulted`. This confirms the F# `EvalFinished(Error ex)` handler
design: Faulted is only reachable via explicit reset operations.

### ✅ `push_aging` — non-trivial modular arithmetic proved

The `push_aging` theorem proves that after a push, items previously at age `k`
appear at age `k+1` in the new buffer (modulo eviction). This required
non-trivial reasoning about modular arithmetic (`index_after_push`,
`newHead_ne_index`) and is the kind of property that could catch a subtle
off-by-one in the ring-buffer head calculation.

### ✅ `toggle_involution` — elegant round-trip property

`HotReloadState.lean` proves that toggling a path's watch state twice returns
to the original state. This is a crisp correctness property that would catch
an implementation bug where toggle does not properly alternate between add and
remove.

### ✅ Affordances access-control properties

Seven theorems in `Affordances.lean` collectively prove that:
- Code evaluation is denied unless the session is `Ready`.
- Hard reset is denied during evaluation.
- Session creation is denied during evaluation.

These are security-relevant gate conditions verified to match the declared policy.

---

## File Summary

| File | Theorems | Sorry | Level | Key result | Bug-catching |
|------|----------|-------|-------|------------|-------------|
| `RingBuffer.lean` | 20 | 0 | High | `push_aging`, `toList_length` | High |
| `ResultEx.lean` | 17 | 0 | Mid | `resBind_assoc`, `resSequence_length` | Medium |
| `RetryPolicy.lean` | 13 | 0 | Mid | `baseDelay_mono`, `rtDecide_*` | High |
| `RestartPolicy.lean` | 9 | 0 | Mid | Backoff correctness | Medium |
| `Affordances.lean` | 19 | 0 | High | Access-control gating | High |
| `EvalPipeline.lean` | 17 | 0 | Mid | Trace structure | Medium |
| `HotReloadState.lean` | 23 | 0 | High | `toggle_involution`, directory ops | High |
| `SessionLifecycle.lean` | 16 | 0 | High | `toState_never_uninitialized` | High |
| `Theme.lean` | 20 | 0 | Low–Mid | `withOverrides` identity/isolation | Low–Medium |
| `Composition.lean` | 12 | 0 | High | `send_fsharp_code_iff_ready_phase` | High |
| `PhaseTransition.lean` | 11 | 0 | High | `eval_cannot_fault_directly` | High |
| **Total** | **177** | **0** | — | — | — |
