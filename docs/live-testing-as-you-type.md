# As-You-Type Live Testing — Architecture & Design

> **Priority**: #1. This is the feature that makes VS Enterprise look like a joke.

## The Pitch (Honest Framing)

VS Enterprise's Live Unit Testing triggers on unsaved edits — same as us. The difference is **architecture**: they copy your buffer to a ProjFS workspace, run full MSBuild, instrument IL, then execute tests. That takes **5-30 seconds**. SageFs sends the changed function definition straight to FSI — a REPL that redefines bindings on the fly. No build. No file copying. No IL instrumentation. **Sub-second feedback**.

| Dimension | VS Enterprise | SageFs |
|-----------|--------------|--------|
| **Trigger** | Unsaved edits | Unsaved edits |
| **Speed** | 5-30s (MSBuild + IL instrumentation) | 300-800ms end-to-end (debounce + type-check + FSI eval) |
| **Mechanism** | ProjFS workspace → MSBuild → IL instrumentation → test run | Extract scope → type-check snippet → FSI eval → test run |
| **Broken code** | Dead — must compile to instrument | Tree-sitter/LSP works mid-keystroke |
| **Scope** | Rebuilds impacted projects | Single function definition |
| **Frameworks** | xUnit, NUnit, MSTest | + Expecto, TUnit, extensible |
| **Editors** | Visual Studio only | Neovim, VS Code, Visual Studio, TUI, GUI |
| **Platform** | Windows only (ProjFS) | Cross-platform (.NET) |
| **Cost** | ~$250/month Enterprise license | Free, MIT |

**We don't compete on trigger mechanism — we compete on speed, scope, broken-code tolerance, editor breadth, framework breadth, and cost.**

## Core Insight: FSI Is a REPL

FSI (F# Interactive) is a **REPL**. You send it a function definition, it redefines that binding immediately. You don't need to send a whole file. You don't need `#load`. You don't need temp files. You don't need shadow copies. This is what SageFs already does — `sagefs-send_fsharp_code` sends arbitrary F# snippets to FSI all day long.

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

**No files written. No `#load`. No shadow copies. No patching. Just a REPL doing what REPLs do.**

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

### Why Scope-Level, Not Full-File

FSI is a REPL. It evaluates expressions and definitions, not files. Sending the full file would mean:
- Redefining EVERY binding in the file on every keystroke (wasteful)
- Re-running the module's side effects (if any)
- Slower type-checking (whole file vs one function)
- Sending 10-80KB instead of 0.5-5KB

The scope-level payload matches how FSI actually works. The editor extracts the function being edited, sends just that definition, FSI redefines just that binding.

## Scope Detection Per Editor

Each editor uses its native mechanism to find the enclosing function:

| Editor | Mechanism | Broken-Code Behavior |
|--------|-----------|---------------------|
| **Neovim** | Tree-sitter `value_declaration` walk | Error-tolerant — works mid-keystroke |
| **VS Code** | `vscode.executeDocumentSymbolProvider` (Ionide LSP) | Cached symbols from last successful parse |
| **Visual Studio** | Indentation-based scan | F# indentation-sensitivity makes this 90%+ accurate |

All three are 5-15 lines of editor-specific code. They don't need to agree on implementation — they just need to produce `{ scopeName, scopeText, startLine, endLine }`.

**When scope detection fails** (e.g., cursor is between functions, or syntax is too broken): the editor simply doesn't POST. No harm done — user sees stale results until the code stabilizes.

## Server Pipeline

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

If the user changes a function's return type (even implicitly via type inference), callers compiled against the old signature are stale. FSI redefines the function with the new signature, but tests compiled against the old one may fail with type mismatches.

**Approach**: Type-check the scope, compare inferred signature with the cached previous signature:
- **Signature stable** (90% of edits): Send to FSI, run tests. Fast path.
- **Signature changed** (10%): Mark dependent tests as "Stale" via SSE. Save triggers the existing file-watcher reload path (recompile dependents). Still faster than VS Enterprise's 5-30s.

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

Type-checking a single function in warm FCS context is significantly faster than type-checking a whole file. This is another advantage of scope-level evaluation.

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

**Keys are fully-qualified names** (e.g., `Payments.validate`, not `validate`) to distinguish same-named functions in different modules.

Transitive closure is computed eagerly at map-build time. Editing function B (where A calls B and test T covers A) correctly triggers T.

Local functions and closures are captured by their parent scope — no separate mapping needed.

## Failure Modes

| Scenario | Behavior | User Experience |
|----------|----------|----------------|
| Broken code mid-typing | Scope detection still works (tree-sitter/LSP) but type-check fails | No test update. Previous results remain. |
| Scope detection fails | Editor doesn't POST | No test update. Previous results remain. |
| Type signature changed | FSI redefines binding, dependent tests may fail | Tests marked Stale. Save triggers full reload. |
| Cross-file type change | Only the edited function is redefined | Stale until save triggers broader reload. |
| Two editors same file | Generation counter orders requests | Most recent edit evaluated. |
| SageFs daemon not running | POST fails | Editor shows "SageFs not connected". |

**The 90/10 rule**: Body changes (90% of edits) get instant feedback. Signature changes (10%) degrade to save-triggered refresh. Both are still faster than VS Enterprise.

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
- `TextChanged` + `TextChangedI` autocmds (currently only fires for `*.fsx` — fix to include `*.fs`)
- Tree-sitter `value_declaration` walk for scope extraction (error-tolerant, works on broken code)
- Existing SSE + gutter rendering already handles results

### VS Code
- `workspace.onDidChangeTextDocument` for F# files
- `vscode.executeDocumentSymbolProvider` for scope extraction (Ionide provides this)
- Fallback: indentation-based scan if Ionide not available
- Existing `LiveTestingListener` + `TestDecorations` handle results

### Visual Studio
- `ITextViewChangedListener` from VS Extensibility SDK
- Indentation-based scan for scope extraction (F# indentation-sensitivity makes this reliable)
- Existing `LiveTestingSubscriber` + CodeLens handle results

## Decision Log

| Decision | Alternatives Considered | Rationale |
|----------|------------------------|-----------|
| Scope-level payload | Full-file payload | FSI is a REPL — it redefines individual bindings. Sending the whole file is wasteful and misunderstands the tool. |
| Client-side scope detection | Server-side detection | Editor knows the cursor position and has native scope detection (tree-sitter, LSP, indentation). Server doesn't know where the user is typing. |
| Three editor-specific scope detectors | Shared module via Fable | Each is 5-15 lines. Sharing adds build complexity for no gain. |
| Signature change → stale until save | Cross-file reload in FSI | 90/10 rule — body changes are instant, signature changes are rare. |
| Generation counter for ordering | Timestamp-based | Monotonic counter avoids clock skew between editors. |
| 300ms debounce default | Adaptive per-editor | Ship consistent, tune per-editor from user feedback later. |
