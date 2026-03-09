using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SageFs.VisualStudio.Editor;

// ── Adornment layer definition ────────────────────────────────────────────────

internal static class SageFsCellHighlightLayer
{
  [Export(typeof(AdornmentLayerDefinition))]
  [Name("SageFsCellHighlight")]
  [Order(Before = PredefinedAdornmentLayers.Text)]
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
internal sealed class CellHighlightAdornmentListener : IWpfTextViewCreationListener
{
  [ImportingConstructor]
  public CellHighlightAdornmentListener() { }

  public void TextViewCreated(IWpfTextView textView)
  {
    _ = new CellHighlightAdornmentManager(textView);
  }
}

// ── Per-view adornment manager ────────────────────────────────────────────────

internal sealed class CellHighlightAdornmentManager : IDisposable
{
  private readonly IWpfTextView _view;
  private readonly IAdornmentLayer _layer;

  private bool _pending;

  public CellHighlightAdornmentManager(IWpfTextView view)
  {
    _view  = view;
    _layer = view.GetAdornmentLayer("SageFsCellHighlight");

    _view.Caret.PositionChanged += OnCaretPositionChanged;
    _view.LayoutChanged         += OnLayoutChanged;
    _view.Closed                += OnViewClosed;
  }

  private void OnCaretPositionChanged(object? sender, CaretPositionChangedEventArgs e)
  {
    if (_pending) return;
    _pending = true;
    _ = Application.Current?.Dispatcher.BeginInvoke(
      DispatcherPriority.Normal,
      (Action)(() =>
      {
        _pending = false;
        RenderHighlight();
      }));
  }

  private void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs e)
  {
    if (e.NewOrReformattedLines.Count == 0 && e.TranslatedLines.Count == 0) return;
    RenderHighlight();
  }

  private void RenderHighlight()
  {
    _layer.RemoveAllAdornments();

    var snapshot     = _view.TextSnapshot;
    var cursorOffset = _view.Caret.Position.BufferPosition.Position;
    var text         = snapshot.GetText();

    var (startLine, endLine) = BlockHelpers.FindBlockLineRange(text, cursorOffset);
    if (startLine == 0 && endLine == 0 && string.IsNullOrEmpty(text)) return;

    var brush = GetCellHighlightBrush(IsDarkTheme());

    for (var lineNum = startLine; lineNum <= endLine; lineNum++)
    {
      if (lineNum >= snapshot.LineCount) break;

      var snapshotLine = snapshot.GetLineFromLineNumber(lineNum);
      var viewLine = _view.TextViewLines.GetTextViewLineContainingBufferPosition(
        snapshotLine.Start);
      if (viewLine == null) continue;

      var rect = new Rectangle
      {
        Width  = Math.Max(viewLine.Right - viewLine.Left, _view.ViewportWidth),
        Height = viewLine.Height,
        Fill   = brush,
      };

      Canvas.SetLeft(rect, viewLine.Left);
      Canvas.SetTop(rect, viewLine.Top);

      _layer.AddAdornment(
        AdornmentPositioningBehavior.TextRelative,
        new SnapshotSpan(snapshotLine.Start, 0),
        tag:             null,
        adornment:       rect,
        removedCallback: null);
    }
  }

  /// <summary>
  /// Returns the semi-transparent highlight color based on theme.
  /// Public and static so it can be tested without a VS host.
  /// </summary>
  public static Color GetCellHighlightColor(bool isDarkTheme) =>
    isDarkTheme
      ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) // semi-transparent white (dark theme)
      : Color.FromArgb(0x18, 0x00, 0x00, 0x00); // semi-transparent black (light theme)

  private static Brush GetCellHighlightBrush(bool isDarkTheme)
  {
    var c = GetCellHighlightColor(isDarkTheme);
    var b = new SolidColorBrush(c);
    b.Freeze();
    return b;
  }

  private static bool IsDarkTheme()
  {
    try
    {
      var c = SystemColors.WindowTextColor;
      return (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) > 128;
    }
    catch { return true; }
  }

  private void OnViewClosed(object? sender, EventArgs e) => Dispose();

  public void Dispose()
  {
    _view.Caret.PositionChanged -= OnCaretPositionChanged;
    _view.LayoutChanged         -= OnLayoutChanged;
    _view.Closed                -= OnViewClosed;
  }
}
