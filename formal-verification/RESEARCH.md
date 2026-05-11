# SageFs — Formal Verification Research

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

## Last Updated
- **Date**: 2026-05-11 17:10 UTC
- **Commit**: `545fe0d6bc4961ea08fd0feea2f002b3b5130e23`

---

## Overview

SageFs is a live F# development environment — a REPL-powered daemon with editor integrations. The primary language is **F#**, targeting `.NET 10`. This document surveys the codebase for formal verification candidates and establishes the overall approach.

## FV Tool Choice: Lean 4 + Mathlib

**Rationale**: SageFs is written in F#, not Rust, so Aeneas/Charon (the Rust→Lean extraction pipeline) is not applicable. We use **Lean 4** with **Mathlib** for hand-written formal specifications and proofs. The primary source of truth is the F# source code; Lean models are written by hand, capturing the pure functional core of each target.

**Approach**:
- Write Lean 4 functional models that mirror the pure F# logic
- State key correctness properties as Lean `theorem` declarations
- Prove them with Lean tactics (`omega`, `simp`, `decide`, `cases`, `induction`)
- Use `sorry` for currently unprovable goals; mark and track them
- Prefer decidable propositions where possible

**Mathlib relevance**: `Mathlib.Data.List.Basic`, `Mathlib.Data.Nat.Basic`, `Mathlib.Logic.Basic` cover most of what we need for list/count/arithmetic properties.

> **Note (2026-05-06)**: Mathlib is blocked in CI (firewall blocks `lakecache.blob.core.windows.net`).
> All FVSquad files use **pure Lean 4 stdlib only** — no `import Mathlib`. The tactics available are:
> `omega`, `simp`/`simp only`, `decide`/`native_decide`, `cases`, `rcases`, `induction`, `rw`, `exact`.
> Tactic workarounds for missing Mathlib are documented in §FV Toolchain Notes below.

---

## FV Target Survey

### Target 1: `RingBuffer` — `SageFs.Core/RingBuffer.fs`

**Description**: A fixed-capacity ring buffer for time-travel model snapshots. Pure functional module (each operation returns a new `RingBuffer<'T>` record). Uses a mutable backing array under the hood, but all operations are written in a functional style.

**Why FV-amenable**:
- All public operations are pure functions with no I/O or external state
- Clear, testable invariants: `0 ≤ count ≤ capacity`, `evictedCount = totalPushed - count`
- Rich existing property-test suite (`RingBufferTests.fs`) providing specification hints
- Simple index arithmetic: `(Head + age) % capacity` — ideal for `omega` / `decide`
- Already has textbook-style documentation on semantics

**Properties to verify**:
1. `count buf ≤ capacity buf` — count never exceeds capacity
2. `evictedCount buf = totalPushed buf - count buf` — accounting identity
3. `totalPushed` monotonically increases with each `push`
4. After `n` pushes on a fresh buffer of capacity `c`, `count = min n c`
5. `(toList buf).Length = count buf` — list length matches count
6. Most-recent-first ordering: `(push x buf |> tryGet 0) = Some x`
7. After `clear`, `count = 0` and `capacity` is preserved

**Spec size**: ~80 Lean lines (types + theorems)
**Proof tractability**: Mostly `omega` / `simp` / `decide` for bounded cases. Induction on push count for the general case. Index-wrap arithmetic requires `omega` or `Nat.mod_cast`.
**Approximations**: The Lean model uses `List` rather than a mutable array; this models the pure input/output semantics but not the physical memory layout.
**Phase**: 5 ✅ **Complete** — 20/20 theorems proved, 0 sorry remaining.
All core invariants, ordering, eviction accounting, and list-length properties are fully verified.
See `formal-verification/lean/FVSquad/RingBuffer.lean`.

---

### Target 2: `ResultEx` — `SageFs.Core/ResultEx.fs`

**Description**: Railway-oriented programming combinators for `Result<'T, 'E>`. Provides `map`, `bind`, `mapError`, `apply`, `zip`, `sequence`, `traverse`, `partition`, and utility predicates.

