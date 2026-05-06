# SageFs — FV Target List

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

## Last Updated
- **Date**: 2026-05-05 01:15 UTC
- **Commit**: `69cadf8b9e951c75dfe96812c54bd4c7bda2fdfe`

---

| # | Target | Source File | Phase | Status | Notes |
|---|--------|-------------|-------|--------|-------|
| 1 | `RingBuffer` | `SageFs.Core/RingBuffer.fs` | 5 | ✅ 20/20 proved (0 sorry) | Complete. All invariants, ordering, eviction accounting verified. |
| 2 | `ResultEx` | `SageFs.Core/ResultEx.fs` | 5 | ✅ 17/17 proved (0 sorry) | Monad/functor laws + sequence/partition length lemmas all proved. |
| 3 | `RetryPolicy.decide` | `SageFs.Core/RetryPolicy.fs` | 5 | ✅ 13/13 proved (0 sorry) | Decision correctness + delay monotonicity fully verified. |
| 4 | `RestartPolicy.decide` | `SageFs.Core/RestartPolicy.fs` | 5 | ✅ 9/9 proved (0 sorry) | Backoff correctness verified. |
| 5 | `Affordances.availableTools` | `SageFs.Core/Affordances.fs` | 5 | ✅ 19/19 proved (0 sorry) | Tool-gating policy fully verified. |
| 6 | `EvalPipeline` | `SageFs.Core/EvalPipeline.fs` | 5 | ✅ 17/17 proved (0 sorry) | CE trace structure verified. |
| 7 | `HotReloadState` | `SageFs.Core/HotReloadState.fs` | 5 | ✅ 23/23 proved (0 sorry) | Watch/unwatch/toggle invariants verified. |
| 8 | `SessionLifecycle` | `SageFs.Core/AppState.fs` | 5 | ✅ 16/16 proved (0 sorry) | Phase projection + unreachability of Uninitialized proved. |
| 9 | `Theme` | `SageFs.Core/Theme.fs` | 5 | ✅ 20/20 proved (0 sorry) | `withOverrides` identity, idempotency, field isolation; hex length of defaults. |

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
| `lean/FVSquad/HotReloadState.lean` | 23 | 0 | 5 ✅ |
| `lean/FVSquad/SessionLifecycle.lean` | 16 | 0 | 5 ✅ |
| `lean/FVSquad/Theme.lean` | 20 | 0 | 5 ✅ |
| **Total** | **154** | **0** | — |

## Open Issues / PRs

- Task 9 (CI): No lean-ci.yml yet — needs creating
- Task 8 (Correspondence): No runnable test harnesses yet
- Task 7 (Critique): Pending — especially to assess `clear_total` divergence in RingBuffer


