# Informal Specification: `WorkflowTypes.SessionWorkflow`

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

**Source file**: `SageFs.Core/WorkflowTypes.fs`
**Module**: `SageFs.WorkflowTypes`
**Run**: 25749476031 (2026-05-12)

---

## Purpose

`SessionWorkflow` is a discriminated union that models the **hot-reload / REPL tradeoff**
in SageFs sessions. The CLR imposes a hard constraint: hot reload (via Harmony JIT patching)
requires `--multiemit-` (single-assembly FSI mode), which prevents type redefinition in the
REPL. This means "hot reload + full REPL" is structurally impossible — and the DU makes that
impossible state unrepresentable.

There are exactly two cases:
- `Interactive`: full REPL, no hot reload — the "exploring and prototyping" workflow.
- `WebLive cfg`: hot reload active, restricted REPL — the "building a web app" workflow.

The module also defines a suite of **total projection functions** (`feedbackStrategy`,
`replCapability`, `fsiArgs`, `label`, `isHotReloadActive`) and a **round-trip constructor**
`fromHotReloadBool`.

Additionally, `WorkflowSwitchOutcome` models the result of a workflow switch as a DU with
three structurally distinct cases, eliminating the ambiguous old record representation.

---

## Types

### `SessionWorkflow`
```
Interactive                 -- full REPL, no hot reload
WebLive of BrowserRefreshConfig  -- hot reload, restricted REPL
```

### `FeedbackStrategy`
```
ReplDriven                  -- multi-emit FSI (type redefinition allowed)
SaveDriven of BrowserRefreshConfig  -- single-emit FSI (--multiemit-)
```

### `ReplCapability`
```
Full            -- type/module redefinition, expression eval
ExpressionOnly  -- expression eval only, no type redefinition
```

### `WorkflowSwitchOutcome`
```
AlreadyActive of cost * message   -- target = current, no switch
DryRunPreview of cost * message   -- dry run, no switch
Executed of previous * target * cost * sessionId * message  -- switch executed
```

---

## Preconditions

All projection functions are **total** — no preconditions on inputs.
`fromHotReloadBool` accepts any `bool`.

---

## Postconditions / Key Properties

### 1. `feedbackStrategy` correctness
- `feedbackStrategy Interactive = ReplDriven`
- `feedbackStrategy (WebLive cfg) = SaveDriven cfg`

### 2. `replCapability` derived from feedbackStrategy
- `replCapability Interactive = Full`
- `replCapability (WebLive cfg) = ExpressionOnly`
- If `feedbackStrategy w = ReplDriven` then `replCapability w = Full`
- If `feedbackStrategy w = SaveDriven cfg` then `replCapability w = ExpressionOnly`

### 3. `fsiArgs` correctness
- `fsiArgs Interactive = []`
- `fsiArgs (WebLive _) = ["--multiemit-"]`

### 4. `isHotReloadActive` correctness
- `isHotReloadActive Interactive = false`
- `isHotReloadActive (WebLive _) = true`

### 5. `label` non-empty
- `label w ≠ ""` for all `w`
- `label Interactive = "REPL"`
- `label (WebLive _) = "Live"`

### 6. Illegal state unrepresentable
- No value of type `SessionWorkflow` can simultaneously have `isHotReloadActive = true`
  and `replCapability = Full`. Proved by exhaustion over the two DU cases.

### 7. `fromHotReloadBool` round-trip
- `fromHotReloadBool true = WebLive BrowserRefreshConfig.defaults`
- `fromHotReloadBool false = Interactive`
- `isHotReloadActive (fromHotReloadBool b) = b` for all `b : Bool`

### 8. `TransitionCost.isZeroCost` correctness
- `isZeroCost c ↔ c.DefinitionsLost = 0 ∧ c.CellsLost = 0`
- `isZeroCost zero = true`

### 9. `WorkflowSwitchOutcome.wasExecuted` correctness
- `wasExecuted (Executed ...) = true`
- `wasExecuted (AlreadyActive ...) = false`
- `wasExecuted (DryRunPreview ...) = false`

### 10. `WorkflowSwitchOutcome.sessionId` is None iff not Executed
- `sessionId (AlreadyActive ...) = None`
- `sessionId (DryRunPreview ...) = None`
- `sessionId (Executed _ _ _ sid _) = Some sid`

### 11. `cost` extracts correctly from all cases
- `cost (AlreadyActive c _) = c`
- `cost (DryRunPreview c _) = c`
- `cost (Executed _ _ c _ _) = c`

---

## Invariants

- `feedbackStrategy` and `replCapability` are **consistent**: hot-reload always implies
  expression-only REPL, and full REPL always implies no hot reload.
- The DU exhaustively partitions session modes — no other combination is possible.

---

## Edge Cases

- `BrowserRefreshConfig.defaults = { WatchPatterns = ["*.fs"; "*.fsx"] }` — a concrete
  default that appears in `fromHotReloadBool true` and in `WorkflowDetection.suggest`.
- `TransitionCost.zero` has all numeric fields = 0 and `StandbyReady = false`.

---

## Open Questions

1. Should `fsiArgs` be verified to never contain duplicate flags?
2. Should `label` values be verified to be distinct? (They are — "REPL" ≠ "Live")
3. Is it worth proving `TransitionCost.compute` postconditions (e.g., `StandbyReady = b`)?