**Why FV-amenable**:
- All functions are pure, polymorphic, and algebraically structured
- Functor/monad/applicative laws are classical Lean proof exercises
- `sequence` and `traverse` have well-known correctness properties
- No I/O, no mutation, no external state

**Properties to verify**:
1. Functor identity: `map id r = r`
2. Functor composition: `map (f ∘ g) r = (map f ∘ map g) r`
3. Monad left identity: `bind (Ok x) f = f x`
4. Monad right identity: `bind r Ok = r`
5. Monad associativity: `bind (bind r f) g = bind r (fun x → bind (f x) g)`
6. `sequence` correctness: `sequence [Ok a; Ok b] = Ok [a; b]`, first error wins
7. `zip (Ok a) (Ok b) = Ok (a, b)`, `zip (Error e) _ = Error e`
8. `isOk ∘ Ok = true`, `isError ∘ Error = true`

**Spec size**: ~100 Lean lines
**Proof tractability**: Nearly all can be proved by `cases` or `simp` — monad/functor laws on a two-constructor type are essentially trivial in Lean 4. `sequence` requires induction on the list.
**Approximations**: The Lean proofs work over the abstract `Result` type; the specific `SageFsError` type is not needed for algebraic laws.
**Phase**: 3–4 🔄 **In progress** — 15/17 theorems proved, 2 sorry remaining.
Monad/functor laws fully proved. `resSequence_length` and `resPartition_length` require
accumulator-based induction and are guarded with `sorry` for future resolution.
See `formal-verification/lean/FVSquad/ResultEx.lean`.

---

### Target 3: `RetryPolicy` — `SageFs.Core/RetryPolicy.fs`

**Description**: Pure decision function for retry/backoff logic. Given a retryability predicate, a config, an attempt number, and an exception, returns either `RetryAfter delay` or `GiveUp ex`.

**Why FV-amenable**:
- The `decide` function is entirely pure: no I/O, no mutation
- Simple finite case analysis: isRetryable × shouldRetry → 4 cases
- The random jitter in `backoffMs` is NOT pure — we model only the deterministic structure
- `shouldRetry` is a simple linear predicate: `attempt < config.MaxRetries`

**Properties to verify**:
1. Non-retryable exceptions always result in `GiveUp`
2. Retryable exceptions beyond `MaxRetries` result in `GiveUp`
3. Retryable exceptions within `MaxRetries` result in `RetryAfter`
4. `shouldRetry config attempt = (attempt < config.MaxRetries)`

**Spec size**: ~60 Lean lines
**Proof tractability**: All can be proved by `decide` or `cases` — pure case analysis.
**Approximations**: `backoffMs` with random jitter is not modelled; only the deterministic non-random branch is captured.
**Phase**: 3–5 ✅ **Complete** — 12/12 theorems proved, 0 sorry.
Decision correctness, base-delay formula, monotonicity, and edge cases fully verified.
Jitter abstracted away (deterministic model). See `formal-verification/lean/FVSquad/RetryPolicy.lean`.

---

### Target 4: `RestartPolicy` — `SageFs.Core/RestartPolicy.fs`

**Description**: Pure Erlang-style restart policy with exponential backoff and a reset window. `decide` returns `Restart delay` or `GiveUp` depending on restart count vs. policy limits.

**Why FV-amenable**:
- `decide` and `nextBackoff` are pure functions of their inputs
- Properties relate to monotonicity, caps, and decision correctness
- Reset window logic adds mild complexity but is still pure

**Properties to verify**:
1. `nextBackoff` is monotonically non-decreasing in restart count
2. `nextBackoff` is always bounded by `policy.BackoffMax`
3. When `restartCount ≥ policy.MaxRestarts`, `decide` returns `GiveUp`
4. When count is zero with no window, `decide` returns `Restart`

