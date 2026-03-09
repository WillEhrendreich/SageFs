using FluentAssertions;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// Tests for <see cref="DensityModeHelper.IsAdornmentVisible"/>.
/// Covers all 9 mode × kind combinations.
/// </summary>
public sealed class DensityModeTests
{
  [Theory]
  [InlineData(DensityMode.Full, AdornmentKind.InlineResult,  true)]
  [InlineData(DensityMode.Full, AdornmentKind.CellHighlight, true)]
  [InlineData(DensityMode.Full, AdornmentKind.GlyphMargin,   true)]
  [InlineData(DensityMode.Normal, AdornmentKind.InlineResult,  true)]
  [InlineData(DensityMode.Normal, AdornmentKind.CellHighlight, false)]
  [InlineData(DensityMode.Normal, AdornmentKind.GlyphMargin,   true)]
  [InlineData(DensityMode.Minimal, AdornmentKind.InlineResult,  false)]
  [InlineData(DensityMode.Minimal, AdornmentKind.CellHighlight, false)]
  [InlineData(DensityMode.Minimal, AdornmentKind.GlyphMargin,   true)]
  public void IsAdornmentVisible_AllCombinations(DensityMode mode, AdornmentKind kind, bool expected)
  {
    DensityModeHelper.IsAdornmentVisible(mode, kind)
      .Should().Be(expected,
        because: $"{mode} + {kind} visibility should be {expected}");
  }
}
