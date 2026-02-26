# As-You-Type Live Testing for VS Code Extension

## Why This Matters

As-you-type live testing is table stakes — VS Enterprise already does it. The differentiator:
- **Faster**: Scope-level reloading via tree-sitter extraction, not whole-solution Roslyn rebuild
- **Lighter**: No VS Enterprise license required. Works in any editor.
- **Cross-editor**: Neovim, VS Code, VS — identical experience everywhere
- **F#-native**: Built for F# from the ground up, not a C#-first afterthought

## Current State

- Extension is F# compiled to JS via Fable
- Connects to SageFs daemon via HTTP + SSE (port 37749)
- Has `LiveTestingListener` for SSE test events, `TestControllerAdapter` for VS Code Test API
- **No `onDidChangeTextDocument` listener** — zero reaction to typing
- **No tree-sitter or syntax analysis** — relies entirely on SageFs daemon
- Live testing commands exist (enable/disable/run) but are manual-trigger only

## Design Principles (same as Neovim — NON-NEGOTIABLE)

- **NO auto-save.** File on disk is NEVER touched.
- **Scope extraction via Document Symbol Provider.** VS Code + Ionide already provides document
  symbols (function boundaries) via LSP. No need for tree-sitter or custom parsing.
  `vscode.executeDocumentSymbolProvider` returns cached, compiler-accurate function ranges.
- **Pre-computed affected test mappings.** Same SageFs endpoint, same mappings.
- **Send just the scope.** Extract the changed function text from the document, POST to SageFs.
- **Results via existing SSE.** `LiveTestingListener` already handles test result events.

## Architecture

```
onDidChangeTextDocument (*.fs files)
  → debounce 300-500ms (configurable)
  → vscode.executeDocumentSymbolProvider → find enclosing function at cursor
  → extract { filePath, scopeName, scopeText, startLine, endLine }
  → POST /api/live-testing/evaluate-scope (same endpoint as Neovim)
  → SageFs patches, type-checks, #load, runs tests
  → SSE pushes results → LiveTestingListener handles as normal
  → TestDecorations + TestCodeLens update automatically
```

## Implementation Plan

### Step 1: Register TextDocument Change Listener

In `Extension.fs`, register `onDidChangeTextDocument` for F# files:

```fsharp
// Debounced — cancel previous timer on each change
let mutable scopeTimer: Disposable option = None

workspace.onDidChangeTextDocument.Invoke(fun ev ->
  if ev.document.languageId = "fsharp" && ev.document.uri.scheme = "file" then
    scopeTimer |> Option.iter (fun t -> t.dispose())
    scopeTimer <- Some(
      window.setTimeout(fun () -> extractAndPostScope ev.document, 400)
    )
) |> context.subscriptions.Add
```

### Step 2: Scope Extraction via Document Symbol Provider

Query Ionide's document symbols to find the enclosing function:

```fsharp
let extractAndPostScope (doc: TextDocument) = promise {
  let pos = window.activeTextEditor |> Option.map (fun e -> e.selection.active)
  match pos with
  | None -> ()
  | Some cursor ->
    let! symbols = commands.executeCommand("vscode.executeDocumentSymbolProvider", doc.uri)
    // Walk symbols to find the innermost function/method containing cursor
    let enclosing = findEnclosingSymbol symbols cursor
    match enclosing with
    | None -> ()
    | Some sym ->
      let scopeText = doc.getText(sym.range)
      let payload = {|
        filePath = doc.uri.fsPath
        scopeName = sym.name
        scopeText = scopeText
        startLine = sym.range.start.line
        endLine = sym.range.``end``.line
      |}
      do! client.postEvaluateScope payload
}
```

### Step 3: Add HTTP Client Method

In `SageFsClient.fs`, add:

```fsharp
member _.postEvaluateScope (scope: EvaluateScopeRequest) =
  httpPost (sprintf "%s/api/live-testing/evaluate-scope" baseUrl) scope
```

### Step 4: Handle Broken Code Gracefully

Document Symbol Provider may return stale/partial symbols when code is broken mid-typing.
This is fine — worst case, the scope boundaries are slightly off, SageFs type-checks and
rejects. Same error-tolerant flow as Neovim's tree-sitter approach.

If Document Symbol Provider returns nothing (Ionide can't parse at all), skip the POST.
Don't flood SageFs with unparseable content.

## Fallback: Simple Indentation-Based Scope Finder

If Document Symbol Provider is too slow or unavailable (Ionide not running), implement a
30-line F# fallback that scans backwards from cursor for `let` at lower/equal indentation:

```fsharp
let findScopeByIndentation (lines: string[]) (cursorLine: int) =
  // Scan backwards for `let` keyword at same or lower indentation
  // Scan forwards for next `let`/`module`/`type` at same indentation
  // Return the range
```

This handles 95% of F# function structures. Tree-sitter-level accuracy is nice-to-have,
not a blocker for shipping.

## Files to Modify

1. `src/Extension.fs` — register `onDidChangeTextDocument`, debounce, scope extraction
2. `src/SageFsClient.fs` — add `postEvaluateScope` HTTP method
3. `src/LiveTestingTypes.fs` — add `EvaluateScopeRequest` type (if not shared)

## Dependencies

- **Requires Phase 2 of SageFs plan** — the `POST /api/live-testing/evaluate-scope` endpoint
- **Requires Ionide** for Document Symbol Provider (graceful degradation without it)
- No new npm/NuGet dependencies needed
