# As-You-Type Live Testing for Visual Studio Extension

## 🔴 PRIORITY 1 (same feature as sagefs.nvim + VS Code, different editor)

## Current State

- Extension uses new VS Extensibility SDK (`VisualStudio.Extensibility`)
- C# shim (`SageFs.VisualStudio`) + F# core logic (`SageFs.VisualStudio.Core`)
- Targets `net8.0-windows8.0` (Windows-only)
- Connects to SageFs daemon via HTTP + SSE (port 37749)
- Has `LiveTestingSubscriber` for SSE test events
- **No text change listeners** — no reaction to typing or even saving
- **No Roslyn integration** — no syntax analysis at all
- Live testing is observe-only (SSE events update state, CodeLens displays results)

## Design Principles (same as Neovim/VS Code — NON-NEGOTIABLE)

- **NO auto-save.** File on disk is NEVER touched.
- **Scope extraction via indentation scan → LSP (upgradeable).** Ship the indentation-based
  approach first (fast, 95% accurate for F#). Upgrade to LSP document symbols later for the
  remaining edge cases. Full as-you-type from day one — no save-triggered half-measures.
- **Pre-computed affected test mappings.** Same SageFs endpoint, same mappings.
- **Send just the scope.** Extract the changed function, POST to SageFs.
- **Results via existing SSE.** `LiveTestingSubscriber` already handles events.

## Architecture

```
ITextViewChangedListener.TextViewChanged (*.fs files)
  → debounce 300-500ms
  → find enclosing function scope (indentation scan → later LSP)
  → extract { filePath, scopeName, scopeText, startLine, endLine }
  → POST /api/live-testing/evaluate-scope (same endpoint as all editors)
  → SageFs patches, type-checks, #load, runs tests
  → SSE pushes results → LiveTestingSubscriber handles as normal
  → CodeLens + Error List update automatically
```

## No Phased Rollout — Ship Full As-You-Type From Day One

Every editor gets the same experience: type → see test results. As-you-type live testing is
table stakes — VS Enterprise already does this. The differentiator is doing it BETTER:
faster feedback (tree-sitter/indentation scope extraction is instant vs Roslyn's heavier
analysis), lighter weight (no VS Enterprise license), cross-editor (Neovim, VS Code, VS —
same experience everywhere), and F#-native (not an afterthought bolted onto C# tooling).

## Implementation Plan

### Step 1: Register Text Change Listener (C# shim)

The VS Extensibility SDK provides `ITextViewChangedListener`:

```csharp
[VisualStudioContribution]
internal class FSharpTextChangeListener : ExtensionPart, ITextViewChangedListener
{
    private CancellationTokenSource? _debounceCts;
    private readonly ISageFsScopeEvaluator _evaluator;

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
            await Task.Delay(400, token);
            if (!token.IsCancellationRequested)
            {
                var snapshot = args.AfterTextView.Document.AsTextDocumentSnapshot();
                await _evaluator.EvaluateScopeAtCursorAsync(snapshot, token);
            }
        }
        catch (TaskCanceledException) { /* debounce cancelled — expected */ }
    }
}
```

### Step 2: Scope Extraction — Indentation-Based (F# core)

F# is indentation-sensitive. Scan backwards from cursor for `let` at same/lower indent,
forward for next binding at same level:

```fsharp
module ScopeExtractor =

  type Scope =
    { Name: string
      Text: string
      StartLine: int
      EndLine: int }

  let findEnclosingScope (lines: string[]) (cursorLine: int) : Scope option =
    // 1. Scan backwards from cursorLine for line matching: ^\s*let\s+(\w+)
    //    where indentation <= cursorLine's indentation
    // 2. That's the start of the scope, capture the name
    // 3. Scan forwards for next line at same/lower indentation that starts
    //    a new binding (let/type/module/open) or is end-of-file
    // 4. Return scope with text = lines[start..end] joined
    ...
```

### Step 3: Add HTTP Client Method (F# core)

In `SageFsClient.fs`:

```fsharp
member _.PostEvaluateScopeAsync(scope: EvaluateScopeRequest, ct: CancellationToken) =
  httpPostAsync (sprintf "%s/api/live-testing/evaluate-scope" baseUrl) scope ct
```

### Step 4: Wire C# → F# Core

```csharp
public class SageFsScopeEvaluator : ISageFsScopeEvaluator
{
    private readonly SageFsClient _client;

    public async Task EvaluateScopeAtCursorAsync(
        ITextDocumentSnapshot snapshot,
        CancellationToken ct)
    {
        var lines = snapshot.Lines.Select(l => l.Text).ToArray();
        var cursorLine = /* get active caret line from ITextViewSnapshot */;
        var scope = ScopeExtractor.findEnclosingScope(lines, cursorLine);

        if (scope != null)
        {
            await _client.PostEvaluateScopeAsync(new EvaluateScopeRequest
            {
                FilePath = snapshot.Uri.LocalPath,
                ScopeName = scope.Name,
                ScopeText = scope.Text,
                StartLine = scope.StartLine,
                EndLine = scope.EndLine
            }, ct);
        }
    }
}
```

## Files to Modify

### C# shim (`SageFs.VisualStudio/`)
1. Add `FSharpTextChangeListener.cs` — text change with debounce
2. Add `ISageFsScopeEvaluator.cs` — interface
3. Add `SageFsScopeEvaluator.cs` — implementation wiring to F# core
4. Register in `SageFsExtension.cs` DI container

### F# core (`SageFs.VisualStudio.Core/`)
1. Add `ScopeExtractor.fs` — indentation-based scope finder (shared logic)
2. `SageFsClient.fs` — add `PostEvaluateScopeAsync` method
3. `LiveTestingTypes.fs` — add `EvaluateScopeRequest` type

## Dependencies

- **Requires Phase 2 of SageFs plan** — the `POST /api/live-testing/evaluate-scope` endpoint
- No new NuGet dependencies for Phase A/B (indentation scan)
- Phase C may need `Microsoft.VisualStudio.LanguageServer.Protocol` for LSP queries
- VS Extensibility SDK already provides `ITextViewChangedListener`

## VS-Specific Considerations

- **Document filter**: F# files are content type `"F#"` in VS.
- **Threading**: VS extensibility listeners run on background threads. HTTP POST is async.
  No UI thread concerns for scope extraction + POST.
- **CodeLens refresh**: `LiveTestingSubscriber.StateChanged` already signals CodeLens refresh.
  No changes needed on the results path.
- **Error List**: If SageFs emits `scope_check_failed` SSE events, `DiagnosticsSubscriber`
  should surface them as warnings in the Error List.
- **Cursor position**: Getting active caret line from `ITextViewChangedListener` requires
  the `ITextViewSnapshot`. Check `args.AfterTextView.Selection` for caret position.

## Expert Panel Notes

- **Carmack**: Ship the simplest scope extraction first. Iterate on accuracy later.
  The indentation heuristic covers 95% of F# — ship that, measure real failures, then improve.
- **Muratori**: Indentation-based scope finder is the right first step. Don't pull in Roslyn
  or a language server just to find a `let` binding. 30 lines of code.
- **Holden**: The indentation scan WILL break on multiline computation expressions and nested
  modules. Ship it anyway, but log when the heuristic fails so you know when Phase C matters.
- **Miller**: If it didn't cost much to write, it doesn't cost much to throw away. Ship the
  simple version, replace it when real users hit the edge cases.
- **Stannard**: The `ScopeExtractor` module should live in F# core and be shared between
  VS Code (Fable) and VS (native F#). Same algorithm, two compilation targets.
- **The whole point**: As-you-type is table stakes (VS Enterprise already does it). The
  differentiator is: faster (scope-level, not whole-solution rebuild), lighter (no Enterprise
  license), cross-editor (Neovim + VS Code + VS, same experience), and F#-native.
