# Theme — Informal Specification

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*

## Purpose

`Theme` provides the color configuration system for SageFs. It has two main components:

1. **`ThemeConfig`** — a record of 34 named color values (hex RGB strings like `"#c8d3f5"`) covering
   foreground colors, background colors, border colors, status colors, and syntax-highlighting token colors.

2. **`Theme.withOverrides`** — a pure function that applies a partial color map (mapping camelCase
   field names to hex values) onto a base `ThemeConfig`, producing a new config where each field is
   either overridden (if the key is present in the map) or preserved from the base (if absent).

3. **`Theme.tokenColorOfCapture`** — maps a tree-sitter capture name (e.g. `"keyword"`, `"string"`)
   to a hex color from a theme, via prefix matching with a fixed priority order.

4. **`Theme.defaults`** — the built-in One Dark–inspired default theme config.

## Source File

`SageFs.Core/Theme.fs` — 237 lines, no I/O, no side effects.

---

## `withOverrides` Specification

### Signature

```fsharp
val withOverrides : Map<string, string> → ThemeConfig → ThemeConfig
```

### Preconditions

- `overrides` is any map (including empty) from camelCase field names to hex color strings.
- `base'` is any `ThemeConfig` (values are arbitrary strings — no hex validation is performed).
- Neither argument has special constraints.

### Postconditions

For each of the 34 fields, let `key` be its camelCase string key. The result satisfies:

1. **Override takes effect**: if `overrides.ContainsKey(key)`, then `result.Field = overrides[key]`.
2. **Default preserved**: if not `overrides.ContainsKey(key)`, then `result.Field = base'.Field`.

### Invariants

- **Identity**: `withOverrides Map.empty base = base` — an empty override map is a no-op.
- **Idempotence**: `withOverrides m (withOverrides m base) = withOverrides m base` — applying the
  same overrides twice has the same effect as applying them once. This holds because each field is
  either replaced by a fixed value from `m` (applying again yields the same value) or preserved
  (and the preserved value is re-preserved on second application).
- **Field independence**: overriding one field does not affect any other field's value.

### Edge Cases

- Empty map: all fields preserved from base (identity).
- Map with all 34 keys: all fields overridden (full replacement).
- Partial map: only matching fields are replaced.
- Map with unknown keys (e.g. `"foo"`): silently ignored; no field matches.
- Map with a known key mapping to an invalid hex string: the invalid string is still used as the
  field value — no validation occurs at this layer.

### Examples

```fsharp
// Identity
withOverrides Map.empty defaults = defaults

// Single override
let t = withOverrides (Map.ofList [("fgDefault", "#ff0000")]) defaults
t.FgDefault = "#ff0000"         // overridden
t.FgDim = defaults.FgDim       // preserved

// All other fields unchanged when only one key is in the map
withOverrides (Map.ofList [("synKeyword", "#aabbcc")]) defaults =
  { defaults with SynKeyword = "#aabbcc" }

// Idempotence
withOverrides m (withOverrides m t) = withOverrides m t  // for any m, t
```

### Inferred Intent

The function is designed to support user-supplied theme config files (e.g. `config.fsx`) that
specify only the colors they want to change. The camelCase key names are the canonical identifiers;
the `defaults` record provides the fallback. The function makes no assumptions about whether the
hex strings are valid — validation is the caller's responsibility.

### Open Questions

- **Unrecognised keys**: what should happen if the map contains a key that is not one of the 34
  known field names? Currently silently ignored. Is this intentional?
- **Case sensitivity**: keys are compared literally (case-sensitive). Is this documented to callers?
- **Compositionality**: is `withOverrides (Map.union m1 m2) base` intended to equal a specific
  composition of two `withOverrides` calls? `Map.union` in F# takes the first map's value for
  duplicate keys, so `withOverrides (Map.union m1 m2)` is NOT the same as
  `withOverrides m2 (withOverrides m1 base)` in general.

---

## `tokenColorOfCapture` Specification

### Signature

```fsharp
val tokenColorOfCapture : ThemeConfig → string → string
```

### Behaviour

Priority-ordered prefix matching on the capture name:

| Priority | Prefix | Result field |
|----------|--------|-------------|
| 1 | `"keyword"` | `SynKeyword` |
| 2 | `"string"` | `SynString` |
| 3 | `"comment"` | `SynComment` |
| 4 | `"number"` | `SynNumber` |
| 5 | `"operator"` | `SynOperator` |
| 6 | `"type"` | `SynType` |
| 7 | `"function"` | `SynFunction` |
| 8 | `"variable.parameter"` | `SynVariable` |
| 9 | `"variable.member"` | `SynProperty` |
| 10 | `"variable"` | `SynVariable` |
| 11 | `"punctuation"` | `SynPunctuation` |
| 12 | `"constant.macro"` | `SynModule` |
| 13 | `"constant"` | `SynConstant` |
| 14 | `"module"` | `SynModule` |
| 15 | `"attribute"` | `SynAttribute` |
| 16 | `"property"` | `SynProperty` |
| 17 | `"boolean"` | `SynConstant` |
| 18 | `"character"` | `SynOperator` |
| 19 | `"spell"` | `FgDefault` |
| catch-all | (any other) | `FgDefault` |

### Preconditions

- `theme` is any `ThemeConfig`.
- `capture` is any string (tree-sitter capture name).

### Postconditions

- The result is one of the 34 hex color strings from `theme`.
- The result is never empty (assuming theme fields are non-empty).
- The mapping is deterministic for a given `(theme, capture)` pair.

### Edge Cases

- `"variable.parameter"` matches `"variable.parameter"` prefix before `"variable"` prefix
  (priority 8 > 10 since it appears earlier in the match expression).
- `"constant.macro"` matches before `"constant"` (priority 12 > 13).
- `"boolean"` maps to `SynConstant`, not a dedicated bool color.
- `"character"` maps to `SynOperator` (unusual choice — may be worth revisiting).
- The empty string `""` hits the catch-all, returning `FgDefault`.

---

## Modelling Notes

**Pure functional**: both `withOverrides` and `tokenColorOfCapture` are pure with no I/O or side
effects — ideal for direct Lean 4 modelling.

**Abstraction used**: the F# `Map<string, string>` is modelled in Lean as a function
`String → Option String` (a lookup oracle). This captures the semantic interface of the map
without importing a full ordered-map library.

**Not modelled**: `hexToRgb`, `rgbR/G/B`, `toCssVariables`, `parseConfigLines` — these involve
string parsing or imperative mutation and are lower priority for FV.
