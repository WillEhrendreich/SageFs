namespace SageFs.VisualStudio.StatusBar;

/// <summary>
/// SDK-side bridge stub for status bar updates.
/// In VS 2022's dual-runtime extension model the MEF (net472) and SDK (net8.0)
/// components run in separate CLR instances, so the MEF-side
/// <c>SageFs.VisualStudio.Editor.StatusBar.StatusBarBridge</c> cannot be reached directly.
/// This SDK-side bridge is wired here to support future unified-hosting scenarios.
/// </summary>
public static class StatusBarBridge
{
  public static System.Action<string>? SetText { get; set; }
  public static System.Action? Clear { get; set; }
}
