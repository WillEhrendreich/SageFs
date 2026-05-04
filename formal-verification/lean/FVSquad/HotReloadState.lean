/-
  HotReloadState.lean
  Formal specification and proofs for SageFs hot-reload file tracking.

  Target: `HotReloadState` module in `SageFs.Core/HotReloadState.fs`

  What is modelled:
  - `HRS` (state record): `watched : List String`
  - `isWatched`, `watch`, `unwatch`, `toggle`
  - `watchMany`, `unwatchAll`, `watchAll`, `watchedCount`
  - `watchByDirectory`, `unwatchByDirectory`, `watchedInDirectory`

  Model abstractions:
  - `normalize` is modelled as identity (path normalisation is irrelevant to
    the set-theoretic invariants verified here).
  - `watchByDirectory` uses a simplified `dirOf` that strips the last
    path component (everything before the last '/').
  - The F# `Set<string>` is modelled as `List String` with an idempotent
    `watch` (no-op if already present) preserving absence of duplicates.

  No Mathlib. Pure Lean 4 stdlib only (network firewalled in sandbox/CI).
  Source: SageFs.Core/HotReloadState.fs

  🔬 Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.
-/

namespace HotReloadState

-- ─────────────────────────────────────────────────────────────────────────────
-- Type definition
-- ─────────────────────────────────────────────────────────────────────────────

/-- State tracking which file paths are opted-in for hot-reload.
    Mirrors F# `HotReloadState.T`. -/
structure HRS where
  watched : List String
  deriving Repr, DecidableEq

-- ─────────────────────────────────────────────────────────────────────────────
-- Operations
-- ─────────────────────────────────────────────────────────────────────────────

def empty : HRS := { watched := [] }

def isWatched (p : String) (s : HRS) : Bool := s.watched.contains p

/-- Add a path (idempotent — no-op if already present). -/
def watch (p : String) (s : HRS) : HRS :=
  if s.watched.contains p then s
  else { s with watched := p :: s.watched }

/-- Remove all occurrences of a path (no-op if absent). -/
def unwatch (p : String) (s : HRS) : HRS :=
  { s with watched := s.watched.filter (fun x => x != p) }

/-- Toggle membership. -/
def toggle (p : String) (s : HRS) : HRS :=
  if s.watched.contains p
  then { s with watched := s.watched.filter (fun x => x != p) }
  else { s with watched := p :: s.watched }

/-- Add many paths (fold over watch). -/
def watchMany (ps : List String) (s : HRS) : HRS :=
  ps.foldl (fun acc p => watch p acc) s

def unwatchAll (_ : HRS) : HRS := empty

def watchAll (ps : List String) (_ : HRS) : HRS :=
  ps.foldl (fun acc p => watch p acc) empty

def watchedCount (s : HRS) : Nat := s.watched.length

-- ─────────────────────────────────────────────────────────────────────────────
-- Directory helpers
-- ─────────────────────────────────────────────────────────────────────────────

/-- Directory of a path: everything before the last '/'. -/
def dirOf (p : String) : String :=
  match p.splitOn "/" |>.reverse with
  | _ :: rest => "/".intercalate rest.reverse
  | [] => ""

def watchByDirectory (dir : String) (allPaths : List String) (s : HRS) : HRS :=
  let matching := allPaths.filter (fun p =>
    let d := dirOf p
    d == dir || d.startsWith (dir ++ "/"))
  watchMany matching s

def unwatchByDirectory (dir : String) (s : HRS) : HRS :=
  { s with watched := s.watched.filter (fun p =>
    let d := dirOf p
    !(d == dir || d.startsWith (dir ++ "/"))) }

def watchedInDirectory (dir : String) (s : HRS) : List String :=
  s.watched.filter (fun p =>
    let d := dirOf p
    d == dir || d.startsWith (dir ++ "/"))

-- ─────────────────────────────────────────────────────────────────────────────
-- Private helpers
-- ─────────────────────────────────────────────────────────────────────────────

private theorem isWatched_iff (p : String) (s : HRS) :
    isWatched p s = true ↔ p ∈ s.watched :=
  List.contains_iff_mem

private theorem isWatched_false_iff (p : String) (s : HRS) :
    isWatched p s = false ↔ p ∉ s.watched := by
  simp [isWatched, Bool.eq_false_iff]

-- After adding p to front, p is contained.
private theorem contains_cons_self (p : String) (l : List String) :
    (p :: l).contains p = true := by simp

-- Filtering by ≠ p removes p entirely.
private theorem contains_filter_ne (p : String) (l : List String) :
    (l.filter (fun x => x != p)).contains p = false := by simp

