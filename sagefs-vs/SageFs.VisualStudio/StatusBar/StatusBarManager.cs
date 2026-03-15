namespace SageFs.VisualStudio.StatusBar;

using System;
using System.IO;
using System.Linq;
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
/// daemon if it is not running. Also subscribes to SSE events for session vitals
/// and test summary updates in the status bar.
/// </summary>
[VisualStudioContribution]
internal class StatusBarManager : ExtensionPart
{
  private readonly Core.SageFsClient _client;
  private readonly Core.LiveTestingSubscriber _testSubscriber;
  private readonly Core.SessionSubscriber _sessionSubscriber;
  private readonly Core.EvalCancellation _evalCancellation;
  private bool _connected;
  private int _passingTests;
  private int _failedTests;
  private int _sessionCount;
  private string _daemonStatus = "Stopped";
  private string _workflowLabel = "REPL";
  private string? _warmupMessage;
  private OutputChannel? _output;
  private InfoBarService? _infoBar;
  private Timer? _vitalsTimer;

  public StatusBarManager(
    Core.SageFsClient client,
    Core.LiveTestingSubscriber testSubscriber,
    Core.SessionSubscriber sessionSubscriber,
    Core.EvalCancellation evalCancellation)
  {
    _client = client;
    _testSubscriber = testSubscriber;
    _sessionSubscriber = sessionSubscriber;
    _evalCancellation = evalCancellation;
  }

  protected override async Task InitializeAsync(CancellationToken ct)
  {
    _output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    _infoBar = new InfoBarService(Extensibility);
    await base.InitializeAsync(ct);

    // Subscribe to SSE events for live status bar updates
    _testSubscriber.SummaryChanged += OnTestSummaryChanged;
    _sessionSubscriber.StateChanged += OnSessionStateChanged;

    // Subscribe to eval watchdog — fires when SSE disconnects during in-flight eval
    _evalCancellation.EvalInterrupted += OnEvalInterrupted;

    // Periodic vitals refresh (session count requires an API call)
    _vitalsTimer = new Timer(_ => _ = RefreshVitalsAsync(), null,
      TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));