**Spec size**: ~80 Lean lines
**Proof tractability**: `linarith` / `norm_num` for the delay bound. Case analysis for decision properties. `DateTime` arithmetic requires some modelling.
**Approximations**: `DateTime` is modelled as abstract `Int` (ticks or seconds); the reset window check is abstracted.
**Phase**: 5 ✅ **Complete** — 8/8 theorems proved, 0 sorry.
`nextBackoffMs_ge_base` proved this run using a custom `one_le_two_pow` helper (no Mathlib needed).
See `formal-verification/lean/FVSquad/RestartPolicy.lean`.

---

### Target 5: `Affordances.availableTools` — `SageFs.Core/Affordances.fs`

**Description**: Pure state machine function mapping `SessionState` (4 states) to the list of MCP tool names valid in that state. Used to enforce the affordance-driven API.

**Why FV-amenable**:
- Finite domain: exactly 4 session states
- All properties are decidable: `decide` can close them all
- Security-relevant: incorrect affordances could expose tools at the wrong time
- Properties have direct practical meaning

**Properties to verify**:
1. `send_fsharp_code` is ONLY available in `Ready` state
2. `cancel_eval` is ONLY available in `Evaluating` and `Ready` states
3. `create_session` is available in ALL states
4. No duplicate tool names in any state's list
5. `checkToolAvailability` returns `Ok ()` iff the tool is in `availableTools`
6. State transitions preserve the availability monotonicity invariant (adding/removing tools correctly)

**Spec size**: ~80 Lean lines (define state DU + tool lists + theorems)
**Proof tractability**: All `decide` since the domain is finite and concrete. Very tractable.
**Approximations**: Tool names are modelled as `String` literals — exact match. State transitions are not modelled (would require SessionManager).
**Phase**: 1 (Research identified) — next target for formal spec writing.

---

## FV Toolchain Notes (2026-04-27)

**Network constraint**: `lakecache.blob.core.windows.net` is blocked by the CI sandbox
firewall. Mathlib download is not possible. All Lean files use **pure Lean 4 stdlib only**
(no `import Mathlib`). This restricts available tactics — no `linarith`, `nlinarith`,
`positivity`, `conv_rhs`, or `split_ifs`. Workarounds:
- Use `omega` for linear Nat/Int arithmetic
- Prove exponential lemmas by manual induction (`one_le_two_pow` helper)
- Use `cases`, `rcases`, `simp`, `simp only`, `rw` for structural proofs
- Use `Nat.mul_le_mul_left`, `Nat.le_min`, `Nat.pos_pow_of_pos` — verify exact names in Lean 4.30

**Lean version**: `leanprover/lean4:v4.30.0-rc2` (pinned in `lean-toolchain`)

---

## Summary Priority Order

| # | Target | File | Benefit | Tractability | Priority | Phase |
|---|--------|------|---------|-------------|----------|-------|
| 1 | RingBuffer | `SageFs.Core/RingBuffer.fs` | Invariant correctness, rich existing tests | High (omega+simp) | **Complete** ✅ | 5 |
| 2 | ResultEx | `SageFs.Core/ResultEx.fs` | Monad/functor laws, algebraic correctness | Very high (cases/simp) | **Complete** ✅ | 5 |
| 3 | RetryPolicy | `SageFs.Core/RetryPolicy.fs` | Decision correctness, backoff properties | High (cases/omega) | **Complete** ✅ | 5 |
| 4 | RestartPolicy | `SageFs.Core/RestartPolicy.fs` | Backoff monotonicity, cap invariant | Medium (linarith) | **Complete** ✅ | 5 |
| 5 | Affordances | `SageFs.Core/Affordances.fs` | Security-relevant, finite decidable domain | Very high (decide) | **Complete** ✅ | 5 |
| 6 | EvalPipeline | `SageFs.Core/EvalPipeline.fs` | Trace structure, error propagation | High (simp/cases) | **Complete** ✅ | 5 |
| 7 | HotReloadState | `SageFs.Core/HotReloadState.fs` | Watch/unwatch/toggle invariants | High (simp/decide) | **Complete** ✅ | 5 |
| 8 | SessionLifecycle | `SageFs.Core/AppState.fs` | Phase→state projection, unreachable Uninitialized | High (cases/simp) | **Complete** ✅ | 5 |
| 9 | Theme | `SageFs.Core/Theme.fs` | withOverrides identity/idempotency/isolation | High (decide/simp) | **Complete** ✅ | 5 |
| 10 | **Composition** | `FVSquad/Composition.lean` | Cross-module system-level properties | High (simp/decide) | **Complete** ✅ | 5 |