-- watchMany unfolds one step.
private theorem watchMany_cons (q : String) (qs : List String) (s : HRS) :
    watchMany (q :: qs) s = watchMany qs (watch q s) := by
  simp [watchMany, List.foldl_cons]

-- Helper: watch preserves existing membership.
private theorem watch_preserves_contained (q p : String) (s : HRS)
    (h : s.watched.contains p = true) :
    (watch q s).watched.contains p = true := by
  unfold watch
  by_cases hq : s.watched.contains q = true
  · rw [if_pos hq]; exact h
  · rw [if_neg hq]
    rw [List.contains_cons]
    by_cases hpq : p = q
    · subst hpq; simp
    · rw [beq_false_of_ne hpq]; simp [List.contains_iff_mem.mp h]

-- watchMany preserves existing membership.
private theorem watchMany_preserves (p : String) (ps : List String) (s : HRS)
    (h : isWatched p s = true) :
    isWatched p (watchMany ps s) = true := by
  induction ps generalizing s with
  | nil => simpa [watchMany]
  | cons q qs ih =>
    rw [watchMany_cons]
    apply ih
    exact watch_preserves_contained q p s h

-- ─────────────────────────────────────────────────────────────────────────────
-- Theorems: watch
-- ─────────────────────────────────────────────────────────────────────────────

theorem watch_makes_watched (p : String) (s : HRS) :
    isWatched p (watch p s) = true := by
  simp only [watch, isWatched]
  by_cases h : s.watched.contains p = true
  · rw [if_pos h]; exact h
  · rw [if_neg h]
    exact contains_cons_self p s.watched

theorem watch_idempotent (p : String) (s : HRS) :
    watch p (watch p s) = watch p s := by
  unfold watch
  by_cases h : s.watched.contains p = true
  · simp only [if_pos h]
  · simp only [if_neg h]
    have hc : (p :: s.watched).contains p = true := by simp
    simp only [if_pos hc]

theorem watch_preserves_other (p q : String) (s : HRS) (h : p ≠ q) :
    isWatched q (watch p s) = isWatched q s := by
  simp only [watch, isWatched]
  by_cases hc : s.watched.contains p = true
  · rw [if_pos hc]
  · rw [if_neg hc]
    rw [List.contains_cons, beq_false_of_ne (Ne.symm h)]
    simp

-- ─────────────────────────────────────────────────────────────────────────────
-- Theorems: unwatch
-- ─────────────────────────────────────────────────────────────────────────────

theorem unwatch_not_watched (p : String) (s : HRS) :
    isWatched p (unwatch p s) = false := by
  simp [unwatch, isWatched]

theorem unwatch_preserves_other (p q : String) (s : HRS) (h : p ≠ q) :
    isWatched q (unwatch p s) = isWatched q s := by
  simp only [unwatch, isWatched]
  rw [Bool.eq_iff_iff, List.contains_iff_mem, List.contains_iff_mem,
      List.mem_filter, bne_iff_ne]
  constructor
  · rintro ⟨hm, _⟩; exact hm
  · intro hm; exact ⟨hm, fun heq => h heq.symm⟩

theorem unwatch_watch_new (p : String) (s : HRS) (hn : isWatched p s = false) :
    unwatch p (watch p s) = s := by
  have hmem : p ∉ s.watched := (isWatched_false_iff p s).mp hn
  have hc : s.watched.contains p = false := hn
  simp only [watch, unwatch]
  rw [if_neg (by rw [hc]; decide)]
  cases s with
  | mk w =>
    simp only
    congr 1
    rw [List.filter_cons, show (p != p) = false from by simp]
    simp only [if_neg (show ¬ false = true from by decide)]
    apply List.filter_eq_self.mpr
    intro x hx
    exact bne_iff_ne.mpr (fun heq => hmem (heq ▸ hx))

-- ─────────────────────────────────────────────────────────────────────────────
-- Theorems: toggle
-- ─────────────────────────────────────────────────────────────────────────────

theorem toggle_adds_unwatched (p : String) (s : HRS) (h : isWatched p s = false) :
    isWatched p (toggle p s) = true := by
  simp only [toggle, isWatched] at *
  simp only [h]
  exact contains_cons_self p s.watched

theorem toggle_removes_watched (p : String) (s : HRS) (h : isWatched p s = true) :
    isWatched p (toggle p s) = false := by
  simp only [toggle, isWatched] at *
  simp only [h, ↓reduceIte]
  exact contains_filter_ne p s.watched

