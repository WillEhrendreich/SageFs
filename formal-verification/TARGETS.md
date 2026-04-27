# SageFs — FV Target List

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

## Last Updated
- **Date**: 2026-04-25
- **Commit**: (current run — branch lean-squad/fv-task5-push-aging-and-resultex)

---

| # | Target | Source File | Phase | Status | Notes |
|---|--------|-------------|-------|--------|-------|
| 1 | `RingBuffer` | `SageFs.Core/RingBuffer.fs` | 5 | ✅ All 20 theorems proved (0 sorry) | push_aging proved this run |
| 2 | `ResultEx` | `SageFs.Core/ResultEx.fs` | 3 | 🔄 Lean spec written (2 sorry) | Functor/monad/applicative laws; 2 sorry on accumulator lemmas |
| 3 | `Affordances.availableTools` | `SageFs.Core/Affordances.fs` | 1 | ⬜ Identified | Finite state machine; all properties decidable |
| 4 | `RetryPolicy.decide` | `SageFs.Core/RetryPolicy.fs` | 1 | ⬜ Identified | Pure retry decision; case analysis |
| 5 | `RestartPolicy.decide` | `SageFs.Core/RestartPolicy.fs` | 1 | ⬜ Identified | Exponential backoff; monotonicity/cap properties |

## Phase Legend

| Phase | Description |
|-------|-------------|
| 1 | Research: target identified, approach noted |
| 2 | Informal spec extracted (`specs/<name>_informal.md`) |
| 3 | Lean 4 spec written (`lean/FVSquad/<Name>.lean` with `sorry` proofs) |
| 4 | Lean 4 implementation model extracted (concrete definitions, not just sigs) |
| 5 | Proofs attempted; `sorry`s reduced/eliminated |

## Open Issues / PRs

- Issue #54: Lean Squad Status (tracking issue)
- ResultEx: 2 `sorry` remain — `resSequence_length` and `resPartition_length` require accumulator induction
