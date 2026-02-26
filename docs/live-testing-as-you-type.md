# As-You-Type Live Testing — Architecture & Design

> **Priority**: #1. This is the feature that makes VS Enterprise look like a joke.

## The Pitch (Honest Framing)

VS Enterprise's Live Unit Testing triggers on unsaved edits — same as us. The difference is **architecture**: they copy your buffer to a ProjFS workspace, run full MSBuild, instrument IL, then execute tests. That takes **5-30 seconds**. SageFs replaces that entire chain with FSI hot-reload: type-check the changed file, `#load` it, run affected tests. **Sub-second feedback** for function body changes, vs 5-30 seconds for VS Enterprise.

| Dimension | VS Enterprise | SageFs |
|-----------|--------------|--------|
| **Trigger** | Unsaved edits | Unsaved edits |
| **Speed** | 5-30s (MSBuild + IL instrumentation) | 400ms-1.5s end-to-end (debounce + FCS + FSI) |
| **Broken code** | Dead — must compile to instrument | Tree-sitter works mid-keystroke |
| **Scope** | Rebuilds impacted projects | Single file, affected tests only |
| **Frameworks** | xUnit, NUnit, MSTest | + Expecto, TUnit, extensible |
| **Editors** | Visual Studio only | Neovim, VS Code, Visual Studio, TUI, GUI |
| **Platform** | Windows only (ProjFS) | Cross-platform (.NET) |
| **Cost** | ~$250/month Enterprise license | Free, MIT |

**We don't compete on trigger mechanism — we compete on speed, scope, broken-code tolerance, editor breadth, framework breadth, and cost.**

## Endpoint Contract

All editors POST to one endpoint:

```
POST /api/live-testing/evaluate-scope
Content-Type: application/json

{
  "filePath": "C:/Code/Project/src/Domain.fs",
  "fullText": "module Domain\n\nlet validate x = ...\n...",
  "editRegion": { "startLine": 5, "endLine": 12 },
  "generation": 42
}
```

| Field | Purpose |
|-------|---------|
| `filePath` | Identifies the file being edited |
| `fullText` | Complete buffer content (NOT a diff, NOT a scope fragment) |
| `editRegion` | Optimization hint — where the keystrokes landed. Server uses this for incremental FCS checking. If wrong, server degrades to full-file analysis. |
| `generation` | Client-side monotonic counter. Server discards requests where `generation < currentGeneration` for this file. Prevents stale evaluations. |

### Why Full-File, Not Scope-Level

The original design had editors extract just the changed function scope and POST that. The expert panel unanimously pivoted to full-file payloads because:

1. **Eliminates line-number drift** — no shadow patching, no merge conflicts between editors
2. **Server already needs the full file** — FCS type-checks files, not functions. FSI `#load`s files, not functions.
3. **Makes clients trivially simple** — "register change listener → debounce → send full text" is 5-10 lines per editor
4. **Cross-editor parity by construction** — all editors send identical payloads, server owns all intelligence
5. **Concurrent editors just work** — last-write-wins on the server's shadow copy, generation counter orders them

The bandwidth cost (~10-80KB per request over localhost) is negligible. The server was paying the FCS type-check cost regardless.

## Server Pipeline

```
Request received
  → Discard if generation < current for this filePath
  → Replace in-memory shadow copy with fullText
  → FCS type-check the patched file (using editRegion hint for incremental check)
  → If type-check fails: emit scope_check_failed SSE event with diagnostics, STOP
  → Extract inferred type signatures from typed AST
  → Compare signatures with cached previous signatures
  → If signatures stable:
      → #load temp file in FSI (module redefined, tests pick up new definitions)
      → Look up affected tests via PerFileIndex + TransitiveCoverage
      → Run affected tests
      → SSE push results
  → If signatures changed:
      → Mark dependent tests as "Stale"
      → SSE push stale markers
      → Schedule broader reload on next file save
```

