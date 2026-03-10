namespace SageFs.VisualStudio.Commands;

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Shell;
using SageFs.VisualStudio.Services;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

[VisualStudioContribution]
internal class StartDaemonCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public StartDaemonCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.StartDaemon.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.Play, IconSettings.IconAndText),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    // Check if daemon is already running
    var alive = await client.PingAsync(ct);
    if (alive)
    {
      if (output is not null)
        await output.WriteLineAsync("✓ SageFs daemon is already running.");
      return;
    }

    var solutionDir = await SolutionDirectory.GetAsync(Extensibility, ct);
    if (solutionDir is null)
    {
      if (output is not null)
        await output.WriteLineAsync("✗ No solution is open. Open a solution first, then start the daemon.");
      return;
    }

    var scanResult = Core.DaemonTargetFinder.findTargetWithCandidates(solutionDir);

    string target;
    if (scanResult.IsNoTargets)
    {
      var noTargets = (Core.DaemonTargetFinder.TargetScanResult.NoTargets)scanResult;
      if (output is not null)
        await output.WriteLineAsync($"✗ {noTargets.message}");
      return;
    }
    else if (scanResult.IsSingleTarget)
    {
      target = ((Core.DaemonTargetFinder.TargetScanResult.SingleTarget)scanResult).Item;
    }
    else
    {
      // Multiple targets found — prompt user to confirm default choice
      var multi = (Core.DaemonTargetFinder.TargetScanResult.MultipleTargets)scanResult;
      var chosen = multi.chosen;
      var all = multi.all;

      if (output is not null)
      {
        await output.WriteLineAsync($"Multiple F# projects found ({all.Length}):");
        for (int i = 0; i < all.Length; i++)
          await output.WriteLineAsync($"  [{i + 1}] {Path.GetFileName(all[i])}");
      }

      var defaultName = Path.GetFileName(chosen);
      var confirmed = await Extensibility.Shell().ShowPromptAsync(
        $"Multiple projects found. Start SageFs with {defaultName}?",
        PromptOptions.OKCancel, ct);
      if (!confirmed) return;
      target = chosen;
    }

    if (output is not null)
      await output.WriteLineAsync($"▶ Starting SageFs with: {Path.GetFileName(target)}");

    var result = Core.DaemonManager.startDaemon(target);
    if (result.IsOk)
    {
      var proc = result.ResultValue;
      if (output is not null)
        await output.WriteLineAsync($"✓ SageFs daemon started (PID: {proc.Id})");
    }
    else
    {
      if (output is not null)
        await output.WriteLineAsync($"✗ Failed to start daemon: {result.ErrorValue}");
    }
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
    Core.DaemonManager.openDashboard(client.DashboardPort);
    return Task.CompletedTask;
  }
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
