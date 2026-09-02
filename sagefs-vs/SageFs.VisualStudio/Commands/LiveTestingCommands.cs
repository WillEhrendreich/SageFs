namespace SageFs.VisualStudio.Commands;

using System;
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
    var ok = await client.RunTestsAsync("", ct);
    if (output is not null)
      await output.WriteLineAsync(
        ok
          ? "▶ Running all tests..."
          : "✗ Failed to start test run — is the daemon running?");
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
  private readonly Core.LiveTestingSubscriber subscriber;
  private OutputChannel? output;

  private static readonly Core.RunPolicy[] Cycle =
    [Core.RunPolicy.EveryKeystroke, Core.RunPolicy.OnSave, Core.RunPolicy.OnDemand, Core.RunPolicy.Disabled];
  private static readonly string[] PolicyLabels =
    Core.RunPolicy.All.Select(p => Core.RunPolicy.DisplayName(p)).ToArray();
  private static readonly string[] CategoryLabels =
    Core.TestCategory.All.Select(c => Core.TestCategory.DisplayName(c)).ToArray();

  public SetRunPolicyCommand(Core.SageFsClient client, Core.LiveTestingSubscriber subscriber)
  {
    this.client = client;
    this.subscriber = subscriber;
  }

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

  /// <summary>
  /// Cycles ALL test categories to the next run policy in order:
  /// every → save → demand → disabled → every …
  ///
  /// The current policy is read from the live subscriber state so the first
  /// click always advances from wherever the daemon actually is.
  /// </summary>
  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    // Determine the "representative" current policy (most common across categories).
    var policies = subscriber.CurrentState.Policies;
    Core.RunPolicy currentPolicy;
    if (policies.IsEmpty)
    {
      currentPolicy = Core.RunPolicy.EveryKeystroke;
    }
    else
    {
      // Pick the most-common policy; tie-break by cycle order (first wins).
      currentPolicy = policies
        .Select(kv => kv.Value)
        .GroupBy(p => p)
        .OrderByDescending(g => g.Count())
        .ThenBy(g => Array.IndexOf(Cycle, g.Key))
        .First()
        .Key;
    }

    // Advance to next policy in the cycle.
    int idx = Array.IndexOf(Cycle, currentPolicy);
    var nextPolicy = Cycle[(idx + 1) % Cycle.Length];
    string nextLabel = Core.RunPolicy.DisplayName(nextPolicy);

    // Apply to all categories.
    var categories = Core.TestCategory.All;
    var tasks = categories.Select(cat => client.SetRunPolicyAsync(cat, nextPolicy, ct));
    await Task.WhenAll(tasks);

    if (output is not null)
    {
      await output.WriteLineAsync($"⚙ All test categories → {nextLabel}");
      await output.WriteLineAsync("   (unit · integration · browser · benchmark · architecture · property)");
      await output.WriteLineAsync("   Open the Live Testing window to fine-tune per-category policies.");
    }
  }
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
