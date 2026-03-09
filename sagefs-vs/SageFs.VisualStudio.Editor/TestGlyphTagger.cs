using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace SageFs.VisualStudio.Editor;

// ── Glyph tag type ────────────────────────────────────────────────────────────

public sealed class TestStatusGlyphTag : IGlyphTag
{
    public TestStatus Status { get; }
    public TestStatusGlyphTag(TestStatus status) { Status = status; }
}

// ── Tagger: maps buffer lines → TestStatusGlyphTag ───────────────────────────

internal sealed class TestGlyphTagger : ITagger<TestStatusGlyphTag>, IDisposable
{
    private readonly ITextBuffer _buffer;
    private readonly TestStateTracker _tracker;

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public TestGlyphTagger(ITextBuffer buffer, TestStateTracker tracker)
    {
        _buffer = buffer;
        _tracker = tracker;
        _tracker.StateChanged += OnStateChanged;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        var snapshot = _buffer.CurrentSnapshot;
        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
            new SnapshotSpan(snapshot, 0, snapshot.Length)));
    }

    public IEnumerable<ITagSpan<TestStatusGlyphTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0) yield break;
        var snapshot = spans[0].Snapshot;
        var filePath = TryGetFilePath();
        if (filePath == null) yield break;

        foreach (var span in spans)
        {
            var startLine = snapshot.GetLineNumberFromPosition(span.Start);
            var endLine   = snapshot.GetLineNumberFromPosition(span.End);

            for (var lineNum = startLine; lineNum <= endLine; lineNum++)
            {
                // GetStatusForLine expects 1-based line numbers
                var status = _tracker.GetStatusForLine(filePath, lineNum + 1);
                if (status == TestStatus.Unknown) continue;

                var line = snapshot.GetLineFromLineNumber(lineNum);
                yield return new TagSpan<TestStatusGlyphTag>(
                    new SnapshotSpan(line.Start, 0),
                    new TestStatusGlyphTag(status));
            }
        }
    }

    private string? TryGetFilePath()
    {
        if (_buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
            return doc.FilePath;
        return null;
    }

    public void Dispose() => _tracker.StateChanged -= OnStateChanged;
}

// ── Tagger provider: MEF export ───────────────────────────────────────────────

[Export(typeof(ITaggerProvider))]
[ContentType("F#")]
[TagType(typeof(TestStatusGlyphTag))]
internal sealed class TestGlyphTaggerProvider : ITaggerProvider
{
    private readonly TestStateTracker _tracker;

    [ImportingConstructor]
    public TestGlyphTaggerProvider()
    {
        _tracker = new TestStateTracker();
        var url = PortConfig.TryGetDaemonUrl();
        if (url != null)
        {
            var sseClient = new SseClient();
            sseClient.EventReceived += (_, ev) => _tracker.ProcessEvent(ev);
            sseClient.Start(url);
        }
    }

    public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag
    {
        return new TestGlyphTagger(buffer, _tracker) as ITagger<T>;
    }
}

// ── Glyph factory provider: MEF export ───────────────────────────────────────

[Export(typeof(IGlyphFactoryProvider))]
[Name("SageFsTestGlyphs")]
[ContentType("F#")]
[TagType(typeof(TestStatusGlyphTag))]
[Order(After = "VsTextMarker")]
internal sealed class TestGlyphFactoryProvider : IGlyphFactoryProvider
{
    public IGlyphFactory GetGlyphFactory(IWpfTextView view, IWpfTextViewMargin margin) =>
        new TestGlyphFactory();
}

// ── Glyph factory: draws the WPF circle ──────────────────────────────────────

internal sealed class TestGlyphFactory : IGlyphFactory
{
    private static readonly Brush PassedBrush  = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))); // green
    private static readonly Brush FailedBrush  = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36))); // red
    private static readonly Brush RunningBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00))); // amber
    private static readonly Brush NotRunBrush  = Freeze(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E))); // gray

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public UIElement? GenerateGlyph(IWpfTextViewLine line, IGlyphTag tag)
    {
        if (tag is not TestStatusGlyphTag testTag) return null;

        var brush = testTag.Status switch
        {
            TestStatus.Passed  => PassedBrush,
            TestStatus.Failed  => FailedBrush,
            TestStatus.Running => RunningBrush,
            TestStatus.NotRun  => NotRunBrush,
            _                  => null
        };
        if (brush == null) return null;

        return new Ellipse
        {
            Width  = 10,
            Height = 10,
            Fill   = brush,
            Margin = new Thickness(1),
        };
    }
}
