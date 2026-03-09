namespace SageFs.VisualStudio.Commands;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Shell;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

/// <summary>
/// Loads the currently active .fsx or .fs file into the SageFs session via #load.
/// For .fsx scripts, this is equivalent to typing #load "file.fsx";; in the REPL.
/// For .fs source files, it compiles and loads the module into the session scope.
/// Equivalent to the SageFs TUI "Load Script" command (Ctrl+L in Neovim plugin).
/// </summary>
[VisualStudioContribution]
internal class LoadScriptCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public LoadScriptCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.LoadScript.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.Script, IconSettings.IconAndText),
    VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveEditorContentType, ".+"),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    using var textView = await context.GetActiveTextViewAsync(ct);
    if (textView is null)
    {
      if (output is not null)
        await output.WriteLineAsync("⚠ No active editor — open an .fs or .fsx file first");
      return;
    }

    var filePath = textView.Document.Uri.LocalPath;
    if (string.IsNullOrEmpty(filePath))
    {
      if (output is not null)
        await output.WriteLineAsync("⚠ File has no path — save it first");
      return;
    }

    // Build a #load directive, then submit it to the daemon as a cell eval
    var loadCode = $"#load \"{filePath}\";;";

    if (output is not null)
      await output.WriteLineAsync($"⟳ Loading {System.IO.Path.GetFileName(filePath)}…");

    var result = await client.EvalAsync(loadCode, ct);

    if (output is not null)
    {
      if (result.ExitCode == 0)
        await output.WriteLineAsync($"✓ Loaded {System.IO.Path.GetFileName(filePath)}");
      else
      {
        await output.WriteLineAsync($"✗ Load failed (exit {result.ExitCode})");
        if (!string.IsNullOrEmpty(result.Output))
          await output.WriteLineAsync(result.Output);
        foreach (var diag in result.Diagnostics)
          await output.WriteLineAsync($"  ⚠ {diag}");
      }
    }
  }
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
