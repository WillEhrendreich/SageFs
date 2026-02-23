namespace SageFs.VisualStudio.ToolWindows;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

[VisualStudioContribution]
internal class SessionContextWindow : ToolWindow
{
  private readonly Core.SageFsClient client;
  private SessionContextData? dataContext;

  public SessionContextWindow(Core.SageFsClient client)
  {
    this.client = client;
    this.Title = "SageFs Session Context";
  }

  public override ToolWindowConfiguration ToolWindowConfiguration => new()
  {
    Placement = ToolWindowPlacement.DocumentWell,
  };

  public override Task InitializeAsync(CancellationToken ct)
  {
    dataContext = new SessionContextData(Extensibility, client);
    return Task.CompletedTask;
  }

  public override Task<IRemoteUserControl> GetContentAsync(CancellationToken ct)
  {
    return Task.FromResult<IRemoteUserControl>(new SessionContextControl(dataContext));
  }
}

[VisualStudioContribution]
internal class ShowSessionContextCommand : Command
{
  public override CommandConfiguration CommandConfiguration => new("%SageFs.ShowSessionContext.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.StatusInformation, IconSettings.IconAndText),
  };

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    await Extensibility.Shell().ShowToolWindowAsync<SessionContextWindow>(activate: true, ct);
  }
}
