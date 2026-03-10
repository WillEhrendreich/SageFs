using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace SageFs.VisualStudio.Editor;

// ── Coverage health enum ─────────────────────────────────────────────────────

public enum CoverageHealth { AllPassing, SomeFailing, NoCoverage }

// ── Glyph tag type ───────────────────────────────────────────────────────────

public sealed class CoverageGlyphTag : IGlyphTag
{
    public CoverageHealth Health { get; }
    public CoverageGlyphTag(CoverageHealth health) { Health = health; }
}

// ── Tagger: maps buffer lines → CoverageGlyphTag ────────────────────────────

internal sealed class CoverageGlyphTagger : ITagger<CoverageGlyphTag>, IDisposable
{
    private readonly ITextBuffer _buffer;
    private readonly FileAnnotationTracker _tracker;

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public CoverageGlyphTagger(ITextBuffer buffer, FileAnnotationTracker tracker)
    {
        _buffer = buffer;
        _tracker = tracker;
        _tracker.CoverageUpdated += OnCoverageUpdated;
    }

    private void OnCoverageUpdated(object? sender, string updatedFilePath)
    {
        var myPath = TryGetFilePath();
        if (myPath == null) return;
        if (!string.Equals(
                myPath.Replace('/', '\\'),
                updatedFilePath,
                StringComparison.OrdinalIgnoreCase))
            return;

        // CRITICAL: TagsChanged MUST be raised on the UI thread.
        var snapshot = _buffer.CurrentSnapshot;
        var span = new SnapshotSpan(snapshot, 0, snapshot.Length);
        _ = Application.Current?.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            (Action)(() => TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(span))));
    }

    public IEnumerable<ITagSpan<CoverageGlyphTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0) yield break;
        var snapshot = spans[0].Snapshot;
        var filePath = TryGetFilePath();
        if (filePath == null) yield break;
        if (!_tracker.HasAnyCoverageForFile(filePath)) yield break;

        foreach (var span in spans)
        {
            var startLine = snapshot.GetLineNumberFromPosition(span.Start);
            var endLine   = snapshot.GetLineNumberFromPosition(span.End);

            for (var lineNum = startLine; lineNum <= endLine; lineNum++)
            {
                // GetCoverageForLine expects 1-based line numbers
                var health = _tracker.GetCoverageForLine(filePath, lineNum + 1);
                if (health == CoverageHealth.NoCoverage && !SageFsFeatureFlags.CoverageGlyphsEnabled)
                    continue;
                if (health == CoverageHealth.NoCoverage) continue;

                var line = snapshot.GetLineFromLineNumber(lineNum);
                yield return new TagSpan<CoverageGlyphTag>(
                    new SnapshotSpan(line.Start, 0),
                    new CoverageGlyphTag(health));
            }
        }
    }

    private string? TryGetFilePath()
    {
        if (_buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
            return doc.FilePath;
        return null;
    }

    public void Dispose() => _tracker.CoverageUpdated -= OnCoverageUpdated;
}

// ── Tagger provider: MEF export ──────────────────────────────────────────────

[Export(typeof(ITaggerProvider))]
[ContentType("F#")]
[ContentType("F# Script")]
[TagType(typeof(CoverageGlyphTag))]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class CoverageGlyphTaggerProvider : ITaggerProvider
{
    public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag
    {
        if (!SageFsFeatureFlags.CoverageGlyphsEnabled) return null;

        // Reuse the shared annotation tracker — it already subscribes to /events SSE
        var tracker = SharedAnnotationTracker.Instance;
        return new CoverageGlyphTagger(buffer, tracker) as ITagger<T>;
    }
}

// ── Glyph factory provider: MEF export ───────────────────────────────────────

[Export(typeof(IGlyphFactoryProvider))]
[Name("SageFsCoverageGlyphs")]
[ContentType("F#")]
[ContentType("F# Script")]
[TagType(typeof(CoverageGlyphTag))]
[Order(Before = "SageFsTestGlyphs")]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class CoverageGlyphFactoryProvider : IGlyphFactoryProvider
{
    public IGlyphFactory GetGlyphFactory(IWpfTextView view, IWpfTextViewMargin margin) =>
        new CoverageGlyphFactory();
}

// ── Glyph factory: draws thin colored bars ───────────────────────────────────

internal sealed class CoverageGlyphFactory : IGlyphFactory
{
    private static readonly Brush CoveredBrush   = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))); // green
    private static readonly Brush PartialBrush   = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36))); // red
    private static readonly Brush UncoveredBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E))); // gray

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public UIElement? GenerateGlyph(IWpfTextViewLine line, IGlyphTag tag)
    {
        if (tag is not CoverageGlyphTag coverageTag) return null;

        var brush = coverageTag.Health switch
        {
            CoverageHealth.AllPassing  => CoveredBrush,
            CoverageHealth.SomeFailing => PartialBrush,
            CoverageHealth.NoCoverage  => UncoveredBrush,
            _                          => null
        };
        if (brush == null) return null;

        return new Rectangle
        {
            Width  = 3,
            Height = 16,
            Fill   = brush,
            Margin = new Thickness(1, 0, 0, 0),
        };
    }
}
