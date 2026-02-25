namespace SageFs.VisualStudio.ToolWindows;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

[VisualStudioContribution]
internal class LiveTestingWindow : ToolWindow
{
  private readonly Core.SageFsClient client;
  private readonly Core.LiveTestingSubscriber subscriber;
  private LiveTestingData? dataContext;

  public LiveTestingWindow(Core.SageFsClient client, Core.LiveTestingSubscriber subscriber)
  {
    this.client = client;
    this.subscriber = subscriber;
    this.Title = "SageFs Live Testing";
  }

  public override ToolWindowConfiguration ToolWindowConfiguration => new()
  {
    Placement = ToolWindowPlacement.DocumentWell,
  };

  public override Task InitializeAsync(CancellationToken ct)
  {
    dataContext = new LiveTestingData(Extensibility, client, subscriber);
    return Task.CompletedTask;
  }

  public override Task<IRemoteUserControl> GetContentAsync(CancellationToken ct)
  {
    return Task.FromResult<IRemoteUserControl>(new LiveTestingControl(dataContext));
  }
}

[VisualStudioContribution]
internal class ShowLiveTestingCommand : Command
{
  public override CommandConfiguration CommandConfiguration => new("%SageFs.ShowLiveTesting.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.TestPass, IconSettings.IconAndText),
  };

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    await Extensibility.Shell().ShowToolWindowAsync<LiveTestingWindow>(activate: true, ct);
  }
}
