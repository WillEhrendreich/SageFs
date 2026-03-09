using System;
using System.IO;

namespace SageFs.VisualStudio.Editor;

/// <summary>
/// Reads the SageFs daemon URL written by the out-of-process extension.
/// The out-of-process extension (net8.0) writes:
///   %TEMP%\sagefs-port.txt  containing e.g. "http://localhost:37749"
/// </summary>
internal static class PortConfig
{
    private static readonly string FilePath =
        Path.Combine(Path.GetTempPath(), "sagefs-port.txt");

    public static string? TryGetDaemonUrl()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var url = File.ReadAllText(FilePath).Trim();
            return string.IsNullOrEmpty(url) ? null : url;
        }
        catch
        {
            return null;
        }
    }
}
