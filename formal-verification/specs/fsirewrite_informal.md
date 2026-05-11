# Informal Specification: FsiRewrite

> 🔬 *Lean Squad — automated formal verification for `WillEhrendreich/SageFs`.*
> Source: `SageFs.Core/FsiRewrite.fs`

## Purpose

`rewriteInlineUseStatements (code: string) : string` preprocesses F# source code before
sending it to F# Interactive (FSI). FSI cannot accept `use x = expr` bindings in expression
context (i.e. as part of an indented expression block). This function rewrites every
**indented** `use` binding to `let`, making such code valid in FSI.

Top-level (non-indented) `use` bindings are left unchanged, since they are valid at
module level in FSI.

## Preconditions

- `code` is a valid UTF-8 string (may be empty).
- Line endings are `'\n'` (the Split separator).

## Postconditions

For each line in `code.Split('\n')`:
- If `line.TrimStart().StartsWith("use ")` **and** `line.Length > line.TrimStart().Length`
  (i.e. the line is indented), then the line in the output has `"use "` replaced by `"let "`
  in the trimmed position — keeping the original indentation prefix intact.
- Otherwise, the line is unchanged.

The output is `String.Join('\n', rewrittenLines)`:
- if any line was rewritten, the rewritten join is returned;
- if no lines needed rewriting, the original `code` string is returned (optimisation).

Both branches produce the same semantic output.

## Invariants

1. **Idempotency**: `rewrite(rewrite(code)) = rewrite(code)` — applying twice is the same
   as applying once. After a `use` is rewritten to `let`, the resulting line starts with
   `"let "` (not `"use "`), so the second pass leaves it unchanged.

2. **Character-count preservation**: `"use "` and `"let "` are both exactly 4 characters,
   so the total character count of the output equals the total character count of the input.

3. **Line-count preservation**: splitting and joining with the same delimiter preserves the
   number of lines.

4. **Non-indented use untouched**: a top-level `use foo = expr` (no leading whitespace) is
   not rewritten. The condition `line.Length > trimmed.Length` prevents it.

5. **Non-use lines untouched**: lines that do not start (after trimming) with `"use "` are
   passed through unchanged.

## Edge Cases

- **Empty string**: `""` splits to `[""]`, neither of which matches the condition; output is `""`.
- **All whitespace line**: `"   "` trims to `""`, which does not start with `"use "`; unchanged.
- **`use` with no space**: `"  useX"` trims to `"useX"`, which starts with `"useX"` not `"use "`;
  unchanged.
- **Top-level `use`**: `"use x = foo()"` has `trimmed.Length = line.Length`; unchanged.
- **Multiple indented uses**: each is rewritten independently; idempotency still holds.
- **`use` as part of a longer word**: `"  used_variable"` trims to `"used_variable"` which
  does NOT start with `"use "` (it starts with `"used"`); unchanged.

## Examples

| Input line | Output line | Reason |
|-----------|-------------|--------|
| `"  use x = File.OpenRead(path)"` | `"  let x = File.OpenRead(path)"` | Indented use → let |
| `"    use conn = db.Open()"` | `"    let conn = db.Open()"` | Indented use → let |
| `"use x = foo"` | `"use x = foo"` | Top-level use, no indent → unchanged |
| `"  let y = x + 1"` | `"  let y = x + 1"` | Not a use → unchanged |
| `"  // use x = ..."` | `"  // use x = ..."` | Trimmed starts with `"//"` → unchanged |
| `""` | `""` | Empty line → unchanged |
| `"use "` | `"use "` | No indent → unchanged |

## Inferred Intent

The function exists because FSI evaluates code snippets in expression context, where
`use` binding syntax is not valid. The rewrite allows SageFs to pass user code (which may
use idiomatic `use` for IDisposable resources) to FSI without syntax errors.

The identity-return optimisation (`if rewritten then join else code`) avoids creating a
new string object when no rewriting is needed. This is semantically equivalent to always
returning the joined form.

## Open Questions

None. The semantics are fully captured by the source code and the preconditions above.
