# Informal Specification: EvalPipeline

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*
> Source: `SageFs.Core/EvalPipeline.fs`

## Last Updated

- **Date**: 2026-04-28 09:30 UTC
- **Commit**: `833513b25ab7b8c754043e6bb4faf4000b39c026`

---

## Purpose

`EvalPipeline` is a computation expression (CE) builder for staged, traced F#
evaluations. It enables the `pipeline { ... }` syntax, where each `let! x =
stage "name" (fun () -> ...)` step is:

1. **Timed** — elapsed milliseconds are recorded.
2. **Traced** — on completion the step is appended to a `CompletedStage list`.
3. **Railway-oriented** — if a step returns `Error e`, the rest of the pipeline
   is short-circuited; no further steps are executed.

The primary exported value is the `pipeline` CE instance together with helper
functions `stage`, `stageOk`, `totalMs`, `succeeded`, and `formatRailway`.

---

## Key Types

| Type | Description |
|------|-------------|
| `StageOutcome` | `Succeeded \| Failed of SageFsError` — per-step result |
| `CompletedStage` | `{ Name; ElapsedMs; Outcome }` — immutable record of one step |
| `TrackedResult<'T>` | Intermediate struct: carries `Value : Result<'T,SageFsError>`, `StageName`, `ElapsedMs` |
| `PipelineTrace<'T>` | Final: `{ Result : Result<'T,SageFsError>; Stages : CompletedStage list }` |
| `PipelineBuilder` | The CE class implementing `Bind`, `Return`, `ReturnFrom`, `Zero` |

---

## Preconditions

- `stage` and `stageOk` accept any `name : string` (including empty, though
  meaningful names are intended for display).
- `PipelineBuilder.Bind` assumes its `TrackedResult` argument has already
  been produced (timing already recorded).
- `totalMs` and `formatRailway` are pure read operations; they have no preconditions.

---

## Postconditions

### `PipelineBuilder.Bind`

Let `tracked : TrackedResult<'a>` and `f : 'a -> PipelineTrace<'b>`.

- **Always**: the `CompletedStage` for `tracked` is **prepended** to the front of
  the resulting trace's `Stages` list.
- **Success path** (`tracked.Value = Ok v`):
  - Returns `{ Result = (f v).Result; Stages = completed :: (f v).Stages }`.
  - The current step's `Outcome` is `StageOutcome.Succeeded`.
  - The rest of the pipeline (`f v`) is executed and its stages appended after.
- **Failure path** (`tracked.Value = Error e`):
  - Returns `{ Result = Error e; Stages = [completed] }`.
  - The current step's `Outcome` is `StageOutcome.Failed e`.
  - `f` is **never called** — no further stages are executed.
  - The resulting `Stages` list has exactly **one** element.

### `PipelineBuilder.Return`

- Returns `{ Result = Ok x; Stages = [] }` — a successful trace with no stages.

### `PipelineBuilder.Zero`

- Returns `{ Result = Ok (); Stages = [] }`.

### `totalMs`

- Returns the sum of `ElapsedMs` over all stages in the trace.
- For an empty trace: `totalMs { ... Stages = [] } = 0.0<ms>`.

### `succeeded`

- Returns `true` iff `trace.Result = Ok _`.
- Returns `false` iff `trace.Result = Error _`.
- `succeeded` is equivalent to `ResultEx.isOk trace.Result`.

### `formatRailway`

- For an empty trace: returns the string `"(empty pipeline)"`.
- For a non-empty trace: returns stages joined by ` → `, each formatted as
  `"<name> ✓ [<ms>ms]"` (Succeeded) or `"<name> ✗ [<ms>ms]"` (Failed).
- The result always displays stages in the same order they appear in `Stages`.

---

## Invariants

### I1 — Stage list length after failure

After any `Bind` on a `TrackedResult` whose `Value = Error _`:
- `(result.Stages).Length = 1` (just the failing stage).

### I2 — Stage list length after success (relative)

After any `Bind` on a `TrackedResult` whose `Value = Ok _`:
- `(result.Stages).Length = 1 + (f v).Stages.Length`.

