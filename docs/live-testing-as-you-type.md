# As-You-Type Live Testing — Architecture & Design

> **Status note**: This document is partly historical, and I'm leaving it that way on
> purpose. It explains *why* the design looks the way it does, even where the shipped
> mechanism ended up different. The currently shipped compiled-project editor path uses
> **session-scoped buffer sync** via `POST /api/sessions/{sid}/buffer-changed` with
> debounced unsaved buffer content from the current editors (VS Code and Neovim). The
> `POST /api/live-testing/evaluate-scope` contract below never shipped as written. It's a
> design exploration, and it isn't the current editor/daemon contract.

> **Priority**: #1. This feature is meant to outclass VS Enterprise's Live Unit Testing.
> I don't say that to be cocky about it; I say it because it's the actual bar I'm
> measuring against, and if I don't beat it there's no reason for this feature to exist.

> **Maturity**: Live testing is functional but still being stabilized. Expect rough
> edges around session switching and test-discovery timing. Expecto has the best
> coverage. The speed numbers below are the design target and typical measurements.
> Don't hold me to a specific millisecond count on your machine.

## How This Compares to VS Enterprise

VS Enterprise's Live Unit Testing triggers on unsaved edits, same as SageFs. The
architecture is different: VS Enterprise copies your buffer to a ProjFS workspace, runs
full MSBuild, instruments IL, then runs the tests, which takes 5-30 seconds. SageFs sends
the changed function definition straight to FSI, a REPL that redefines bindings on the
fly, with no build, no file copying, and no IL instrumentation. Feedback arrives in under
a second, which is the whole point. Tests run as you type and go green before you even
hit save, and it still feels great every time it works.

| Dimension | VS Enterprise | SageFs |
|-----------|--------------|--------|
| **Trigger** | Unsaved edits | Unsaved edits |
| **Speed** | 5-30s (MSBuild + IL instrumentation) | 300-800ms end-to-end (debounce + type-check + FSI eval) |
| **Mechanism** | ProjFS workspace → MSBuild → IL instrumentation → test run | Extract scope → type-check snippet → FSI eval → test run |
| **Broken code** | Dead: must compile to instrument | Tree-sitter/LSP works mid-keystroke |
| **Scope** | Rebuilds impacted projects | Single function definition |
| **Frameworks** | xUnit, NUnit, MSTest | + Expecto, TUnit, extensible; FsCheck `[<Property>]` tests are discovered and shown in the live panel |
| **Clients** | Visual Studio only | Neovim, VS Code, web dashboard, MCP |
| **Platform** | Windows only (ProjFS) | Cross-platform (.NET) |
| **Cost** | ~$250/month Enterprise license | Free, MIT |

The trigger mechanism is the same as VS Enterprise's. The advantages are speed, scope,
tolerance for broken code, editor breadth, framework breadth, and cost.

## FSI Is a REPL

