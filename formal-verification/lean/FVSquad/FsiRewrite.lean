/-!
# Formal Specification: FsiRewrite

Models `rewriteInlineUseStatements` from `SageFs.Core/FsiRewrite.fs`.

This function rewrites **indented** `use x = expr` F# bindings to `let x = expr` so
they can run in F# Interactive (FSI), where `use` is not valid in expression context.
The rewrite is per-line: only lines with leading whitespace AND whose trimmed form
starts with `"use "` are modified.

## Key theorems

- `rewriteLine_idempotent`: applying the line rewrite twice = applying it once.
- `rewriteLine_length_preserved`: `"use "` and `"let "` are both 4 chars.
- `rewriteLines_idempotent`: idempotency extends to lists of lines.
- `rewriteLines_length_preserved`: number of lines is unchanged.

## Model

- Lines are `List Char`. The split/join on `'\n'` is abstracted away.
- Whitespace = `' '` or `'\t'`.
- `dropWs` models `String.TrimStart`.

## Abstractions / omissions

- `'\r'` and other Unicode whitespace are not modelled.
- The `mutable rewritten` flag is abstracted.
- Error handling, encoding, and I/O are not modelled.

> 🔬 Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.
> Source: `SageFs.Core/FsiRewrite.fs`
> No Mathlib. Pure Lean 4 stdlib only (CI firewall blocks lakecache).
-/

namespace FsiRewrite

-- ── Primitive definitions ─────────────────────────────────────────────────────

/-- Characters treated as whitespace (space or tab). -/
@[inline] def isWs (c : Char) : Bool := c == ' ' || c == '\t'

/-- A list is all-whitespace. -/
def allWs : List Char → Bool
  | []      => true
  | c :: cs => isWs c && allWs cs

/-- Drop leading whitespace (models `String.TrimStart`). -/
def dropWs : List Char → List Char
  | []      => []
  | c :: cs => if isWs c then dropWs cs else c :: cs

/-- Prefix match on `List Char` (models `String.StartsWith`). -/
def startsWith4 : List Char → List Char → Bool
  | _,       []      => true
  | [],      _ :: _  => false
  | c :: cs, p :: ps => (c == p) && startsWith4 cs ps

/-- A line is an "indented use" if it starts with whitespace and after trimming starts
    with `"use "`. -/
def hasLeadingWs : List Char → Bool
  | []     => false
  | c :: _ => isWs c

def isIndentedUse (line : List Char) : Bool :=
  hasLeadingWs line && startsWith4 (dropWs line) ['u', 's', 'e', ' ']

/-- Per-line rewrite: replace the leading `"use "` with `"let "` on indented lines. -/
def rewriteLine (line : List Char) : List Char :=
  if isIndentedUse line then
    let trimmed := dropWs line
    let n       := line.length - trimmed.length
    line.take n ++ ['l', 'e', 't', ' '] ++ trimmed.drop 4
  else
    line

-- ── Bool helper (stdlib-only, no Mathlib) ────────────────────────────────────

private theorem ne_true_eq_false {b : Bool} (h : ¬(b = true)) : b = false := by
  match b with
  | false => rfl
  | true  => exact False.elim (h rfl)

-- ── Basic lemmas about dropWs ─────────────────────────────────────────────────

@[simp] theorem dropWs_nil : dropWs [] = [] := rfl

theorem dropWs_ws {c : Char} (cs : List Char) (h : isWs c = true) :
    dropWs (c :: cs) = dropWs cs := by simp [dropWs, h]

theorem dropWs_non_ws {c : Char} (cs : List Char) (h : isWs c = false) :
    dropWs (c :: cs) = c :: cs := by simp [dropWs, h]

/-- `dropWs` never makes a list longer. -/
theorem dropWs_length_le (line : List Char) : (dropWs line).length ≤ line.length := by
  induction line with
  | nil => simp [dropWs]
  | cons c cs ih =>
    by_cases hc : isWs c = true
    · rw [dropWs_ws cs hc, List.length_cons]; omega
    · rw [dropWs_non_ws cs (ne_true_eq_false hc)]; omega