---

## Critique-Driven Research Notes (2026-05-06)

The CRITIQUE.md (Task 7) identified the following gaps and informed this run's Task 1 research:

### Addressed this run: Cross-file composition theorems

`CRITIQUE.md §Concerns` flagged: *"No cross-file composition theorems — each Lean file is self-contained."*

**Action taken**: `FVSquad/Composition.lean` was created this run (Task 5), establishing:
- A canonical isomorphism bridge between `SessionLifecycle.State` and `Affordances.SessionState`
- System-level theorems proving the evaluation gate end-to-end:
  - `send_fsharp_code_iff_ready_phase`: code submission available ↔ Phase.Active _ .Idle
  - `evaluating_cannot_send_code`: reentrancy guard correct end-to-end
  - `faulted_can_hard_reset`: recovery always possible from Faulted
  - `cancel_eval_available_iff_evaluating_or_ready`: cancel gate correct end-to-end
  - `hard_reset_available_iff_ready_or_faulted`: hard reset gate correct end-to-end

**All 12 composition theorems proved (0 sorry), `lake build` passes.**

### New research target: Session phase transition model

`CRITIQUE.md §Gap 3` flagged: *"SessionLifecycle.lean does not prove anything about valid transitions between phases."*

**Recommended new target** (phase 1 → 2): Write an informal spec and then a Lean spec for the session phase transition relation. Concretely:
- Define a `validTransition : Phase α → Phase α → Prop` predicate
- Key property: `Faulted` cannot transition directly to `Active` (must go through `Initializing`)
- Key property: `Initializing` can only go to `Active` or `Faulted`
- Key property: `Active Idle` ↔ `Active Evaluating` are the only intra-Active transitions
- This would subsume a large class of potential state machine bugs

**Source**: `SageFs.Core/SessionManager.fs` (the only place transitions are enacted).
**Tractability**: `decide` over the finite state space, or inductive `Prop`.
**Priority**: Medium — high value for correctness, tractable once the transition relation is formalised.

### New research target: Theme field completeness

`CRITIQUE.md §Gap 5` flagged: *"The 18 `withOverrides_*_preserves_*` theorems are weak — each pair is independent."*

**Recommended improvement**: Replace or supplement the 18 pairwise theorems with a single theorem:
```
∀ k v base, ∀ field ≠ k, (withOverrides [(k, v)] base).field = base.field
```
This requires decidable equality on field names (or a tagless encoding), but would be a stronger and more useful single property that subsumes all 18 theorems. The `decide` tactic may be able to close it for the finite field set.

**Priority**: Low — the current theorems are correct; this is a quality-of-proof improvement.

### Ongoing: EvalPipeline correctness depth

`CRITIQUE.md §Gap 4` flagged: *"EvalPipeline theorems prove only structural properties of the trace model."*

**Recommended**: Add error-classification properties — e.g. "a code submission containing `;;` always produces an `Error` result in the trace." This requires modelling the FSI output parser, which is more complex. Lower priority until the simpler gaps are addressed.

---

---

## New Target Survey (2026-05-11 — Run 25685202317)

All 16 previously identified targets are now at Phase 5 (complete) following the TimeTravel integration in run 25661826548. This section identifies the next wave of FV-amenable targets from the remaining 64 unexplored F# source files.

### New Target 17: `BinaryFormat.Crc32` — `SageFs.Core/BinaryFormat.fs`

