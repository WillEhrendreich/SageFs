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
3. After debounce: POST full buffer text + editRegion hint + generation counter
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
        postFullBuffer ev.document generation
      , 300)
    )
) |> context.subscriptions.Add
```

### Step 2: POST Full Buffer

```fsharp
let postFullBuffer (doc: TextDocument) (gen: int64) = promise {
  let cursor = window.activeTextEditor |> Option.map (fun e -> e.selection.active)
  let editRegion =
    match cursor with
    | Some pos ->
      // Use Document Symbol Provider for editRegion hint (Ionide provides this)
      let! symbols = commands.executeCommand("vscode.executeDocumentSymbolProvider", doc.uri)
      findEnclosingSymbolRange symbols pos
    | None -> { startLine = 0; endLine = doc.lineCount - 1 }

  do! client.postEvaluateScope {|
    filePath = doc.uri.fsPath
    fullText = doc.getText()
    editRegion = editRegion
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

If Document Symbol Provider returns nothing (Ionide not running or can't parse), send
the full file range as the editRegion hint. The server handles this gracefully — it just
does full-file scope detection instead of targeted analysis.

## Files to Modify

1. `src/Extension.fs` — register `onDidChangeTextDocument`, debounce, POST full buffer
2. `src/SageFsClient.fs` — add `postEvaluateScope` HTTP method
3. `src/LiveTestingTypes.fs` — add `EvaluateScopeRequest` type (if not shared)

## VS Code-Specific Considerations

- **Document Symbol Provider** via Ionide gives compiler-accurate function ranges for
  the `editRegion` hint. Cached and fast. This is a better hint than indentation scanning.
- **Broken code**: Document Symbol Provider may return stale/partial symbols. This is fine —
  `editRegion` is only a hint. Worst case, server does full-file analysis.
- **No new dependencies**: `onDidChangeTextDocument` is built-in VS Code API.

## Dependencies

- **Requires** `POST /api/live-testing/evaluate-scope` endpoint in SageFs daemon
- **Ionide** for Document Symbol Provider (graceful degradation without it)
- No new npm dependencies needed
