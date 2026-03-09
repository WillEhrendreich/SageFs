namespace SageFs.VisualStudio.Services;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable VSEXTPREVIEW_CODELENS
#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

/// <summary>
/// Provides "▶ Eval" CodeLens on F# functions and methods.
/// Clicking evaluates the code element's body via the injected SageFsClient singleton.
/// The client is registered as a singleton in SageFsExtension.cs — this provider never
/// creates its own HttpClient, avoiding socket exhaustion under heavy editing.
/// </summary>
[VisualStudioContribution]
internal class EvalCodeLensProvider : ExtensionPart, ICodeLensProvider
{
  private readonly Core.SageFsClient client;
  private readonly Core.EvalCancellation cancellation;

  public EvalCodeLensProvider(Core.SageFsClient client, Core.EvalCancellation cancellation)
  {
    this.client = client;
    this.cancellation = cancellation;
  }

  public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
  {
    AppliesTo =
    [
      DocumentFilter.FromGlobPattern("**/*.fs", true),
      DocumentFilter.FromGlobPattern("**/*.fsx", true),
    ],
  };

  public CodeLensProviderConfiguration CodeLensProviderConfiguration =>
    new("%SageFs.CodeLens.DisplayName%")
    {
      Priority = 200,
    };

  public Task<CodeLens?> TryCreateCodeLensAsync(
    CodeElement codeElement,
    CodeElementContext codeElementContext,
    CancellationToken token)
  {
    if (codeElement.Kind == CodeElementKind.KnownValues.Function
        || codeElement.Kind == CodeElementKind.KnownValues.Method
        || codeElement.Kind == CodeElementKind.KnownValues.Type
        || codeElement.Kind == CodeElementKind.KnownValues.Module)
    {
      return Task.FromResult<CodeLens?>(
        new EvalCodeLens(codeElement, client, cancellation));
    }

    return Task.FromResult<CodeLens?>(null);
  }
}

/// <summary>
/// An invokable CodeLens that evaluates the code element in SageFs.
/// Uses the shared SageFsClient singleton and EvalCancellation for cooperative cancellation.
/// </summary>
internal class EvalCodeLens : InvokableCodeLens
{
  private readonly CodeElement codeElement;
  private readonly Core.SageFsClient client;
  private readonly Core.EvalCancellation cancellation;
  private string lastResult = "";
  private bool isRunning = false;

  public EvalCodeLens(
    CodeElement codeElement,
    Core.SageFsClient client,
    Core.EvalCancellation cancellation)
  {
    this.codeElement = codeElement;
    this.client = client;
    this.cancellation = cancellation;
  }

  public override void Dispose() { }

  public override Task<CodeLensLabel> GetLabelAsync(
    CodeElementContext codeElementContext, CancellationToken token)
  {
    var text = isRunning
      ? $"⟳ Evaluating {codeElement.Description}…"
      : string.IsNullOrEmpty(lastResult)
        ? $"▶ Eval {codeElement.Description}"
        : $"✓ {lastResult}";

    var tooltip = isRunning
      ? "Evaluating — press Ctrl+Alt+C to cancel"
      : "Evaluate this code element in SageFs (Ctrl+Alt+Enter for block eval)";

    return Task.FromResult(new CodeLensLabel { Text = text, Tooltip = tooltip });
  }

  public override async Task ExecuteAsync(
    CodeElementContext codeElementContext,
    IClientContext clientContext,
    CancellationToken cancelToken)
  {
    var range = codeElementContext.Range;
    var code = range.CopyToString();
    if (string.IsNullOrWhiteSpace(code)) return;

    if (!code.TrimEnd().EndsWith(";;"))
      code += ";;";

    // Register with the shared cancellation so Ctrl+Alt+C can abort this eval.
    // Also link to the VS-provided cancelToken so VS can cancel us on shutdown.
    using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
      cancelToken, cancellation.StartNew());

    isRunning = true;
    Invalidate();
    try
    {
      var result = await client.EvalAsync(code, linked.Token);
      lastResult = result.ExitCode == 0
        ? (result.Output.Length > 60 ? result.Output[..60] + "…" : result.Output)
        : $"✗ Exit {result.ExitCode}";
    }
    catch (System.OperationCanceledException)
    {
      lastResult = "⊘ Cancelled";
    }
    finally
    {
      isRunning = false;
      cancellation.Done();
      Invalidate();
    }
  }
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
#pragma warning restore VSEXTPREVIEW_CODELENS