**Description**: A pure CRC-32 implementation (ISO 3309 / Ethernet / ZIP polynomial 0xEDB88320) used to validate binary manifest integrity. Two entry points: `compute offset length` and `computeAll` (which calls `compute 0 data.Length`).

**Why FV-amenable**:
- Entirely pure: no I/O, no mutation visible to callers, no external state
- The table is a deterministic precomputed constant — fully decidable for small cases
- CRC-32 has well-known algebraic properties: `computeAll []` = 0x00000000 (with standard complement), self-consistency between `compute` and `computeAll`
- Security-adjacent: CRC mismatches trigger binary-format reload, so correctness matters
- The relationship `computeAll data = compute data 0 data.Length` is the most immediately valuable property to verify

**Properties to verify**:
1. `computeAll data = compute data 0 data.Length` — consistency between the two entry points
2. `compute [] _ 0 = 0x00000000u` — empty range yields the complement-finalized zero: CRC32 of empty bytes is 0x00000000
3. Table completeness: `table.Length = 256` (always, since it is `[| for i in 0u..255u do ... |]`)
4. Table entry ranges: each entry `≤ 0xFFFFFFFFu` (trivially true for UInt32, verifiable by `decide` for small samples)
5. `compute data 0 (data.Length) ≠ compute (data ++ [b]) 0 (data.Length + 1)` for at least one choice — demonstrates sensitivity (non-trivial)

**Spec size**: ~70 Lean lines
**Proof tractability**:
- Properties 1 and 2: `simp` + `rfl` once the Lean model mirrors the F# loop
- Table length: trivially `decide`
- Full CRC correctness over arbitrary byte arrays: requires induction on array length; tractable with standard loop-invariant encoding
**Approximations**:
- The Lean model will use `List UInt8` instead of `Array UInt8` — pure functional semantics captured; physical layout (mutation of `crc` variable) abstracted away
- The Lean model will represent the 256-entry table as a pure function or `Array.ofList` — computable and deterministic, same values

**Priority**: **HIGH** — pure algorithm, first truly algorithmic target (prior targets were mostly DUs and combinators), good proof showcase
**Phase**: 1 (research only)

---

### New Target 18: `WorkflowTypes.SessionWorkflow` — `SageFs.Core/WorkflowTypes.fs`

**Description**: A discriminated union encoding the hot-reload / REPL tradeoff at the type level. Two cases: `Interactive` (full REPL, no hot reload) and `WebLive cfg` (restricted REPL, Harmony patching). The key invariant is structural: "hot reload + full REPL" is an illegal state that cannot be constructed.

**Why FV-amenable**:
- The core safety guarantee is structural / type-theoretic: exhaustive `match` coverage in Lean will mirror the DU
- `replCapability` is a total deterministic function with exactly two outputs; `decide` can verify all its properties
- `feedbackStrategy` is likewise total and testable — derivable fully from `SessionWorkflow`
- `fsiArgs Interactive = []` and `fsiArgs (WebLive _) = ["--multiemit-"]` are byte-for-byte verifiable
- `isHotReloadActive` is a boolean indicator that should be consistent with `fsiArgs` (hot reload ↔ `--multiemit-` present)

**Properties to verify**:
1. `replCapability Interactive = ReplCapability.Full`
2. `replCapability (WebLive cfg) = ReplCapability.ExpressionOnly`
3. `isHotReloadActive ↔ fsiArgs ≠ []` — hot reload indicator ↔ extra FSI args
4. `feedbackStrategy Interactive = FeedbackStrategy.ReplDriven`
5. `feedbackStrategy (WebLive cfg) = FeedbackStrategy.SaveDriven cfg`
6. The illegal state theorem: `∀ wf, ¬ (isHotReloadActive wf ∧ replCapability wf = Full)` — the core safety invariant
7. `fromHotReloadBool true` and `fromHotReloadBool false` both round-trip through `isHotReloadActive`

