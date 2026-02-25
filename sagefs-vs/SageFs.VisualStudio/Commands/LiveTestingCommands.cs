namespace SageFs.VisualStudio.Commands;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Shell;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

[VisualStudioContribution]
internal class ToggleLiveTestingCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public ToggleLiveTestingCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.ToggleLiveTesting.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.TestRun, IconSettings.IconAndText),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    var enabled = await client.ToggleLiveTestingAsync(ct);
    if (output is not null)
      await output.WriteLineAsync(enabled ? "✓ Live testing enabled" : "○ Live testing disabled");
  }
}

[VisualStudioContribution]
internal class RunTestsCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public RunTestsCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.RunTests.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.RunAll, IconSettings.IconAndText),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    await client.RunTestsAsync("", ct);
    if (output is not null)
      await output.WriteLineAsync("▶ Running all tests...");
  }
}

[VisualStudioContribution]
internal class ShowRecentEventsCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public ShowRecentEventsCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.ShowRecentEvents.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.History, IconSettings.IconAndText),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    var events = await client.GetRecentEventsAsync(30, ct);
    if (output is not null)
    {
      await output.WriteLineAsync("── Recent Events ──");
      await output.WriteLineAsync(events);
    }
  }
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
