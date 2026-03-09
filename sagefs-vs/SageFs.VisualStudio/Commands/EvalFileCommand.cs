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
  private readonly Core.EvalCancellation cancellation;
  private OutputChannel? output;

  public EvalFileCommand(Core.SageFsClient client, Core.EvalCancellation cancellation)
  {
    this.client       = client;
    this.cancellation = cancellation;
  }

  public override CommandConfiguration CommandConfiguration => new("%SageFs.EvalFile.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.FSFileNode, IconSettings.IconAndText),
    Shortcuts = [new CommandShortcutConfiguration(ModifierKey.ShiftLeftAlt, Key.Enter)],
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
    if (textView is null) return;

    var text     = textView.Document.Text.CopyToString();
    var filePath = textView.Document.Uri.LocalPath;
    var blocks   = BlockHelpers.FindAllBlocks(text);

    if (blocks.Count == 0)
    {
      if (output is not null)
        await output.WriteLineAsync("○ No blocks found in file");
      return;
    }

    if (output is not null)
      await output.WriteLineAsync($"▶ Evaluating {blocks.Count} block(s) in {System.IO.Path.GetFileName(filePath)}…");

    using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
      ct, cancellation.StartNew());

    var evaluated = 0;
    try
    {
      for (var i = 0; i < blocks.Count; i++)
      {
        linked.Token.ThrowIfCancellationRequested();

        if (output is not null)
          await output.WriteLineAsync($"  Evaluating block {i + 1}/{blocks.Count}…");

        var result = await client.EvalWithContextAsync(blocks[i], filePath, "block", 0, linked.Token);
        evaluated++;

        if (result.ExitCode != 0)
        {
          if (output is not null)
          {
            await output.WriteLineAsync($"✗ Stopped at block {i + 1}: Exit {result.ExitCode}");
            if (!string.IsNullOrEmpty(result.Output))
              await output.WriteLineAsync($"  {result.Output.Trim()}");
            foreach (var diag in result.Diagnostics)
              await output.WriteLineAsync($"  ⚠ {diag}");
            await output.WriteLineAsync("───────────────────────────────────────");
          }
          return;
        }
      }

      if (output is not null)
      {
        await output.WriteLineAsync($"✓ Evaluated {evaluated}/{blocks.Count} blocks");
        await output.WriteLineAsync("───────────────────────────────────────");
      }
    }
    catch (OperationCanceledException)
    {
      if (output is not null)
        await output.WriteLineAsync($"⊘ Cancelled after {evaluated}/{blocks.Count} blocks");
    }
    finally { cancellation.Done(); }
  }
}
#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW

