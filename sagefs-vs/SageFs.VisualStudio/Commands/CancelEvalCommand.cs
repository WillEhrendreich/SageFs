namespace SageFs.VisualStudio.Commands;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

/// <summary>
/// Cancels any in-flight evaluation immediately.
/// Bind to Ctrl+Alt+C for instant abort of runaway eval (infinite loops, long computations).
/// The cancel signal is cooperative — long-running .NET code may not honour it instantly,
/// but the FSI session will cancel at the next async yield point.
/// </summary>
[VisualStudioContribution]
internal class CancelEvalCommand : Command
{
  private readonly Core.EvalCancellation cancellation;

  public CancelEvalCommand(Core.EvalCancellation cancellation)
    => this.cancellation = cancellation;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.CancelEval.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.Cancel, IconSettings.IconAndText),
    Shortcuts = [new CommandShortcutConfiguration(ModifierKey.ControlLeftAlt, Key.C)],
    VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveEditorContentType, ".+"),
  };

  public override Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    cancellation.Cancel();
    return Task.CompletedTask;
  }
}
