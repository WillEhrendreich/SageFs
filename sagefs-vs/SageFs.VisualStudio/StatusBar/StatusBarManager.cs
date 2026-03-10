namespace SageFs.VisualStudio.StatusBar;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using SageFs.VisualStudio.Services;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

/// <summary>
/// Subscribes to daemon connection state and formats status bar text using
/// <see cref="FormatStatusBarText"/>. The same pure formatting logic exists in
/// <c>SageFs.VisualStudio.Editor.StatusBarText</c> (net472) for unit-test coverage.
/// On initialization, performs a one-time daemon health check and auto-starts the
/// daemon if it is not running.
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
    // Fire-and-forget: wait for VS to settle, then report daemon status and auto-start if needed.
    _ = Task.Run(() => RunStartupHealthCheckAsync(), CancellationToken.None);
  }

  private async Task RunStartupHealthCheckAsync()
  {
    try
    {
      await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
      var alive = await _client.PingAsync(CancellationToken.None).ConfigureAwait(false);
      if (alive)
      {
        if (_output is not null)
          await _output.WriteLineAsync("✓ SageFs daemon connected.");
        // TODO: call CheckVersionAsync here when wired (separate task)
        return;
      }

      // Daemon is down — attempt auto-start.
      if (_output is not null)
        await _output.WriteLineAsync("⏳ SageFs daemon not running — attempting auto-start...");

      var solutionDir = await SolutionDirectory.GetAsync(Extensibility, CancellationToken.None).ConfigureAwait(false);
      if (solutionDir is null)
      {
        if (_output is not null)
          await _output.WriteLineAsync("⚠ Auto-start skipped: no solution is open.");
        return;
      }

      var targetResult = Core.DaemonTargetFinder.findTarget(solutionDir);

      if (!targetResult.IsOk)
      {
        if (_output is not null)
          await _output.WriteLineAsync($"⚠ Auto-start failed: {targetResult.ErrorValue}. Use Extensions → SageFs → Start Daemon.");
        return;
      }

      var startResult = Core.DaemonManager.startDaemon(targetResult.ResultValue);
      if (!startResult.IsOk)
      {
        if (_output is not null)
          await _output.WriteLineAsync($"✗ Failed to start daemon: {startResult.ErrorValue}");
        return;
      }

      if (_output is not null)
        await _output.WriteLineAsync($"▶ SageFs started with {Path.GetFileName(targetResult.ResultValue)} — waiting for ready...");

      var started = false;
      for (var i = 0; i < 10; i++)
      {
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        if (await _client.PingAsync(CancellationToken.None).ConfigureAwait(false))
        {
          started = true;
          break;
        }
      }

      if (_output is not null)
        await _output.WriteLineAsync(started
          ? $"✓ SageFs daemon auto-started (PID: {startResult.ResultValue})"
          : "✗ SageFs daemon started but not reachable after 10s — check output for errors.");
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
    // The MEF layer (net472) exports IStatusBarService which calls IVsStatusbar directly.
    // In VS 2022's dual-runtime model the SDK (net8.0) and MEF (net472) components run in
    // separate CLR instances, so StatusBarBridge.SetText is wired for future unified-hosting
    // scenarios. The MEF-side StatusBarBridge.Current (set by StatusBarService) handles the
    // actual IVsStatusbar calls within the net472 layer.
    StatusBarBridge.SetText?.Invoke(text);
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
