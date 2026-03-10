namespace SageFs.VisualStudio.ToolWindows;

using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;

[DataContract]
internal class HotReloadData : NotifyPropertyChangedObject
{
  private readonly VisualStudioExtensibility extensibility;
  private readonly Core.SageFsClient client;

  private string filesText = "Loading...";
  private string summaryText = "";

  public HotReloadData(VisualStudioExtensibility extensibility, Core.SageFsClient client)
  {
    this.extensibility = extensibility;
    this.client = client;
    this.RefreshCommand = new AsyncCommand(this.RefreshAsync);
    this.WatchAllCommand = new AsyncCommand(this.WatchAllAsync);
    this.UnwatchAllCommand = new AsyncCommand(this.UnwatchAllAsync);
    this.ReloadCommand = new AsyncCommand(this.ReloadAsync);

    _ = RefreshAsync(null, CancellationToken.None);
  }

  [DataMember] public IAsyncCommand RefreshCommand { get; }
  [DataMember] public IAsyncCommand WatchAllCommand { get; }
  [DataMember] public IAsyncCommand UnwatchAllCommand { get; }
  [DataMember] public IAsyncCommand ReloadCommand { get; }

  [DataMember]
  public string FilesText
  {
    get => filesText;
    set => SetProperty(ref filesText, value);
  }

  [DataMember]
  public string SummaryText
  {
    get => summaryText;
    set => SetProperty(ref summaryText, value);
  }

  private async Task<string?> GetFirstSessionIdAsync(CancellationToken ct)
  {
    var sessions = (await client.GetSessionsAsync(ct)).ToList();
    return sessions.Count > 0 ? sessions[0].Id : null;
  }

  private async Task RefreshAsync(object? parameter, CancellationToken ct)
  {
    try
    {
      var sessionId = await GetFirstSessionIdAsync(ct);
      if (sessionId == null)
      {
        FilesText = "No active sessions.";
        SummaryText = "";
        return;
      }

      var state = await client.GetHotReloadStateAsync(sessionId, ct);
      if (state == null)
      {
        FilesText = "Hot reload state not available.";
        return;
      }

      var s = state.Value;
      SummaryText = $"👁 {s.WatchedCount} watched of {s.Files.Length} total files";

      var lines = s.Files
        .OrderBy(f => f.Path)
        .Select(f =>
        {
          var icon = f.Watched ? "👁" : "  ";
          var name = System.IO.Path.GetFileName(f.Path);
          return $"  {icon} {name}";
        })
        .ToArray();
      FilesText = lines.Length > 0
        ? string.Join("\n", lines)
        : "No files tracked.";
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine(
        $"[SageFs] {nameof(HotReloadData)}.Refresh: {ex.GetType().Name}: {ex.Message}");
      FilesText = "⚠ Unable to load hot reload state.";
      SummaryText = "Click '↻' to retry, or check that the SageFs daemon is running.";
    }
  }

  private async Task WatchAllAsync(object? parameter, CancellationToken ct)
  {
    try
    {
      var sessionId = await GetFirstSessionIdAsync(ct);
      if (sessionId == null) return;
      await client.WatchAllAsync(sessionId, ct);
      await RefreshAsync(null, ct);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine(
        $"[SageFs] {nameof(HotReloadData)}.WatchAll: {ex.GetType().Name}: {ex.Message}");
    }
  }

  private async Task UnwatchAllAsync(object? parameter, CancellationToken ct)
  {
    try
    {
      var sessionId = await GetFirstSessionIdAsync(ct);
      if (sessionId == null) return;
      await client.UnwatchAllAsync(sessionId, ct);
      await RefreshAsync(null, ct);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine(
        $"[SageFs] {nameof(HotReloadData)}.UnwatchAll: {ex.GetType().Name}: {ex.Message}");
    }
  }

  private async Task ReloadAsync(object? parameter, CancellationToken ct)
  {
    try
    {
      var sessionId = await GetFirstSessionIdAsync(ct);
      if (sessionId == null) return;
      SummaryText = "⟳ Refreshing...";
      await client.RefreshHotReloadAsync(sessionId, ct);
      await RefreshAsync(null, ct);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine(
        $"[SageFs] {nameof(HotReloadData)}.Reload: {ex.GetType().Name}: {ex.Message}");
    }
  }
}
