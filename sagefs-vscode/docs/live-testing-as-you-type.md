# As-You-Type Live Testing — VS Code Extension

> See `docs/live-testing-as-you-type.md` in the repo root for the centralized architecture,
> endpoint contract, server pipeline, and decision log. This doc covers **VS Code-specific** implementation only.

## Current State

- Extension is F# compiled to JS via Fable
- Connects to SageFs daemon via HTTP + SSE (port 37749)
- Has `LiveTestingListener` for SSE test events, `TestControllerAdapter` for VS Code Test API
- **No `onDidChangeTextDocument` listener** — zero reaction to typing
- Live testing commands exist (enable/disable/run) but are manual-trigger only

## What Needs to Change

Per the centralized design, the editor's only job is:

1. Register `onDidChangeTextDocument` for `*.fs` files
2. On change: debounce 300ms, cancel previous timer
3. After debounce: extract the enclosing function scope, POST it to SageFs
4. Display results from existing SSE subscription (already done)

## Implementation

### Step 1: Register TextDocument Change Listener

In `Extension.fs`:

```fsharp
let mutable scopeTimer: Disposable option = None
let mutable generation = 0L

workspace.onDidChangeTextDocument.Invoke(fun ev ->
  if ev.document.languageId = "fsharp" && ev.document.uri.scheme = "file" then
    scopeTimer |> Option.iter (fun t -> t.dispose())
    scopeTimer <- Some(
      window.setTimeout(fun () ->
        generation <- generation + 1L
        extractAndPostScope ev.document generation
      , 300)
    )
) |> context.subscriptions.Add
```

### Step 2: Extract Scope and POST

Use Ionide's Document Symbol Provider to find the enclosing function, then POST
just that function's text. SageFs sends it to FSI, which redefines the binding.

```fsharp
let extractAndPostScope (doc: TextDocument) (gen: int64) = promise {
  let cursor = window.activeTextEditor |> Option.map (fun e -> e.selection.active)
  match cursor with
  | None -> ()
  | Some pos ->
    let! symbols = commands.executeCommand("vscode.executeDocumentSymbolProvider", doc.uri)
    let enclosing = findEnclosingSymbol symbols pos
    match enclosing with
    | None -> ()
    | Some sym ->
      let scopeText = doc.getText(sym.range)
      do! client.postEvaluateScope {|
        filePath = doc.uri.fsPath
        scopeName = sym.name
        scopeText = scopeText
        startLine = sym.range.start.line
        endLine = sym.range.``end``.line
        generation = gen
      |}
}
```

### Step 3: Add HTTP Client Method

In `SageFsClient.fs`:

```fsharp
member _.postEvaluateScope (request: EvaluateScopeRequest) =
  httpPost (sprintf "%s/api/live-testing/evaluate-scope" baseUrl) request
```

### Step 4: Fallback When Ionide Unavailable

If Document Symbol Provider returns nothing (Ionide not running or can't parse),
fall back to an indentation-based scan (~30 lines F#). If that also fails,
don't POST — user sees stale results until code stabilizes. No harm done.

## Files to Modify

1. `src/Extension.fs` — register `onDidChangeTextDocument`, debounce, scope extraction + POST
2. `src/SageFsClient.fs` — add `postEvaluateScope` HTTP method
3. `src/LiveTestingTypes.fs` — add `EvaluateScopeRequest` type (if not shared)

## VS Code-Specific Considerations

- **Document Symbol Provider** via Ionide gives compiler-accurate function ranges.
  Cached and fast. Best scope detection available in VS Code.
- **Broken code**: Document Symbol Provider may return stale/partial symbols from last
  successful parse. Scope boundaries may be slightly off — SageFs type-checks and
  rejects if the extracted code doesn't compile. Benign failure mode.
- **No new dependencies**: `onDidChangeTextDocument` is built-in VS Code API.

## Dependencies

- **Requires** `POST /api/live-testing/evaluate-scope` endpoint in SageFs daemon
- **Ionide** for Document Symbol Provider (indentation fallback without it)
- No new npm dependencies needed
