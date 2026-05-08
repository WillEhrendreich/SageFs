/-!
  SmartReset.lean
  🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*
  Source: `SageFs.Core/SmartReset.fs`

  Formalises the **pure escalation logic** of `SmartReset.execute`:
  try soft reset first; if it fails, escalate to hard reset.

  ## Design

  `SmartReset.execute` is an async function that:
  1. Calls `softReset ()` and awaits the result.
  2. If the soft reset succeeds, returns `SoftResetSucceeded`.
  3. If it fails, calls `hardReset ()` and awaits that result.
  4. If the hard reset succeeds, returns `EscalatedToHardReset msg`.
  5. If both fail, returns `AllResetsFailed(softErr, hardErr)`.

  ## Model

  We model the Task<_>/async computation as a pure function of two
  synchronous results.  `SageFsError` is abstracted as `String`
  (we care only that errors carry a message, not their precise type).

  ## Key properties proved

  - Outcome is exactly determined by the two input results.
  - `SoftResetSucceeded` ↔ soft reset returned `Ok`.
  - `AllResetsFailed e1 e2` ↔ soft returned `Error e1` AND hard returned `Error e2`.
  - `EscalatedToHardReset msg` ↔ soft failed AND hard returned `Ok msg`.
  - Soft failure never silently produces `SoftResetSucceeded`.
  - Soft success never produces `AllResetsFailed`.
  - Every outcome is one of the three cases (exhaustiveness by construction).

  ## Abstractions / omissions

  - `Task<_>` / async execution modelled as pure synchronous functions.
  - `SageFsError` abstracted as `String`.
  - Timing and logging are omitted.
  - The `execute` function's `TaskCancellationToken` is not modelled.

  No Mathlib. Pure Lean 4 stdlib only (network firewalled in CI).
  Source: `SageFs.Core/SmartReset.fs`
-/

namespace SmartReset

-- ── Types ─────────────────────────────────────────────────────────────────────

/-- Mirrors F# `SmartReset.Outcome` — the result of a one-button smart reset. -/
inductive SROutcome where
  /-- Soft reset completed successfully. No escalation was needed. -/
  | SoftResetSucceeded : SROutcome
  /-- Soft reset failed; hard reset succeeded. `msg` is from the hard-reset handler. -/
  | EscalatedToHardReset (msg : String) : SROutcome
  /-- Both soft and hard reset failed. Carries both error messages. -/
  | AllResetsFailed (softError hardError : String) : SROutcome
  deriving DecidableEq, Repr

-- ── Implementation model ──────────────────────────────────────────────────────

