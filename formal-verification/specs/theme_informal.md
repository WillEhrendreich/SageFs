# Informal Specification: Theme

🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*
Source: `SageFs.Core/Theme.fs`

---

## Purpose

The `Theme` module defines a typed color palette for the SageFs TUI, GUI, and
dashboard. It provides:

1. **`ThemeConfig`** — a 34-field record where each field is a named hex RGB
   string (format `#rrggbb`), covering foreground colors, background colors,
   border colors, status indicator colors, and syntax-highlighting token colors.

2. **`defaults`** — the default SageFs "dark" theme, fully specified with
   hard-coded hex strings.

3. **`withOverrides`** — a combinator that layers a partial `Map<string, string>`
   of key-value overrides onto a base `ThemeConfig`.  Each field checks whether
   the override map contains a matching key; if so, the override value is used;
   otherwise the base field value is preserved.

---

## Preconditions

- `withOverrides` accepts any `Map<string, string>` — there is no precondition on
  the override values being valid hex strings.
- Override keys are field names in camelCase (e.g. `"fgDefault"`, `"bgPanel"`).
  An unrecognised key is silently ignored.

---

## Postconditions

### `withOverrides overrides base`

- **Override present**: if `overrides` contains key `k` with value `v`, and
  `k` is the canonical name for field `F`, then `result.F = v`.
- **Override absent**: if `overrides` does not contain the canonical name for
  field `F`, then `result.F = base.F`.
- **Empty override**: `withOverrides Map.empty base = base`.
- **Unknown keys**: keys in `overrides` that do not correspond to any field are
  ignored; all fields retain their base values.

### `defaults`

- Every field holds a well-formed hex RGB string: length 7, first character `#`,
  remaining 6 characters are hex digits (`0–9`, `a–f`).
- The color scheme is a "dark" theme: background colors are near-black and
  foreground colors are light.

---

## Invariants

- All fields of `ThemeConfig` are `string`; no field is `null` or empty in
  well-formed configs (i.e., configs derived from `defaults` or from
  `withOverrides` applied to a non-empty-field base).
- `withOverrides` is idempotent: applying the same override map twice has the
  same effect as applying it once.
- `withOverrides` is independent across fields: overriding field `A` has no
  effect on any other field `B ≠ A`.
- First-match semantics: if the override list has multiple entries with the same
  key, the first one wins (consistent with `Map.tryFind` on a unique-key map).

---

## Edge Cases

- **Empty override map**: identity on the base config.
- **Override with same value as base**: no observable change.
- **Unknown key in override**: silently ignored.
- **Two overrides for the same key**: first-match wins.

---

## Examples

```
withOverrides (Map.ofList [("fgDefault", "#ff0000")]) defaults
→ { defaults with FgDefault = "#ff0000"; (* all other fields unchanged *) }

withOverrides Map.empty defaults = defaults
```

---

## Inferred Intent

The module is designed for easy theme customisation from config files or
extension settings without requiring recompilation.  The `withOverrides`
design ensures safe partial application: any subset of the 34 fields can be
overridden, and the remainder always fall back to a known-good default.

---

## Open Questions

1. Should `withOverrides` validate that override values are valid hex strings,
   or is that the caller's responsibility?
2. The F# `defaults` module-level aliases (e.g. `let fgDefault = defaults.FgDefault`)
   are not part of the formal model — are they used externally?
3. Is case-insensitive key matching intended for override map keys?
