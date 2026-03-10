namespace SageFs.VisualStudio.ToolWindows;

using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;

[DataContract]
internal class SessionContextData : NotifyPropertyChangedObject, IDisposable
{
  private readonly VisualStudioExtensibility extensibility;
  private readonly Core.SageFsClient client;
  private readonly Core.SessionSubscriber subscriber;
  private Timer? pingTimer;

  private string connectionStatus = "⟳ Checking...";
  private string sessionInfo = "Loading...";
  private string assembliesInfo = "";
  private string namespacesInfo = "";
  private string failedOpensInfo = "";
  private string hotReloadInfo = "";
  private bool isLoading;

  public SessionContextData(
    VisualStudioExtensibility extensibility,
    Core.SageFsClient client,
    Core.SessionSubscriber subscriber)
  {
    this.extensibility = extensibility;
    this.client = client;
    this.subscriber = subscriber;
    this.RefreshCommand = new AsyncCommand(this.RefreshAsync);

    subscriber.StateChanged += OnSessionStateChanged;
    subscriber.EventReceived += OnEventReceived;

    _ = RefreshAsync(null, CancellationToken.None);
    // Ping-only timer — session data arrives via SSE
    pingTimer = new Timer(_ => _ = PingAsync(CancellationToken.None),
      null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
  }

  [DataMember] public IAsyncCommand RefreshCommand { get; }

  [DataMember]
  public string ConnectionStatus
  {
    get => connectionStatus;
    set => SetProperty(ref connectionStatus, value);
  }

  [DataMember]
  public string SessionInfo
  {
    get => sessionInfo;
    set => SetProperty(ref sessionInfo, value);
  }

  [DataMember]
  public string AssembliesInfo
  {
    get => assembliesInfo;
    set => SetProperty(ref assembliesInfo, value);
  }

  [DataMember]
  public string NamespacesInfo
  {
    get => namespacesInfo;
    set => SetProperty(ref namespacesInfo, value);
  }

  [DataMember]
  public string FailedOpensInfo
  {
    get => failedOpensInfo;
    set => SetProperty(ref failedOpensInfo, value);
  }

  [DataMember]
  public string HotReloadInfo
  {
    get => hotReloadInfo;
    set => SetProperty(ref hotReloadInfo, value);
  }

  [DataMember]
  public bool IsLoading
  {
    get => isLoading;
    set => SetProperty(ref isLoading, value);
  }

  private async Task RefreshAsync(object? parameter, CancellationToken ct)
  {
    IsLoading = true;
    try
    {
      var alive = await client.PingAsync(ct);
      ConnectionStatus = alive ? "● Connected" : "○ Offline";

      if (!alive)
      {
        SessionInfo = "Daemon not running. Use 'SageFs: Start Daemon' to begin.";
        AssembliesInfo = "";
        NamespacesInfo = "";
        FailedOpensInfo = "";
        HotReloadInfo = "";
        return;
      }

      var sessions = (await client.GetSessionsAsync(ct)).ToList();
      if (sessions.Count == 0)
      {
        SessionInfo = "No sessions active. Create a session to begin.";
        AssembliesInfo = "";
        NamespacesInfo = "";
        FailedOpensInfo = "";
        HotReloadInfo = "";
        return;
      }

      var active = sessions[0];
      await FetchSessionDetailsAsync(active.Id, ct);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine(
        $"[SageFs] {nameof(SessionContextData)}.Refresh: {ex.GetType().Name}: {ex.Message}");
      ConnectionStatus = "✗ Error";
      SessionInfo = "⚠ Unable to load session information.\nClick '↻ Refresh' to retry, or check that the SageFs daemon is running.";
    }
    finally
    {
      IsLoading = false;
    }
  }

  private async Task PingAsync(CancellationToken ct)
  {
    try
    {
      var alive = await client.PingAsync(ct);
      ConnectionStatus = alive ? "● Connected" : "○ Offline";
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine(
        $"[SageFs] {nameof(SessionContextData)}.Ping: {ex.GetType().Name}: {ex.Message}");
    }
  }

  private void OnSessionStateChanged(object? sender, Core.SessionStreamState s)
  {
    ConnectionStatus = s.IsFaulted ? "✗ Faulted"
      : s.IsReady ? "● Connected"
      : "⟳ Connecting...";

    var sessionId = s.ActiveSessionId?.Value ?? "unknown";
    var faultMsg = s.FaultMessage?.Value ?? "Unknown error";

    SessionInfo = s.IsFaulted
      ? $"Session: {sessionId}\nStatus: FAULTED\nError: {faultMsg}"
      : s.WarmupProgress != null
        ? $"Session: {sessionId}\nStatus: Warming Up ({s.WarmupProgress.Value.Step}/{s.WarmupProgress.Value.Total})\n{s.WarmupProgress.Value.Message}"
        : s.IsReady
          ? $"Session: {sessionId}\nStatus: Ready\nLast Event: {FormatEvent(s.LastEvent?.Value)} at {s.LastEventAt:HH:mm:ss}"
          : $"Session: {sessionId}\nStatus: Initializing";

    if (s.IsReady && s.ActiveSessionId != null)
      _ = FetchSessionDetailsAsync(s.ActiveSessionId.Value, CancellationToken.None);
  }

  private void OnEventReceived(object? sender, Core.SessionEvent evt)
  {
    var current = SessionInfo;
    var lastLine = $"Last Event: {FormatEvent(evt)} at {DateTimeOffset.Now:HH:mm:ss}";
    var idx = current.IndexOf("\nLast Event:", StringComparison.Ordinal);
    SessionInfo = idx >= 0
      ? current[..idx] + "\n" + lastLine
      : current + "\n" + lastLine;
  }

  private static string FormatEvent(Core.SessionEvent? evt)
  {
    if (evt == null) return "none";
    if (evt.IsSessionStarted) return "session_started";
    if (evt.IsSessionWarmupCompleted) return "session_warmup_completed";
    if (evt.IsSessionWarmupProgress) return "session_warmup_progress";
    if (evt.IsSessionReady) return "session_ready";
    if (evt.IsSessionFaulted) return "session_faulted";
    if (evt.IsSessionReset) return "session_reset";
    if (evt.IsSessionHardReset) return "session_hard_reset";
    if (evt.IsDaemonSessionCreated) return "daemon_session_created";
    if (evt.IsDaemonSessionStopped) return "daemon_session_stopped";
    if (evt.IsDaemonSessionSwitched) return "daemon_session_switched";
    return "unknown";
  }

  private async Task FetchSessionDetailsAsync(string sessionId, CancellationToken ct)
  {
    try
    {
      var warmup = await client.GetWarmupContextAsync(sessionId, ct);
      if (warmup != null)
      {
        var w = warmup.Value;
        var asmList = w.AssembliesLoaded.ToList();
        AssembliesInfo = $"Assemblies ({asmList.Count} loaded, {w.WarmupDurationMs}ms warmup):\n" +
          string.Join("\n", asmList.Select(a => $"  {a.Name} ({a.NamespaceCount} ns, {a.ModuleCount} mod)"));

        var nsList = w.NamespacesOpened.ToList();
        NamespacesInfo = $"Namespaces ({nsList.Count} opened):\n" +
          string.Join("\n", nsList.Select(n => $"  {n.Name} ({(n.IsModule ? "module" : "namespace")} via {n.Source})"));

        var failedList = w.FailedOpens.ToList();
        FailedOpensInfo = failedList.Count > 0
          ? $"Failed Opens ({failedList.Count}):\n" + string.Join("\n", failedList.Select(f => "  ✗ " + string.Join(" → ", f)))
          : "No failed opens ✓";
      }
      else
      {
        AssembliesInfo = "Warmup context not available";
        NamespacesInfo = "";
        FailedOpensInfo = "";
      }

      var hotReload = await client.GetHotReloadStateAsync(sessionId, ct);
      if (hotReload != null)
      {
        var hr = hotReload.Value;
        var fileList = hr.Files.ToList();
        HotReloadInfo = $"Hot Reload ({hr.WatchedCount}/{fileList.Count} watched):\n" +
          string.Join("\n", fileList.Select(f => $"  {(f.Watched ? "●" : "○")} {f.Path}"));
      }
      else
      {
        HotReloadInfo = "Hot reload not available";
      }
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine(
        $"[SageFs] {nameof(SessionContextData)}.FetchSessionDetails: {ex.GetType().Name}: {ex.Message}");
    }
  }

  public void Dispose()
  {
    subscriber.StateChanged -= OnSessionStateChanged;
    subscriber.EventReceived -= OnEventReceived;
    pingTimer?.Dispose();
    pingTimer = null;
  }
}
