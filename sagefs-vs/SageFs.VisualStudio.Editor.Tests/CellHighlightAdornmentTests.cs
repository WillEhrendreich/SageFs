using System.Windows.Media;
using FluentAssertions;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// Tests for <see cref="CellHighlightAdornmentManager"/> pure static helpers
/// and <see cref="BlockHelpers.FindBlockLineRange"/>.
/// </summary>
public sealed class CellHighlightAdornmentTests
{
  // ── FindBlockLineRange ────────────────────────────────────────────────────

  [Fact]
  public void FindBlockLineRange_EmptyFile_ReturnsZeroZero()
  {
    var (start, end) = BlockHelpers.FindBlockLineRange("", 0);
    start.Should().Be(0);
    end.Should().Be(0);
  }

  [Fact]
  public void FindBlockLineRange_SemicolonMode_CursorInMiddleBlock_ReturnsCorrectRange()
  {
    const string text = "let a = 1;;\nlet b = 2;;\nlet c = 3;;";
    var cursor = text.IndexOf("let b", System.StringComparison.Ordinal) + 3;
    var (start, end) = BlockHelpers.FindBlockLineRange(text, cursor);
    // "let b = 2;;" is on line 1 (0-based)
    start.Should().Be(1);
    end.Should().Be(1);
  }

  [Fact]
  public void FindBlockLineRange_BlankLineMode_CursorInBlock_ReturnsCorrectRange()
  {
    const string text = "let a = 1\n\nlet b = 2\n\nlet c = 3";
    var cursor = text.IndexOf("let b", System.StringComparison.Ordinal) + 3;
    var (start, end) = BlockHelpers.FindBlockLineRange(text, cursor);
    // "let b = 2" is on line 2 (0-based), separated by a blank line
    start.Should().Be(2);
    end.Should().Be(2);
  }

  [Fact]
  public void FindBlockLineRange_CursorAtBoundary_ReturnsBlock()
  {
    const string text = "let a = 1;;\nlet b = 2;;";
    // Cursor at very start — should land in first block
    var (start, end) = BlockHelpers.FindBlockLineRange(text, 0);
    start.Should().Be(0);
    end.Should().Be(0);
  }

  // ── GetCellHighlightColor ─────────────────────────────────────────────────

  [Fact]
  public void GetCellHighlightColor_DarkTheme_IsLightTransparentColor()
  {
    var color = CellHighlightAdornmentManager.GetCellHighlightColor(isDarkTheme: true);
    // Dark theme → transparent white overlay
    color.R.Should().Be(0xFF);
    color.G.Should().Be(0xFF);
    color.B.Should().Be(0xFF);
    color.A.Should().Be(0x18, because: "semi-transparent");
  }

  [Fact]
  public void GetCellHighlightColor_LightTheme_IsDarkTransparentColor()
  {
    var color = CellHighlightAdornmentManager.GetCellHighlightColor(isDarkTheme: false);
    // Light theme → transparent dark overlay
    color.R.Should().Be(0x00);
    color.G.Should().Be(0x00);
    color.B.Should().Be(0x00);
    color.A.Should().Be(0x18, because: "semi-transparent");
  }
}
