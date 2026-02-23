namespace SageFs.VisualStudio.Commands;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Editor;

[VisualStudioContribution]
#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW
internal class EvalFileCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public EvalFileCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.EvalFile.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.FSFileNode, IconSettings.IconAndText),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    using var textView = await context.GetActiveTextViewAsync(ct);
    if (textView is null) return;

    var code = textView.Document.Text.CopyToString();

    if (output is not null)
    {
      await output.WriteLineAsync($"▶ Evaluating file ({code.Length} chars)...");
    }

    var result = await client.EvalAsync(code, ct);
    if (output is not null)
    {
      if (result.ExitCode == 0)
      {
        await output.WriteLineAsync($"✓ {result.Output}");
      }
      else
      {
        await output.WriteLineAsync($"✗ Exit code {result.ExitCode}");
        if (!string.IsNullOrEmpty(result.Output))
          await output.WriteLineAsync(result.Output);
        foreach (var diag in result.Diagnostics)
          await output.WriteLineAsync($"  ⚠ {diag}");
      }
      await output.WriteLineAsync("───────────────────────────────────────");
    }
  }
}
#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