**Spec size**: ~80 Lean lines
**Proof tractability**: Almost everything decidable by `cases`/`simp`/`decide` — finite cases. The illegal-state theorem is a one-liner: `cases wf <;> simp [isHotReloadActive, replCapability]`.
**Approximations**: `BrowserRefreshConfig` is abstracted as an opaque type — `WatchPatterns` contents not modelled (not needed for the safety properties).

**Priority**: **HIGH** — the illegal-state invariant is exactly the kind of structural safety property that type-theory-based FV excels at; will make an excellent demonstration in the conference paper
**Phase**: 1 (research only)

---

### New Target 19: `ValidTimeout` — `SageFs.Core/Timeouts.fs`

**Description**: A simple validated wrapper type. `ValidTimeout.create` enforces the range [1s, 10min] and returns `Result<ValidTimeout, string>`. `ValidTimeout.value` unwraps it. Used in `Timeouts.setPerTestTimeout` to safely update mutable timeout settings.

**Why FV-amenable**:
- Simple, pure, and self-contained
- The range invariant is the canonical correctness property: if `create t = Ok v` then `value v = t` and `t ∈ [1s, 10min]`
- Complementarily: if `t < 1s` or `t > 10min`, `create t` must return `Err _`
- Round-trip: `create (value v) = Ok v` for any well-constructed `ValidTimeout`
- Good vehicle for demonstrating constrained constructor verification — a common pattern in safe APIs

**Properties to verify**:
1. `create t = Ok v → value v = t` — unwrap identity
2. `create t = Ok v → t ≥ minTimeout ∧ t ≤ maxTimeout` — construction implies valid range
3. `t < minTimeout ∨ t > maxTimeout → ∃ e, create t = Error e` — out-of-range → error
4. `∀ v, create (value v) = Ok v` — round-trip (requires abstracting over the opaque type)
5. `create t = Ok v₁ → create t = Ok v₂ → v₁ = v₂` — uniqueness (deterministic)

**Spec size**: ~50 Lean lines
**Proof tractability**: All properties close by `simp` / `omega` (using `ℚ` or `ℝ` arithmetic for TimeSpan; or modelling as rational seconds). The key challenge is modelling `TimeSpan` — we can represent it as `ℕ` (nanoseconds) or `ℚ` (seconds) with clear approximation documentation.
**Approximations**: `TimeSpan` modelled as `Int64` ticks or `Nat` seconds to avoid floating-point. The `1s–10min` range becomes `100_000_000 ≤ ticks ≤ 6_000_000_000` in tick units.

**Priority**: **MEDIUM** — clean example of validated constructor pattern; good for teaching value but lower research novelty than Crc32 or WorkflowTypes
**Phase**: 1 (research only)

---

### Critique Feedback Incorporated (run 25685202317)

The prior critique (from merged CRITIQUE.md) highlighted these ongoing gaps:
- **No algorithmic targets**: all prior specs verify DUs, combinators, and state machines; `Crc32` fills this gap
- **Structural safety properties**: `WorkflowTypes` directly addresses the critique's suggestion to verify the hot-reload/REPL illegal-state invariant
- **Validated constructor patterns**: `ValidTimeout` adds a new category (constrained construction)

---

## Web Research Notes

Lean 4 Mathlib provides:
- `Mathlib.Data.List.Basic`: `List.length`, `List.take`, `List.get?`, `List.Nodup`
- `Mathlib.Data.Nat.Basic`: `Nat.min`, `Nat.mod`, modular arithmetic lemmas
- `Mathlib.Logic.Basic`: propositional logic combinators
- `decide` tactic: closes decidable propositions automatically — ideal for `Affordances`
- `omega`: linear integer arithmetic — ideal for `RingBuffer` index/count properties
- `simp [...]`: conditional rewriting — ideal for monad laws on `Result`

Pattern reference: monad laws for `Result`/`Option` are standard Lean 4 exercises well supported by Mathlib's algebraic hierarchy. We avoid re-proving these if Mathlib already provides them under `Functor`/`Monad` instances.
