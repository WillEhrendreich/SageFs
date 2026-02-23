namespace SageFs.VisualStudio.Commands;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

[VisualStudioContribution]
internal class CreateSessionCommand : Command
{
  private readonly Core.SageFsClient client;
  public CreateSessionCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.CreateSession.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.AddItem, IconSettings.IconAndText),
  };

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    await client.CreateSessionAsync(ct);
  }
}

[VisualStudioContribution]
internal class SwitchSessionCommand : Command
{
  private readonly Core.SageFsClient client;
  public SwitchSessionCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.SwitchSession.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.SwitchSourceOrTarget, IconSettings.IconAndText),
  };

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    await client.SwitchSessionAsync(ct);
  }
}

[VisualStudioContribution]
internal class ResetSessionCommand : Command
{
  private readonly Core.SageFsClient client;
  public ResetSessionCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.ResetSession.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.Restart, IconSettings.IconAndText),
  };

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    await client.ResetSessionAsync(false, ct);
  }
}

[VisualStudioContribution]
internal class HardResetCommand : Command
{
  private readonly Core.SageFsClient client;
  public HardResetCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.HardReset.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.Refresh, IconSettings.IconAndText),
  };

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    await client.ResetSessionAsync(true, ct);
  }
}
