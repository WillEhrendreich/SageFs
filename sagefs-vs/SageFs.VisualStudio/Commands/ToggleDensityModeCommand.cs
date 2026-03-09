namespace SageFs.VisualStudio.Commands;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

/// <summary>
/// Cycles through density modes: Full → Normal → Minimal → Full.
/// Persists the selection to %LOCALAPPDATA%\SageFs\density-mode.txt,
/// which the net472 MEF layer reads via <c>DensityModeState.CurrentMode</c>.
/// </summary>
[VisualStudioContribution]
internal class ToggleDensityModeCommand : Command
{
  public override CommandConfiguration CommandConfiguration =>
    new("%SageFs.ToggleDensity.DisplayName%")
    {
      Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
      Icon = new(ImageMoniker.KnownValues.ToggleAllBreakpoints, IconSettings.IconAndText),
    };

  public override Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    var current = ReadMode();
    var next = current switch
    {
      Options.DensityMode.Full    => Options.DensityMode.Normal,
      Options.DensityMode.Normal  => Options.DensityMode.Minimal,
      _                           => Options.DensityMode.Full,
    };
    WriteMode(next);
    return Task.CompletedTask;
  }

  private static Options.DensityMode ReadMode()
  {
    try
    {
      var text = File.ReadAllText(ModeFile()).Trim();
      return Enum.TryParse<Options.DensityMode>(text, out var mode) ? mode : Options.DensityMode.Normal;
    }
    catch { return Options.DensityMode.Normal; }
  }

  private static void WriteMode(Options.DensityMode mode)
  {
    try
    {
      var dir = Path.GetDirectoryName(ModeFile())!;
      Directory.CreateDirectory(dir);
      File.WriteAllText(ModeFile(), mode.ToString());
    }
    catch { }
  }

  private static string ModeFile() =>
    Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "SageFs", "density-mode.txt");
}
