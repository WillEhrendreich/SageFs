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
/// On initialization, performs a one-time daemon health check and reports the result
/// to the SageFs output channel.
/// </summary>
[VisualStudioContribution]
internal class StatusBarManager : ExtensionPart
{
  private readonly Core.SageFsClient _client;
  private bool _connected;
  private int _passingTests;
  private int _latencyMs;
  private OutputChannel? _output;

  public StatusBarManager(Core.SageFsClient client) => _client = client;

  protected override async Task InitializeAsync(CancellationToken ct)
  {
    _output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
    // Fire-and-forget: wait for VS to settle, then report daemon status.
    _ = Task.Run(() => RunStartupHealthCheckAsync(), CancellationToken.None);
  }

  private async Task RunStartupHealthCheckAsync()
  {
    try
    {
      await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
      var alive = await _client.PingAsync(CancellationToken.None).ConfigureAwait(false);
      if (_output is not null)
      {
        var msg = alive
          ? "✓ SageFs daemon connected."
          : "⚠ SageFs daemon is not running. Use Extensions → SageFs → Start Daemon to start it.";
        await _output.WriteLineAsync(msg);
      }
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[SageFs] Startup health check failed: {ex.Message}");
    }
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
    _ = TryUpdateVsStatusBarAsync(text);
  }

  private Task TryUpdateVsStatusBarAsync(string text)
  {
    // VS Extensibility SDK 17.14 does not expose a StatusBar property on ShellExtensibility.
    // The native VS status bar update is skipped; connection state is surfaced via the
    // SageFs output channel (see UpdateConnectionState above).
    // TODO: add IVsStatusbar interop when Microsoft.VisualStudio.Shell.Interop reference is available.
    System.Diagnostics.Debug.WriteLine($"[SageFs] {nameof(StatusBarManager)}: {text}");
    return Task.CompletedTask;
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