### Performance Budget

| Step | Time |
|------|------|
| Debounce (client) | 300ms (configurable) |
| HTTP POST localhost | <1ms |
| FCS type-check (warm, single file) | 50-200ms |
| Signature comparison | <1ms |
| FSI `#load` | 10-50ms |
| Affected test execution | 10-500ms (depends on test count/complexity) |
| SSE push | <5ms |
| **Total end-to-end** | **400ms-1.5s typical** |

First type-check after project load may take 300-500ms (cold FCS context). Subsequent checks reuse cached dependency resolution: 50-150ms.

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

**Keys are fully-qualified names** (e.g., `Payments.validate`, not `validate`) to distinguish same-named functions in different modules. FCS `FSharpSymbolUse.FullName` provides this.

Transitive closure is computed eagerly at map-build time. Editing function B (where A calls B and test T covers A) correctly triggers T.

Local functions and closures are captured by their parent scope — no separate mapping needed.

## Failure Modes

| Scenario | Behavior | User Experience |
|----------|----------|----------------|
| Broken code mid-typing | FCS type-check fails | No test results, diagnostic event pushed. Previous results remain. |
| Scope detection hint wrong | Server falls back to full-file analysis | Slightly slower, still correct. |
| Type signature changed | Tests marked as Stale | Gutter shows stale markers. Save triggers full refresh. |
| Cross-file type change | Dependent files not reloaded | Stale until save triggers broader reload. |
| Two editors same file | Last-write-wins via generation counter | Most recent edit evaluated. |
| SageFs daemon not running | POST fails | Editor shows "SageFs not connected" (existing behavior). |
| FCS crashes | Pipeline catches exception | Error logged, session remains alive. |

**The 90/10 rule**: Body changes (90% of edits) get instant feedback. Signature changes (10%) degrade to save-triggered refresh. Both are still faster than VS Enterprise.

## Editor Implementation Guide

Each editor implements the same minimal contract:

```
1. Register text change listener for *.fs files
2. On change: debounce 300ms (cancel previous timer)
3. After debounce: POST full buffer text + editRegion hint + generation counter
4. Display results from existing SSE subscription (already implemented in all editors)
```

### Neovim
- `TextChanged` + `TextChangedI` autocmds (currently only fires for `*.fsx` — fix to include `*.fs`)
- Tree-sitter `value_declaration` walk for `editRegion` hint (error-tolerant, works on broken code)
- Existing SSE + gutter rendering already handles results

### VS Code
- `workspace.onDidChangeTextDocument` for F# files
- `vscode.executeDocumentSymbolProvider` for `editRegion` hint (Ionide provides this)
- Fallback: indentation-based scan if Ionide not available
- Existing `LiveTestingListener` + `TestDecorations` handle results

### Visual Studio
- `ITextViewChangedListener` from VS Extensibility SDK
- Indentation-based scan for `editRegion` hint (F# indentation-sensitivity makes this reliable)
- Existing `LiveTestingSubscriber` + CodeLens handle results

**The client code per editor is 5-15 lines of glue.** The intelligence lives in the server.

## Decision Log

| Decision | Alternatives Considered | Rationale |
|----------|------------------------|-----------|
| Full-file payload | Scope-level payload with line numbers | Eliminates drift, simplifies clients, server needs full file for FCS anyway |
| Server-side scope detection | Client-side per editor | Server owns truth, clients trivially simple, parity by construction |
| Signature change → stale until save | Cross-file reload in FSI | 90/10 rule — body changes are instant, signature changes are rare. Ship fast path first. |
| Three editor-specific change listeners | Shared module via Fable | Each is 5-15 lines, sharing adds build complexity for no gain |
| Generation counter for ordering | Timestamp-based | Monotonic counter avoids clock skew between editors |
| 300ms debounce default | Adaptive per-editor | Ship consistent, tune per-editor from user feedback later |
