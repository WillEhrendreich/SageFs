/-!
# Formal Specification: WorkflowTypes.SessionWorkflow

> 🔬 Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.
> Source: `SageFs.Core/WorkflowTypes.fs`, module `SageFs.WorkflowTypes`

This file models the session-workflow DU that encodes the hot-reload/REPL tradeoff
in SageFs. The key insight: "hot reload + full REPL" is an illegal state that the
discriminated union makes structurally unrepresentable.

## Model abstractions
- `BrowserRefreshConfig.WatchPatterns : List String` is modelled as an opaque record field.
- `TransitionCost` numeric fields are `Nat` (non-negative integers), matching F# `int`.
- `WorkflowSwitchOutcome.Executed`'s `sessionId : String` is kept abstract.
- I/O, `System.TimeSpan`, environment variables, mutable state, and logging are omitted.
- `WorkflowDetection.suggest` (impure string matching) is not modelled.

## Properties proved (20 theorems)
1–2:   `feedbackStrategy` is correct for both cases.
3–4:   `replCapability` is correct for both cases.
5–6:   `fsiArgs` is correct for both cases.
7–8:   `isHotReloadActive` is correct for both cases.
9:     `label` values are non-empty.
10–11: `label` concrete values ("REPL", "Live").
12:    Illegal state is unrepresentable: no workflow has both hot-reload and full REPL.
13–14: `fromHotReloadBool` round-trip: `isHotReloadActive (fromHotReloadBool b) = b`.
15:    `TransitionCost.isZeroCost` iff both fields are zero.
16:    `TransitionCost.zero` is zero-cost.
17–19: `WorkflowSwitchOutcome.sessionId` is None iff not Executed; Some for Executed.
20:    `WorkflowSwitchOutcome.cost` extraction is correct for all cases.
-/

-- ============================================================
-- Types
-- ============================================================

/-- Browser refresh configuration. WatchPatterns is a list of file glob strings. -/
structure BrowserRefreshConfig where
  watchPatterns : List String
  deriving DecidableEq

/-- Default browser refresh config: watch *.fs and *.fsx files. -/
def BrowserRefreshConfig.defaults : BrowserRefreshConfig :=
  { watchPatterns := ["*.fs", "*.fsx"] }

/-- How the user wants to see their changes reflected.
    Determined at session creation — controls FSI compiler flags. -/
inductive FeedbackStrategy where
  /-- Full REPL: type redefinition, interactive exploration.
      FSI runs with default flags (multi-emit enabled). -/
  | ReplDriven : FeedbackStrategy
  /-- Hot reload: save → #load → Harmony patch → SSE refresh.
      FSI runs with --multiemit- (single assembly mode). -/
  | SaveDriven : BrowserRefreshConfig → FeedbackStrategy
  deriving DecidableEq

/-- What the REPL can do — derived from FeedbackStrategy, never set independently. -/
inductive ReplCapability where
  /-- Type/module redefinition, expression eval, everything. -/
  | Full : ReplCapability
  /-- Expression eval, function calls — no type/module redefinition. -/
  | ExpressionOnly : ReplCapability
  deriving DecidableEq

/-- The session workflow — what the user chose at session creation. -/
inductive SessionWorkflow where
  /-- Full REPL, no hot reload. The "exploring and prototyping" workflow. -/
  | Interactive : SessionWorkflow
  /-- Hot reload active, restricted REPL. The "building a web app" workflow. -/
  | WebLive : BrowserRefreshConfig → SessionWorkflow
  deriving DecidableEq

