# SageFs — FV Target List

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

## Last Updated
- **Date**: 2026-04-27 09:00 UTC
- **Commit**: `460650525a57cfd40c9fd24a57bde43c00f712b3` (+ current run)

---

| # | Target | Source File | Phase | Status | Notes |
|---|--------|-------------|-------|--------|-------|
| 1 | `RingBuffer` | `SageFs.Core/RingBuffer.fs` | 5 | ✅ 20/20 proved (0 sorry) | Complete. All invariants, ordering, eviction accounting verified. |
| 2 | `ResultEx` | `SageFs.Core/ResultEx.fs` | 3–4 | 🔄 15/17 proved (2 sorry) | Monad/functor laws done. 2 sorry on accumulator-length lemmas. |
| 3 | `RetryPolicy.decide` | `SageFs.Core/RetryPolicy.fs` | 3–5 | ✅ 12/12 proved (0 sorry) | Complete this run. Decision correctness + delay monotonicity fully verified. |
| 4 | `RestartPolicy.decide` | `SageFs.Core/RestartPolicy.fs` | 5 | ✅ 8/8 proved (0 sorry) | Complete this run. ge_base sorry resolved with one_le_two_pow helper. |
| 5 | `Affordances.availableTools` | `SageFs.Core/Affordances.fs` | 1 | ⬜ Next target | Security-relevant finite state machine; all properties decidable via `decide`. |

## Phase Legend

| Phase | Description |
|-------|-------------|
| 1 | Research: target identified, approach noted |
| 2 | Informal spec extracted (`specs/<name>_informal.md`) |
| 3 | Lean 4 spec written (`lean/FVSquad/<Name>.lean` with `sorry` proofs) |
| 4 | Lean 4 implementation model extracted (concrete definitions, not just sigs) |
| 5 | Proofs attempted; `sorry`s reduced/eliminated |

## Lean Files

| File | Theorems | Sorry | Phase |
|------|----------|-------|-------|
| `lean/FVSquad/RingBuffer.lean` | 20 | 0 | 5 ✅ |
| `lean/FVSquad/ResultEx.lean` | 17 | 2 | 3–4 🔄 |
| `lean/FVSquad/RestartPolicy.lean` | 8 | 0 | 5 ✅ |
| `lean/FVSquad/RetryPolicy.lean` | 12 | 0 | 3–5 ✅ |
| **Total** | **57** | **2** | — |

## Open Issues / PRs

- Issue #54: Lean Squad Status (tracking issue, always-open dashboard)
- ResultEx: 2 `sorry` remain — `resSequence_length` and `resPartition_length` require accumulator induction
- Next: Affordances.availableTools informal spec + Lean spec (Task 2 + Task 3)
