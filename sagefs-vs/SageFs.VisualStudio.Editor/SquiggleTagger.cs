using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace SageFs.VisualStudio.Editor;

// ── Error tag: carries a tooltip message ─────────────────────────────────────

internal sealed class SageFsErrorTag : IErrorTag
{
    /// <summary>Error, warning, or message squiggle color.</summary>
    public string ErrorType { get; }

    /// <summary>Tooltip shown on hover.</summary>
    public object? ToolTipContent { get; }

    public SageFsErrorTag(DiagnosticSeverity severity, string message)
    {
        ErrorType = severity switch
        {
            DiagnosticSeverity.Error   => PredefinedErrorTypeNames.SyntaxError,
            DiagnosticSeverity.Warning => PredefinedErrorTypeNames.Warning,
            DiagnosticSeverity.Info    => PredefinedErrorTypeNames.HintedSuggestion,
            _                          => PredefinedErrorTypeNames.HintedSuggestion,
        };
        ToolTipContent = message;
    }
}

// ── Tagger: maps buffer lines → SageFsErrorTag ───────────────────────────────

/// <summary>
/// Produces squiggles under F# lines that have daemon-reported diagnostics.
///
/// The daemon streams compilation diagnostics via GET /diagnostics (SSE).
/// Line numbers are 1-based in the daemon; ITextSnapshot uses 0-based line indices.
/// </summary>
internal sealed class SquiggleTagger : ITagger<SageFsErrorTag>, IDisposable
{
    private readonly ITextBuffer _buffer;
    private readonly DiagnosticStateTracker _tracker;

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public SquiggleTagger(ITextBuffer buffer, DiagnosticStateTracker tracker)
    {
        _buffer = buffer;
        _tracker = tracker;
        _tracker.StateChanged += OnStateChanged;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        var snapshot = _buffer.CurrentSnapshot;
        var span = new SnapshotSpan(snapshot, 0, snapshot.Length);
        _ = Application.Current?.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            (Action)(() => TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(span))));
    }

    public IEnumerable<ITagSpan<SageFsErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0) yield break;

        var snapshot = spans[0].Snapshot;
        var all = _tracker.GetAll();
        if (all.Count == 0) yield break;

        foreach (var span in spans)
        {
            var spanStartLine = snapshot.GetLineNumberFromPosition(span.Start) + 1; // 1-based
            var spanEndLine   = snapshot.GetLineNumberFromPosition(span.End)   + 1; // 1-based

            foreach (var diag in all)
            {
                if (diag.StartLine < spanStartLine || diag.StartLine > spanEndLine) continue;

                var snapshotLine = snapshot.GetLineFromLineNumber(diag.StartLine - 1); // back to 0-based

                // Build a span from startColumn to endColumn (or whole line if columns are missing)
                int startOff = Math.Max(0, Math.Min(diag.StartColumn, snapshotLine.Length));
                int endOff   = diag.EndLine == diag.StartLine
                    ? Math.Max(startOff, Math.Min(diag.EndColumn, snapshotLine.Length))
                    : snapshotLine.Length;

                // Ensure at least 1 character so the squiggle is visible
                if (endOff == startOff && snapshotLine.Length > 0)
                    endOff = Math.Min(startOff + 1, snapshotLine.Length);

                var tagSpan = new SnapshotSpan(snapshotLine.Start + startOff, endOff - startOff);
                yield return new TagSpan<SageFsErrorTag>(tagSpan, new SageFsErrorTag(diag.Severity, diag.Message));
            }
        }
    }

    public void Dispose() => _tracker.StateChanged -= OnStateChanged;
}

// ── Tagger provider: MEF export ───────────────────────────────────────────────

/// <summary>
/// MEF provider for SageFs squiggles. Subscribes to GET /diagnostics SSE stream.
///
/// Kill switch: create %LOCALAPPDATA%\SageFs\disable-glyphs.flag to disable entirely.
/// </summary>
[Export(typeof(ITaggerProvider))]
[ContentType("F#")]
[ContentType("F# Script")] // .fsx files — VS does NOT walk the base-type chain for MEF tagger exports
[TagType(typeof(SageFsErrorTag))]
internal sealed class SquiggleTaggerProvider : ITaggerProvider
{
    private readonly DiagnosticStateTracker _tracker;

    [ImportingConstructor]
    public SquiggleTaggerProvider()
    {
        _tracker = new DiagnosticStateTracker();

        if (!SageFsFeatureFlags.SquigglesEnabled) return;

        var url = PortConfig.TryGetDaemonUrl();
        if (url != null)
        {
            SseConnectionHub.Initialize(url);
            // /diagnostics is its own SSE endpoint, separate from /events
            SseConnectionHub.Subscribe("/diagnostics", ev => _tracker.ProcessEvent(ev));
        }
    }

    public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag =>
        new SquiggleTagger(buffer, _tracker) as ITagger<T>;
}
