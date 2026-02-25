namespace SageFs.VisualStudio.ToolWindows;

using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;

[DataContract]
internal class LiveTestingData : NotifyPropertyChangedObject, IDisposable
{
  private readonly VisualStudioExtensibility extensibility;
  private readonly Core.SageFsClient client;
  private readonly Core.LiveTestingSubscriber subscriber;

  private string enabledStatus = "⟳ Checking...";
  private string summaryText = "";
  private string testResultsText = "";
  private string recentEventsText = "";
  private bool isEnabled;

  public LiveTestingData(
    VisualStudioExtensibility extensibility,
    Core.SageFsClient client,
    Core.LiveTestingSubscriber subscriber)
  {
    this.extensibility = extensibility;
    this.client = client;
    this.subscriber = subscriber;
    this.RefreshCommand = new AsyncCommand(this.RefreshAsync);
    this.ToggleCommand = new AsyncCommand(this.ToggleAsync);
    this.RunAllCommand = new AsyncCommand(this.RunAllAsync);

    subscriber.StateChanged += OnStateChanged;
    subscriber.SummaryChanged += OnSummaryChanged;

    _ = RefreshAsync(null, CancellationToken.None);
  }

  [DataMember] public IAsyncCommand RefreshCommand { get; }
  [DataMember] public IAsyncCommand ToggleCommand { get; }
  [DataMember] public IAsyncCommand RunAllCommand { get; }

  [DataMember]
  public string EnabledStatus
  {
    get => enabledStatus;
    set => SetProperty(ref enabledStatus, value);
  }

  [DataMember]
  public string SummaryText
  {
    get => summaryText;
    set => SetProperty(ref summaryText, value);
  }

  [DataMember]
  public string TestResultsText
  {
    get => testResultsText;
    set => SetProperty(ref testResultsText, value);
  }

  [DataMember]
  public string RecentEventsText
  {
    get => recentEventsText;
    set => SetProperty(ref recentEventsText, value);
  }

  [DataMember]
  public bool IsEnabled
  {
    get => isEnabled;
    set => SetProperty(ref isEnabled, value);
  }

  private void OnStateChanged(object? sender, Core.LiveTestState state)
  {
    UpdateFromState(state);
  }

  private void OnSummaryChanged(object? sender, Core.TestSummary summary)
  {
    var icon = summary.Failed > 0 ? "✗" : "✓";
    SummaryText = $"{icon} {summary.Passed}/{summary.Total} passed, {summary.Failed} failed";
    if (summary.Running > 0)
      SummaryText += $", {summary.Running} running";
    if (summary.Stale > 0)
      SummaryText += $", {summary.Stale} stale";
  }

  private void UpdateFromState(Core.LiveTestState state)
  {
    IsEnabled = state.Enabled.IsOn;
    EnabledStatus = IsEnabled ? "● Live Testing ON" : "○ Live Testing OFF";

    if (state.LastSummary != null)
      OnSummaryChanged(null, state.LastSummary.Value);

    var testCount = state.Tests.Count;
    if (testCount == 0)
    {
      TestResultsText = "No tests discovered yet.";
      return;
    }

    var lines = state.Tests
      .Select(kv =>
      {
        var info = kv.Value;
        var result = Microsoft.FSharp.Collections.MapModule.TryFind(info.Id, state.Results);
        var label = Core.LiveTestingSubscriber.formatTestLabel(info, result);
        return $"  {label}";
      })
      .ToArray();
    TestResultsText = $"Tests ({testCount}):\n" + string.Join("\n", lines);
  }

  private async Task RefreshAsync(object? parameter, CancellationToken ct)
  {
    try
    {
      var status = await client.GetLiveTestingStatusAsync(ct);
      if (status != null)
      {
        IsEnabled = status.Value.Enabled;
        EnabledStatus = IsEnabled ? "● Live Testing ON" : "○ Live Testing OFF";
        if (status.Value.Summary != null)
          OnSummaryChanged(null, status.Value.Summary.Value);
      }

      var eventsJson = await client.GetRecentEventsAsync(10, ct);
      if (!string.IsNullOrEmpty(eventsJson) && eventsJson != "[]")
      {
        var truncated = eventsJson.Length > 500 ? eventsJson[..500] + "..." : eventsJson;
        RecentEventsText = $"Recent Events:\n  {truncated}";
      }
      else
      {
        RecentEventsText = "No recent events.";
      }
    }
    catch (Exception ex)
    {
      EnabledStatus = "✗ Error";
      SummaryText = $"Error: {ex.Message}";
    }
  }

  private async Task ToggleAsync(object? parameter, CancellationToken ct)
  {
    try
    {
      await client.ToggleLiveTestingAsync(ct);
      IsEnabled = !IsEnabled;
      EnabledStatus = IsEnabled ? "● Live Testing ON" : "○ Live Testing OFF";
    }
    catch { /* best effort */ }
  }

  private async Task RunAllAsync(object? parameter, CancellationToken ct)
  {
    try
    {
      SummaryText = "⟳ Running all tests...";
      await client.RunTestsAsync("", ct);
    }
    catch { /* best effort */ }
  }

  public void Dispose()
  {
    subscriber.StateChanged -= OnStateChanged;
    subscriber.SummaryChanged -= OnSummaryChanged;
  }
}
