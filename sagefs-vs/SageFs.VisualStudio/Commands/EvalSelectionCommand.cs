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
internal class EvalSelectionCommand : Command
{
  private readonly Core.SageFsClient client;
  private readonly Core.EvalCancellation cancellation;
  private OutputChannel? output;

  public EvalSelectionCommand(Core.SageFsClient client, Core.EvalCancellation cancellation)
  {
    this.client = client;
    this.cancellation = cancellation;
  }

  public override CommandConfiguration CommandConfiguration => new("%SageFs.EvalSelection.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.PlayStepGroup, IconSettings.IconAndText),
    Shortcuts = [new CommandShortcutConfiguration(ModifierKey.LeftAlt, Key.Enter)],
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

    var filePath = textView.Document.Uri.LocalPath;
    var selection = textView.Selection;
    string code;
    int startLine = 0;
    if (!selection.IsEmpty)
    {
      code = selection.Extent.CopyToString();
      startLine = selection.Extent.Start.GetContainingLine().LineNumber + 1; // 0-based → 1-based
    }
    else
    {
      var allText = textView.Document.Text.CopyToString();
      var lines = allText.Split('\n');
      var cursorLine = textView.Selection.ActivePosition.GetContainingLine().LineNumber; // 0-based
      var (blockStart, blockEnd) = EvalBlockCommand.FindBlock(lines, cursorLine);
      var blockLines = lines[blockStart..(blockEnd + 1)];
      code = string.Join("\n", blockLines);
      startLine = blockStart + 1; // 0-based → 1-based
    }

    if (output is not null)
    {
      await output.WriteLineAsync($"▶ Evaluating ({code.Length} chars)...");
    }

    using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
      cancellation.StartNew(), ct);
    try
    {
      var result = await client.EvalWithContextAsync(code, filePath, "block", startLine, linked.Token);
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
    finally { cancellation.Done(); }
  }
}
#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