theorem toggle_involution (p : String) (s : HRS) :
    isWatched p (toggle p (toggle p s)) = isWatched p s := by
  by_cases h : isWatched p s = true
  · have h1 := toggle_removes_watched p s h
    have h2 := toggle_adds_unwatched p (toggle p s) h1
    simp [h2, h]
  · simp only [Bool.not_eq_true] at h
    have h1 := toggle_adds_unwatched p s h
    have h2 := toggle_removes_watched p (toggle p s) h1
    simp [h2, h]

-- ─────────────────────────────────────────────────────────────────────────────
-- Theorems: watchMany
-- ─────────────────────────────────────────────────────────────────────────────

theorem watchMany_makes_watched (p : String) (ps : List String) (s : HRS)
    (h : p ∈ ps) :
    isWatched p (watchMany ps s) = true := by
  induction ps generalizing s with
  | nil => exact absurd h List.not_mem_nil
  | cons q qs ih =>
    rw [watchMany_cons]
    cases List.mem_cons.mp h with
    | inl heq =>
      subst heq
      exact watchMany_preserves p qs (watch p s) (watch_makes_watched p s)
    | inr htail =>
      exact ih (watch q s) htail

theorem watchMany_preserves_watched (p : String) (ps : List String) (s : HRS)
    (h : isWatched p s = true) :
    isWatched p (watchMany ps s) = true :=
  watchMany_preserves p ps s h

theorem watchMany_nil (s : HRS) : watchMany [] s = s := by
  simp [watchMany]

-- ─────────────────────────────────────────────────────────────────────────────
-- Theorems: unwatchAll / watchAll
-- ─────────────────────────────────────────────────────────────────────────────

theorem unwatchAll_is_empty (s : HRS) : unwatchAll s = empty := rfl

theorem unwatchAll_clears (p : String) (s : HRS) :
    isWatched p (unwatchAll s) = false := by
  simp [unwatchAll, empty, isWatched]

theorem watchAll_ignores_prior (ps : List String) (s1 s2 : HRS) :
    watchAll ps s1 = watchAll ps s2 := by
  simp [watchAll]

theorem watchAll_nil (s : HRS) : watchAll [] s = empty := by
  simp [watchAll]

theorem watchAll_makes_watched (p : String) (ps : List String) (s : HRS)
    (h : p ∈ ps) :
    isWatched p (watchAll ps s) = true := by
  simp only [watchAll]
  exact watchMany_makes_watched p ps empty h

-- ─────────────────────────────────────────────────────────────────────────────
-- Theorems: watchedCount
-- ─────────────────────────────────────────────────────────────────────────────

theorem watchedCount_empty : watchedCount empty = 0 := by
  simp [watchedCount, empty]

theorem watchedCount_watch_new (p : String) (s : HRS) (h : isWatched p s = false) :
    watchedCount (watch p s) = watchedCount s + 1 := by
  simp only [watch, isWatched] at *
  rw [if_neg (by rw [h]; decide)]
  simp [watchedCount]

theorem watchedCount_watch_existing (p : String) (s : HRS) (h : isWatched p s = true) :
    watchedCount (watch p s) = watchedCount s := by
  simp only [watch, isWatched] at *
  rw [if_pos h]

-- ─────────────────────────────────────────────────────────────────────────────
-- Theorems: directory operations
-- ─────────────────────────────────────────────────────────────────────────────

theorem unwatchByDirectory_removes (dir p : String) (s : HRS)
    (hd : (dirOf p == dir) = true) :
    isWatched p (unwatchByDirectory dir s) = false := by
  simp only [unwatchByDirectory, isWatched]
  apply Bool.eq_false_iff.mpr
  intro hc
  have hmem := List.contains_iff_mem.mp hc
  rw [List.mem_filter] at hmem
  obtain ⟨_, hkeep⟩ := hmem
  simp [hd] at hkeep

theorem watchByDirectory_adds (dir p : String) (allPaths : List String)
    (s : HRS) (hp : p ∈ allPaths) (hd : (dirOf p == dir) = true) :
    isWatched p (watchByDirectory dir allPaths s) = true := by
  simp only [watchByDirectory]
  apply watchMany_makes_watched
  simp only [List.mem_filter]
  exact ⟨hp, by simp [hd]⟩

theorem watchedInDirectory_spec (dir p : String) (s : HRS)
    (h : p ∈ watchedInDirectory dir s) :
    p ∈ s.watched := by
  simp only [watchedInDirectory, List.mem_filter] at h
  exact h.1

end HotReloadState
