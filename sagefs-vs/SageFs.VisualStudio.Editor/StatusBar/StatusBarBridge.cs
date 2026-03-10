namespace SageFs.VisualStudio.Editor.StatusBar;

/// <summary>
/// MEF-layer static bridge for status bar access.
/// Populated by <see cref="StatusBarService"/> on construction so other MEF components
/// can update the status bar without importing IStatusBarService directly.
/// </summary>
internal static class StatusBarBridge
{
  public static IStatusBarService? Current { get; set; }
}
