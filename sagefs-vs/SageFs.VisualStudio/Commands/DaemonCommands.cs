namespace SageFs.VisualStudio.Commands;

using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Shell;

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

    // Try to find F# projects in the solution directory
    var solutionDir = Directory.GetCurrentDirectory();
    var fsproj = Directory.GetFiles(solutionDir, "*.fsproj", SearchOption.AllDirectories)
      .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\"))
      .ToArray();
    var slnx = Directory.GetFiles(solutionDir, "*.slnx", SearchOption.TopDirectoryOnly);
    var sln = Directory.GetFiles(solutionDir, "*.sln", SearchOption.TopDirectoryOnly);

    string? target = null;
    if (slnx.Length > 0)
      target = slnx[0];
    else if (sln.Length > 0)
      target = sln[0];
    else if (fsproj.Length == 1)
      target = fsproj[0];
    else if (fsproj.Length > 1)
    {
      // Prefer test projects
      var testProj = fsproj.FirstOrDefault(f => f.Contains("Test", System.StringComparison.OrdinalIgnoreCase));
      target = testProj ?? fsproj[0];
    }

    if (target is null)
    {
      await Extensibility.Shell().ShowPromptAsync(
        "No F# projects found in the workspace. Open a folder with .fsproj files first.",
        PromptOptions.OK, ct);
      return;
    }

    if (output is not null)
      await output.WriteLineAsync($"▶ Starting SageFs with: {Path.GetFileName(target)}");

    var result = Core.DaemonManager.startDaemon(target);
    if (result.IsOk)
    {
      if (output is not null)
        await output.WriteLineAsync($"✓ SageFs daemon started (PID: {result.ResultValue})");
    }
    else
    {
      await Extensibility.Shell().ShowPromptAsync(
        $"Failed: {result.ErrorValue}", PromptOptions.OK, ct);
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
    Core.DaemonManager.OpenDashboard(client.DashboardPort);
    return Task.CompletedTask;
  }
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
