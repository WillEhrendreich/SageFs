# SageFs — FV Target List

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

## Last Updated
- **Date**: 2026-04-23 19:55 UTC
- **Commit**: `c4784d68d4f0c69c21dd4e7255f042868fa3fc80`

---

| # | Target | Source File | Phase | Status | Notes |
|---|--------|-------------|-------|--------|-------|
| 1 | `RingBuffer` | `SageFs.Core/RingBuffer.fs` | 2 | 🔄 Informal spec written | Pure functional ring buffer; count/capacity/ordering invariants |
| 2 | `ResultEx` | `SageFs.Core/ResultEx.fs` | 1 | ⬜ Identified | Functor/monad/applicative laws for `Result<'T,'E>` |
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

*(None yet — first run)*
