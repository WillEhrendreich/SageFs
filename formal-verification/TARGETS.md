# SageFs — FV Target List

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

## Last Updated
- **Date**: 2026-05-05 01:15 UTC
- **Date**: 2026-05-05 17:00 UTC
- **Date**: 2026-05-06 00:00 UTC
- **Date**: 2026-05-08 09:00 UTC
- **Commit**: `c4087f7`

---

| # | Target | Source File | Phase | Status | Notes |
|---|--------|-------------|-------|--------|-------|
| 1 | `RingBuffer` | `SageFs.Core/RingBuffer.fs` | 5 | ✅ 20 proved (0 sorry) | WellFormed invariants, push_aging, toList_length verified. |
| 2 | `ResultEx` | `SageFs.Core/ResultEx.fs` | 5 | ✅ 17 proved (0 sorry) | Monad/functor laws + sequence/partition length lemmas all proved. |
| 3 | `RetryPolicy.decide` | `SageFs.Core/RetryPolicy.fs` | 5 | ✅ 13 proved (0 sorry) | Decision correctness + delay monotonicity fully verified. |
| 4 | `RestartPolicy.decide` | `SageFs.Core/RestartPolicy.fs` | 5 | ✅ 9 proved (0 sorry) | Backoff correctness verified. |
| 5 | `Affordances.availableTools` | `SageFs.Core/Affordances.fs` | 5 | ✅ 19 proved (0 sorry) | Tool-gating policy + access-control fully verified. |
| 6 | `EvalPipeline` | `SageFs.Core/EvalPipeline.fs` | 5 | ✅ 20 proved (0 sorry) | CE trace structure + stage-name tracking + success propagation verified. |
| 7 | `HotReloadState` | `SageFs.Core/HotReloadState.fs` | 5 | ✅ 23 proved (0 sorry) | Watch/unwatch/toggle invariants + directory ops verified. |
| 8 | `SessionLifecycle` | `SageFs.Core/AppState.fs` | 5 | ✅ 16 proved (0 sorry) | Phase projection + unreachability of Uninitialized proved. |
| 9 | `Theme` | `SageFs.Core/Theme.fs` | 5 | ✅ 20 proved (0 sorry) | `withOverrides` identity, idempotency, field isolation; hex length of defaults. |
| 10 | `Composition` | `SageFs.Core/Composition.fs` | 5 | ✅ 12 proved (0 sorry) | Function composition laws verified. |
| 11 | `PhaseTransition` | `SageFs.Core/AppState.fs` | 5 | ✅ 14 proved (0 sorry) | Session lifecycle safety: no direct Faulted←Eval, successor coverage, always-successor. |
| 12 | `SmartReset` | `SageFs.Core/SmartReset.fs` | 5 | ✅ 8 proved (0 sorry) | Escalation logic: outcome biconditionals + negative cases fully proved. |
| 13 | `SageFsError` | `SageFs.Core/SageFsError.fs` | 5 | ✅ 26 proved (0 sorry) | Error category partition + HTTP status consistency + log severity proved. |
| 14 | `SseReplayBuffer` | `SageFs.Core/SseReplayBuffer.fs` | 5 | ✅ 19 proved (0 sorry) | seqId monotonicity, well-formedness preservation, 4 exhaustive replay cases proved. |

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
| `lean/FVSquad/EvalPipeline.lean` | 20 | 0 | 5 ✅ |
| `lean/FVSquad/HotReloadState.lean` | 23 | 0 | 5 ✅ |
| `lean/FVSquad/SessionLifecycle.lean` | 16 | 0 | 5 ✅ |
| `lean/FVSquad/Theme.lean` | 20 | 0 | 5 ✅ |
| `lean/FVSquad/Composition.lean` | 12 | 0 | 5 ✅ |
| `lean/FVSquad/PhaseTransition.lean` | 14 | 0 | 5 ✅ |
| `lean/FVSquad/SmartReset.lean` | 8 | 0 | 5 ✅ |
| `lean/FVSquad/SageFsError.lean` | 26 | 0 | 5 ✅ |
| `lean/FVSquad/SseReplayBuffer.lean` | 19 | 0 | 5 ✅ |
| **Total** | **236** | **0** | — |

## Open Issues / PRs

- Task 8 (Correspondence): No runnable test harnesses yet — HIGH PRIORITY
- Task 7 (Critique): ✅ DONE — CRITIQUE.md created
- Task 9 (CI): ✅ DONE — lean-ci.yml created
- Task 4+5 (SageFsError): ✅ DONE this run — 26 theorems, 0 sorry


