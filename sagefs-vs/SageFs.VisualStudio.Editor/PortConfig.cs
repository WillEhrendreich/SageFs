using System;
using System.IO;

namespace SageFs.VisualStudio.Editor;

/// <summary>
/// Reads the SageFs daemon URL written by the out-of-process extension.
///
/// The out-of-process extension writes:
///   %LOCALAPPDATA%\SageFs\daemon.json  →  {"Url":"http://localhost:37749"}
///
/// The file is written on daemon start; deleted on extension shutdown.
/// </summary>
internal static class PortConfig
{
    private static readonly string FilePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SageFs", "daemon.json");

    public static string? TryGetDaemonUrl()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = File.ReadAllText(FilePath).Trim();
            if (string.IsNullOrEmpty(json)) return null;
            // Parse {"Url":"http://localhost:37749"} without taking a hard dep on System.Text.Json
            var marker = "\"Url\":\"";
            var start  = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            start += marker.Length;
            var end = json.IndexOf('"', start);
            if (end < 0) return null;
            var url = json.Substring(start, end - start);
            return string.IsNullOrEmpty(url) ? null : url;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Runtime kill switch for the MEF glyph spike.
/// Create %LOCALAPPDATA%\SageFs\disable-glyphs.flag to disable glyph rendering
/// without uninstalling the extension. Delete the file to re-enable.
/// </summary>
internal static class GlyphSpikeGuard
{
    private static readonly string FlagPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SageFs", "disable-glyphs.flag");

    public static bool IsDisabled => File.Exists(FlagPath);
}