FSI (F# Interactive) is a REPL: send it a function definition and it redefines that
binding immediately. There's no need to send a whole file, use `#load`, create temp
files, or use shadow copies. SageFs already works this way: `sagefs-send_fsharp_code`
sends arbitrary F# snippets to FSI continuously.

```fsharp
// Send this to FSI:
let validate x = x + 1;;
// FSI redefines `validate`. Any code that references `validate` picks up the new definition.
```

This means the as-you-type pipeline is:

1. Editor extracts the changed function scope (the `let` binding being edited)
2. Editor POSTs just that scope to SageFs
3. SageFs type-checks the snippet in project context
4. If it type-checks: SageFs sends it to FSI, which redefines the binding
5. SageFs runs affected tests (which now call the redefined function)
6. Results pushed via SSE

No files are written, no `#load`, no shadow copies, and no patching. The REPL just
redefines bindings directly. That's also just... what a REPL is supposed to do. I
kept being surprised how few tools actually lean on that.

## Endpoint Contract

All editors POST to one endpoint:

```
POST /api/live-testing/evaluate-scope
Content-Type: application/json

{
  "filePath": "C:/Code/Project/src/Domain.fs",
  "scopeName": "validate",
  "scopeText": "let validate x =\n  if x > 0 then Ok x\n  else Error \"negative\"",
  "startLine": 5,
  "endLine": 8,
  "generation": 42
}
```

| Field | Purpose |
|-------|---------|
| `filePath` | Identifies which file the scope belongs to (for affected-test lookup) |
| `scopeName` | Function name (for dependency graph lookup) |
| `scopeText` | The actual code to send to FSI |
| `startLine` / `endLine` | Where in the file this scope lives (for mapping) |
| `generation` | Client-side monotonic counter. Server discards stale requests. |

### Why Scope-Level Payloads

FSI evaluates expressions and definitions, not whole files. Sending the full file on
every keystroke would redefine every binding in the file (wasteful), re-run the module's
side effects if it has any, take longer to type-check (a whole file instead of one
function), and send 10-80KB instead of 0.5-5KB.

The scope-level payload matches how FSI actually works: the editor extracts the function
being edited, sends just that definition, and FSI redefines just that binding.

## Scope Detection Per Editor

Each editor uses its native mechanism to find the enclosing function:

| Editor | Mechanism | Broken-Code Behavior |
|--------|-----------|---------------------|
| **Neovim** | Tree-sitter `value_declaration` walk | Error-tolerant: works mid-keystroke |
| **VS Code** | `vscode.executeDocumentSymbolProvider` (Ionide LSP) | Cached symbols from last successful parse |

Both are 5-15 lines of editor-specific code. They don't need to share an
implementation; they just need to produce `{ scopeName, scopeText, startLine, endLine }`.

When scope detection fails (for example, the cursor is between functions, or the syntax
is too broken), the editor doesn't POST. The user just sees stale results until the code
stabilizes.

## Server Test Cycle

```
POST /api/live-testing/evaluate-scope received
  → Discard if generation < current for this filePath
  → Type-check scopeText in project context (FCS)
  → If type-check fails: emit scope_check_failed SSE event with diagnostics, STOP
  → Send scopeText to FSI session (redefines the binding)
  → Look up affected tests via PerFileIndex + TransitiveCoverage using filePath + scopeName
  → Run affected tests
  → SSE push results
```

### What About Signature Changes?

If the user changes a function's return type (even implicitly via type inference),
callers compiled against the old signature are stale. FSI redefines the function with
the new signature, but tests compiled against the old one may fail with type mismatches.

The approach: type-check the scope and compare the inferred signature with the cached
previous one. If the signature is stable (90% of edits), send it to FSI and run the
tests: the fast path. If the signature changed (10% of edits), mark dependent tests as
"Stale" via SSE; saving the file triggers the existing file-watcher reload path, which
recompiles dependents. Even this path is faster than VS Enterprise's 5-30s.

### Performance Budget

| Step | Time |
|------|------|
| Debounce (client) | 300ms (configurable) |
| HTTP POST localhost | <1ms |
| FCS type-check (single function, warm context) | 20-100ms |
| FSI eval (redefine binding) | 5-20ms |
| Affected test execution | 10-500ms (depends on test count/complexity) |
| SSE push | <5ms |
| **Total end-to-end** | **300-800ms typical** |

Type-checking a single function in a warm FCS context is much faster than type-checking a
whole file, which is another advantage of scope-level evaluation.

## Dependency Graph Model

SageFs already maintains `TestDependencyGraph`:

```fsharp
type TestDependencyGraph = {
  SymbolToTests: Map<string, TestId array>        // fully-qualified symbol → tests that cover it
  TransitiveCoverage: Map<string, TestId array>    // includes indirect callers
  PerFileIndex: Map<string, Map<string, TestId array>>  // filePath → symbol → tests
  SourceVersion: int
}
```

Keys are fully-qualified names (for example, `Payments.validate`, not `validate`) so
functions with the same name in different modules stay distinct.

Transitive closure is computed eagerly when the map is built. If test T covers function
A, and A calls function B, editing B correctly triggers T.

Local functions and closures are captured by their parent scope, so they need no
separate mapping.

## Failure Modes

| Scenario | Behavior | User Experience |
|----------|----------|----------------|
| Broken code mid-typing | Scope detection still works (tree-sitter/LSP) but type-check fails | No test update. Previous results remain. |
| Scope detection fails | Editor doesn't POST | No test update. Previous results remain. |
| Type signature changed | FSI redefines binding, dependent tests may fail | Tests marked Stale. Save triggers full reload. |
| Cross-file type change | Only the edited function is redefined | Stale until save triggers broader reload. |
| Two editors same file | Generation counter orders requests | Most recent edit evaluated. |
| SageFs daemon not running | POST fails | Editor shows "SageFs not connected". |

By this design, body changes (90% of edits) get instant feedback, and signature changes
(10%) fall back to a save-triggered refresh. Both are still faster than VS Enterprise.

## Editor Implementation Guide

Each editor implements:

```
1. Register text change listener for *.fs files
2. On change: debounce 300ms (cancel previous timer)
3. After debounce: find enclosing function scope
4. POST scope to /api/live-testing/evaluate-scope
5. Display results from existing SSE subscription (already implemented)
```

### Neovim
- `TextChanged` + `TextChangedI` autocmds (currently only fires for `*.fsx`; fix to include `*.fs`)
- Tree-sitter `value_declaration` walk for scope extraction (error-tolerant, works on broken code)
- Existing SSE + gutter rendering already handles results

### VS Code
- `workspace.onDidChangeTextDocument` for F# files
- `vscode.executeDocumentSymbolProvider` for scope extraction (Ionide provides this)
- Fallback: indentation-based scan if Ionide not available
- Existing `LiveTestingListener` + `TestDecorations` handle results

## Decision Log

| Decision | Alternatives Considered | Rationale |
|----------|------------------------|-----------|
| Scope-level payload | Full-file payload | FSI is a REPL: it redefines individual bindings. Sending the whole file is wasteful and misunderstands the tool. |
| Client-side scope detection | Server-side detection | Editor knows the cursor position and has native scope detection (tree-sitter, LSP, indentation). Server doesn't know where the user is typing. |
| Three editor-specific scope detectors | Shared module via Fable | Each is 5-15 lines. Sharing adds build complexity for no gain. |
| Signature change → stale until save | Cross-file reload in FSI | 90/10 rule: body changes are instant, signature changes are rare. |
| Generation counter for ordering | Timestamp-based | Monotonic counter avoids clock skew between editors. |
| 300ms debounce default | Adaptive per-editor | Ship consistent, tune per-editor from user feedback later. |

If you're reading this looking for the actual shipped contract, go read
[buffer-changed in the HTTP API tests](../SageFs.Tests/HttpApiIntegrationTests.fs) instead.
This page is the map of the road not fully taken, kept around because the reasoning in
it is still good even where the exact endpoint shape changed.
