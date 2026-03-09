namespace SageFs.VisualStudio.Options;

using System.ComponentModel;

/// <summary>
/// Controls the level of adornment decoration rendered in the editor.
/// Full: inline results + cell highlight + glyph margin.
/// Normal: inline results + glyph margin only (current Sprint 7 behaviour).
/// Minimal: glyph margin only.
/// </summary>
public enum DensityMode { Full, Normal, Minimal }

/// <summary>
/// SageFs configuration. Stored in VS user settings via the VS Extensibility SDK settings API.
/// </summary>
[System.Runtime.InteropServices.Guid("b2d3f4a5-c6e7-4d8f-9a0b-1c2d3e4f5a6b")]
public sealed class SageFsOptions
{
    [Category("Connection")]
    [DisplayName("Daemon URL")]
    [Description("URL of the SageFs daemon (default: http://localhost:37749)")]
    public string DaemonUrl { get; set; } = "http://localhost:37749";

    [Category("Inline Results")]
    [DisplayName("Active Result Opacity")]
    [Description("Opacity for fresh eval results (0.0–1.0, default 0.75)")]
    public double ActiveOpacity { get; set; } = 0.75;

    [Category("Inline Results")]
    [DisplayName("Stale Result Opacity")]
    [Description("Opacity for stale eval results (0.0–1.0, default 0.35)")]
    public double StaleOpacity { get; set; } = 0.35;

    [Category("Features")]
    [DisplayName("Enable Squiggles")]
    [Description("Show inline squiggle diagnostics from the daemon")]
    public bool SquigglesEnabled { get; set; } = true;

    [Category("Features")]
    [DisplayName("Enable Glyph Margin")]
    [Description("Show test status glyphs in the editor margin")]
    public bool GlyphsEnabled { get; set; } = true;

    [Category("Features")]
    [DisplayName("Enable CodeLens")]
    [Description("Show test coverage CodeLens badges")]
    public bool CodeLensEnabled { get; set; } = true;

    [Category("Features")]
    [DisplayName("Enable Cell Highlight")]
    [Description("Highlight the current ;; block under the cursor")]
    public bool CellHighlightEnabled { get; set; } = true;

    [Category("Features")]
    [DisplayName("Density Mode")]
    [Description("Controls which adornments are visible: Full, Normal, or Minimal")]
    public DensityMode DensityMode { get; set; } = DensityMode.Normal;

    /// <summary>
    /// Parses the TCP port from a URL such as "http://localhost:37749".
    /// Returns <c>null</c> if the URL is invalid or has no explicit port.
    /// </summary>
    public static int? ParsePort(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return System.Uri.TryCreate(url, System.UriKind.Absolute, out var uri) && uri.Port > 0
            ? uri.Port
            : (int?)null;
    }
}

