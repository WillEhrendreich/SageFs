namespace SageFs.VisualStudio.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Languages;
using Microsoft.VisualStudio.RpcContracts.DiagnosticManagement;
using RpcRange = Microsoft.VisualStudio.RpcContracts.Utilities.Range;

#pragma warning disable VSEXTPREVIEW_DIAGNOSTICS

/// <summary>
/// Routes live test failures to the VS Error List via DiagnosticsReporter.
/// Failed tests appear as warnings alongside compiler errors.
/// </summary>
internal class TestDiagnosticsBridge : IDisposable
{
  private readonly VisualStudioExtensibility extensibility;
  private readonly Core.LiveTestingSubscriber subscriber;
  private DiagnosticsReporter? reporter;

  public TestDiagnosticsBridge(
    VisualStudioExtensibility extensibility,
    Core.LiveTestingSubscriber subscriber)
  {
    this.extensibility = extensibility;
    this.subscriber = subscriber;
  }

  public void Start()
  {
    reporter = extensibility.Languages().GetDiagnosticsReporter("SageFs.Tests");
    subscriber.StateChanged += OnStateChanged;
  }

  private void OnStateChanged(object? sender, Core.LiveTestState state)
  {
    _ = UpdateDiagnosticsAsync(state);
  }

  private async Task UpdateDiagnosticsAsync(Core.LiveTestState state)
  {
    if (reporter is null) return;

    var diagnostics = new List<DocumentDiagnostic>();

    foreach (var kvp in state.Results)
    {
      var result = kvp.Value;
      var outcome = result.Outcome;

      // Only report failures
      if (!outcome.IsFailed && !outcome.IsErrored) continue;

      var message = "Test failed";
      if (outcome.IsFailed)
      {
        var failed = (Core.TestOutcome.Failed)outcome;
        message = $"Test failed: {failed.message}";
      }
      else if (outcome.IsErrored)
      {
        var errored = (Core.TestOutcome.Errored)outcome;
        message = $"Test error: {errored.message}";
      }

      // Find test info for file/line
      var testInfo = state.Tests.TryFind(kvp.Key);
      if (FSharpOption<Core.TestInfo>.get_IsNone(testInfo)) continue;

      var info = testInfo.Value;
      if (FSharpOption<string>.get_IsNone(info.FilePath)) continue;

      var file = info.FilePath.Value;
      var line = FSharpOption<int>.get_IsSome(info.Line) ? info.Line.Value : 1;
      var uri = new Uri(file, UriKind.RelativeOrAbsolute);
      if (!uri.IsAbsoluteUri)
        uri = new Uri(System.IO.Path.GetFullPath(file));

      var range = new RpcRange(Math.Max(0, line - 1), 0, Math.Max(0, line - 1), 0);
      diagnostics.Add(new DocumentDiagnostic(uri, range, message)
      {
        Severity = DiagnosticSeverity.Warning,
        ErrorCode = "SageFsTest",
        ProviderName = "SageFs Live Tests",
      });
    }

    try
    {
      if (diagnostics.Count > 0)
        await reporter.ReportDiagnosticsAsync(diagnostics, CancellationToken.None);
    }
    catch
    {
      // Best effort
    }
  }

  public void Stop()
  {
    subscriber.StateChanged -= OnStateChanged;
  }

  public void Dispose()
  {
    Stop();
    reporter?.Dispose();
  }
}

#pragma warning restore VSEXTPREVIEW_DIAGNOSTICS