/-- `dropWs` is idempotent. -/
theorem dropWs_idempotent (line : List Char) :
    dropWs (dropWs line) = dropWs line := by
  induction line with
  | nil => rfl
  | cons c cs ih =>
    by_cases hc : isWs c = true
    · rw [dropWs_ws cs hc]; exact ih
    · have hf := ne_true_eq_false hc
      rw [dropWs_non_ws cs hf]; exact dropWs_non_ws cs hf

/-- An all-whitespace prefix is transparent to `dropWs`. -/
theorem dropWs_allWs_pfx (pfx : List Char) (rest : List Char)
    (h : allWs pfx = true) :
    dropWs (pfx ++ rest) = dropWs rest := by
  induction pfx with
  | nil => simp
  | cons c cs ih =>
    simp only [allWs, Bool.and_eq_true] at h
    obtain ⟨hc, hcs⟩ := h
    simp only [List.cons_append]
    rw [dropWs_ws (cs ++ rest) hc]
    exact ih hcs

-- ── The prefix before `dropWs` is all-whitespace ─────────────────────────────

/-- The chars stripped by `dropWs` (the `take` prefix) are all whitespace. -/
theorem allWs_take_dropWs_pfx (line : List Char) :
    allWs (line.take (line.length - (dropWs line).length)) = true := by
  induction line with
  | nil => rfl
  | cons c cs ih =>
    by_cases hc : isWs c = true
    · rw [dropWs_ws cs hc, List.length_cons]
      have hle : (dropWs cs).length ≤ cs.length := dropWs_length_le cs
      rw [show cs.length + 1 - (dropWs cs).length = (cs.length - (dropWs cs).length) + 1 by omega]
      -- (c :: cs).take (k + 1) definitionally = c :: cs.take k
      show allWs (c :: cs.take (cs.length - (dropWs cs).length)) = true
      simp [allWs, hc, ih]
    · have hf := ne_true_eq_false hc
      rw [dropWs_non_ws cs hf]
      simp [allWs]

-- ── dropWs on a 'l'-prefixed list is identity ────────────────────────────────

/-- `dropWs` on a list starting with `'l'` (not whitespace) is the identity.
    Bridges the syntactic gap between `['l','e','t',' '] ++ rest` and `'l' :: ...`. -/
private theorem dropWs_l_pfx (rest : List Char) :
    dropWs (['l', 'e', 't', ' '] ++ rest) = ['l', 'e', 't', ' '] ++ rest := by
  -- ['l','e','t',' '] ++ rest is definitionally 'l' :: ('e' :: ('t' :: (' ' :: rest)))
  show dropWs ('l' :: ('e' :: ('t' :: (' ' :: rest)))) = 'l' :: ('e' :: ('t' :: (' ' :: rest)))
  exact dropWs_non_ws ('e' :: ('t' :: (' ' :: rest))) (by decide)

-- ── "let " ≠ "use " ──────────────────────────────────────────────────────────

/-- `"let "` does not start with `"use "` — 'l' ≠ 'u'. -/
@[simp] theorem startsWith4_let_not_use (rest : List Char) :
    startsWith4 (['l', 'e', 't', ' '] ++ rest) ['u', 's', 'e', ' '] = false := by
  have hne : (('l' : Char) == 'u') = false := by decide
  simp [startsWith4, List.cons_append, List.nil_append, hne]

-- ── rewriteLine identity on non-use lines ────────────────────────────────────

/-- A line that is not an indented use is unchanged. -/
theorem rewriteLine_non_use (line : List Char) (h : isIndentedUse line = false) :
    rewriteLine line = line := by
  unfold rewriteLine; simp [h]

-- ── After rewriting, the line is no longer an indented use ───────────────────

/-- After rewriting an indented-use line, the result is not an indented use.
    Core chain: take-prefix is all-ws → `dropWs` sees through it → gets `'l','e','t',' '`
    → `startsWith4` returns false because `'l' ≠ 'u'`. -/
