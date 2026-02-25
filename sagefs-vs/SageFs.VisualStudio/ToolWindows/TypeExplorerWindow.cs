namespace SageFs.VisualStudio.ToolWindows;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

[VisualStudioContribution]
internal class TypeExplorerWindow : ToolWindow
{
  private readonly Core.SageFsClient client;
  private TypeExplorerData? dataContext;

  public TypeExplorerWindow(Core.SageFsClient client)
  {
    this.client = client;
    this.Title = "SageFs Type Explorer";
  }

  public override ToolWindowConfiguration ToolWindowConfiguration => new()
  {
    Placement = ToolWindowPlacement.DocumentWell,
  };

  public override Task InitializeAsync(CancellationToken ct)
  {
    dataContext = new TypeExplorerData(Extensibility, client);
    return Task.CompletedTask;
  }

  public override Task<IRemoteUserControl> GetContentAsync(CancellationToken ct)
  {
    return Task.FromResult<IRemoteUserControl>(new TypeExplorerControl(dataContext));
  }
}

[VisualStudioContribution]
internal class ShowTypeExplorerCommand : Command
{
  public override CommandConfiguration CommandConfiguration => new("%SageFs.ShowTypeExplorer.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.ClassPublic, IconSettings.IconAndText),
  };

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    await Extensibility.Shell().ShowToolWindowAsync<TypeExplorerWindow>(activate: true, ct);
  }
}