    // Fire-and-forget: wait for VS to settle, then report daemon status and auto-start if needed.
    _ = Task.Run(() => RunStartupHealthCheckAsync(), CancellationToken.None);
  }

  // ── SSE event handlers ──────────────────────────────────────────────────

  private void OnTestSummaryChanged(object? sender, Core.TestSummary summary)
  {
    _passingTests = summary.Passed;
    _failedTests = summary.Failed;
    PushStatusBar();
  }

  private void OnEvalInterrupted(object? sender, Microsoft.FSharp.Core.Unit args)
  {
    _ = _infoBar?.ShowEvalInterruptedAsync();
    _daemonStatus = "Stopped";
    PushStatusBar();
  }

  private void OnSessionStateChanged(object? sender, Core.SessionStreamState state)
  {
    _workflowLabel = state.WorkflowLabel;
    if (state.IsFaulted)
    {
      _daemonStatus = "Faulted";
      _warmupMessage = null;
      var sessionName = Microsoft.FSharp.Core.OptionModule.DefaultValue("unknown", state.ActiveSessionId);
      _ = ShowFaultedWithTypedErrorAsync(sessionName);
    }
    else if (state.IsReady)
    {
      _daemonStatus = "Running";
      _warmupMessage = null;
      _connected = true;
    }
    else if (state.WarmupProgress is not null)
    {
      _daemonStatus = "Connecting";
      var wp = state.WarmupProgress.Value;
      _warmupMessage = wp.Total <= 5
        ? wp.Step switch
          {
            1 => "Creating FSI...",
            2 => "Scanning sources...",
            3 => "Loading assemblies...",
            _ => "Finalizing...",
          }
        : $"Opening namespaces ({wp.Step}/{wp.Total})...";
    }
    else
    {
      _daemonStatus = "Connecting";
    }
    PushStatusBar();
  }

  /// Fetch typed error from /health and show it, falling back to generic warmup-failed message.
  private async Task ShowFaultedWithTypedErrorAsync(string sessionName)
  {
    try
    {
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
      var healthError = await _client.GetHealthErrorAsync(cts.Token).ConfigureAwait(false);
      if (healthError != null)
      {
        var err = healthError.Value;
        _ = _infoBar?.ShowTypedErrorAsync(err.Case, err.Message, err.SuggestedAction);
      }
      else
      {
        _ = _infoBar?.ShowWarmupFailedAsync(sessionName);
      }
    }
    catch
    {
      _ = _infoBar?.ShowWarmupFailedAsync(sessionName);
    }
  }

  // ── Periodic vitals refresh ─────────────────────────────────────────────

  private async Task RefreshVitalsAsync()
  {
    try
    {
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
      var alive = await _client.PingAsync(cts.Token).ConfigureAwait(false);
      var wasConnected = _connected;
      _connected = alive;
      _daemonStatus = alive ? "Running" : "Stopped";

      if (alive)
      {
        var sessions = (await _client.GetSessionsAsync(cts.Token).ConfigureAwait(false)).ToList();
        _sessionCount = sessions.Count;
      }
      else
      {
        _sessionCount = 0;
        if (wasConnected)
        {
          if (_evalCancellation.IsEvaluating)
            _ = _infoBar?.ShowEvalInterruptedAsync();
          else
            _ = _infoBar?.ShowDaemonDisconnectedAsync();
        }
      }

      PushStatusBar();
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine(
        $"[SageFs] {nameof(StatusBarManager)}.RefreshVitals: {ex.GetType().Name}: {ex.Message}");
    }
  }

  // ── Startup health check ────────────────────────────────────────────────

  private async Task RunStartupHealthCheckAsync()
  {
    try
    {
      // Check for first-run experience
      await ShowWelcomeIfFirstRunAsync().ConfigureAwait(false);

      await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
      var alive = await _client.PingAsync(CancellationToken.None).ConfigureAwait(false);
        if (alive)
        {
          _connected = true;
          _daemonStatus = "Running";
          PushStatusBar();

        var output = _output;
        if (output is not null)
          await output.WriteLineAsync("✓ SageFs daemon connected.");
        var versionResult = await _client.CheckVersionAsync(CancellationToken.None).ConfigureAwait(false);
        if (versionResult.IsError && output is not null)
          await output.WriteLineAsync($"⚠ {versionResult.ErrorValue}");

        // Fetch initial session count
        try
        {
          using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
          var sessions = (await _client.GetSessionsAsync(cts.Token).ConfigureAwait(false)).ToList();
          _sessionCount = sessions.Count;
          PushStatusBar();
        }
        catch { /* non-critical */ }
        return;
      }

      _daemonStatus = "Stopped";
      PushStatusBar();

      // Daemon is down — notify and attempt auto-start.
      if (_infoBar is not null)
        await _infoBar.ShowDaemonNotRunningAsync();

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

      var daemonProc = startResult.ResultValue;

      _daemonStatus = "Connecting";
      PushStatusBar();

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

      if (started)
      {
        _connected = true;
        _daemonStatus = "Running";
      }

      PushStatusBar();

      if (_output is not null)
      {
        if (started)
        {
          await _output.WriteLineAsync($"✓ SageFs daemon auto-started (PID: {daemonProc.Id})");
        }
        else
        {
          var stderr = Core.DaemonManager.readStderr(daemonProc);
          var msg = string.IsNullOrWhiteSpace(stderr)
            ? "✗ SageFs daemon started but not reachable after 10s — check output for errors."
            : $"✗ SageFs daemon started but not reachable after 10s.\n\nDaemon output:\n{(stderr.Length > 500 ? stderr[..500] + "…" : stderr)}";
          await _output.WriteLineAsync(msg);
        }
      }
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[SageFs] Startup health check failed: {ex.Message}");
    }
  }

  // ── Welcome / first-run ─────────────────────────────────────────────────

  /// <summary>Path to the first-run sentinel file.</summary>
  internal static string FirstRunSentinelPath => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "SageFs", ".vs-extension-welcomed");

  private async Task ShowWelcomeIfFirstRunAsync()
  {
    try
    {
      if (File.Exists(FirstRunSentinelPath)) return;

      if (_output is not null)
      {
        await _output.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        await _output.WriteLineAsync("🎉 Welcome to SageFs — F# Live Development for Visual Studio!");
        await _output.WriteLineAsync("");
        await _output.WriteLineAsync("  Get Started:");
        await _output.WriteLineAsync("    • Extensions → SageFs: Start Daemon — to launch the daemon");
        await _output.WriteLineAsync("    • Extensions → SageFs: Session Context — to view session details");
        await _output.WriteLineAsync("    • Extensions → SageFs: Live Testing Dashboard — for live test results");
        await _output.WriteLineAsync("");
        await _output.WriteLineAsync("  Documentation: https://github.com/WillEhrendreich/SageFs");
        await _output.WriteLineAsync("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
      }

      // Write the sentinel file so we don't show this again.
      var dir = Path.GetDirectoryName(FirstRunSentinelPath)!;
      Directory.CreateDirectory(dir);
      await File.WriteAllTextAsync(FirstRunSentinelPath, DateTimeOffset.UtcNow.ToString("o"));
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine(
        $"[SageFs] {nameof(StatusBarManager)}.ShowWelcome: {ex.GetType().Name}: {ex.Message}");
    }
  }

  // ── Public API for external callers ─────────────────────────────────────

  /// <summary>
  /// Called by the SSE subscription or health poll when connection state changes.
  /// </summary>
  public void UpdateConnectionState(bool connected, int passingTests = 0, int latencyMs = 0)
  {
    _connected = connected;
    _passingTests = passingTests;
    _daemonStatus = connected ? "Running" : "Stopped";
    PushStatusBar();
  }

  /// <summary>
  /// Called when the SSE connection drops and reconnection is in progress.
  /// Shows a transient "reconnecting" state in the status bar.
  /// </summary>
  public void SetReconnecting()
  {
    _daemonStatus = "Connecting";
    PushStatusBar();
  }

  // ── Status bar formatting and push ──────────────────────────────────────

  private void PushStatusBar()
  {
    var text = FormatStatusBarText(_daemonStatus, _sessionCount, _passingTests, _failedTests, _warmupMessage, _workflowLabel);
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
  /// Formats the status bar display string with daemon status, session count, and test summary.
  /// Mirrors <c>SageFs.VisualStudio.Editor.StatusBarText.FormatStatusBarText</c> (net472 layer).
  /// Both implementations must remain in sync for the basic connected/disconnected case.
  /// </summary>
  public static string FormatStatusBarText(
    string daemonStatus, int sessionCount, int passingTests, int failedTests, string? warmupMessage = null, string? workflowLabel = null)
  {
    var icon = daemonStatus switch
    {
      "Running" => "⬤",
      "Connecting" => "⟳",
      _ => "○",
    };
    if (daemonStatus == "Connecting" && warmupMessage is not null)
      return $"{icon} SageFs {warmupMessage}";
    var label = string.IsNullOrWhiteSpace(workflowLabel) ? "" : $" [{workflowLabel}]";
    var sessions = sessionCount > 0 ? $"  {sessionCount} session{(sessionCount != 1 ? "s" : "")}" : "";
    var tests = (passingTests > 0 || failedTests > 0)
      ? $"  ✓ {passingTests} / ✗ {failedTests}"
      : "";
    return $"{icon} SageFs{label} {daemonStatus}{sessions}{tests}";
  }

  /// <summary>
  /// Legacy overload for backward compatibility with the net472 layer.
  /// </summary>
  public static string FormatStatusBarText(bool connected, int passingTests, int latencyMs) =>
    FormatStatusBarText(
      connected ? "Running" : "Stopped",
      sessionCount: 0,
      passingTests,
      failedTests: 0);
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
