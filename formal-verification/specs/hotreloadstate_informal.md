# Informal Specification: HotReloadState

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*
> Source: `SageFs.Core/HotReloadState.fs`

---

## Purpose

`HotReloadState` manages the set of file paths that are opted-in for hot-reload
in a live SageFs session. Each session starts with an empty watched set; users
explicitly add or remove paths. The module provides pure functional operations
over an immutable state record.

---

## Type

```
T = { Watched: Set<string> }
```

Paths are **normalised** before storage: backslashes become forward slashes and
all characters are lowercased. The normalisation is transparent to callers.

---

## Preconditions

- All `path` arguments must be non-null strings.
- `watch`, `unwatch`, `toggle`, `isWatched` accept a single path.
- `watchMany`, `watchAll`, `watchByDirectory` etc. accept sequences/collections.

---

## Postconditions

| Operation | Result |
|-----------|--------|
| `empty` | `Watched = ∅` |
| `watch p s` | `Watched = s.Watched ∪ {normalize p}` |
| `unwatch p s` | `Watched = s.Watched \ {normalize p}` |
| `isWatched p s` | `normalize p ∈ s.Watched` |
| `watchMany ps s` | `Watched = s.Watched ∪ {normalize p | p ∈ ps}` |
| `unwatchAll s` | `Watched = ∅` (returns `empty`) |
| `watchAll ps _` | `Watched = {normalize p | p ∈ ps}` (ignores prior state) |
| `toggle p s` | if watched: `unwatch p s`; else: `watch p s` |
| `watchedCount s` | `\|s.Watched\|` |
| `watchByDirectory dir allPaths s` | adds all paths whose directory equals or is under `dir` |
| `unwatchByDirectory dir s` | removes all watched paths under `dir` |
| `watchedInDirectory dir s` | list of watched paths under `dir` |
| `watchByProject ps s` | equivalent to `watchMany ps s` |
| `unwatchByProject ps s` | removes all paths that appear in `ps` |

---

## Invariants

1. **Set semantics**: `Watched` is a set — no duplicates, order-independent membership.
2. **Normalisation idempotence**: normalising an already-normalised path is a no-op.
3. **watch/unwatch inverse**: `unwatch p (watch p s)` has the same `Watched` as `s`.
4. **toggle involution**: `toggle p (toggle p s)` has the same `Watched` as `s`.
5. **watchAll ignores prior state**: result depends only on `ps`, not on `s`.
6. **unwatchAll resets**: `(unwatchAll s).Watched = ∅` for all `s`.

---

## Edge Cases

- `watch p s` when `p` is already watched: idempotent (set semantics).
- `unwatch p s` when `p` is not watched: no-op.
- `toggle p s` on empty state: adds `p`.
- `watchMany [] s` = `s` (no change).
- `watchAll [] _` = `empty`.
- `watchedCount empty = 0`.

---

## Examples

```
let s0 = empty                       // Watched = {}
let s1 = watch "Foo.fs" s0           // Watched = {"foo.fs"}
let s2 = watch "FOO.FS" s1           // Watched = {"foo.fs"}  (normalised, idempotent)
let s3 = watch "Bar.fs" s1           // Watched = {"foo.fs", "bar.fs"}
let s4 = unwatch "foo.fs" s3         // Watched = {"bar.fs"}
let s5 = toggle "foo.fs" s4          // Watched = {"foo.fs", "bar.fs"}
let s6 = toggle "foo.fs" s5          // Watched = {"bar.fs"}
let s7 = unwatchAll s3               // Watched = {}
let s8 = watchAll ["A.fs";"B.fs"] s3 // Watched = {"a.fs", "b.fs"}
```

---

## Inferred Intent

- The normalisation exists so that callers on Windows (backslash paths) and
  Linux (forward slash) refer to the same file.
- The design is intentionally simple: a pure set. No event notification,
  no file-system access.

---

## Open Questions

- Should `normalize` be exposed as a public API for callers to pre-normalise?
- Are `watchByDirectory` and `watchedInDirectory` considered part of the core spec
  or convenience helpers? (They depend on `System.IO.Path.GetDirectoryName`.)
