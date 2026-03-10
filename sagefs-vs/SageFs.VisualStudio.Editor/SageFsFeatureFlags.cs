using System.IO;

namespace SageFs.VisualStudio.Editor;

/// <summary>
/// Per-feature runtime kill switches for the SageFs editor assembly.
///
/// Create marker files in %LOCALAPPDATA%\SageFs\ to disable individual features
/// without rebuilding the VSIX. Useful when a feature misbehaves in production VS
/// and you need a targeted disable without touching other features.
///
/// Flag files:
///   disable-glyphs.flag       — disables ALL editor features (legacy, from GlyphSpikeGuard)
///   disable-squiggles.flag    — disables /diagnostics SSE squiggles
///   disable-inline-hints.flag — disables inline failure adornments
///
/// To disable: create (or touch) the file. To re-enable: delete the file, then reload VS.
/// </summary>
internal static class SageFsFeatureFlags
{
    private static readonly string _dir =
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SageFs");

    /// <summary>Squiggles from the /diagnostics SSE endpoint.</summary>
    public static bool SquigglesEnabled => !File.Exists(Path.Combine(_dir, "disable-squiggles.flag"))
                                        && !GlyphSpikeGuard.IsDisabled;

    /// <summary>Inline failure adornments from file_annotations events.</summary>
    public static bool InlineHintsEnabled => !File.Exists(Path.Combine(_dir, "disable-inline-hints.flag"))
                                          && !GlyphSpikeGuard.IsDisabled;

    /// <summary>Margin glyphs (green/red/amber circles) per test function line.</summary>
    public static bool GlyphsEnabled => !GlyphSpikeGuard.IsDisabled;

    /// <summary>Coverage gutter glyphs (colored bars) from file_annotations coverage data.</summary>
    public static bool CoverageGlyphsEnabled => !File.Exists(Path.Combine(_dir, "disable-coverage-glyphs.flag"))
                                              && !GlyphSpikeGuard.IsDisabled;
}
