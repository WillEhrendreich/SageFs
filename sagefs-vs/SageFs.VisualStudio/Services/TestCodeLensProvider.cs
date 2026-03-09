namespace SageFs.VisualStudio.Services;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Control;
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
        new TestStatusCodeLens(subscriber));
    }

    return Task.FromResult<CodeLens?>(null);
  }
}

/// <summary>
/// Displays live test result status above test functions.
/// Updates in real-time from SSE subscription.
/// Subscribes once to StateChanged in the constructor and calls Invalidate()
/// so VS re-requests the label whenever test results arrive.
/// </summary>
internal class TestStatusCodeLens : CodeLens
{
  private readonly Core.LiveTestingSubscriber subscriber;
  private readonly FSharpHandler<Core.LiveTestState> stateChangedHandler;

  public TestStatusCodeLens(Core.LiveTestingSubscriber subscriber)
  {
    this.subscriber = subscriber;
    stateChangedHandler = (_, _) => Invalidate();
    subscriber.StateChanged += stateChangedHandler;
  }

  public override void Dispose()
  {
    subscriber.StateChanged -= stateChangedHandler;
  }

  public override Task<CodeLensLabel> GetLabelAsync(
    CodeElementContext codeElementContext, CancellationToken token)
  {
    var state = subscriber.CurrentState;
    var range = codeElementContext.Range;
    var doc = range.Document;

    // Convert character offset to 1-based line number.
    // GetLineNumberFromPosition returns a 0-based index; test frameworks
    // (Expecto, NUnit) and the SageFs daemon use 1-based source locations.
    var filePath = doc.Uri.LocalPath;
    var line = doc.GetLineNumberFromPosition(range.Start.Offset) + 1;

    var found = Core.LiveTestingSubscriber.findTestAtLine(state, filePath, line);
    if (found.IsValueNone)
    {
      return Task.FromResult(new CodeLensLabel
      {
        Text = "● SageFs",
        Tooltip = "SageFs — waiting for test discovery",
      });
    }

    var pair = found.Value;
    var info = pair.Item1;
    var testResult = pair.Item2;
    var text = Core.LiveTestingSubscriber.formatTestLabel(info, testResult);
    var tooltip = Core.LiveTestingSubscriber.formatTestTooltip(
      info, testResult, FSharpOption<Core.ResultFreshness>.None);

    return Task.FromResult(new CodeLensLabel
    {
      Text = text,
      Tooltip = tooltip,
    });
  }
}

#pragma warning restore VSEXTPREVIEW_CODELENS
