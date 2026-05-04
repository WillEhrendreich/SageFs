# SageFs — FV Target List

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

## Last Updated
- **Date**: 2026-04-27 09:00 UTC
- **Commit**: `460650525a57cfd40c9fd24a57bde43c00f712b3` (+ current run)

---

| # | Target | Source File | Phase | Status | Notes |
|---|--------|-------------|-------|--------|-------|
| 1 | `RingBuffer` | `SageFs.Core/RingBuffer.fs` | 5 | ✅ 20/20 proved (0 sorry) | Complete. All invariants, ordering, eviction accounting verified. |
| 2 | `ResultEx` | `SageFs.Core/ResultEx.fs` | 5 | ✅ 17/17 proved (0 sorry) | Complete. Monad/functor laws + sequence/partition length lemmas. |
| 3 | `RetryPolicy.decide` | `SageFs.Core/RetryPolicy.fs` | 5 | ✅ 13/13 proved (0 sorry) | Complete. Decision correctness + delay monotonicity fully verified. |
| 4 | `RestartPolicy.decide` | `SageFs.Core/RestartPolicy.fs` | 5 | ✅ 9/9 proved (0 sorry) | Complete. |
| 5 | `Affordances.availableTools` | `SageFs.Core/Affordances.fs` | 5 | ✅ 19/19 proved (0 sorry) | Complete. Tool-gating policy verified. |
| 6 | `EvalPipeline` | `SageFs.Core/EvalPipeline.fs` | 5 | ✅ 17/17 proved (0 sorry) | Complete. |
| 7 | `SessionLifecycle` | `SageFs.Core/SessionLifecycle.fs` | 5 | ✅ 16/16 proved (0 sorry) | KEY FINDING: `toState_never_uninitialized` — Uninitialized unreachable from any phase. |
| 8 | `HotReloadState` | `SageFs.Core/HotReloadState.fs` | 5 | ✅ 23/23 proved (0 sorry) | Complete. |
| 9 | `Theme` | `SageFs.Core/Theme.fs` | 5 | ✅ 74/74 proved (0 sorry) | Complete. Identity, idempotence, all 34 fields override/preserve, tokenColorOfCapture. |

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
| `lean/FVSquad/ResultEx.lean` | 17 | 0 | 5 ✅ |
| `lean/FVSquad/RestartPolicy.lean` | 9 | 0 | 5 ✅ |
| `lean/FVSquad/RetryPolicy.lean` | 13 | 0 | 5 ✅ |
| `lean/FVSquad/Affordances.lean` | 19 | 0 | 5 ✅ |
| `lean/FVSquad/EvalPipeline.lean` | 17 | 0 | 5 ✅ |
| `lean/FVSquad/SessionLifecycle.lean` | 16 | 0 | 5 ✅ |
| `lean/FVSquad/HotReloadState.lean` | 23 | 0 | 5 ✅ |
| `lean/FVSquad/Theme.lean` | 74 | 0 | 5 ✅ |
| **Total** | **208** | **0** | — |

## Open Issues / PRs

- Issue #54: Lean Squad Status (tracking issue, always-open dashboard)
- ResultEx: 2 `sorry` remain — `resSequence_length` and `resPartition_length` require accumulator induction
- Next: Consider `EvalPipeline` or `SessionLifecycle` state machine as next target
