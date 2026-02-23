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
    await Extensibility.Shell().ShowPromptAsync("Session created.", PromptOptions.OK, ct);
  }
}

[VisualStudioContribution]
internal class SwitchSessionCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public SwitchSessionCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.SwitchSession.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.SwitchSourceOrTarget, IconSettings.IconAndText),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    var choices = await client.GetSessionChoicesAsync(ct);
    var choicesList = choices.ToList();
    if (choicesList.Count == 0)
    {
      await Extensibility.Shell().ShowPromptAsync(
        "No sessions available. Start SageFs first.", PromptOptions.OK, ct);
      return;
    }

    if (choicesList.Count == 1)
    {
      if (output is not null)
        await output.WriteLineAsync($"Only one session active: {choicesList[0].Item1}");
      return;
    }

    // Show session list in output window and ask for confirmation
    if (output is not null)
    {
      await output.WriteLineAsync("Available sessions:");
      for (int i = 0; i < choicesList.Count; i++)
        await output.WriteLineAsync($"  [{i + 1}] {choicesList[i].Item1}");
    }

    // Use simple OK/Cancel to switch to the second session (most common case)
    var switchTo = choicesList.Count > 1 ? choicesList[1] : choicesList[0];
    var confirmed = await Extensibility.Shell().ShowPromptAsync(
      $"Switch to session: {switchTo.Item1}?",
      PromptOptions.OKCancel, ct);

    if (confirmed)
    {
      var ok = await client.SwitchToSessionAsync(switchTo.Item2, ct);
      if (!ok)
      {
        await Extensibility.Shell().ShowPromptAsync(
          $"Failed to switch session.", PromptOptions.OK, ct);
      }
      else if (output is not null)
      {
        await output.WriteLineAsync($"✓ Switched to {switchTo.Item1}");
      }
    }
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
    var confirmed = await Extensibility.Shell().ShowPromptAsync(
      "Reset the active FSI session? All definitions will be lost.",
      PromptOptions.OKCancel, ct);
    if (confirmed)
    {
      await client.ResetSessionAsync(false, ct);
    }
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
    var confirmed = await Extensibility.Shell().ShowPromptAsync(
      "Hard reset? This destroys the session and rebuilds DLLs.",
      PromptOptions.OKCancel.WithCancelAsDefault(), ct);
    if (confirmed)
    {
      await client.ResetSessionAsync(true, ct);
    }
  }
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
