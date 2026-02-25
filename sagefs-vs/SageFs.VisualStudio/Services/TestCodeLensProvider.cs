namespace SageFs.VisualStudio.Services;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

#pragma warning disable VSEXTPREVIEW_CODELENS

/// <summary>
/// Shows live test status (✓/✗/●) as CodeLens on test functions.
/// Subscribes to LiveTestingSubscriber for real-time updates.
/// </summary>
[VisualStudioContribution]
internal class TestCodeLensProvider : ExtensionPart, ICodeLensProvider
{
  private readonly Core.LiveTestingSubscriber subscriber;

  public TestCodeLensProvider(Core.LiveTestingSubscriber subscriber)
  {
    this.subscriber = subscriber;
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
    new("%SageFs.TestCodeLens.DisplayName%")
    {
      Priority = 100,
    };

  public Task<CodeLens?> TryCreateCodeLensAsync(
    CodeElement codeElement,
    CodeElementContext codeElementContext,
    CancellationToken token)
  {
    if (codeElement.Kind == CodeElementKind.KnownValues.Function
        || codeElement.Kind == CodeElementKind.KnownValues.Method)
    {
      return Task.FromResult<CodeLens?>(
        new TestStatusCodeLens(codeElement, subscriber));
    }

    return Task.FromResult<CodeLens?>(null);
  }
}

/// <summary>
/// Displays live test result status above test functions.
/// Updates in real-time from SSE subscription.
/// </summary>
internal class TestStatusCodeLens : CodeLens
{
  private readonly CodeElement codeElement;
  private readonly Core.LiveTestingSubscriber subscriber;

  public TestStatusCodeLens(
    CodeElement codeElement,
    Core.LiveTestingSubscriber subscriber)
  {
    this.codeElement = codeElement;
    this.subscriber = subscriber;
    subscriber.StateChanged += (_, _) => Invalidate();
  }

  public override void Dispose() { }

  public override Task<CodeLensLabel> GetLabelAsync(
    CodeElementContext codeElementContext, CancellationToken token)
  {
    var state = subscriber.CurrentState;
    // Match by code element description (function/method name)
    var name = codeElement.Description ?? "";
    var result = Core.LiveTestingSubscriber.findTestByName(state, name);
    if (result == null)
    {
      return Task.FromResult(new CodeLensLabel { Text = "", Tooltip = "" });
    }

    var (info, testResult) = result.Value;
    var text = Core.LiveTestingSubscriber.formatTestLabel(info, testResult);
    var tooltip = Core.LiveTestingSubscriber.formatTestTooltip(info, testResult);

    return Task.FromResult(new CodeLensLabel
    {
      Text = text,
      Tooltip = tooltip,
    });
  }
}

#pragma warning restore VSEXTPREVIEW_CODELENS
