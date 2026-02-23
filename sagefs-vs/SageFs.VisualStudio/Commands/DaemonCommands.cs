namespace SageFs.VisualStudio.Commands;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

[VisualStudioContribution]
internal class StartDaemonCommand : Command
{
  private readonly Core.SageFsClient client;
  public StartDaemonCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.StartDaemon.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.Play, IconSettings.IconAndText),
  };

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    await client.StartDaemonAsync(ct);
  }
}

[VisualStudioContribution]
internal class StopDaemonCommand : Command
{
  private readonly Core.SageFsClient client;
  public StopDaemonCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.StopDaemon.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.Stop, IconSettings.IconAndText),
  };

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    await client.StopDaemonAsync(ct);
  }
}

[VisualStudioContribution]
internal class OpenDashboardCommand : Command
{
  private readonly Core.SageFsClient client;
  public OpenDashboardCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.OpenDashboard.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.Web, IconSettings.IconAndText),
  };

  public override Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    Core.DaemonManager.OpenDashboard(client.DashboardPort);
    return Task.CompletedTask;
  }
}
