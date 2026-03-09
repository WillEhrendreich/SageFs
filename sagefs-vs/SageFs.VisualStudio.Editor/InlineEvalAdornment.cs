using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace SageFs.VisualStudio.Editor;

// ── Adornment layer definition ────────────────────────────────────────────────

internal static class SageFsEvalResultLayer
{
    [Export(typeof(AdornmentLayerDefinition))]
    [Name("SageFsEvalResults")]
    [Order(After = "SageFsInlineFailures")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
#pragma warning disable CS0649
    public static AdornmentLayerDefinition? Definition;
#pragma warning restore CS0649
}

// ── View creation listener ────────────────────────────────────────────────────

[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("F#")]
[ContentType("F# Script")]
[TextViewRole(PredefinedTextViewRoles.Document)]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class InlineEvalAdornmentListener : IWpfTextViewCreationListener
{
    [Import]
    internal ITextDocumentFactoryService? TextDocumentFactory { get; set; }

    private readonly InlineEvalResultTracker _tracker;

    [ImportingConstructor]
    public InlineEvalAdornmentListener()
    {
        _tracker = SharedEvalResultTracker.Instance;
    }

    public void TextViewCreated(IWpfTextView textView)
    {
        _ = new InlineEvalAdornmentManager(textView, _tracker, TextDocumentFactory);
    }
}

// ── Per-view adornment manager ────────────────────────────────────────────────

internal sealed class InlineEvalAdornmentManager : IDisposable
{
    private readonly IWpfTextView _view;
    private readonly IAdornmentLayer _layer;
    private readonly InlineEvalResultTracker _tracker;
    private readonly ITextDocumentFactoryService? _docFactory;

    // Active: 75% opacity gray. Stale: 35% opacity gray.
    private static readonly Brush ActiveBrush = CreateBrush(Colors.Gray, 0.75);
    private static readonly Brush StaleBrush  = CreateBrush(Colors.Gray, 0.35);

    private static Brush CreateBrush(Color c, double opacity)
    {
        var b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    public InlineEvalAdornmentManager(
        IWpfTextView view,
        InlineEvalResultTracker tracker,
        ITextDocumentFactoryService? docFactory)
    {
        _view       = view;
        _tracker    = tracker;
        _docFactory = docFactory;

        _layer = view.GetAdornmentLayer("SageFsEvalResults");

        _tracker.StateChanged          += OnTrackerStateChanged;
        _view.LayoutChanged            += OnLayoutChanged;
        _view.TextBuffer.Changed       += OnBufferChanged;
        _view.Closed                   += OnViewClosed;
    }

    private void OnTrackerStateChanged(string filePath, int line)
    {
        var myPath = TryGetFilePath();
        if (myPath == null) return;
        if (!string.Equals(myPath, filePath, StringComparison.OrdinalIgnoreCase)) return;

        _ = Application.Current?.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            (Action)RenderAdornments);
    }

    private void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e)
    {
        if (e.NewOrReformattedLines.Count == 0 && e.TranslatedLines.Count == 0) return;
        RenderAdornments();
    }

    private void OnBufferChanged(object? sender, TextContentChangedEventArgs e)
    {
        // Any edit marks all results for this file as Stale (Sprint 7).
        // Line-aware staleness (mark only the edited block) is Sprint 8.
        var filePath = TryGetFilePath();
        if (filePath == null) return;

        foreach (var (line, state) in _tracker.GetAllForFile(filePath))
        {
            if (state is AdornmentState.ActiveState)
                _tracker.MarkStale(filePath, line);
        }
    }

    private void RenderAdornments()
    {
        _layer.RemoveAllAdornments();

        var filePath = TryGetFilePath();
        if (filePath == null) return;
        if (!_tracker.HasAnyForFile(filePath)) return;

        var snapshot   = _view.TextSnapshot;
        var fontFamily = TryGetEditorFontFamily() ?? new FontFamily("Consolas, Courier New");
        var fontSize   = TryGetEditorFontSize() * 0.9;

        foreach (var line in _view.TextViewLines)
        {
            var lineNum = snapshot.GetLineNumberFromPosition(line.Start) + 1; // 1-based
            var state   = _tracker.Get(filePath, lineNum);

            string? displayText;
            Brush? brush;
            switch (state)
            {
                case AdornmentState.ActiveState a:
                    displayText = $"// => {a.Result}";
                    brush = ActiveBrush;
                    break;
                case AdornmentState.StaleState s:
                    displayText = $"// => {s.Result}";
                    brush = StaleBrush;
                    break;
                default:
                    continue;
            }

            var block = new TextBlock
            {
                Text              = displayText,
                Foreground        = brush,
                FontFamily        = fontFamily,
                FontSize          = fontSize,
                FontStyle         = FontStyles.Italic,
                Padding           = new Thickness(12, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip           = displayText,
            };

            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var top = line.Top + (line.Height - block.DesiredSize.Height) / 2.0;

            Canvas.SetLeft(block, line.Right);
            Canvas.SetTop(block, top);

            _layer.AddAdornment(
                AdornmentPositioningBehavior.TextRelative,
                new SnapshotSpan(line.Start, 0),
                tag:             null,
                adornment:       block,
                removedCallback: null);
        }
    }

    private FontFamily? TryGetEditorFontFamily()
    {
        try { return _view.FormattedLineSource?.DefaultTextProperties.Typeface.FontFamily; }
        catch { return null; }
    }

    private double TryGetEditorFontSize()
    {
        try
        {
            var size = _view.FormattedLineSource?.DefaultTextProperties.FontRenderingEmSize;
            if (size > 4) return size.Value;
        }
        catch { }
        return 12.0;
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
        _tracker.StateChanged    -= OnTrackerStateChanged;
        _view.LayoutChanged      -= OnLayoutChanged;
        _view.TextBuffer.Changed -= OnBufferChanged;
        _view.Closed             -= OnViewClosed;
    }
}
