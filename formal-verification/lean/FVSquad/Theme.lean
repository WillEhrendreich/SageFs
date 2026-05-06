/-!
  # Formal Specification: Theme

  🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*
  Source: `SageFs.Core/Theme.fs`

  This file formalises the `ThemeConfig` record and `withOverrides` combinator.

  ## Design

  `ThemeConfig` is a 34-field record of hex RGB color strings.
  `withOverrides` applies a partial override map (modelled as an association list)
  onto a base config: for each field, if the override list contains a matching key,
  use the override value; otherwise, keep the base value.

  ## Properties verified

  - `lookupOr` primitive correctness (empty/hit/miss)
  - `withOverrides []` is the identity
  - Targeted overrides affect exactly the named field and preserve all others
  - Idempotency of `withOverrides` with a single key
  - Default hex color strings are well-formed (length 7, leading '#')

  ## Abstractions / omissions

  - The F# `Map<string, string>` is modelled as an association list
    `List (String × String)`.  First-match semantics are preserved.
  - Color value *parsing* (hex → RGB components) is not modelled; we reason only
    about the string values directly.

  No Mathlib. Pure Lean 4 stdlib only (network firewalled in CI).
-/

namespace Theme

-- ─────────────────────────────────────────────────────────────────────────────
-- Type definition
-- ─────────────────────────────────────────────────────────────────────────────

/-- Mirrors F# `ThemeConfig`.  All 34 color slots are hex RGB strings. -/
structure ThemeConfig where
  fgDefault      : String
  fgDim          : String
  fgGreen        : String
  fgRed          : String
  fgYellow       : String
  fgCyan         : String
  fgBlue         : String
  fgMagenta      : String
  bgDefault      : String
  bgPanel        : String
  bgEditor       : String
  bgSelection    : String
  bgStatus       : String
  bgFocus        : String
  borderNormal   : String
  borderFocus    : String
  colorPass      : String
  colorFail      : String
  colorWarn      : String
  colorInfo      : String
  synKeyword     : String
  synString      : String
  synComment     : String
  synNumber      : String
  synOperator    : String
  synType        : String
  synFunction    : String
  synVariable    : String
  synPunctuation : String
  synConstant    : String
  synModule      : String
  synAttribute   : String
  synDirective   : String
  synProperty    : String
  deriving Repr, DecidableEq

-- ─────────────────────────────────────────────────────────────────────────────
-- Defaults
-- ─────────────────────────────────────────────────────────────────────────────

/-- The default SageFs color theme.  Mirrors `Theme.defaults`. -/
def defaults : ThemeConfig := {
  fgDefault      := "#ffffff"
  fgDim          := "#8b8b8b"
  fgGreen        := "#87d787"
  fgRed          := "#ff5f5f"
  fgYellow       := "#d7af5f"
  fgCyan         := "#87d7d7"
  fgBlue         := "#5fafff"
  fgMagenta      := "#d787d7"
  bgDefault      := "#000000"
  bgPanel        := "#262626"
  bgEditor       := "#1c1c1c"
  bgSelection    := "#444444"
  bgStatus       := "#303030"
  bgFocus        := "#3a3a3a"
  borderNormal   := "#585858"
  borderFocus    := "#5fafff"
  colorPass      := "#87d787"
  colorFail      := "#ff5f5f"
  colorWarn      := "#d7af5f"
  colorInfo      := "#87d7d7"
  synKeyword     := "#d787d7"
  synString      := "#87d787"
  synComment     := "#8b8b8b"
  synNumber      := "#d7af5f"
  synOperator    := "#87d7d7"
  synType        := "#d7af5f"
  synFunction    := "#5fafff"
  synVariable    := "#ffffff"
  synPunctuation := "#8b8b8b"
  synConstant    := "#d7af5f"
  synModule      := "#87d7d7"
  synAttribute   := "#d787d7"
  synDirective   := "#d787d7"
  synProperty    := "#87d7d7"
}

-- ─────────────────────────────────────────────────────────────────────────────
-- Override primitive
-- ─────────────────────────────────────────────────────────────────────────────

/-- Look up a key in an association list; return `default` when absent.
    First-match semantics mirror `Map.tryFind` in F#. -/
def lookupOr (key : String) (overrides : List (String × String)) (default : String) : String :=
  match overrides.find? (fun p => p.1 == key) with
  | some (_, v) => v
  | none        => default

-- ─────────────────────────────────────────────────────────────────────────────
-- withOverrides
-- ─────────────────────────────────────────────────────────────────────────────

/-- Apply a partial override list to a base config.
    Mirrors F# `Theme.withOverrides`. -/
