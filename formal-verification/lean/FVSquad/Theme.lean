/-
  Formal verification of SageFs.Theme — specifically `Theme.withOverrides`.

  Source: `SageFs.Core/Theme.fs`
  Informal spec: `formal-verification/specs/theme_informal.md`

  ## What is modelled
  - `ThemeConfig`: 34-field record of hex color strings.
  - `withOverrides`: applies a partial override map (modelled as `String → Option String`)
    onto a base `ThemeConfig`.
  - `tokenColorOfCapture`: maps a tree-sitter capture name to a theme color via
    prefix-match priority order.

  ## What is abstracted
  - The F# `Map<string,string>` is modelled as a pure function `String → Option String`.
    This captures the semantic interface (lookup by key) without importing a map library.

  ## What is NOT modelled
  - `hexToRgb`, `rgbR/G/B`, `toCssVariables`, `parseConfigLines` — lower priority.
  - No validation of hex string format is performed (matching the F# source).

  ## Verification summary
  - `withOverrides_identity` — empty overrides is a no-op
  - `withOverrides_idempotent` — applying same overrides twice = once
  - 34 × `withOverrides_override_*` — each field is overridden when its key is present
  - 34 × `withOverrides_preserve_*` — each field is preserved when its key is absent
  - 4 × `tokenColorOfCapture_*` — representative token capture mappings
  Total: 74 theorems, 0 sorry.

  🔬 Lean Squad — automated formal verification for WillEhrendreich/SageFs.
-/

/-- Lean model of `SageFs.ThemeConfig`.
    Fields use camelCase (matching the F# override key names). -/
structure ThemeConfig where
  fgDefault    : String
  fgDim        : String
  fgGreen      : String
  fgRed        : String
  fgYellow     : String
  fgCyan       : String
  fgBlue       : String
  fgMagenta    : String
  bgDefault    : String
  bgPanel      : String
  bgEditor     : String
  bgSelection  : String
  bgStatus     : String
  bgFocus      : String
  borderNormal : String
  borderFocus  : String
  colorPass    : String
  colorFail    : String
  colorWarn    : String
  colorInfo    : String
  synKeyword   : String
  synString    : String
  synComment   : String
  synNumber    : String
  synOperator  : String
  synType      : String
  synFunction  : String
  synVariable  : String
  synPunctuation : String
  synConstant  : String
  synModule    : String
  synAttribute : String
  synDirective : String
  synProperty  : String
  deriving DecidableEq

/-- Lean model of `Theme.withOverrides`.
    The override map is abstracted as `g : String → Option String`.
    Each field uses `(g key).getD base.field`: override if present, else fall back. -/
def withOverrides (g : String → Option String) (base : ThemeConfig) : ThemeConfig :=
  { fgDefault    := (g "fgDefault").getD base.fgDefault
    fgDim        := (g "fgDim").getD base.fgDim
    fgGreen      := (g "fgGreen").getD base.fgGreen
    fgRed        := (g "fgRed").getD base.fgRed
    fgYellow     := (g "fgYellow").getD base.fgYellow
    fgCyan       := (g "fgCyan").getD base.fgCyan
    fgBlue       := (g "fgBlue").getD base.fgBlue
    fgMagenta    := (g "fgMagenta").getD base.fgMagenta
    bgDefault    := (g "bgDefault").getD base.bgDefault
    bgPanel      := (g "bgPanel").getD base.bgPanel
    bgEditor     := (g "bgEditor").getD base.bgEditor
    bgSelection  := (g "bgSelection").getD base.bgSelection
    bgStatus     := (g "bgStatus").getD base.bgStatus
    bgFocus      := (g "bgFocus").getD base.bgFocus
    borderNormal := (g "borderNormal").getD base.borderNormal
    borderFocus  := (g "borderFocus").getD base.borderFocus
    colorPass    := (g "colorPass").getD base.colorPass
    colorFail    := (g "colorFail").getD base.colorFail
    colorWarn    := (g "colorWarn").getD base.colorWarn
    colorInfo    := (g "colorInfo").getD base.colorInfo
    synKeyword   := (g "synKeyword").getD base.synKeyword
    synString    := (g "synString").getD base.synString
    synComment   := (g "synComment").getD base.synComment
    synNumber    := (g "synNumber").getD base.synNumber
    synOperator  := (g "synOperator").getD base.synOperator
    synType      := (g "synType").getD base.synType
    synFunction  := (g "synFunction").getD base.synFunction
    synVariable  := (g "synVariable").getD base.synVariable
    synPunctuation := (g "synPunctuation").getD base.synPunctuation
    synConstant  := (g "synConstant").getD base.synConstant
    synModule    := (g "synModule").getD base.synModule
    synAttribute := (g "synAttribute").getD base.synAttribute
    synDirective := (g "synDirective").getD base.synDirective
    synProperty  := (g "synProperty").getD base.synProperty }

/-- Lean model of `Theme.tokenColorOfCapture`.
    Maps a tree-sitter capture name to a theme color via priority-ordered prefix matching.
    Abstraction: F# `String.StartsWith(prefix, StringComparison.Ordinal)` is modelled
    as Lean `String.startsWith`. -/
def tokenColorOfCapture (theme : ThemeConfig) (capture : String) : String :=
  if capture.startsWith "keyword" then theme.synKeyword
  else if capture.startsWith "string" then theme.synString
  else if capture.startsWith "comment" then theme.synComment
  else if capture.startsWith "number" then theme.synNumber
  else if capture.startsWith "operator" then theme.synOperator
  else if capture.startsWith "type" then theme.synType
  else if capture.startsWith "function" then theme.synFunction
  else if capture.startsWith "variable.parameter" then theme.synVariable
  else if capture.startsWith "variable.member" then theme.synProperty
  else if capture.startsWith "variable" then theme.synVariable
  else if capture.startsWith "punctuation" then theme.synPunctuation
  else if capture.startsWith "constant.macro" then theme.synModule
  else if capture.startsWith "constant" then theme.synConstant
  else if capture.startsWith "module" then theme.synModule
  else if capture.startsWith "attribute" then theme.synAttribute
  else if capture.startsWith "property" then theme.synProperty
  else if capture.startsWith "boolean" then theme.synConstant
  else if capture.startsWith "character" then theme.synOperator
  else if capture.startsWith "spell" then theme.fgDefault
  else theme.fgDefault

-- ============================================================
-- Helper lemma: Option.getD is idempotent in the nested sense.
-- Used to prove withOverrides_idempotent.
-- ============================================================

@[simp] theorem getD_idempotent (o : Option α) (v : α) :
    o.getD (o.getD v) = o.getD v := by
  cases o <;> simp [Option.getD]

-- ============================================================
-- Theorem 1: Identity — empty overrides is a no-op
-- ============================================================

/-- Applying an all-absent override function returns the base config unchanged. -/
theorem withOverrides_identity (base : ThemeConfig) :
    withOverrides (fun _ => none) base = base := by
  simp [withOverrides]

-- ============================================================
-- Theorem 2: Idempotence — applying same overrides twice = once
-- ============================================================

/-- `withOverrides` is idempotent: applying the same override function twice produces
    the same result as applying it once. -/
theorem withOverrides_idempotent (g : String → Option String) (base : ThemeConfig) :
    withOverrides g (withOverrides g base) = withOverrides g base := by
  simp [withOverrides]

-- ============================================================
-- Theorems 3–36: Override takes effect (all 34 fields)
-- If a key is present in the overrides, the corresponding field uses that value.
-- ============================================================

theorem withOverrides_override_fgDefault (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "fgDefault" = some v) :
    (withOverrides g base).fgDefault = v := by simp [withOverrides, h]

theorem withOverrides_override_fgDim (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "fgDim" = some v) :
    (withOverrides g base).fgDim = v := by simp [withOverrides, h]

theorem withOverrides_override_fgGreen (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "fgGreen" = some v) :
    (withOverrides g base).fgGreen = v := by simp [withOverrides, h]

theorem withOverrides_override_fgRed (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "fgRed" = some v) :
    (withOverrides g base).fgRed = v := by simp [withOverrides, h]

theorem withOverrides_override_fgYellow (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "fgYellow" = some v) :
    (withOverrides g base).fgYellow = v := by simp [withOverrides, h]

theorem withOverrides_override_fgCyan (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "fgCyan" = some v) :
    (withOverrides g base).fgCyan = v := by simp [withOverrides, h]

theorem withOverrides_override_fgBlue (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "fgBlue" = some v) :
    (withOverrides g base).fgBlue = v := by simp [withOverrides, h]

theorem withOverrides_override_fgMagenta (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "fgMagenta" = some v) :
    (withOverrides g base).fgMagenta = v := by simp [withOverrides, h]

theorem withOverrides_override_bgDefault (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "bgDefault" = some v) :
    (withOverrides g base).bgDefault = v := by simp [withOverrides, h]

theorem withOverrides_override_bgPanel (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "bgPanel" = some v) :
    (withOverrides g base).bgPanel = v := by simp [withOverrides, h]

theorem withOverrides_override_bgEditor (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "bgEditor" = some v) :
    (withOverrides g base).bgEditor = v := by simp [withOverrides, h]

theorem withOverrides_override_bgSelection (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "bgSelection" = some v) :
    (withOverrides g base).bgSelection = v := by simp [withOverrides, h]

theorem withOverrides_override_bgStatus (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "bgStatus" = some v) :
    (withOverrides g base).bgStatus = v := by simp [withOverrides, h]

theorem withOverrides_override_bgFocus (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "bgFocus" = some v) :
    (withOverrides g base).bgFocus = v := by simp [withOverrides, h]

theorem withOverrides_override_borderNormal (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "borderNormal" = some v) :
    (withOverrides g base).borderNormal = v := by simp [withOverrides, h]

theorem withOverrides_override_borderFocus (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "borderFocus" = some v) :
    (withOverrides g base).borderFocus = v := by simp [withOverrides, h]

theorem withOverrides_override_colorPass (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "colorPass" = some v) :
    (withOverrides g base).colorPass = v := by simp [withOverrides, h]

theorem withOverrides_override_colorFail (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "colorFail" = some v) :
    (withOverrides g base).colorFail = v := by simp [withOverrides, h]

theorem withOverrides_override_colorWarn (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "colorWarn" = some v) :
    (withOverrides g base).colorWarn = v := by simp [withOverrides, h]

theorem withOverrides_override_colorInfo (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "colorInfo" = some v) :
    (withOverrides g base).colorInfo = v := by simp [withOverrides, h]

theorem withOverrides_override_synKeyword (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synKeyword" = some v) :
    (withOverrides g base).synKeyword = v := by simp [withOverrides, h]

theorem withOverrides_override_synString (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synString" = some v) :
    (withOverrides g base).synString = v := by simp [withOverrides, h]

theorem withOverrides_override_synComment (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synComment" = some v) :
    (withOverrides g base).synComment = v := by simp [withOverrides, h]

theorem withOverrides_override_synNumber (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synNumber" = some v) :
    (withOverrides g base).synNumber = v := by simp [withOverrides, h]

theorem withOverrides_override_synOperator (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synOperator" = some v) :
    (withOverrides g base).synOperator = v := by simp [withOverrides, h]

theorem withOverrides_override_synType (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synType" = some v) :
    (withOverrides g base).synType = v := by simp [withOverrides, h]

theorem withOverrides_override_synFunction (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synFunction" = some v) :
    (withOverrides g base).synFunction = v := by simp [withOverrides, h]

theorem withOverrides_override_synVariable (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synVariable" = some v) :
    (withOverrides g base).synVariable = v := by simp [withOverrides, h]

theorem withOverrides_override_synPunctuation (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synPunctuation" = some v) :
    (withOverrides g base).synPunctuation = v := by simp [withOverrides, h]

theorem withOverrides_override_synConstant (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synConstant" = some v) :
    (withOverrides g base).synConstant = v := by simp [withOverrides, h]

theorem withOverrides_override_synModule (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synModule" = some v) :
    (withOverrides g base).synModule = v := by simp [withOverrides, h]

theorem withOverrides_override_synAttribute (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synAttribute" = some v) :
    (withOverrides g base).synAttribute = v := by simp [withOverrides, h]

theorem withOverrides_override_synDirective (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synDirective" = some v) :
    (withOverrides g base).synDirective = v := by simp [withOverrides, h]

theorem withOverrides_override_synProperty (g : String → Option String) (base : ThemeConfig)
    (v : String) (h : g "synProperty" = some v) :
    (withOverrides g base).synProperty = v := by simp [withOverrides, h]

-- ============================================================
-- Theorems 37–70: Preservation — absent key leaves field unchanged (all 34 fields)
-- ============================================================

theorem withOverrides_preserve_fgDefault (g : String → Option String) (base : ThemeConfig)
    (h : g "fgDefault" = none) :
    (withOverrides g base).fgDefault = base.fgDefault := by simp [withOverrides, h]

theorem withOverrides_preserve_fgDim (g : String → Option String) (base : ThemeConfig)
    (h : g "fgDim" = none) :
    (withOverrides g base).fgDim = base.fgDim := by simp [withOverrides, h]

theorem withOverrides_preserve_fgGreen (g : String → Option String) (base : ThemeConfig)
    (h : g "fgGreen" = none) :
    (withOverrides g base).fgGreen = base.fgGreen := by simp [withOverrides, h]

theorem withOverrides_preserve_fgRed (g : String → Option String) (base : ThemeConfig)
    (h : g "fgRed" = none) :
    (withOverrides g base).fgRed = base.fgRed := by simp [withOverrides, h]

theorem withOverrides_preserve_fgYellow (g : String → Option String) (base : ThemeConfig)
    (h : g "fgYellow" = none) :
    (withOverrides g base).fgYellow = base.fgYellow := by simp [withOverrides, h]

theorem withOverrides_preserve_fgCyan (g : String → Option String) (base : ThemeConfig)
    (h : g "fgCyan" = none) :
    (withOverrides g base).fgCyan = base.fgCyan := by simp [withOverrides, h]

theorem withOverrides_preserve_fgBlue (g : String → Option String) (base : ThemeConfig)
    (h : g "fgBlue" = none) :
    (withOverrides g base).fgBlue = base.fgBlue := by simp [withOverrides, h]

theorem withOverrides_preserve_fgMagenta (g : String → Option String) (base : ThemeConfig)
    (h : g "fgMagenta" = none) :
    (withOverrides g base).fgMagenta = base.fgMagenta := by simp [withOverrides, h]

theorem withOverrides_preserve_bgDefault (g : String → Option String) (base : ThemeConfig)
    (h : g "bgDefault" = none) :
    (withOverrides g base).bgDefault = base.bgDefault := by simp [withOverrides, h]

theorem withOverrides_preserve_bgPanel (g : String → Option String) (base : ThemeConfig)
    (h : g "bgPanel" = none) :
    (withOverrides g base).bgPanel = base.bgPanel := by simp [withOverrides, h]

theorem withOverrides_preserve_bgEditor (g : String → Option String) (base : ThemeConfig)
    (h : g "bgEditor" = none) :
    (withOverrides g base).bgEditor = base.bgEditor := by simp [withOverrides, h]

theorem withOverrides_preserve_bgSelection (g : String → Option String) (base : ThemeConfig)
    (h : g "bgSelection" = none) :
    (withOverrides g base).bgSelection = base.bgSelection := by simp [withOverrides, h]

theorem withOverrides_preserve_bgStatus (g : String → Option String) (base : ThemeConfig)
    (h : g "bgStatus" = none) :
    (withOverrides g base).bgStatus = base.bgStatus := by simp [withOverrides, h]

theorem withOverrides_preserve_bgFocus (g : String → Option String) (base : ThemeConfig)
    (h : g "bgFocus" = none) :
    (withOverrides g base).bgFocus = base.bgFocus := by simp [withOverrides, h]

theorem withOverrides_preserve_borderNormal (g : String → Option String) (base : ThemeConfig)
    (h : g "borderNormal" = none) :
    (withOverrides g base).borderNormal = base.borderNormal := by simp [withOverrides, h]

theorem withOverrides_preserve_borderFocus (g : String → Option String) (base : ThemeConfig)
    (h : g "borderFocus" = none) :
    (withOverrides g base).borderFocus = base.borderFocus := by simp [withOverrides, h]

theorem withOverrides_preserve_colorPass (g : String → Option String) (base : ThemeConfig)
    (h : g "colorPass" = none) :
    (withOverrides g base).colorPass = base.colorPass := by simp [withOverrides, h]

theorem withOverrides_preserve_colorFail (g : String → Option String) (base : ThemeConfig)
    (h : g "colorFail" = none) :
    (withOverrides g base).colorFail = base.colorFail := by simp [withOverrides, h]

theorem withOverrides_preserve_colorWarn (g : String → Option String) (base : ThemeConfig)
    (h : g "colorWarn" = none) :
    (withOverrides g base).colorWarn = base.colorWarn := by simp [withOverrides, h]

theorem withOverrides_preserve_colorInfo (g : String → Option String) (base : ThemeConfig)
    (h : g "colorInfo" = none) :
    (withOverrides g base).colorInfo = base.colorInfo := by simp [withOverrides, h]

theorem withOverrides_preserve_synKeyword (g : String → Option String) (base : ThemeConfig)
    (h : g "synKeyword" = none) :
    (withOverrides g base).synKeyword = base.synKeyword := by simp [withOverrides, h]

theorem withOverrides_preserve_synString (g : String → Option String) (base : ThemeConfig)
    (h : g "synString" = none) :
    (withOverrides g base).synString = base.synString := by simp [withOverrides, h]

theorem withOverrides_preserve_synComment (g : String → Option String) (base : ThemeConfig)
    (h : g "synComment" = none) :
    (withOverrides g base).synComment = base.synComment := by simp [withOverrides, h]

theorem withOverrides_preserve_synNumber (g : String → Option String) (base : ThemeConfig)
    (h : g "synNumber" = none) :
    (withOverrides g base).synNumber = base.synNumber := by simp [withOverrides, h]

theorem withOverrides_preserve_synOperator (g : String → Option String) (base : ThemeConfig)
    (h : g "synOperator" = none) :
    (withOverrides g base).synOperator = base.synOperator := by simp [withOverrides, h]

theorem withOverrides_preserve_synType (g : String → Option String) (base : ThemeConfig)
    (h : g "synType" = none) :
    (withOverrides g base).synType = base.synType := by simp [withOverrides, h]

theorem withOverrides_preserve_synFunction (g : String → Option String) (base : ThemeConfig)
    (h : g "synFunction" = none) :
    (withOverrides g base).synFunction = base.synFunction := by simp [withOverrides, h]

theorem withOverrides_preserve_synVariable (g : String → Option String) (base : ThemeConfig)
    (h : g "synVariable" = none) :
    (withOverrides g base).synVariable = base.synVariable := by simp [withOverrides, h]

theorem withOverrides_preserve_synPunctuation (g : String → Option String) (base : ThemeConfig)
    (h : g "synPunctuation" = none) :
    (withOverrides g base).synPunctuation = base.synPunctuation := by simp [withOverrides, h]

theorem withOverrides_preserve_synConstant (g : String → Option String) (base : ThemeConfig)
    (h : g "synConstant" = none) :
    (withOverrides g base).synConstant = base.synConstant := by simp [withOverrides, h]

theorem withOverrides_preserve_synModule (g : String → Option String) (base : ThemeConfig)
    (h : g "synModule" = none) :
    (withOverrides g base).synModule = base.synModule := by simp [withOverrides, h]

theorem withOverrides_preserve_synAttribute (g : String → Option String) (base : ThemeConfig)
    (h : g "synAttribute" = none) :
    (withOverrides g base).synAttribute = base.synAttribute := by simp [withOverrides, h]

theorem withOverrides_preserve_synDirective (g : String → Option String) (base : ThemeConfig)
    (h : g "synDirective" = none) :
    (withOverrides g base).synDirective = base.synDirective := by simp [withOverrides, h]

theorem withOverrides_preserve_synProperty (g : String → Option String) (base : ThemeConfig)
    (h : g "synProperty" = none) :
    (withOverrides g base).synProperty = base.synProperty := by simp [withOverrides, h]

-- ============================================================
-- Theorems 71–74: tokenColorOfCapture — representative capture mappings
-- ============================================================

/-- The "keyword" capture name maps to the synKeyword color. -/
theorem tokenColorOfCapture_keyword (theme : ThemeConfig) :
    tokenColorOfCapture theme "keyword" = theme.synKeyword := by
  simp [tokenColorOfCapture]

/-- The "string" capture name maps to the synString color. -/
theorem tokenColorOfCapture_string (theme : ThemeConfig) :
    tokenColorOfCapture theme "string" = theme.synString := by
  simp [tokenColorOfCapture]

/-- The "variable.parameter" capture matches before "variable" (priority check). -/
theorem tokenColorOfCapture_variable_parameter (theme : ThemeConfig) :
    tokenColorOfCapture theme "variable.parameter" = theme.synVariable := by
  simp [tokenColorOfCapture]

/-- Unknown capture names fall back to fgDefault. -/
theorem tokenColorOfCapture_unknown (theme : ThemeConfig) :
    tokenColorOfCapture theme "xyz_unknown" = theme.fgDefault := by
  simp [tokenColorOfCapture]
