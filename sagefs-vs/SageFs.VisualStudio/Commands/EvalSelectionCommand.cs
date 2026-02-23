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
  private OutputChannel? output;

  public EvalSelectionCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.EvalSelection.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.PlayStepGroup, IconSettings.IconAndText),
    Shortcuts = [new CommandShortcutConfiguration(ModifierKey.ControlShift, Key.Enter)],
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

    var selection = textView.Selection;
    string code;
    if (!selection.IsEmpty)
    {
      code = selection.Extent.CopyToString();
    }
    else
    {
      code = textView.Document.Text.CopyToString();
    }

    var result = await client.EvalAsync(code, ct);
    if (output is not null)
    {
      await output.WriteLineAsync(result);
    }
  }
}
#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