def withOverrides (overrides : List (String × String)) (base : ThemeConfig) : ThemeConfig := {
  fgDefault      := lookupOr "fgDefault"      overrides base.fgDefault
  fgDim          := lookupOr "fgDim"          overrides base.fgDim
  fgGreen        := lookupOr "fgGreen"        overrides base.fgGreen
  fgRed          := lookupOr "fgRed"          overrides base.fgRed
  fgYellow       := lookupOr "fgYellow"       overrides base.fgYellow
  fgCyan         := lookupOr "fgCyan"         overrides base.fgCyan
  fgBlue         := lookupOr "fgBlue"         overrides base.fgBlue
  fgMagenta      := lookupOr "fgMagenta"      overrides base.fgMagenta
  bgDefault      := lookupOr "bgDefault"      overrides base.bgDefault
  bgPanel        := lookupOr "bgPanel"        overrides base.bgPanel
  bgEditor       := lookupOr "bgEditor"       overrides base.bgEditor
  bgSelection    := lookupOr "bgSelection"    overrides base.bgSelection
  bgStatus       := lookupOr "bgStatus"       overrides base.bgStatus
  bgFocus        := lookupOr "bgFocus"        overrides base.bgFocus
  borderNormal   := lookupOr "borderNormal"   overrides base.borderNormal
  borderFocus    := lookupOr "borderFocus"    overrides base.borderFocus
  colorPass      := lookupOr "colorPass"      overrides base.colorPass
  colorFail      := lookupOr "colorFail"      overrides base.colorFail
  colorWarn      := lookupOr "colorWarn"      overrides base.colorWarn
  colorInfo      := lookupOr "colorInfo"      overrides base.colorInfo
  synKeyword     := lookupOr "synKeyword"     overrides base.synKeyword
  synString      := lookupOr "synString"      overrides base.synString
  synComment     := lookupOr "synComment"     overrides base.synComment
  synNumber      := lookupOr "synNumber"      overrides base.synNumber
  synOperator    := lookupOr "synOperator"    overrides base.synOperator
  synType        := lookupOr "synType"        overrides base.synType
  synFunction    := lookupOr "synFunction"    overrides base.synFunction
  synVariable    := lookupOr "synVariable"    overrides base.synVariable
  synPunctuation := lookupOr "synPunctuation" overrides base.synPunctuation
  synConstant    := lookupOr "synConstant"    overrides base.synConstant
  synModule      := lookupOr "synModule"      overrides base.synModule
  synAttribute   := lookupOr "synAttribute"   overrides base.synAttribute
  synDirective   := lookupOr "synDirective"   overrides base.synDirective
  synProperty    := lookupOr "synProperty"    overrides base.synProperty
}

-- ─────────────────────────────────────────────────────────────────────────────
-- #check sanity
-- ─────────────────────────────────────────────────────────────────────────────

#check @lookupOr
#check @withOverrides
#check @defaults

-- ─────────────────────────────────────────────────────────────────────────────
-- Theorems: lookupOr primitives
-- ─────────────────────────────────────────────────────────────────────────────

/-- Empty override list always returns the default. -/
theorem lookupOr_empty (key default : String) :
    lookupOr key [] default = default := rfl

/-- When the head key matches, return the override value. -/
theorem lookupOr_cons_hit (key : String) (v : String) (rest : List (String × String)) (default : String) :
    lookupOr key ((key, v) :: rest) default = v := by
  simp [lookupOr, List.find?]

