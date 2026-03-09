using System;
using System.Diagnostics;
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
    private const string Source = nameof(PortConfig);

    private static readonly string FilePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SageFs", "daemon.json");

    /// <summary>
    /// Returns the daemon base URL (e.g. "http://localhost:37749"), or <c>null</c> if not available.
    /// Logs the specific reason for failure to the VS activity log and <see cref="Debug"/> output.
    /// </summary>
    public static string? TryGetDaemonUrl()
    {
        if (!File.Exists(FilePath))
        {
            // Normal case when SageFs daemon hasn't been started yet — not an error.
            Debug.WriteLine($"[{Source}] daemon.json not found at {FilePath} — SageFs daemon not started.");
            return null;
        }

        string json;
        try
        {
            json = File.ReadAllText(FilePath).Trim();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Source}] Failed to read {FilePath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        if (string.IsNullOrEmpty(json))
        {
            Debug.WriteLine($"[{Source}] {FilePath} is empty — daemon may be starting.");
            return null;
        }

        // Parse {"Url":"http://localhost:37749"} without a System.Text.Json dependency.
        const string marker = "\"Url\":\"";
        var start = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            Debug.WriteLine($"[{Source}] {FilePath} does not contain expected '\"Url\":\"' key. " +
                            $"Content: {(json.Length > 200 ? json.Substring(0, 200) + "…" : json)}");
            return null;
        }

        start += marker.Length;
        var end = json.IndexOf('"', start);
        if (end < 0)
        {
            Debug.WriteLine($"[{Source}] Malformed URL value in {FilePath}: no closing quote after position {start}.");
            return null;
        }

        var url = json.Substring(start, end - start);
        if (string.IsNullOrEmpty(url))
        {
            Debug.WriteLine($"[{Source}] Parsed empty URL from {FilePath}.");
            return null;
        }

        Debug.WriteLine($"[{Source}] Daemon URL: {url}");
        return url;
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