/-- Pure model of `SmartReset.execute`.
    `softResult` mirrors the result of awaiting `softReset ()`.
    `hardResult` mirrors the result of awaiting `hardReset ()`.
    The hard reset is only "called" when soft fails — modelled here as a
    plain value (laziness doesn't affect the correctness properties). -/
def smartResetLogic
    (softResult : Except String Unit)
    (hardResult : Except String String) : SROutcome :=
  match softResult with
  | .ok ()         => .SoftResetSucceeded
  | .error softErr =>
    match hardResult with
    | .ok msg      => .EscalatedToHardReset msg
    | .error hardErr => .AllResetsFailed softErr hardErr

/-- Mirrors F# `SmartReset.describe` — human-readable description of each outcome. -/
def srDescribe : SROutcome → String
  | .SoftResetSucceeded            => "Session reset. All definitions cleared."
  | .EscalatedToHardReset msg      =>
      "Soft reset failed — escalated to hard reset. " ++ msg
  | .AllResetsFailed softErr hardErr =>
      "All resets failed. Soft: " ++ softErr ++ ". Hard: " ++ hardErr

-- ── #check sanity ─────────────────────────────────────────────────────────────

#check @smartResetLogic
#check @srDescribe

-- ── Outcome correctness theorems ─────────────────────────────────────────────

/-- When soft reset succeeds, the outcome is always `SoftResetSucceeded`.
    No escalation occurs. -/
theorem smartReset_soft_ok (hard : Except String String) :
    smartResetLogic (.ok ()) hard = .SoftResetSucceeded := by
  simp [smartResetLogic]

/-- When soft reset fails but hard reset succeeds, outcome is `EscalatedToHardReset`
    carrying the success message from the hard-reset handler. -/
theorem smartReset_escalated (softErr msg : String) :
    smartResetLogic (.error softErr) (.ok msg) = .EscalatedToHardReset msg := by
  simp [smartResetLogic]

/-- When both resets fail, outcome is `AllResetsFailed` carrying both error messages
    in order: soft error first, hard error second.  The order matches the F# source. -/
theorem smartReset_all_failed (softErr hardErr : String) :
    smartResetLogic (.error softErr) (.error hardErr) = .AllResetsFailed softErr hardErr := by
  simp [smartResetLogic]

-- ── Biconditional characterizations ──────────────────────────────────────────

/-- Outcome is `SoftResetSucceeded` if and only if the soft reset succeeded. -/
theorem smartReset_succeeded_iff
    (soft : Except String Unit) (hard : Except String String) :
    smartResetLogic soft hard = .SoftResetSucceeded ↔ soft = .ok () := by
  cases soft with
  | ok u  => cases u; simp [smartResetLogic]
  | error e =>
    simp only [smartResetLogic]
    cases hard <;> simp

/-- Soft failure **never** produces `SoftResetSucceeded`. -/
theorem smartReset_soft_fail_never_succeeded
    (e : String) (hard : Except String String) :
    smartResetLogic (.error e) hard ≠ .SoftResetSucceeded := by
  cases hard <;> simp [smartResetLogic]

/-- Soft success **never** produces `AllResetsFailed`. -/
theorem smartReset_soft_ok_not_all_failed
    (hard : Except String String) (e1 e2 : String) :
    smartResetLogic (.ok ()) hard ≠ .AllResetsFailed e1 e2 := by
  simp [smartResetLogic]

/-- `AllResetsFailed e1 e2` if and only if soft returned `Error e1` and hard returned
    `Error e2`.  Both error messages are captured exactly. -/
theorem smartReset_all_failed_iff
    (soft : Except String Unit) (hard : Except String String) (e1 e2 : String) :
    smartResetLogic soft hard = .AllResetsFailed e1 e2 ↔ soft = .error e1 ∧ hard = .error e2 := by
  constructor
  · intro h
    cases soft with
    | ok u  => cases u; simp [smartResetLogic] at h
    | error e =>
      cases hard with
      | ok msg   => simp [smartResetLogic] at h
      | error e' =>
        simp only [smartResetLogic] at h
        simp only [SROutcome.AllResetsFailed.injEq] at h
        obtain ⟨rfl, rfl⟩ := h
        exact ⟨rfl, rfl⟩
  · rintro ⟨rfl, rfl⟩
    simp [smartResetLogic]

/-- `EscalatedToHardReset msg` iff soft failed and hard returned `Ok msg`. -/
theorem smartReset_escalated_iff
    (soft : Except String Unit) (hard : Except String String) (msg : String) :
    smartResetLogic soft hard = .EscalatedToHardReset msg ↔
    (∃ e, soft = .error e) ∧ hard = .ok msg := by
  constructor
  · intro h
    cases soft with
    | ok u  => cases u; simp [smartResetLogic] at h
    | error e =>
      cases hard with
      | ok msg' =>
        simp only [smartResetLogic] at h
        simp only [SROutcome.EscalatedToHardReset.injEq] at h
        exact ⟨⟨e, rfl⟩, congrArg Except.ok h⟩
      | error e' => simp [smartResetLogic] at h
  · rintro ⟨⟨e, rfl⟩, rfl⟩
    simp [smartResetLogic]

end SmartReset
