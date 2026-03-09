namespace SageFs.VisualStudio.ToolWindows;

using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;

[DataContract]
internal class TypeExplorerData : NotifyPropertyChangedObject, IDisposable
{
  private readonly VisualStudioExtensibility extensibility;
  private readonly Core.SageFsClient client;
  private readonly Core.LiveTestingSubscriber? subscriber;

  private string namespacesText = "Loading...";
  private string typesText = "";
  private string searchQuery = "";

  public TypeExplorerData(VisualStudioExtensibility extensibility, Core.SageFsClient client,
    Core.LiveTestingSubscriber? subscriber = null)
  {
    this.extensibility = extensibility;
    this.client = client;
    this.subscriber = subscriber;
    this.RefreshCommand = new AsyncCommand(this.RefreshAsync);
    this.SearchCommand = new AsyncCommand(this.SearchAsync);

    if (subscriber != null)
      subscriber.FeatureReceived += OnFeatureReceived;

    _ = RefreshAsync(null, CancellationToken.None);
  }

  [DataMember] public IAsyncCommand RefreshCommand { get; }
  [DataMember] public IAsyncCommand SearchCommand { get; }

  [DataMember]
  public string NamespacesText
  {
    get => namespacesText;
    set => SetProperty(ref namespacesText, value);
  }

  [DataMember]
  public string TypesText
  {
    get => typesText;
    set => SetProperty(ref typesText, value);
  }

  [DataMember]
  public string SearchQuery
  {
    get => searchQuery;
    set => SetProperty(ref searchQuery, value);
  }

  // ── SSE subscription ──────────────────────────────────────────────────────

  /// <summary>
  /// Returns true when the given SSE event type should trigger a TypeExplorer refresh.
  /// Exposed as a pure static method for testability.
  /// </summary>
  internal static bool ShouldRefreshOnEvent(string? eventType) =>
    eventType == "warmup_context_snapshot";

  private void OnFeatureReceived(object? sender, Core.FeatureEvent featureEvent)
  {
    if (featureEvent.IsWarmupContextSnapshot)
      _ = RefreshAsync(null, CancellationToken.None);
  }

  public void Dispose()
  {
    if (subscriber != null)
      subscriber.FeatureReceived -= OnFeatureReceived;
  }

  private async Task RefreshAsync(object? parameter, CancellationToken ct)
  {
    try
    {
      var sessions = (await client.GetSessionsAsync(ct)).ToList();
      if (sessions.Count == 0)
      {
        NamespacesText = "No active sessions.";
        TypesText = "";
        return;
      }

      var warmup = await client.GetWarmupContextAsync(sessions[0].Id, ct);
      if (warmup == null)
      {
        NamespacesText = "Warmup context not available.";
        return;
      }

      var w = warmup.Value;
      var nsList = w.NamespacesOpened.ToList();
      var nsLines = nsList
        .OrderBy(n => n.Name)
        .Select(n => $"  {(n.IsModule ? "📦" : "📁")} {n.Name}")
        .ToArray();
      NamespacesText = $"Namespaces ({nsList.Count}):\n" + string.Join("\n", nsLines);

      var asmList = w.AssembliesLoaded.ToList();
      var asmLines = asmList
        .OrderBy(a => a.Name)
        .Select(a => $"  📋 {a.Name} — {a.NamespaceCount} ns, {a.ModuleCount} mod")
        .ToArray();
      TypesText = $"Assemblies ({asmList.Count}):\n" + string.Join("\n", asmLines);
    }
    catch (Exception ex)
    {
      NamespacesText = $"Error: {ex.Message}";
    }
  }

  private async Task SearchAsync(object? parameter, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(SearchQuery))
    {
      await RefreshAsync(null, ct);
      return;
    }

    try
    {
      var sessions = (await client.GetSessionsAsync(ct)).ToList();
      if (sessions.Count == 0)
      {
        NamespacesText = "No active sessions.";
        return;
      }

      var warmup = await client.GetWarmupContextAsync(sessions[0].Id, ct);
      if (warmup == null) return;

      var w = warmup.Value;
      var query = SearchQuery.ToLowerInvariant();

      var matchedNs = w.NamespacesOpened
        .Where(n => n.Name.ToLowerInvariant().Contains(query))
        .Select(n => $"  {(n.IsModule ? "📦" : "📁")} {n.Name}")
        .ToArray();

      var matchedAsm = w.AssembliesLoaded
        .Where(a => a.Name.ToLowerInvariant().Contains(query))
        .Select(a => $"  📋 {a.Name}")
        .ToArray();

      NamespacesText = matchedNs.Length > 0
        ? $"Matching Namespaces ({matchedNs.Length}):\n" + string.Join("\n", matchedNs)
        : "No matching namespaces.";

      TypesText = matchedAsm.Length > 0
        ? $"Matching Assemblies ({matchedAsm.Length}):\n" + string.Join("\n", matchedAsm)
        : "No matching assemblies.";
    }
    catch (Exception ex)
    {
      NamespacesText = $"Error: {ex.Message}";
    }
  }
}
