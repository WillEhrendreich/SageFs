namespace SageFs.VisualStudio.Commands;

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Shell;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

[VisualStudioContribution]
internal class LiveTestingCommand : Command
{
  private readonly Core.SageFsClient client;
  private readonly Core.LiveTestingSubscriber subscriber;
  private OutputChannel? output;

  public LiveTestingCommand(Core.SageFsClient client, Core.LiveTestingSubscriber subscriber)
  {
    this.client = client;
    this.subscriber = subscriber;
  }

  public override CommandConfiguration CommandConfiguration => new("%SageFs.LiveTesting.DisplayName%")
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
    bool wasEnabled = subscriber.CurrentState.Enabled.IsOn;
    bool enabled;
    if (wasEnabled)
      enabled = await client.DisableLiveTestingAsync(ct);
    else
      enabled = await client.EnableLiveTestingAsync(ct);
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

[VisualStudioContribution]
internal class SetRunPolicyCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  private static readonly string[] Categories =
    Core.TestCategory.All.Select(c => c.ToApiString()).ToArray();
  private static readonly string[] PolicyLabels =
    Core.RunPolicy.All.Select(p => Core.RunPolicy.DisplayName(p)).ToArray();

  private int catIndex;

  public SetRunPolicyCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.SetRunPolicy.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.Settings, IconSettings.IconAndText),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    var cat = Categories[catIndex % Categories.Length];
    await client.SetRunPolicyAsync(cat, "every", ct);

    if (output is not null)
      await output.WriteLineAsync($"⚙ {cat}: On every change — open Live Testing window to configure all policies");

    catIndex = (catIndex + 1) % Categories.Length;
  }
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
