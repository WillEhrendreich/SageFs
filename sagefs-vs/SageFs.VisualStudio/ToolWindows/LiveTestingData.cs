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
  private string connectionStatus = "⟳ Connecting to daemon...";
  private bool connectionStatusReceived;
  private string summaryText = "";
  private string testResultsText = "";
  private string recentEventsText = "";
  private string filterLabel = "All";
  private bool isEnabled;
  private Core.TestStatusFilter currentFilter = Core.TestStatusFilter.All;
  private string searchQuery = "";

  // Run policy section
  private string selectedCategory = "Unit";
  private string selectedPolicy = "On every change";
  private bool runPolicySectionVisible;
  private string currentPoliciesText = "";

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
    this.CycleFilterCommand = new AsyncCommand(this.CycleFilterAsync);
    this.ClearSearchCommand = new AsyncCommand(this.ClearSearchAsync);
    this.ApplyRunPolicyCommand = new AsyncCommand(this.ApplyRunPolicyAsync);
    this.ToggleRunPolicySectionCommand = new AsyncCommand(this.ToggleRunPolicySectionAsync);

    subscriber.StateChanged += OnStateChanged;
    subscriber.SummaryChanged += OnSummaryChanged;

    _ = RefreshAsync(null, CancellationToken.None);
  }

  [DataMember] public IAsyncCommand RefreshCommand { get; }
  [DataMember] public IAsyncCommand ToggleCommand { get; }
  [DataMember] public IAsyncCommand RunAllCommand { get; }
  [DataMember] public IAsyncCommand CycleFilterCommand { get; }
  [DataMember] public IAsyncCommand ClearSearchCommand { get; }
  [DataMember] public IAsyncCommand ApplyRunPolicyCommand { get; }
  [DataMember] public IAsyncCommand ToggleRunPolicySectionCommand { get; }

  // Category and policy display lists (labels shown in ComboBoxes)
  [DataMember] public string[] CategoryLabels { get; } =
    Core.TestCategory.All.Select(c => Core.TestCategory.DisplayName(c)).ToArray();
  [DataMember] public string[] PolicyLabels { get; } =
    Core.RunPolicy.All.Select(p => Core.RunPolicy.DisplayName(p)).ToArray();

  [DataMember]
  public string ConnectionStatus
  {
    get => connectionStatus;
    set => SetProperty(ref connectionStatus, value);
  }

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

  [DataMember]
  public string FilterLabel
  {
    get => filterLabel;
    set => SetProperty(ref filterLabel, value);
  }

  [DataMember]
  public string SearchQuery
  {
    get => searchQuery;
    set
    {
      if (SetProperty(ref searchQuery, value))
        UpdateTestResults();
    }
  }

  [DataMember]
  public string CurrentPoliciesText
  {
    get => currentPoliciesText;
    set => SetProperty(ref currentPoliciesText, value);
  }

  [DataMember]
  public string SelectedCategory
  {
    get => selectedCategory;
    set
    {
      if (SetProperty(ref selectedCategory, value))
        SyncSelectedPolicyFromState();
    }
  }

  [DataMember]
  public string SelectedPolicy
  {
    get => selectedPolicy;
    set => SetProperty(ref selectedPolicy, value);
  }

  [DataMember]
  public bool RunPolicySectionVisible
  {
    get => runPolicySectionVisible;
    set => SetProperty(ref runPolicySectionVisible, value);
  }

  private void OnStateChanged(object? sender, Core.LiveTestState state)
  {
    if (!connectionStatusReceived)
    {
      connectionStatusReceived = true;
      ConnectionStatus = "● Daemon connected";
    }
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

  private Core.LiveTestState? lastState;

  private void UpdateFromState(Core.LiveTestState state)
  {
    lastState = state;
    IsEnabled = state.Enabled.IsOn;
    EnabledStatus = IsEnabled ? "● Live Testing ON" : "○ Live Testing OFF";

    if (state.LastSummary != null)
      OnSummaryChanged(null, state.LastSummary.Value);

    UpdatePoliciesFromState(state);
    UpdateTestResults();
  }

  private void UpdateTestResults()
  {
    if (lastState == null) return;
    TestResultsText = Core.TestTreeViewModel.formatGroupedOutput(
      currentFilter, searchQuery, lastState);
  }

  private void SyncSelectedPolicyFromState()
  {
    if (lastState == null) return;
    var catDu = Core.TestCategory.All
      .FirstOrDefault(c => Core.TestCategory.DisplayName(c) == selectedCategory);
    var policy = lastState.Policies.TryFind(catDu);
    if (Microsoft.FSharp.Core.OptionModule.IsSome(policy))
      SelectedPolicy = Core.RunPolicy.DisplayName(policy.Value);
  }

  private void UpdatePoliciesFromState(Core.LiveTestState state)
  {
    if (state.Policies.IsEmpty) return;
    var lines = Core.TestCategory.All
      .Select(cat =>
      {
        var pol = state.Policies.TryFind(cat);
        var polLabel = Microsoft.FSharp.Core.OptionModule.IsSome(pol)
          ? Core.RunPolicy.DisplayName(pol.Value)
          : "—";
        return $"  {Core.TestCategory.DisplayName(cat),-16}: {polLabel}";
      });
    CurrentPoliciesText = string.Join("\n", lines);
    SyncSelectedPolicyFromState();
  }

  private async Task RefreshAsync(object? parameter, CancellationToken ct)
  {
    try
    {
      var state = subscriber.CurrentState;
      IsEnabled = state.Enabled.IsOn;
      EnabledStatus = IsEnabled ? "● Live Testing ON" : "○ Live Testing OFF";
      if (state.LastSummary != null)
        OnSummaryChanged(null, state.LastSummary.Value);

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
      if (IsEnabled)
        await client.DisableLiveTestingAsync(ct);
      else
        await client.EnableLiveTestingAsync(ct);
      IsEnabled = !IsEnabled;
      EnabledStatus = IsEnabled ? "● Live Testing ON" : "○ Live Testing OFF";
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine(
        $"[SageFs] {nameof(LiveTestingData)}.Toggle: {ex.GetType().Name}: {ex.Message}");
    }
  }

  private async Task RunAllAsync(object? parameter, CancellationToken ct)
  {
    try
    {
      SummaryText = "⟳ Running all tests...";
      await client.RunTestsAsync("", ct);
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine(
        $"[SageFs] {nameof(LiveTestingData)}.RunAll: {ex.GetType().Name}: {ex.Message}");
    }
  }

  private Task CycleFilterAsync(object? parameter, CancellationToken ct)
  {
    currentFilter = Core.TestTreeViewModel.nextFilter(currentFilter);
    FilterLabel = Core.TestTreeViewModel.filterLabel(currentFilter);
    UpdateTestResults();
    return Task.CompletedTask;
  }

  private Task ClearSearchAsync(object? parameter, CancellationToken ct)
  {
    SearchQuery = "";
    return Task.CompletedTask;
  }

  private Task ToggleRunPolicySectionAsync(object? parameter, CancellationToken ct)
  {
    RunPolicySectionVisible = !RunPolicySectionVisible;
    return Task.CompletedTask;
  }

  private async Task ApplyRunPolicyAsync(object? parameter, CancellationToken ct)
  {
    try
    {
      // Convert display name → DU → API string via typed overload
      var catDu = Core.TestCategory.All
        .FirstOrDefault(c => Core.TestCategory.DisplayName(c) == SelectedCategory);
      var polDu = Core.RunPolicy.All
        .FirstOrDefault(p => Core.RunPolicy.DisplayName(p) == SelectedPolicy);
      await client.SetRunPolicyAsync(catDu, polDu, ct);
      SummaryText = $"⚙ {SelectedCategory}: {SelectedPolicy}";
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine(
        $"[SageFs] {nameof(LiveTestingData)}.ApplyRunPolicy: {ex.GetType().Name}: {ex.Message}");
    }
  }

  public void Dispose()
  {
    subscriber.StateChanged -= OnStateChanged;
    subscriber.SummaryChanged -= OnSummaryChanged;
  }
}
