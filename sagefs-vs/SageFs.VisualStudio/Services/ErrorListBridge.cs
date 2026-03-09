namespace SageFs.VisualStudio.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Languages;
using Microsoft.VisualStudio.RpcContracts.DiagnosticManagement;
using RpcRange = Microsoft.VisualStudio.RpcContracts.Utilities.Range;

#pragma warning disable VSEXTPREVIEW_DIAGNOSTICS

/// <summary>
/// Bridges SageFs DiagnosticsSubscriber SSE events to the VS Error List
/// via the new extensibility DiagnosticsReporter API.
/// </summary>
internal class ErrorListBridge : IDisposable
{
  private readonly VisualStudioExtensibility extensibility;
  private DiagnosticsReporter? reporter;
  private Core.DiagnosticsSubscriber? subscriber;
  private readonly Dictionary<string, List<DocumentDiagnostic>> pendingDiagnostics = new();

  public ErrorListBridge(VisualStudioExtensibility extensibility)
  {
    this.extensibility = extensibility;
  }

  public void Start(int port)
  {
    reporter = extensibility.Languages().GetDiagnosticsReporter("SageFs");
    subscriber = new Core.DiagnosticsSubscriber(port);

    subscriber.Start((file, message, startLine, startCol, endLine, endCol, severity) =>
    {
      if (string.IsNullOrEmpty(file)) return;

      var uri = new Uri(file, UriKind.RelativeOrAbsolute);
      if (!uri.IsAbsoluteUri)
        uri = new Uri(System.IO.Path.GetFullPath(file));

      var range = new RpcRange(
        Math.Max(0, startLine - 1),
        Math.Max(0, startCol - 1),
        Math.Max(0, endLine - 1),
        Math.Max(0, endCol - 1));

      var diag = new DocumentDiagnostic(uri, range, message)
      {
        Severity = severity switch
        {
          "error" => DiagnosticSeverity.Error,
          "warning" => DiagnosticSeverity.Warning,
          "info" => DiagnosticSeverity.Information,
          _ => DiagnosticSeverity.Hint,
        },
        ErrorCode = "FS",
        ProviderName = "SageFs",
      };

      lock (pendingDiagnostics)
      {
        var key = uri.AbsoluteUri;
        if (!pendingDiagnostics.ContainsKey(key))
          pendingDiagnostics[key] = new List<DocumentDiagnostic>();
        pendingDiagnostics[key].Add(diag);
      }

      _ = FlushAsync();
    });
  }

  private async Task FlushAsync()
  {
    if (reporter is null) return;

    List<DocumentDiagnostic> batch;
    lock (pendingDiagnostics)
    {
      batch = pendingDiagnostics.Values.SelectMany(d => d).ToList();
      pendingDiagnostics.Clear();
    }

    if (batch.Count > 0)
    {
      try
      {
        await reporter.ReportDiagnosticsAsync(batch, CancellationToken.None);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine(
          $"[SageFs] {nameof(ErrorListBridge)}: diagnostics flush error: {ex.GetType().Name}: {ex.Message}");
      }
    }
  }

  public void Stop()
  {
    subscriber?.Stop();
    subscriber = null;
  }

  public void Dispose()
  {
    Stop();
    reporter?.Dispose();
  }
}

#pragma warning restore VSEXTPREVIEW_DIAGNOSTICS