theorem isIndentedUse_rewriteLine_false (line : List Char) :
    isIndentedUse (rewriteLine line) = false := by
  unfold rewriteLine
  by_cases h : isIndentedUse line = true
  · -- Positive: rewriteLine produced take n ++ ['l','e','t',' '] ++ rest.
    rw [if_pos h, List.append_assoc]
    simp only [isIndentedUse, Bool.and_eq_false_iff]
    right
    rw [dropWs_allWs_pfx _ _ (allWs_take_dropWs_pfx line)]
    rw [dropWs_l_pfx]
    exact startsWith4_let_not_use _
  · -- Negative: rewriteLine was identity; result is isIndentedUse line = false.
    rw [if_neg h]
    exact ne_true_eq_false h

-- ── Idempotency ───────────────────────────────────────────────────────────────

/-- **Core theorem**: applying the line rewrite twice is the same as applying it once.
    After the first rewrite, `isIndentedUse` is false, so the second is a no-op. -/
theorem rewriteLine_idempotent (line : List Char) :
    rewriteLine (rewriteLine line) = rewriteLine line :=
  rewriteLine_non_use _ (isIndentedUse_rewriteLine_false line)

-- ── Length preservation ───────────────────────────────────────────────────────

/-- `startsWith4` implies length lower bound. -/
theorem startsWith4_length_le {line pfx : List Char} (h : startsWith4 line pfx = true) :
    pfx.length ≤ line.length := by
  induction pfx generalizing line with
  | nil => simp
  | cons _ ps ih =>
    cases line with
    | nil => simp [startsWith4] at h
    | cons _ cs =>
      simp only [startsWith4, Bool.and_eq_true] at h
      simp only [List.length_cons]
      exact Nat.succ_le_succ (ih h.2)

/-- An indented-use line has at least 4 chars after trimming. -/
theorem use_prefix_length_le {line : List Char} (h : isIndentedUse line = true) :
    4 ≤ (dropWs line).length := by
  simp only [isIndentedUse, Bool.and_eq_true] at h
  exact startsWith4_length_le h.2

/-- The rewrite preserves the character count of each line.
    `"use "` and `"let "` are both 4 characters, and the indentation prefix is kept. -/
theorem rewriteLine_length_preserved (line : List Char) :
    (rewriteLine line).length = line.length := by
  unfold rewriteLine
  by_cases h : isIndentedUse line = true
  · rw [if_pos h]
    have hle  : (dropWs line).length ≤ line.length := dropWs_length_le line
    have huse : 4 ≤ (dropWs line).length             := use_prefix_length_le h
    have htake : (line.take (line.length - (dropWs line).length)).length =
                 line.length - (dropWs line).length := by
      rw [List.length_take]; exact Nat.min_eq_left (by omega)
    simp only [List.length_append, List.length_drop,
               List.length_cons, List.length_nil, htake]
    omega
  · rw [if_neg h]

-- ── List-level (multi-line) rewrite ──────────────────────────────────────────

/-- **Multi-line idempotency**: mapping the rewrite twice over a list of lines
    is the same as mapping it once. -/
theorem rewriteLines_idempotent (lines : List (List Char)) :
    (lines.map rewriteLine).map rewriteLine = lines.map rewriteLine := by
  induction lines with
  | nil => rfl
  | cons l ls ih => simp [List.map, rewriteLine_idempotent, ih]

/-- Mapping the rewrite preserves the number of lines. -/
theorem rewriteLines_length_preserved (lines : List (List Char)) :
    (lines.map rewriteLine).length = lines.length := by
  simp

/-- Every line in the mapped list has the same length as the original. -/
theorem rewriteLines_char_count_preserved (lines : List (List Char)) (i : Fin lines.length) :
    ((lines.map rewriteLine)[i.val]'(by simp [List.length_map])).length =
    (lines[i.val]).length := by
  simp [List.getElem_map, rewriteLine_length_preserved]

-- ── Concrete verified examples ────────────────────────────────────────────────

/-- Top-level `use` (no indent) is not rewritten. -/
theorem example_toplevel_use_unchanged :
    rewriteLine "use x = foo".toList = "use x = foo".toList := by
  native_decide

/-- An indented `use` is converted to `let`. -/
theorem example_indented_use_becomes_let :
    rewriteLine "  use x = foo".toList = "  let x = foo".toList := by
  native_decide

end FsiRewrite