### I3 — Failure terminates the pipeline

In a multi-step pipeline `let! a = s1 in let! b = s2 in ...`, if step `k`
returns `Error e`, then steps `k+1, k+2, ...` are never executed and their
`CompletedStage` records never appear in the trace.

### I4 — Outcome reflects value

For every `CompletedStage cs` in a trace:
- `cs.Outcome = Succeeded ↔` the corresponding `TrackedResult.Value = Ok _`.
- `cs.Outcome = Failed e ↔` the corresponding `TrackedResult.Value = Error e`.

### I5 — Result consistency

`succeeded trace ↔ trace.Result = Ok _`.

### I6 — Empty return stages

`(PipelineBuilder.Return x).Stages = []`.

---

## Edge Cases

1. **Zero-step pipeline** (`pipeline { return x }`):
   - `Stages = []`, `Result = Ok x`, `totalMs = 0<ms>`, `succeeded = true`.

2. **Single failing step**:
   - `Stages = [{ ...; Outcome = Failed e }]`, `Result = Error e`.
   - `succeeded = false`.

3. **All steps succeed**:
   - `Stages.Length = number of steps`, every `Outcome = Succeeded`.

4. **First step fails in a 3-step pipeline**:
   - Only 1 stage in the trace; steps 2 and 3 never run.

5. **Last step fails in a 3-step pipeline**:
   - All 3 stages in the trace; steps 1–2 `Outcome = Succeeded`, step 3 `Outcome = Failed`.

6. **`stageOk`** wraps infallible computations:
   - `(stageOk name f).Value = Ok (f ())` always.
   - Can never cause a failure path in `Bind`.

---

## Examples

```fsharp
let trace =
  pipeline {
    let! x = stage "Parse" (fun () -> Ok 42)
    let! y = stage "Eval"  (fun () -> Error (SageFsError.Custom "oops"))
    let! z = stage "Print" (fun () -> Ok ())
    return z
  }
// trace.Result     = Error (SageFsError.Custom "oops")
// trace.Stages     = [ { Name="Parse"; Outcome=Succeeded; ... }
//                      { Name="Eval";  Outcome=Failed ...; ... } ]
//                      -- "Print" stage never ran
// succeeded trace  = false
// formatRailway    = "Parse ✓ [Xms] → Eval ✗ [Yms]"
```

---

## Inferred Intent

The design intention is a **structured concurrency record**: every evaluation in
SageFs that uses this CE produces an audit trail usable for the dashboard railway
visualisation. The railway metaphor (`formatRailway`) — using `✓`/`✗` symbols and
` → ` separators — is a deliberate developer-experience feature to show where a
multi-step evaluation succeeded or failed.

The key correctness property is **short-circuit faithfulness**: a stage that
returns `Error` must not only stop execution but also produce exactly one
`CompletedStage` entry (itself), with the correct `Failed` outcome. This is
important because callers (the dashboard) rely on the stages list to render the
railway correctly.

---

## Open Questions

1. **Stage ordering in `Bind`**: the current implementation prepends `completed`
   to `rest.Stages`, so stages appear in **execution order** (first → last). Is
   this the intended display order? *Yes — `formatRailway` iterates `trace.Stages`
   front-to-back, so prepend = execution order.*

2. **Concurrent stages**: the CE does not support parallel stages (`and!` / `MergeSources`).
   Is this intentional? (Likely yes — timing and ordering are sequential by design.)

3. **Null/empty stage names**: are empty string stage names legal? The spec permits
   them but displays them as empty in `formatRailway`. Maintainer confirmation welcome.

4. **`ElapsedMs` precision**: `Measures.toMs` converts from `TimeSpan`; the
   resolution depends on the OS timer. For formal modelling, elapsed times are
   treated as arbitrary non-negative reals (or Nats if discretised).

5. **`stageOk` failure path**: `stageOk` wraps an infallible computation in
   `Ok`, so by invariant `Bind` on its output always takes the success path.
   Should this be a formal theorem? (Recommended: yes.)
