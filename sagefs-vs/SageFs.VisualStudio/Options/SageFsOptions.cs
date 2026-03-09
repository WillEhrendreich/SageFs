namespace SageFs.VisualStudio.Options;

using System.ComponentModel;

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
}

