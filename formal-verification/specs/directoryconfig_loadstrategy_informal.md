# Informal Specification: DirectoryConfig.LoadStrategy

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*
> Source: `SageFs.Core/DirectoryConfig.fs`

---

## Purpose

`LoadStrategy` is a 4-case discriminated union that controls *how* SageFs loads
F# projects and solutions when starting a session for a working directory.

```fsharp
type LoadStrategy =
  | Solution  of path: string       // load a specific .sln / .slnx file
  | Projects  of paths: string list // load specific .fsproj files
  | AutoDetect                      // auto-discover from the directory tree
  | NoLoad                          // bare FSI — no project loading
```

`DirectoryConfig` is the per-directory configuration record that wraps a
`LoadStrategy` together with other settings. `DirectoryConfig.empty` is the
canonical default configuration used when no `.SageFs/config.fsx` file exists.

Path-computing helpers `configDir` and `configPath` derive where the config file
should live relative to a working directory.

---

## Preconditions

### `LoadStrategy` (DU value)
- No preconditions; every constructor is valid by construction.
- `Solution path` is valid for any `string path`, including empty string (the
  path is not validated at construction time — validation happens at session start).
- `Projects paths` is valid for any `string list`, including empty list.

### `DirectoryConfig.empty`
- No preconditions; it is a constant value.

### `configDir workingDir`
- `workingDir` is a non-null, non-empty directory path.

### `configPath workingDir`
- Same as `configDir`.

---

## Postconditions

### `LoadStrategy` constructor exhaustiveness
- There are exactly 4 cases: `Solution`, `Projects`, `AutoDetect`, `NoLoad`.
- Every `LoadStrategy` value is one of these four cases.

### `DirectoryConfig.empty` defaults
- `empty.Load = AutoDetect` — new sessions auto-detect projects unless configured.
- `empty.InitScript = None` — no init script by default.
- `empty.DefaultArgs = []` — no extra FSI args by default.
- `empty.AutoOpenNamespaces = true` — auto-open enabled by default.
- `empty.Keybindings = Map.empty` — no custom keybindings.
- `empty.ThemeOverrides = Map.empty` — no theme overrides.
- `empty.IsRoot = false` — not treated as a monorepo root by default.
- `empty.SessionName = None` — no custom session name.

### `configDir workingDir`
- Returns `workingDir + "/.SageFs"` (using `Path.Combine`).
- The result ends with `"/.SageFs"` (or `"\.SageFs"` on Windows).
- `configDir w` is a suffix-extension of `w`.

### `configPath workingDir`
- Returns `configDir(workingDir) + "/config.fsx"`.
- Equivalent to `workingDir + "/.SageFs/config.fsx"`.
- The result is a suffix-extension of `configDir(workingDir)`.

---

## Invariants

1. **Exhaustiveness**: `LoadStrategy` has exactly 4 constructors. A pattern match
   on `LoadStrategy` must handle all 4 cases.

2. **AutoDetect is the default**: `empty.Load = AutoDetect`. When no config file
   is present, SageFs behaves as if `AutoDetect` was chosen.

3. **Path suffix**: `configPath w` ends with `".SageFs/config.fsx"` for any `w`.
   `configDir w` ends with `".SageFs"` for any `w`.

4. **Monotone path composition**: `configPath w = configDir w + "/config.fsx"`.
   These helpers are pure functions; same input → same output.

5. **Projects empty-list edge case**: `Projects []` is a valid but degenerate
   value — it requests loading "no projects". SageFs may treat this the same as
   `NoLoad` in practice, but the type does not enforce this.

---

## Edge Cases

- **`Solution ""`**: An empty path is a valid `Solution` constructor. Session
  startup will fail when it tries to open the empty-string path, but the
  `LoadStrategy` value itself is constructable.

- **`Projects []`**: An empty project list. Semantically equivalent to `NoLoad`
  for session initialisation, but type-distinct.

- **`configDir ""`**: `Path.Combine("", ".SageFs")` → `".SageFs"` (relative
  path). This is technically valid but will behave differently from an absolute
  path in file-system operations.

- **`configDir`/`configPath` with trailing separator**: `Path.Combine` normalises
  these; the result should not double the separator.

---

## Examples

```fsharp
// Constructor roundtrip: every LoadStrategy value is one of 4 cases
match ls with
| Solution p -> p |> ignore
| Projects ps -> ps |> ignore
| AutoDetect -> ()
| NoLoad -> ()

// empty defaults
DirectoryConfig.empty.Load = LoadStrategy.AutoDetect  // true
DirectoryConfig.empty.DefaultArgs = []                // true
DirectoryConfig.empty.IsRoot = false                  // true

// Path helpers
configDir "/home/user/myproject" = "/home/user/myproject/.SageFs"
configPath "/home/user/myproject" = "/home/user/myproject/.SageFs/config.fsx"
```

---

## Inferred Intent

- `LoadStrategy` is the single discriminated union that drives the entire project-loading
  strategy for a session. The 4 cases cover the full spectrum from "specific solution" to
  "nothing" and are designed to be mutually exclusive and exhaustive by construction.
- `AutoDetect` is the "safe default" — it makes SageFs useful out-of-the-box without any
  configuration.
- `NoLoad` exists for pure FSI scripting use cases where project loading would be harmful
  or unnecessary (e.g., a scratch session, a build script).
- The `configPath`/`configDir` helpers follow a fixed `.SageFs/` subdirectory convention.
  This convention is baked into the type system indirectly — callers that need the config
  path always go through these helpers.

---

## Open Questions

1. **`Projects []` semantics**: Is an empty `Projects` list treated as `NoLoad` by the
   session initialiser, or does it produce an error? The type does not encode this
   constraint. A maintainer should clarify whether `Projects []` should be disallowed
   at construction time or handled explicitly in session startup.

2. **Cross-platform path separators**: `Path.Combine` is OS-aware. The suffix property
   `configPath w` ends with `".SageFs/config.fsx"` on Unix but `".SageFs\config.fsx"`
   on Windows. The Lean model will need to abstract away the path separator or use a
   cross-platform suffix predicate.

3. **`Solution` path normalisation**: Is `Solution "MyApp.slnx"` (relative) handled the
   same as `Solution "/abs/path/MyApp.slnx"` (absolute)? The type accepts both; session
   startup presumably resolves relative paths against the working directory.

---

## Specification for Lean 4 Formalisation

The following properties are directly formalizable in Lean 4 with `decide` or `rfl`:

| Property | Lean approach |
|----------|--------------|
| `LoadStrategy` has exactly 4 cases | `decide` on `#eval [Solution "", Projects [], AutoDetect, NoLoad]` |
| `empty.Load = AutoDetect` | `rfl` |
| `empty.DefaultArgs = []` | `rfl` |
| `empty.IsRoot = false` | `rfl` |
| `empty.AutoOpenNamespaces = true` | `rfl` |
| `empty.SessionName = none` | `rfl` |
| `configDir w = w ++ "/.SageFs"` (abstract) | inductive/`simp` on string concat model |
| `configPath w = configDir w ++ "/config.fsx"` | `rfl` in Lean model |
| `Projects []` distinct from `NoLoad` | `decide` |
| `AutoDetect` distinct from all others | `decide` |

The `configDir`/`configPath` helpers involve `System.IO.Path.Combine` (effectful, OS-aware).
In Lean we model them as pure string concatenation functions, abstracting away
OS path separators.
