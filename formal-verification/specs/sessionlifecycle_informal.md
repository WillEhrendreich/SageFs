# SessionLifecycle — Informal Specification

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

## Target

- **F# source**: `SageFs.Core/AppState.fs`, `SageFs.Core/SessionState.fs`
- **Types**: `SessionActivity`, `SessionPhase`, `SessionState`
- **Functions**: `SessionPhase.toSessionState`, `SessionPhase.tryAppState`, `SessionState.label`

---

## Purpose

SageFs maintains a live F# Interactive session. The session can be warming up,
active-and-idle, active-and-evaluating, or faulted. Two types model this:

- `SessionPhase` — the **rich internal representation**, making impossible states
  unrepresentable. Carries domain data only where meaningful (e.g., `AppState` is
  only present when `Active`).
- `SessionState` — the **external/legacy representation**, exposed to MCP clients
  and the dashboard. Has an extra `Uninitialized` value not reachable from `SessionPhase`.

---

## Types

### `SessionActivity`
```fsharp
type SessionActivity = Idle | Evaluating
```
Describes what an **active** session is currently doing. Only meaningful when
`SessionPhase = Active`. Not a top-level lifecycle state.

### `SessionPhase`
```fsharp
type SessionPhase =
  | Initializing of statusMessage: string option
  | Active of AppState * SessionActivity
  | Faulted
```
- `Initializing msg`: session is warming up; optional human-readable status message.
- `Active (state, activity)`: session is live; `AppState` is valid and non-null.
- `Faulted`: session has failed; no `AppState` is available.

**Invariant**: `AppState` is accessible if and only if the phase is `Active`.

### `SessionState`
```fsharp
type SessionState =
  | Uninitialized | WarmingUp | Ready | Evaluating | Faulted
```
Legacy external type. All values except `Uninitialized` are reachable via
`SessionPhase.toSessionState`.

---

## Functions

### `SessionPhase.toSessionState : SessionPhase → SessionState`
```fsharp
let toSessionState = function
  | Initializing _ -> SessionState.WarmingUp
  | Active (_, Idle) -> SessionState.Ready
  | Active (_, Evaluating) -> SessionState.Evaluating
  | Faulted -> SessionState.Faulted
```

**Postconditions**:
- Result is never `Uninitialized` — `SessionPhase` has no Uninitialized case.
- Result is `Ready` if and only if the phase is `Active _ Idle`.
- Result is `Evaluating` if and only if the phase is `Active _ Evaluating`.
- Result is `Faulted` if and only if the phase is `Faulted`.
- Result is `WarmingUp` if and only if the phase is `Initializing _`.
- Function is deterministic and total.

**Notable gap**: `SessionState.Uninitialized` is unreachable from any `SessionPhase`.
This means external consumers may see an `Uninitialized` state only before the first
`SessionPhase` is published — it is never produced by `toSessionState`.

### `SessionPhase.tryAppState : SessionPhase → AppState option`
```fsharp
let tryAppState = function
  | Active (st, _) -> Some st
  | Initializing _ | Faulted -> None
```

**Postconditions**:
- Returns `Some` if and only if the phase is `Active _ _`.
- Returns `None` for `Initializing` and `Faulted`.
- When result is `Some`, the phase's `toSessionState` is `Ready` or `Evaluating`.

### `SessionState.label : SessionState → string`
```fsharp
let label = function
  | Uninitialized -> "Uninitialized"
  | WarmingUp -> "WarmingUp"
  | Ready -> "Ready"
  | Evaluating -> "Evaluating"
  | Faulted -> "Faulted"
```

**Postconditions**: `label` is injective — different states produce different strings.

---

## Preconditions

These functions are pure and total; no preconditions are required.

---

## Invariants

1. **`AppState` exclusivity**: `AppState` is available (via `tryAppState`) if and only if
   the phase is `Active`, never for `Initializing` or `Faulted`.
2. **Uninitialized unreachability**: No `SessionPhase` value maps to `SessionState.Uninitialized`.
3. **Faulted symmetry**: `Faulted` phase ↔ `Faulted` state (the only case where phase and
   state names match exactly and the relationship is symmetric).

---

## Edge Cases

- `Initializing (None)` — no status message; maps to `WarmingUp` regardless.
- `Initializing (Some msg)` — with message; still maps to `WarmingUp`.
- `Active (st, Idle)` — any `AppState` value; maps to `Ready`.
- `Active (st, Evaluating)` — any `AppState` value; maps to `Evaluating`.

---

## Abstractions for Lean Modelling

- `AppState` is abstracted to a type variable `α`. Its internal fields are irrelevant
  to the lifecycle state machine properties we verify.
- `SessionPhase.toSessionState` and `SessionPhase.tryAppState` are modelled directly.
- `SessionState.label` is modelled directly (String equality is decidable).
- F# evaluation actor state transitions (`publishPhase`, `publishSnapshot`) are **not** modelled;
  they involve `MailboxProcessor` and concurrency, which are out of scope.

---

## Open Questions

- Why does `SessionState` include `Uninitialized` if it is never reachable from `SessionPhase`?
  Is it used for initial value before any phase is published? (See `AppActor` startup.)
- Should `toSessionState` become `toState` to avoid the legacy `SessionState` dependency?
