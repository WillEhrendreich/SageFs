namespace SageFs.VisualStudio.Commands;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Shell;
using SageFs.VisualStudio.Services;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

[Flags]
internal enum WarmupAutoOpenConfigStatus
{
  Created = 1,
  AlreadyDisabled = 2,
  RequiresManualEdit = 4,
}

internal static class WarmupAutoOpenConfig
{
  private const string Template = "{ DirectoryConfig.empty with\n  AutoOpenNamespaces = false\n}\n";

  public static (WarmupAutoOpenConfigStatus Status, string Path) Ensure(string workingDir)
  {
    var path = Path.Combine(workingDir, ".SageFs", "config.fsx");
    if (!File.Exists(path))
    {
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, Template);
      return (WarmupAutoOpenConfigStatus.Created, path);
    }

    var content = File.ReadAllText(path);
    return content.Contains("AutoOpenNamespaces = false", StringComparison.Ordinal)
      || content.Contains("AutoOpenNamespaces=false", StringComparison.Ordinal)
      ? (WarmupAutoOpenConfigStatus.AlreadyDisabled, path)
      : (WarmupAutoOpenConfigStatus.RequiresManualEdit, path);
  }

  public static void TryOpen(string path)
  {
    try
    {
      Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    catch
    {
      // Best-effort: still show the path in the prompt below.
    }
  }
}

[VisualStudioContribution]
internal class CreateSessionCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public CreateSessionCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.CreateSession.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.AddItem, IconSettings.IconAndText),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    await client.CreateSessionAsync(ct);
    if (output is not null)
      await output.WriteLineAsync("✓ Session created.");
  }
}

[VisualStudioContribution]
internal class ConfigureWarmupAutoOpenCommand : Command
{
  private OutputChannel? output;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.ConfigureWarmupAutoOpen.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.AddItem, IconSettings.IconAndText),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    var workingDir = await SolutionDirectory.GetAsync(Extensibility, ct);
    if (workingDir is null)
    {
      if (output is not null)
        await output.WriteLineAsync("✗ No solution is open. Open a solution first.");
      return;
    }

    var (status, path) = WarmupAutoOpenConfig.Ensure(workingDir);
    WarmupAutoOpenConfig.TryOpen(path);

    var message = status switch
    {
      WarmupAutoOpenConfigStatus.Created =>
        $"✓ Created {path} — AutoOpenNamespaces = false",
      WarmupAutoOpenConfigStatus.AlreadyDisabled =>
        $"○ Warmup auto-open already disabled in {path}",
      _ =>
        $"↗ Opened {path} — set AutoOpenNamespaces = false to disable (file not overwritten)",
    };

    if (output is not null)
      await output.WriteLineAsync(message);
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
      if (output is not null)
        await output.WriteLineAsync("○ No sessions available. Start SageFs first.");
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
      if (output is not null)
        await output.WriteLineAsync(ok ? $"✓ Switched to {switchTo.Item1}" : "✗ Failed to switch session.");
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
internal class StopSessionCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public StopSessionCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.StopSession.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.Stop, IconSettings.IconAndText),
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
      if (output is not null)
        await output.WriteLineAsync("○ No sessions available.");
      return;
    }

    if (output is not null)
    {
      await output.WriteLineAsync("Sessions:");
      for (int i = 0; i < choicesList.Count; i++)
        await output.WriteLineAsync($"  [{i + 1}] {choicesList[i].Item1}");
    }

    // Prompt to stop the first (or only) session
    var target = choicesList[0];
    var confirmed = await Extensibility.Shell().ShowPromptAsync(
      $"Stop session: {target.Item1}?",
      PromptOptions.OKCancel.WithCancelAsDefault(), ct);

    if (confirmed)
    {
      var ok = await client.StopSessionAsync(target.Item2, ct);
      if (output is not null)
        await output.WriteLineAsync(ok ? $"✗ Stopped {target.Item1}" : "✗ Failed to stop session.");
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
