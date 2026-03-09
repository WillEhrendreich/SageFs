using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SageFs.VisualStudio.Editor;

// ── Adornment layer registration ─────────────────────────────────────────────

internal static class SageFsAdornmentLayer
{
    [Export(typeof(AdornmentLayerDefinition))]
    [Name("SageFsInlineFailures")]
    [Order(After = PredefinedAdornmentLayers.Caret)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
#pragma warning disable CS0649
    public static AdornmentLayerDefinition? Definition;
#pragma warning restore CS0649
}

// ── Adornment view creation listener (MEF entry point) ───────────────────────

/// <summary>
/// Creates one <see cref="InlineFailureAdornmentManager"/> per F# document view.
/// The manager subscribes to <see cref="FileAnnotationTracker"/> and renders
/// inline failure messages at the right edge of failing lines.
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("F#")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class InlineFailureAdornmentListener : IWpfTextViewCreationListener
{
    [Import]
    internal ITextDocumentFactoryService? TextDocumentFactory { get; set; }

    private readonly FileAnnotationTracker _tracker;

    [ImportingConstructor]
    public InlineFailureAdornmentListener()
    {
        _tracker = SharedAnnotationTracker.Instance;
    }

    public void TextViewCreated(IWpfTextView textView)
    {
        if (GlyphSpikeGuard.IsDisabled) return;

        // Attach manager: it manages its own lifetime via Closed event
        _ = new InlineFailureAdornmentManager(textView, _tracker, TextDocumentFactory);
    }
}

// ── Per-view adornment manager ────────────────────────────────────────────────

/// <summary>
/// Manages inline failure adornments for a single <see cref="IWpfTextView"/>.
///
/// Each failing test line gets a right-aligned <see cref="TextBlock"/> showing:
///   ⊘ myTest — Expected: 1  Actual: 2
///
/// Adornments are cleared when the view is closed or annotations are updated.
/// </summary>
internal sealed class InlineFailureAdornmentManager : IDisposable
{
    private readonly IWpfTextView _view;
    private readonly IAdornmentLayer _layer;
    private readonly FileAnnotationTracker _tracker;
    private readonly ITextDocumentFactoryService? _docFactory;

    // Brush for the inline failure text
    private static readonly Brush FailureBrush = CreateBrush(Color.FromArgb(0xCC, 0xF4, 0x43, 0x36)); // translucent red

    private static Brush CreateBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public InlineFailureAdornmentManager(
        IWpfTextView view,
        FileAnnotationTracker tracker,
        ITextDocumentFactoryService? docFactory)
    {
        _view      = view;
        _tracker   = tracker;
        _docFactory = docFactory;

        _layer = view.GetAdornmentLayer("SageFsInlineFailures");

        _tracker.FileAnnotationsUpdated += OnAnnotationsUpdated;
        _view.LayoutChanged             += OnLayoutChanged;
        _view.Closed                    += OnViewClosed;
    }

    private void OnAnnotationsUpdated(object? sender, string updatedFilePath)
    {
        var myPath = TryGetFilePath();
        if (myPath == null) return;
        if (!string.Equals(myPath, updatedFilePath, StringComparison.OrdinalIgnoreCase)) return;

        _ = Application.Current?.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            (Action)RenderAdornments);
    }

    private void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e) =>
        RenderAdornments();

    private void RenderAdornments()
    {
        _layer.RemoveAllAdornments();

        var filePath = TryGetFilePath();
        if (filePath == null) return;
        if (!_tracker.HasAnyForFile(filePath)) return;

        var snapshot = _view.TextSnapshot;

        foreach (var line in _view.TextViewLines)
        {
            var lineNum = snapshot.GetLineNumberFromPosition(line.Start) + 1; // 1-based
            var failures = _tracker.GetFailuresForLine(filePath, lineNum);
            if (failures.Count == 0) continue;

            // Build inline text: "⊘ test1 — Expected: X  Actual: Y  |  ⊘ test2 — ..."
            var parts = new List<string>(failures.Count);
            foreach (var f in failures)
                parts.Add(f.ToInlineText());
            var displayText = string.Join("  |  ", parts);

            var block = new TextBlock
            {
                Text             = displayText,
                Foreground       = FailureBrush,
                FontFamily       = new FontFamily("Consolas, Courier New"),
                FontSize         = 11,
                FontStyle        = FontStyles.Italic,
                Padding          = new Thickness(8, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity          = 0.85,
            };

            // Position at right of line, vertically centered
            var top = line.Top + (line.Height - 14) / 2;
            Canvas.SetLeft(block, line.Right);
            Canvas.SetTop(block,  top);

            _layer.AddAdornment(
                AdornmentPositioningBehavior.TextRelative,
                new SnapshotSpan(line.Start, 0),
                tag:      null,
                adornment: block,
                removedCallback: null);
        }
    }

    private string? TryGetFilePath()
    {
        if (_docFactory != null &&
            _docFactory.TryGetTextDocument(_view.TextBuffer, out var doc))
            return doc.FilePath;

        if (_view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument d))
            return d.FilePath;

        return null;
    }

    private void OnViewClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        _tracker.FileAnnotationsUpdated -= OnAnnotationsUpdated;
        _view.LayoutChanged             -= OnLayoutChanged;
        _view.Closed                    -= OnViewClosed;
    }
}

// ── Shared tracker singleton (connects SSE subscription to all views) ────────

/// <summary>
/// Holds the single <see cref="FileAnnotationTracker"/> instance shared across all views.
/// The SSE subscription is wired here so one daemon connection feeds all open editors.
/// </summary>
internal static class SharedAnnotationTracker
{
    private static readonly Lazy<FileAnnotationTracker> _instance =
        new Lazy<FileAnnotationTracker>(CreateTracker);

    public static FileAnnotationTracker Instance => _instance.Value;

    private static FileAnnotationTracker CreateTracker()
    {
        var tracker = new FileAnnotationTracker();

        if (GlyphSpikeGuard.IsDisabled) return tracker;

        var url = PortConfig.TryGetDaemonUrl();
        if (url != null)
        {
            var sseClient = new SseClient();
            // file_annotations come through the main /events SSE stream
            sseClient.EventReceived += (_, ev) => tracker.ProcessEvent(ev);
            sseClient.Start(url, "/events");
        }

        return tracker;
    }
}
