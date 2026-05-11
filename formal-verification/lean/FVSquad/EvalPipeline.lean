/-!
  # Formal Specification: EvalPipeline

  🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*
  Source: `SageFs.Core/EvalPipeline.fs`

  This file formalises the `EvalPipeline` computation expression (CE) builder.

  ## Design

  `EvalPipeline` is a railway-oriented pipeline CE. Each step (`Bind`) records
  a `CompletedStage` (name + success/failure) and short-circuits on error.

  We model the *pure structural* properties of the CE:
  - `Bind` prepends the current stage's record to the result trace.
  - `Bind` on an error trace never calls `f` and produces a single-stage trace.
  - `Return` produces an empty trace.
  - `succeeded` reflects whether the final result is `Ok`.

  ## Abstractions / omissions
  - `ElapsedMs` is omitted — timing is a side-effect of `Stopwatch`; we model
    only stage names and outcomes.
  - `SageFsError` is abstracted as `Unit` (we don't model error payloads).
  - `stage` and `stageOk` are not modelled (they wrap IO); we work directly
    with the CE combinators `epBind` and `epReturn`.

  #check @epBind
  #check @epReturn
  #check @epTrace
-/

/-- Outcome of a single pipeline stage. -/
inductive EPOutcome where
  | succeeded : EPOutcome
  | failed : EPOutcome
  deriving DecidableEq, Repr

/-- A completed pipeline stage (name + outcome). -/
structure EPStage where
  name : String
  outcome : EPOutcome
  deriving Repr

/-- A tracked result: a named value that may be an error. -/
structure EPTracked (α : Type) where
  value : Except Unit α
  stageName : String
  deriving Repr

/-- A pipeline trace: final result plus all completed stages (most-recent first). -/
structure EPTrace (α : Type) where
  result : Except Unit α
  stages : List EPStage
  deriving Repr

/-- Mirror of `PipelineBuilder.Bind`. -/
def epBind {α β : Type} (tracked : EPTracked α) (f : α → EPTrace β) : EPTrace β :=
  let completed : EPStage := {
    name := tracked.stageName
    outcome :=
      match tracked.value with
      | .ok _  => .succeeded
      | .error _ => .failed
  }
  match tracked.value with
  | .ok v =>
    let rest := f v
    { result := rest.result, stages := completed :: rest.stages }
  | .error e =>
    { result := .error e, stages := [completed] }

/-- Mirror of `PipelineBuilder.Return`. -/
def epReturn {α : Type} (x : α) : EPTrace α :=
  { result := .ok x, stages := [] }

/-- Mirror of `PipelineBuilder.Zero`. -/
def epZero : EPTrace Unit :=
  { result := .ok (), stages := [] }

/-- Mirror of `EvalPipeline.succeeded`. -/
def epSucceeded {α : Type} (t : EPTrace α) : Bool :=
  match t.result with
  | .ok _    => true
  | .error _ => false

-- ── #check sanity ────────────────────────────────────────────────────────────

#check @epBind
#check @epReturn

-- ── Theorems ─────────────────────────────────────────────────────────────────

/-- `epReturn` always has empty stages (I6 from informal spec). -/
theorem epReturn_stages_empty {α : Type} (x : α) :
    (epReturn x).stages = [] := rfl

/-- `epReturn` always succeeds. -/
theorem epReturn_result_ok {α : Type} (x : α) :
    (epReturn x).result = .ok x := rfl

/-- `epZero` has empty stages. -/
theorem epZero_stages_empty : epZero.stages = [] := rfl

/-- `epSucceeded` is `true` iff result is `Ok` (I5 from informal spec). -/
theorem epSucceeded_iff_ok {α : Type} (t : EPTrace α) :
    epSucceeded t = true ↔ ∃ v, t.result = .ok v := by
  simp [epSucceeded]
  split <;> simp_all

/-- `epReturn` always satisfies `epSucceeded`. -/
theorem epReturn_succeeded {α : Type} (x : α) : epSucceeded (epReturn x) = true := rfl

/-- On the success path, `epBind` prepends the completed stage (I2 from informal spec). -/
theorem epBind_ok_stages {α β : Type}
    (tracked : EPTracked α) (v : α) (f : α → EPTrace β)
    (h : tracked.value = .ok v) :
    (epBind tracked f).stages = { name := tracked.stageName, outcome := .succeeded } :: (f v).stages := by
  simp [epBind, h]

/-- On the success path, `epBind` preserves the downstream result. -/
theorem epBind_ok_result {α β : Type}
    (tracked : EPTracked α) (v : α) (f : α → EPTrace β)
    (h : tracked.value = .ok v) :
    (epBind tracked f).result = (f v).result := by
  simp [epBind, h]

/-- On the error path, `epBind` never calls `f` — single stage in trace (I1 from informal spec). -/
theorem epBind_error_stages {α β : Type}
    (tracked : EPTracked α) (f : α → EPTrace β)
    (h : tracked.value = .error ()) :
    (epBind tracked f).stages = [{ name := tracked.stageName, outcome := .failed }] := by
  simp [epBind, h]

/-- On the error path, `epBind` produces exactly one stage. -/
theorem epBind_error_stages_length {α β : Type}
    (tracked : EPTracked α) (f : α → EPTrace β)
    (h : tracked.value = .error ()) :
    (epBind tracked f).stages.length = 1 := by
  simp [epBind, h]

/-- On the error path, `epBind` propagates the error. -/
theorem epBind_error_result {α β : Type}
    (tracked : EPTracked α) (f : α → EPTrace β)
    (h : tracked.value = .error ()) :
    (epBind tracked f).result = .error () := by
  simp [epBind, h]

/-- On the error path, `epSucceeded` is false. -/
theorem epBind_error_not_succeeded {α β : Type}
    (tracked : EPTracked α) (f : α → EPTrace β)
    (h : tracked.value = .error ()) :
    epSucceeded (epBind tracked f) = false := by
  simp [epBind, h, epSucceeded]

/-- On the success path, the outcome recorded for the stage is `succeeded`. -/
theorem epBind_ok_outcome {α β : Type}
    (tracked : EPTracked α) (v : α) (f : α → EPTrace β)
    (h : tracked.value = .ok v) :
    ((epBind tracked f).stages.head? >>= (fun s => some s.outcome)) = some .succeeded := by
  simp [epBind, h]

/-- On the error path, the outcome recorded for the stage is `failed`. -/
theorem epBind_error_outcome {α β : Type}
    (tracked : EPTracked α) (f : α → EPTrace β)
    (h : tracked.value = .error ()) :
    ((epBind tracked f).stages.head? >>= (fun s => some s.outcome)) = some .failed := by
  simp [epBind, h]

/-- Stage count on success path: 1 + rest stages. -/
theorem epBind_ok_stages_length {α β : Type}
    (tracked : EPTracked α) (v : α) (f : α → EPTrace β)
    (h : tracked.value = .ok v) :
    (epBind tracked f).stages.length = 1 + (f v).stages.length := by
  simp [epBind, h, List.length_cons, Nat.add_comm]

/-- `epSucceeded` is false iff result is `Error`. -/
theorem epSucceeded_false_iff_error {α : Type} (t : EPTrace α) :
    epSucceeded t = false ↔ ∃ e, t.result = .error e := by
  simp [epSucceeded]
  split <;> simp_all

/-- A two-step pipeline where step 1 fails has exactly 1 completed stage. -/
theorem two_step_first_fails_one_stage {β : Type}
    (s1 : String) (s2 : String) (f : Unit → EPTrace β) :
    let t1 : EPTracked Unit := { value := .error (), stageName := s1 }
    let t2 : EPTracked Unit := { value := .ok (),   stageName := s2 }
    (epBind t1 (fun _ => epBind t2 f)).stages.length = 1 := by
  simp [epBind]

/-- A two-step pipeline where step 1 succeeds: stages = 1 + rest. -/
theorem two_step_first_ok_stages {β : Type}
    (s1 s2 : String) (f : Unit → EPTrace β) :
    let t1 : EPTracked Unit := { value := .ok (),   stageName := s1 }
    let t2 : EPTracked Unit := { value := .error (), stageName := s2 }
    (epBind t1 (fun _ => epBind t2 f)).stages.length =
      1 + (epBind t2 f).stages.length := by
  simp [epBind, List.length_cons]

/-- When both steps in a two-step pipeline succeed and `f` returns `epReturn v`,
    the overall result is `.ok v`.  Demonstrates that success propagates end-to-end. -/
theorem two_step_both_ok_result {β : Type}
    (s1 s2 : String) (v : β) (f : Unit → EPTrace β)
    (hf : f () = epReturn v) :
    let t1 : EPTracked Unit := { value := .ok (), stageName := s1 }
    let t2 : EPTracked Unit := { value := .ok (), stageName := s2 }
    (epBind t1 (fun _ => epBind t2 f)).result = .ok v := by
  simp [epBind, hf, epReturn]

/-- The name of the first stage recorded by `epBind` is always the tracked item's name,
    regardless of success or failure.  This confirms the stage trace always starts
    with the stage that was actually executed. -/
theorem epBind_stage_name_is_tracked_name {α β : Type}
    (tracked : EPTracked α) (f : α → EPTrace β) :
    ((epBind tracked f).stages.head?).map (·.name) = some tracked.stageName := by
  simp only [epBind]
  cases tracked.value with
  | ok v  => simp
  | error _ => simp

/-- On the success path, `epSucceeded` of the bind equals `epSucceeded` of the
    downstream trace.  The current stage does not affect the overall success flag —
    only the final outcome of the pipeline matters. -/
theorem epSucceeded_bind_ok_eq {α β : Type}
    (tracked : EPTracked α) (v : α) (f : α → EPTrace β)
    (hv : tracked.value = .ok v) :
    epSucceeded (epBind tracked f) = epSucceeded (f v) := by
  simp [epBind, epSucceeded, hv]
