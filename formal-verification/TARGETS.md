# SageFs — FV Target List

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

## Last Updated
- **Date**: 2026-05-12 17:00 UTC
- **Commit**: `af4c029`

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
| 13 | `SageFsError` | `SageFs.Core/SageFsError.fs` | 5 | ✅ 23 proved (0 sorry) | Error category partition + HTTP status consistency + log severity proved. |
| 14 | `SseReplayBuffer` | `SageFs.Core/SseReplayBuffer.fs` | 5 | ✅ 19 proved (0 sorry) | seqId monotonicity, well-formedness preservation, 4 exhaustive replay cases proved. |
| 15 | `FsiRewrite` | `SageFs/FsiRewrite.fs` | 5 | ✅ 17 proved (0 sorry) | FsiRewrite transformation correctness; 52 correspondence tests. |
| 16 | `TimeTravel` | `SageFs.Core/TimeTravel.fs` | 5 | ✅ 30 proved (0 sorry) | Mode transitions, roundtrip, count invariants, boundary conditions all proved. |
| 17 | `BinaryFormat.Crc32` | `SageFs.Core/BinaryFormat.fs` | 5 | ✅ 16 proved (0 sorry) | CRC-32 standard test vector, slicing consistency, determinism; spec correction. |
| 18 | `WorkflowTypes.SessionWorkflow` | `SageFs.Core/WorkflowTypes.fs` | 5 | ✅ 20 proved (0 sorry) | Illegal-state unrepresentable; feedbackStrategy/replCapability/fsiArgs/label/isHotReloadActive all verified. |
| 19 | `ValidTimeout` | `SageFs.Core/Timeouts.fs` | 1 | ⬜ Research done, no spec yet | Validated constructor: range invariant, round-trip, boundary error cases. |
| 20 | `DirectoryConfig.LoadStrategy` | `SageFs.Core/DirectoryConfig.fs` | 1 | ⬜ New — research only | Pure DU + record defaults; `empty` defaults, LoadStrategy coverage, config merging. |

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
| `lean/FVSquad/SageFsError.lean` | 23 | 0 | 5 ✅ |
| `lean/FVSquad/SseReplayBuffer.lean` | 19 | 0 | 5 ✅ |
| `lean/FVSquad/FsiRewrite.lean` | 17 | 0 | 5 ✅ |
| `lean/FVSquad/TimeTravel.lean` | 30 | 0 | 5 ✅ |
| `lean/FVSquad/BinaryFormat.lean` | 16 | 0 | 5 ✅ |
| `lean/FVSquad/WorkflowTypes.lean` | 20 | 0 | 5 ✅ |
| **Total** | **316** | **0** | — |

## Open Issues / PRs

- Task 2+5 (WorkflowTypes): ✅ DONE run 25749476031 — informal spec + 20 theorems (0 sorry)
- Task 2+3 (ValidTimeout): ⬜ NEXT — informal spec + Lean spec for target 19
- Task 2+3 (DirectoryConfig): ⬜ NEXT — informal spec for target 20
- Task 8 (BinaryFormat): ⬜ HIGH — correspondence tests (Route B) for CRC-32 round-trips