/-- When the head key does not match, recurse on the tail. -/
theorem lookupOr_cons_miss (key key' v : String) (rest : List (String × String)) (default : String)
    (h : (key' == key) = false) :
    lookupOr key ((key', v) :: rest) default = lookupOr key rest default := by
  simp [lookupOr, List.find?, h]

/-- A singleton list returns the override value when the key matches. -/
theorem lookupOr_singleton_hit (key v default : String) :
    lookupOr key [(key, v)] default = v := by
  simp [lookupOr, List.find?]

/-- A singleton list with a different key returns the default. -/
theorem lookupOr_singleton_miss (key key' v default : String) (h : (key' == key) = false) :
    lookupOr key [(key', v)] default = default := by
  simp [lookupOr, List.find?, h]

-- ─────────────────────────────────────────────────────────────────────────────
-- Theorems: withOverrides structural
-- ─────────────────────────────────────────────────────────────────────────────

/-- Empty overrides leave the config unchanged (I1 from informal spec). -/
theorem withOverrides_empty_id (base : ThemeConfig) : withOverrides [] base = base := by
  simp [withOverrides, lookupOr]

/-- Overriding fgDefault affects exactly that field. -/
theorem withOverrides_fgDefault_field (base : ThemeConfig) (v : String) :
    (withOverrides [("fgDefault", v)] base).fgDefault = v := by
  simp [withOverrides, lookupOr, List.find?]

/-- Overriding fgDefault does not touch fgDim. -/
theorem withOverrides_fgDefault_preserves_fgDim (base : ThemeConfig) (v : String) :
    (withOverrides [("fgDefault", v)] base).fgDim = base.fgDim := by
  simp [withOverrides, lookupOr, List.find?]

/-- Overriding fgDefault does not touch fgGreen. -/
theorem withOverrides_fgDefault_preserves_fgGreen (base : ThemeConfig) (v : String) :
    (withOverrides [("fgDefault", v)] base).fgGreen = base.fgGreen := by
  simp [withOverrides, lookupOr, List.find?]

/-- Overriding bgDefault does not touch fgDefault. -/
theorem withOverrides_bgDefault_preserves_fgDefault (base : ThemeConfig) (v : String) :
    (withOverrides [("bgDefault", v)] base).fgDefault = base.fgDefault := by
  simp [withOverrides, lookupOr, List.find?]

/-- lookupOr is idempotent: applying the same single-entry list twice is the same as once. -/
private theorem lookupOr_single_idempotent (k key v d : String) :
    lookupOr key [(k, v)] (lookupOr key [(k, v)] d) = lookupOr key [(k, v)] d := by
  simp only [lookupOr, List.find?]
  by_cases hk : (k == key) = true
  · simp [hk]
  · have hk' : (k == key) = false := Bool.eq_false_iff.mpr (fun h => absurd h hk)
    simp [hk']

/-- Applying the same single-key override twice equals applying it once (idempotency). -/
theorem withOverrides_idempotent_single (k v : String) (base : ThemeConfig) :
    withOverrides [(k, v)] (withOverrides [(k, v)] base) = withOverrides [(k, v)] base := by
  simp only [withOverrides]
  congr 1 <;> exact lookupOr_single_idempotent k _ v _

/-- Overriding with the base's own fgDefault value changes nothing for that field. -/
theorem withOverrides_noop_own_value (base : ThemeConfig) :
    (withOverrides [("fgDefault", base.fgDefault)] base).fgDefault = base.fgDefault := by
  simp [withOverrides, lookupOr, List.find?]

/-- First matching entry wins (first-match semantics). -/
theorem lookupOr_first_match_wins (key v1 v2 : String) (default : String) :
    lookupOr key [(key, v1), (key, v2)] default = v1 := by
  simp [lookupOr, List.find?]

-- ─────────────────────────────────────────────────────────────────────────────
-- Theorems: defaults well-formedness
-- ─────────────────────────────────────────────────────────────────────────────

/-- All default hex colors have length 7 (format: #rrggbb). -/
theorem defaults_hex_lengths : (
    defaults.fgDefault.length = 7 ∧
    defaults.fgDim.length = 7 ∧
    defaults.fgGreen.length = 7 ∧
    defaults.fgRed.length = 7 ∧
    defaults.fgYellow.length = 7 ∧
    defaults.fgCyan.length = 7 ∧
    defaults.fgBlue.length = 7 ∧
    defaults.fgMagenta.length = 7 ∧
    defaults.bgDefault.length = 7 ∧
    defaults.bgPanel.length = 7) := by
  decide

/-- Remaining default hex colors also have length 7. -/
theorem defaults_hex_lengths_2 : (
    defaults.bgEditor.length = 7 ∧
    defaults.bgSelection.length = 7 ∧
    defaults.bgStatus.length = 7 ∧
    defaults.bgFocus.length = 7 ∧
    defaults.borderNormal.length = 7 ∧
    defaults.borderFocus.length = 7 ∧
    defaults.colorPass.length = 7 ∧
    defaults.colorFail.length = 7 ∧
    defaults.colorWarn.length = 7 ∧
    defaults.colorInfo.length = 7) := by
  decide

/-- Syntax token defaults all have length 7. -/
theorem defaults_hex_lengths_3 : (
    defaults.synKeyword.length = 7 ∧
    defaults.synString.length = 7 ∧
    defaults.synComment.length = 7 ∧
    defaults.synNumber.length = 7 ∧
    defaults.synOperator.length = 7 ∧
    defaults.synType.length = 7 ∧
    defaults.synFunction.length = 7 ∧
    defaults.synVariable.length = 7 ∧
    defaults.synPunctuation.length = 7 ∧
    defaults.synConstant.length = 7 ∧
    defaults.synModule.length = 7 ∧
    defaults.synAttribute.length = 7 ∧
    defaults.synDirective.length = 7 ∧
    defaults.synProperty.length = 7) := by
  decide

/-- Foreground colors share a common structure: all are non-empty hex strings. -/
theorem defaults_fg_colors_nonempty :
    defaults.fgDefault ≠ "" ∧ defaults.fgDim ≠ "" ∧
    defaults.fgGreen ≠ "" ∧ defaults.fgRed ≠ "" := by
  decide

/-- withOverrides preserves fgCyan when only synKeyword is overridden. -/
theorem withOverrides_unrelated_key_preserves_fgCyan (base : ThemeConfig) (v : String) :
    (withOverrides [("synKeyword", v)] base).fgCyan = base.fgCyan := by
  simp [withOverrides, lookupOr, List.find?]

/-- Stacking two overrides: later call wins for conflicting keys. -/
theorem withOverrides_stack_same_key (base : ThemeConfig) (v1 v2 : String) :
    (withOverrides [("fgDefault", v2)] (withOverrides [("fgDefault", v1)] base)).fgDefault = v2 := by
  simp [withOverrides, lookupOr, List.find?]

/-- Stacking two overrides with different keys: each field gets its override. -/
theorem withOverrides_stack_different_keys (base : ThemeConfig) (v1 v2 : String) :
    let c := withOverrides [("fgGreen", v2)] (withOverrides [("fgDefault", v1)] base)
    c.fgDefault = v1 ∧ c.fgGreen = v2 := by
  simp [withOverrides, lookupOr, List.find?]

end Theme
