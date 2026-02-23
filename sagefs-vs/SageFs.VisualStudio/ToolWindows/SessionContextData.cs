namespace SageFs.VisualStudio.ToolWindows;

using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;

[DataContract]
internal class SessionContextData : NotifyPropertyChangedObject
{
  private readonly VisualStudioExtensibility extensibility;
  private readonly Core.SageFsClient client;

  private string sessionInfo = "Loading...";
  private string assembliesInfo = "";
  private string namespacesInfo = "";
  private string failedOpensInfo = "";
  private string hotReloadInfo = "";
  private bool isLoading;

  public SessionContextData(VisualStudioExtensibility extensibility, Core.SageFsClient client)
  {
    this.extensibility = extensibility;
    this.client = client;
    this.RefreshCommand = new AsyncCommand(this.RefreshAsync);

    _ = RefreshAsync(null, CancellationToken.None);
  }

  [DataMember] public IAsyncCommand RefreshCommand { get; }

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
      var sessions = (await client.GetSessionsAsync(ct)).ToList();
      if (sessions.Count == 0)
      {
        SessionInfo = "No sessions active. Start SageFs to begin.";
        AssembliesInfo = "";
        NamespacesInfo = "";
        FailedOpensInfo = "";
        HotReloadInfo = "";
        return;
      }

      var active = sessions[0];
      var projects = string.Join(", ", active.ProjectNames);
      SessionInfo = $"Session: {active.Id}\nStatus: {active.State}\nProjects: {projects}\nEvals: {active.EvalCount}\nDir: {active.WorkingDirectory}";

      var warmup = await client.GetWarmupContextAsync(active.Id, ct);
      if (warmup != null)
      {
        var w = warmup.Value;
        var asmList = w.AssembliesLoaded.ToList();
        var asmLines = asmList
          .Select(a => $"  {a.Name} ({a.NamespaceCount} ns, {a.ModuleCount} mod)")
          .ToList();
        AssembliesInfo = $"Assemblies ({asmList.Count} loaded, {w.WarmupDurationMs}ms warmup):\n" +
          string.Join("\n", asmLines);

        var nsList = w.NamespacesOpened.ToList();
        var nsLines = nsList
          .Select(n => $"  {n.Name} ({(n.IsModule ? "module" : "namespace")} via {n.Source})")
          .ToList();
        NamespacesInfo = $"Namespaces ({nsList.Count} opened):\n" +
          string.Join("\n", nsLines);

        var failedList = w.FailedOpens.ToList();
        if (failedList.Count > 0)
        {
          var failLines = failedList
            .Select(f => "  ✗ " + string.Join(" → ", f))
            .ToList();
          FailedOpensInfo = $"Failed Opens ({failedList.Count}):\n" + string.Join("\n", failLines);
        }
        else
        {
          FailedOpensInfo = "No failed opens ✓";
        }
      }
      else
      {
        AssembliesInfo = "Warmup context not available";
        NamespacesInfo = "";
        FailedOpensInfo = "";
      }

      var hotReload = await client.GetHotReloadStateAsync(active.Id, ct);
      if (hotReload != null)
      {
        var hr = hotReload.Value;
        var fileList = hr.Files.ToList();
        var lines = fileList
          .Select(f => $"  {(f.Watched ? "●" : "○")} {f.Path}")
          .ToList();
        HotReloadInfo = $"Hot Reload ({hr.WatchedCount}/{fileList.Count} watched):\n" +
          string.Join("\n", lines);
      }
      else
      {
        HotReloadInfo = "Hot reload not available";
      }
    }
    catch (Exception ex)
    {
      SessionInfo = $"Error: {ex.Message}";
    }
    finally
    {
      IsLoading = false;
    }
  }
}
