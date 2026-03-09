using System;
using System.IO;

namespace SageFs.VisualStudio.Editor;

/// <summary>
/// Controls how much adornment decoration is visible per editor view.
/// Full: all adornments on.
/// Normal: inline results + glyph margin, cell highlight off.
/// Minimal: glyph margin only.
/// </summary>
public enum DensityMode { Full, Normal, Minimal }

public enum AdornmentKind { InlineResult, CellHighlight, GlyphMargin }

/// <summary>
/// Pure helper that maps a <see cref="DensityMode"/> to per-adornment visibility.
/// No VS API dependencies — fully testable.
/// </summary>
public static class DensityModeHelper
{
  public static bool IsAdornmentVisible(DensityMode mode, AdornmentKind kind) =>
    (mode, kind) switch
    {
      (DensityMode.Full, _)                                  => true,
      (DensityMode.Normal, AdornmentKind.InlineResult)       => true,
      (DensityMode.Normal, AdornmentKind.GlyphMargin)        => true,
      (DensityMode.Minimal, AdornmentKind.GlyphMargin)       => true,
      _                                                       => false,
    };
}

/// <summary>
/// Shared runtime state for density mode, persisted via a small file in %LOCALAPPDATA%\SageFs.
/// Updated by the ToggleDensityModeCommand (net8.0 layer) via the same file.
/// </summary>
public static class DensityModeState
{
  private static readonly string _file = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "SageFs", "density-mode.txt");

  public static DensityMode CurrentMode
  {
    get
    {
      try
      {
        var text = File.ReadAllText(_file).Trim();
        return Enum.TryParse<DensityMode>(text, out var mode) ? mode : DensityMode.Normal;
      }
      catch { return DensityMode.Normal; }
    }
    set
    {
      try
      {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, value.ToString());
      }
      catch { }
    }
  }
}
