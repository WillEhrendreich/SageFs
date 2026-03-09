namespace SageFs.VisualStudio.Editor;

/// <summary>
/// Pure text formatting for the SageFs status bar indicator.
/// No VS SDK dependencies — fully testable without a VS host.
/// </summary>
internal static class StatusBarText
{
  /// <summary>
  /// Formats the status bar display string for the SageFs connection indicator.
  /// </summary>
  public static string FormatStatusBarText(bool connected, int passingTests, int latencyMs)
  {
    if (!connected)
      return "⬤ SageFs Disconnected";

    return $"⬤ SageFs Connected  {passingTests} passing  {latencyMs}ms";
  }
}
