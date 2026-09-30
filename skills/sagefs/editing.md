# Editing safely

Read this before your first edit in a session, when you are about to change more than one call site, when a `#load` gives a type-incompatibility error, or when a build fails on F# syntax you could have known.

## Editing: prove it, then write it

The loop (see loop.md) is for CODE. This is for CHANGING it, and it is the second
biggest source of wasted time after drifting back to `dotnet`.

**The rule: never write a change you have not already seen work.** Not "seen
work somewhere similar" — seen *this* change, on *this* code, produce *this*
result. A `send_fsharp_code` eval of the new logic takes under a second. The
`edit_file` that persists it is instant. The `dotnet build` that finds out you
misread the type is two minutes, and it finds out after you have already edited.

So, in order:

1. **Prove the behaviour in the REPL.** Write the new function, call it on real
   input, and *read the output* before it touches a file.
2. **Then persist it** with the editor tool — read the region, replace that
   exact text.
3. **Then re-verify** with `hard_reset_fsi_session rebuild=true`.

### No `sed -i`, no `python3 -c`, no bulk regex rewrites

This is not a style preference. A scripted edit rewrites what it did not read,
swallows the one file that differed, and cannot tell you which of thirty call
sites it actually changed. **A silent no-op reads exactly like a successful
one**, which is the worst possible failure: you go on to build, the build is
green, and the thing you meant to change is unchanged.

- **Use the editor tool for a specific change.** A call that did not match is a
  bug to investigate, not something to widen until something sticks.
- **Scripting is F#, and it is dogfooded.** A genuinely repeated mechanical
  change goes in an `.fsx` run through SageFs — then the script is F# you can
  evaluate, and it reports its own result. It MUST assert that each
  replacement matched, and say so out loud, rather than exiting quietly.
- **Verify the diff after any scripted change.** A pattern that matched in 4
  files and silently missed a 5th is exactly what this rule exists to prevent.

### The `#load` duplicate trap

`#load` a Core file into a session that **already has Core loaded**, and you
get two copies of every type. The symptom is not a load error:

```
The type 'FSI_0035.SageFs.HolderRegistry' is not compatible with the type 'SageFs.HolderRegistry'
```

That is not a version mismatch and not a bug in the code — it is your own
`#load`. The rule:

- **Core (or any already-loaded project) is never `#load`ed.** Use the loaded
  types directly. If the session's Core predates your change, that is *skew*,
  not a reason to `#load` — see "a stale daemon" in troubleshooting.md.
- **`#load` a PURE file** — one with no dependency on the loaded project. That
  is the reliable way to evaluate a pure decision function fast, and it is how
  you prototype a new module before it is wired in.

### F# syntax that costs a build cycle

Cheaper to know up front than to discover in a 90-second build:

- `///` is not valid between DU fields — use `//`.
- A DU case carrying an inline value needs parens (`Case 0`), or F# reads it as
  function application.
- Cases under `[<RequireQualifiedAccess>]` must be qualified at the call site.
- `_.X` does not chain — use an explicit `this` parameter.
- `let mutable` bindings must precede members in a type.
- Two types with the same name in one namespace collide at every call site.
- A case with two fields wants one tuple, not a bare `*` list.
- `ResizeArray.Remove` returns a `bool` that must be explicitly discarded.
