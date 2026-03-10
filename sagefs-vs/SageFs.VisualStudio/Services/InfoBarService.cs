namespace SageFs.VisualStudio.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

/// <summary>
/// Shows actionable notifications for daemon lifecycle events via the SageFs output
/// channel. In the new out-of-process VS Extensibility model, classic InfoBar APIs
/// (IVsInfoBarUIFactory) are unavailable. Instead, critical events are reported to
/// the "SageFs" output channel with clear action guidance, and the channel is
/// activated so the user sees the message immediately.
/// </summary>
internal sealed class InfoBarService : IDisposable
{
  private readonly VisualStudioExtensibility extensibility;
  private OutputChannel? output;
  private bool disposed;

  public InfoBarService(VisualStudioExtensibility extensibility)
  {
    this.extensibility = extensibility;
  }

  /// <summary>
  /// Lazily creates (or reuses) the shared "SageFs" output channel.
  /// </summary>
  private async Task<OutputChannel?> GetOutputAsync(CancellationToken ct)
  {
    if (output is not null) return output;
    try
    {
      output = await extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine(
        $"[SageFs] {nameof(InfoBarService)}: failed to create output channel: {ex.Message}");
    }
    return output;
  }

  /// <summary>
  /// Show "Daemon failed to start" notification with captured stderr output.
  /// </summary>
  public async Task ShowDaemonStartupFailedAsync(string reason, string stderr = "", CancellationToken ct = default)
  {
    var ch = await GetOutputAsync(ct);
    if (ch is null) return;
    await ch.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    await ch.WriteLineAsync($"✗ SageFs daemon failed to start: {reason}");
    if (!string.IsNullOrWhiteSpace(stderr))
    {
      await ch.WriteLineAsync("");
      await ch.WriteLineAsync("Daemon output:");
      // Limit to first 1000 chars to avoid flooding the output channel
      var snippet = stderr.Length > 1000 ? stderr[..1000] + "…" : stderr;
      await ch.WriteLineAsync(snippet);
    }
    await ch.WriteLineAsync("");
    await ch.WriteLineAsync("  → Use  Extensions → SageFs: Start Daemon  to retry.");
    await ch.WriteLineAsync("  → Run 'SageFs --proj YourProject.fsproj' from a terminal for detailed output.");
    await ch.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
  }

  /// <summary>
  /// Show "Evaluation interrupted" notification when SSE drops during an in-flight eval.
  /// </summary>
  public async Task ShowEvalInterruptedAsync(CancellationToken ct = default)
  {
    var ch = await GetOutputAsync(ct);
    if (ch is null) return;
    await ch.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    await ch.WriteLineAsync("⚠ Evaluation interrupted: daemon connection lost.");
    await ch.WriteLineAsync("  → The daemon may have crashed or been restarted.");
    await ch.WriteLineAsync("  → SSE subscriptions will auto-reconnect when the daemon is available.");
    await ch.WriteLineAsync("  → If the daemon crashed, use  Extensions → SageFs: Start Daemon  to restart.");
    await ch.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
  }

  /// <summary>
  /// Show "Daemon not running" notification with guidance to start it.
  /// </summary>
  public async Task ShowDaemonNotRunningAsync(CancellationToken ct = default)
  {
    var ch = await GetOutputAsync(ct);
    if (ch is null) return;
    await ch.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    await ch.WriteLineAsync("⚠ SageFs daemon is not running.");
    await ch.WriteLineAsync("  → Use  Extensions → SageFs: Start Daemon  to start it.");
    await ch.WriteLineAsync("  → Or run 'SageFs --proj YourProject.fsproj' from a terminal.");
    await ch.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
  }

  /// <summary>
  /// Show "Daemon disconnected" notification with reconnect guidance.
  /// </summary>
  public async Task ShowDaemonDisconnectedAsync(CancellationToken ct = default)
  {
    var ch = await GetOutputAsync(ct);
    if (ch is null) return;
    await ch.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    await ch.WriteLineAsync("⚠ SageFs daemon connection lost.");
    await ch.WriteLineAsync("  → SSE subscriptions will auto-reconnect when the daemon is available.");
    await ch.WriteLineAsync("  → If the daemon crashed, use  Extensions → SageFs: Start Daemon  to restart.");
    await ch.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
  }

  /// <summary>
  /// Show "Session warmup failed" notification with retry guidance.
  /// </summary>
  public async Task ShowWarmupFailedAsync(string sessionName, CancellationToken ct = default)
  {
    var ch = await GetOutputAsync(ct);
    if (ch is null) return;
    await ch.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    await ch.WriteLineAsync($"✗ Session warmup failed: {sessionName}");
    await ch.WriteLineAsync("  → Use  Extensions → SageFs: Hard Reset  to rebuild and retry.");
    await ch.WriteLineAsync("  → Check the SageFs console window for detailed error output.");
    await ch.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
  }

  /// <summary>
  /// Show a typed daemon error with its message and suggested action from the /health endpoint.
  /// </summary>
  public async Task ShowTypedErrorAsync(string errorCase, string message, string suggestedAction, CancellationToken ct = default)
  {
    var ch = await GetOutputAsync(ct);
    if (ch is null) return;
    await ch.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    await ch.WriteLineAsync($"✗ [{errorCase}] {message}");
    if (!string.IsNullOrWhiteSpace(suggestedAction))
      await ch.WriteLineAsync($"  → {suggestedAction}");
    await ch.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
  }

  public void Dispose()
  {
    if (disposed) return;
    disposed = true;
    output = null;
  }
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