-- ============================================================
-- Projection functions (models of F# module functions)
-- ============================================================

/-- Derive the feedback strategy from the workflow. -/
def feedbackStrategy : SessionWorkflow → FeedbackStrategy
  | .Interactive  => .ReplDriven
  | .WebLive cfg  => .SaveDriven cfg

/-- Derive what the REPL can do. Total function. -/
def replCapability (w : SessionWorkflow) : ReplCapability :=
  match feedbackStrategy w with
  | .ReplDriven   => .Full
  | .SaveDriven _ => .ExpressionOnly

/-- Derive the extra FSI args needed for this workflow. -/
def fsiArgs : SessionWorkflow → List String
  | .Interactive => []
  | .WebLive _   => ["--multiemit-"]

/-- User-facing label. -/
def label : SessionWorkflow → String
  | .Interactive => "REPL"
  | .WebLive _   => "Live"

/-- Whether hot reload (Harmony patching) is active. -/
def isHotReloadActive : SessionWorkflow → Bool
  | .Interactive => false
  | .WebLive _   => true

/-- Default workflow — full REPL, no restrictions. -/
def defaultWorkflow : SessionWorkflow := .Interactive

/-- Convert from the legacy bool representation. -/
def fromHotReloadBool : Bool → SessionWorkflow
  | true  => .WebLive BrowserRefreshConfig.defaults
  | false => .Interactive

-- ============================================================
-- TransitionCost (pure subset — numeric fields only)
-- ============================================================

/-- What the user will lose when switching workflows. Numeric fields as Nat. -/
structure TransitionCost where
  definitionsLost  : Nat
  cellsLost        : Nat
  standbyReady     : Bool
  deriving DecidableEq

/-- Zero-cost switch cost. -/
def TransitionCost.zero : TransitionCost :=
  { definitionsLost := 0, cellsLost := 0, standbyReady := false }

/-- Zero-cost transitions skip confirmation. -/
def TransitionCost.isZeroCost (c : TransitionCost) : Bool :=
  c.definitionsLost == 0 && c.cellsLost == 0

-- ============================================================
-- WorkflowSwitchOutcome
-- ============================================================

/-- Outcome of a switch_workflow call. -/
inductive WorkflowSwitchOutcome where
  | AlreadyActive  : TransitionCost → String → WorkflowSwitchOutcome
  | DryRunPreview  : TransitionCost → String → WorkflowSwitchOutcome
  | Executed       : SessionWorkflow → SessionWorkflow → TransitionCost → String → String → WorkflowSwitchOutcome

/-- Extract cost from any outcome. -/
def WorkflowSwitchOutcome.cost : WorkflowSwitchOutcome → TransitionCost
  | .AlreadyActive c _         => c
  | .DryRunPreview c _         => c
  | .Executed _ _ c _ _        => c

/-- Extract session ID (only present for Executed outcomes). -/
def WorkflowSwitchOutcome.sessionId : WorkflowSwitchOutcome → Option String
  | .Executed _ _ _ sid _      => some sid
  | _                          => none

/-- Whether the outcome represents an actual switch execution. -/
def WorkflowSwitchOutcome.wasExecuted : WorkflowSwitchOutcome → Bool
  | .Executed _ _ _ _ _ => true
  | _                   => false

-- ============================================================
-- Theorems
-- ============================================================

-- 1. feedbackStrategy Interactive = ReplDriven
theorem feedbackStrategy_interactive :
    feedbackStrategy .Interactive = .ReplDriven := rfl

-- 2. feedbackStrategy (WebLive cfg) = SaveDriven cfg
theorem feedbackStrategy_webLive (cfg : BrowserRefreshConfig) :
    feedbackStrategy (.WebLive cfg) = .SaveDriven cfg := rfl

-- 3. replCapability Interactive = Full
theorem replCapability_interactive :
    replCapability .Interactive = .Full := rfl

-- 4. replCapability (WebLive cfg) = ExpressionOnly
theorem replCapability_webLive (cfg : BrowserRefreshConfig) :
    replCapability (.WebLive cfg) = .ExpressionOnly := rfl

-- 5. fsiArgs Interactive = []
theorem fsiArgs_interactive :
    fsiArgs .Interactive = [] := rfl

-- 6. fsiArgs (WebLive _) = ["--multiemit-"]
theorem fsiArgs_webLive (cfg : BrowserRefreshConfig) :
    fsiArgs (.WebLive cfg) = ["--multiemit-"] := rfl

-- 7. isHotReloadActive Interactive = false
theorem isHotReloadActive_interactive :
    isHotReloadActive .Interactive = false := rfl

-- 8. isHotReloadActive (WebLive _) = true
theorem isHotReloadActive_webLive (cfg : BrowserRefreshConfig) :
    isHotReloadActive (.WebLive cfg) = true := rfl

-- 9. label is non-empty for all workflows
theorem label_nonEmpty (w : SessionWorkflow) :
    label w ≠ "" := by
  cases w <;> simp [label]

-- 10. label Interactive = "REPL"
theorem label_interactive :
    label .Interactive = "REPL" := rfl

-- 11. label (WebLive _) = "Live"
theorem label_webLive (cfg : BrowserRefreshConfig) :
    label (.WebLive cfg) = "Live" := rfl

-- 12. ILLEGAL STATE UNREPRESENTABLE:
--     No workflow can simultaneously have hot reload active and full REPL capability.
theorem no_hotReload_and_fullRepl (w : SessionWorkflow) :
    ¬ (isHotReloadActive w = true ∧ replCapability w = .Full) := by
  cases w <;> simp [isHotReloadActive, replCapability, feedbackStrategy]

-- 13. fromHotReloadBool false = Interactive
theorem fromHotReloadBool_false :
    fromHotReloadBool false = .Interactive := rfl

-- 14. isHotReloadActive ∘ fromHotReloadBool is identity on Bool
theorem fromHotReloadBool_roundtrip (b : Bool) :
    isHotReloadActive (fromHotReloadBool b) = b := by
  cases b <;> rfl

-- 15. isZeroCost iff both fields are zero
theorem isZeroCost_iff (c : TransitionCost) :
    c.isZeroCost = true ↔ c.definitionsLost = 0 ∧ c.cellsLost = 0 := by
  simp [TransitionCost.isZeroCost, Bool.and_eq_true, beq_iff_eq]

-- 16. TransitionCost.zero is zero-cost
theorem zeroCost_isZeroCost :
    TransitionCost.zero.isZeroCost = true := rfl

-- 17. sessionId is None for AlreadyActive
theorem sessionId_alreadyActive (c : TransitionCost) (m : String) :
    WorkflowSwitchOutcome.sessionId (.AlreadyActive c m) = none := rfl

-- 18. sessionId is None for DryRunPreview
theorem sessionId_dryRun (c : TransitionCost) (m : String) :
    WorkflowSwitchOutcome.sessionId (.DryRunPreview c m) = none := rfl

-- 19. sessionId returns Some for Executed
theorem sessionId_executed
    (prev tgt : SessionWorkflow) (c : TransitionCost) (sid msg : String) :
    WorkflowSwitchOutcome.sessionId (.Executed prev tgt c sid msg) = some sid := rfl

-- 20. cost extraction is correct for all outcome cases
theorem cost_extraction_correct :
    (∀ c m, WorkflowSwitchOutcome.cost (.AlreadyActive c m) = c) ∧
    (∀ c m, WorkflowSwitchOutcome.cost (.DryRunPreview c m) = c) ∧
    (∀ prev tgt c sid msg, WorkflowSwitchOutcome.cost (.Executed prev tgt c sid msg) = c) :=
  ⟨fun _ _ => rfl, fun _ _ => rfl, fun _ _ _ _ _ => rfl⟩
