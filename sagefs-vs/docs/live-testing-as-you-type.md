# As-You-Type Live Testing — Visual Studio Extension

> See `docs/live-testing-as-you-type.md` in the repo root for the centralized architecture,
> endpoint contract, server pipeline, and decision log. This doc covers **VS-specific** implementation only.

## Current State

- Extension uses new VS Extensibility SDK (`VisualStudio.Extensibility`)
- C# shim (`SageFs.VisualStudio`) + F# core logic (`SageFs.VisualStudio.Core`)
- Targets `net8.0-windows8.0` (Windows-only)
- Connects to SageFs daemon via HTTP + SSE (port 37749)
- Has `LiveTestingSubscriber` for SSE test events
- **No text change listeners** — no reaction to typing or even saving
- Live testing is observe-only (SSE events update state, CodeLens displays results)

## What Needs to Change

The client-side work is minimal. Per the centralized design, the editor's only job is:

1. Register text change listener for `*.fs` files
2. On change: debounce 300ms, cancel previous timer
3. After debounce: POST full buffer text + editRegion hint + generation counter
4. Display results from existing SSE subscription (already done)

## Implementation

### Step 1: Register Text Change Listener (C# shim)

```csharp
[VisualStudioContribution]
internal class FSharpTextChangeListener : ExtensionPart, ITextViewChangedListener
{
  private CancellationTokenSource? _debounceCts;
  private long _generation;

  public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
  {
    AppliesTo = new[] { DocumentFilter.FromDocumentType("F#") }
  };

  public async Task TextViewChangedAsync(
    TextViewChangedArgs args,
    CancellationToken ct)
  {
    _debounceCts?.Cancel();
    _debounceCts = new CancellationTokenSource();
    var token = _debounceCts.Token;

    try
    {
      await Task.Delay(300, token);
      if (!token.IsCancellationRequested)
      {
        var snapshot = args.AfterTextView.Document.AsTextDocumentSnapshot();
        var fullText = snapshot.Text;
        var cursorLine = args.AfterTextView.Selection.ActivePosition.Line;
        var gen = Interlocked.Increment(ref _generation);

        // editRegion hint: indentation-based scan (F# core)
        var editRegion = ScopeExtractor.findEditRegion(
          fullText.Split('\n'), cursorLine);

        await SageFsClient.PostEvaluateScopeAsync(new
        {
          filePath = snapshot.Uri.LocalPath,
          fullText = fullText,
          editRegion = editRegion,
          generation = gen
        }, token);
      }
    }
    catch (TaskCanceledException) { }
  }
}
```

### Step 2: Edit Region Hint (F# core — optional optimization)

The `editRegion` is a **hint only** — if wrong, the server falls back to full-file analysis.
Indentation-based scan for the enclosing `let` binding:

```fsharp
module ScopeExtractor =

  let findEditRegion (lines: string[]) (cursorLine: int) =
    // Scan backwards from cursorLine for line matching: ^\s*let\s+
    // where indentation <= cursorLine's indentation
    // Scan forwards for next binding at same/lower indentation
    // Return { startLine; endLine } or full-file range as fallback
    ...
```

This handles 90%+ of F# code. Computation expressions and nested modules may produce
slightly wrong hints — the server handles this gracefully (falls back to full-file scope detection).

### Step 3: Add HTTP Client Method (F# core)

In `SageFsClient.fs`:

```fsharp
member _.PostEvaluateScopeAsync(request: EvaluateScopeRequest, ct) =
  httpPostAsync (sprintf "%s/api/live-testing/evaluate-scope" baseUrl) request ct
```

## Files to Modify

### C# shim (`SageFs.VisualStudio/`)
1. Add `FSharpTextChangeListener.cs` — text change with debounce + POST
2. Register in `SageFsExtension.cs` DI container

### F# core (`SageFs.VisualStudio.Core/`)
1. Add `ScopeExtractor.fs` — indentation-based edit region hint (optional)
2. `SageFsClient.fs` — add `PostEvaluateScopeAsync` method
3. `LiveTestingTypes.fs` — add `EvaluateScopeRequest` type

## VS-Specific Considerations

- **Document filter**: F# files are content type `"F#"` in VS
- **Threading**: VS extensibility listeners run on background threads. HTTP POST is async.
  No UI thread concerns.
- **CodeLens refresh**: `LiveTestingSubscriber.StateChanged` already signals CodeLens refresh.
  No changes needed on the results path.
- **Error List**: `scope_check_failed` SSE events → `DiagnosticsSubscriber` surfaces as warnings
- **Caret position**: `args.AfterTextView.Selection.ActivePosition` gives cursor line for editRegion hint

## Dependencies

- **Requires** `POST /api/live-testing/evaluate-scope` endpoint in SageFs daemon
- No new NuGet dependencies
- VS Extensibility SDK already provides `ITextViewChangedListener`
