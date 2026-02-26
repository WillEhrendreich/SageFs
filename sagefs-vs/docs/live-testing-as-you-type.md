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
3. After debounce: extract the enclosing function scope, POST it to SageFs
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
        var lines = snapshot.Lines.Select(l => l.Text).ToArray();
        var cursorLine = args.AfterTextView.Selection.ActivePosition.Line;
        var gen = Interlocked.Increment(ref _generation);

        var scope = ScopeExtractor.findEnclosingScope(lines, cursorLine);
        if (scope != null)
        {
          await SageFsClient.PostEvaluateScopeAsync(new
          {
            filePath = snapshot.Uri.LocalPath,
            scopeName = scope.Name,
            scopeText = scope.Text,
            startLine = scope.StartLine,
            endLine = scope.EndLine,
            generation = gen
          }, token);
        }
      }
    }
    catch (TaskCanceledException) { }
  }
}
```

### Step 2: Scope Extraction — Indentation-Based (F# core)

F# is indentation-sensitive. Scan backwards from cursor for `let` at same/lower indent,
forward for next binding at same level. This extracts the function definition that the
server will send to FSI to redefine the binding.

```fsharp
module ScopeExtractor =

  type Scope =
    { Name: string
      Text: string
      StartLine: int
      EndLine: int }

  let findEnclosingScope (lines: string[]) (cursorLine: int) : Scope option =
    // Scan backwards from cursorLine for line matching: ^\s*let\s+(\w+)
    // where indentation <= cursorLine's indentation
    // Scan forwards for next binding at same/lower indentation
    // Return scope with text = lines[start..end] joined
    ...
```

### Step 3: Add HTTP Client Method (F# core)

In `SageFsClient.fs`:

```fsharp
member _.PostEvaluateScopeAsync(request: EvaluateScopeRequest, ct) =
  httpPostAsync (sprintf "%s/api/live-testing/evaluate-scope" baseUrl) request ct
```

## Files to Modify

### C# shim (`SageFs.VisualStudio/`)
1. Add `FSharpTextChangeListener.cs` — text change with debounce + scope extraction + POST
2. Register in `SageFsExtension.cs` DI container

### F# core (`SageFs.VisualStudio.Core/`)
1. Add `ScopeExtractor.fs` — indentation-based scope finder
2. `SageFsClient.fs` — add `PostEvaluateScopeAsync` method
3. `LiveTestingTypes.fs` — add `EvaluateScopeRequest` type

## VS-Specific Considerations

- **Document filter**: F# files are content type `"F#"` in VS
- **Threading**: VS extensibility listeners run on background threads. HTTP POST is async.
  No UI thread concerns.
- **CodeLens refresh**: `LiveTestingSubscriber.StateChanged` already signals CodeLens refresh.
  No changes needed on the results path.
- **Error List**: `scope_check_failed` SSE events → `DiagnosticsSubscriber` surfaces as warnings
- **Caret position**: `args.AfterTextView.Selection.ActivePosition` gives cursor line for scope extraction

## Dependencies

- **Requires** `POST /api/live-testing/evaluate-scope` endpoint in SageFs daemon
- No new NuGet dependencies
- VS Extensibility SDK already provides `ITextViewChangedListener`
