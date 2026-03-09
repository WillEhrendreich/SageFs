namespace SageFs.VisualStudio.StatusBar;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

/// <summary>
/// Subscribes to daemon connection state and formats status bar text using
/// <see cref="FormatStatusBarText"/>. The same pure formatting logic exists in
/// <c>SageFs.VisualStudio.Editor.StatusBarText</c> (net472) for unit-test coverage.
/// </summary>
[VisualStudioContribution]
internal class StatusBarManager : ExtensionPart
{
  private bool _connected;
  private int _passingTests;
  private int _latencyMs;
  private OutputChannel? _output;

  protected override async Task InitializeAsync(CancellationToken ct)
  {
    _output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  /// <summary>
  /// Called by the SSE subscription or health poll when connection state changes.
  /// </summary>
  public void UpdateConnectionState(bool connected, int passingTests = 0, int latencyMs = 0)
  {
    _connected    = connected;
    _passingTests = passingTests;
    _latencyMs    = latencyMs;

    var text = FormatStatusBarText(connected, passingTests, latencyMs);
    _ = _output?.WriteLineAsync(text);
  }

  /// <summary>
  /// Formats the status bar display string. Mirrors
  /// <c>SageFs.VisualStudio.Editor.StatusBarText.FormatStatusBarText</c> (net472 layer).
  /// Both implementations must remain in sync.
  /// </summary>
  public static string FormatStatusBarText(bool connected, int passingTests, int latencyMs) =>
    connected
      ? $"⬤ SageFs Connected  {passingTests} passing  {latencyMs}ms"
      : "⬤ SageFs Disconnected";
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
