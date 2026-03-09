using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

namespace SageFs.VisualStudio.Editor;

/// <summary>
/// Tracks FSI compilation diagnostics from the /diagnostics SSE endpoint.
/// Thread-safe. Feeds into <see cref="SquiggleTagger"/>.
///
/// JSON shape from daemon (/diagnostics SSE stream):
/// <code>
/// event: diagnostics
/// data: [
///   {
///     "codeHash": "abc123",
///     "diagnostics": [
///       {
///         "message": "The value or constructor 'x' is not defined",
///         "severity": "error",
///         "range": { "startLine": 3, "startColumn": 5, "endLine": 3, "endColumn": 6 }
///       }
///     ]
///   }
/// ]
/// </code>
/// </summary>
internal sealed class DiagnosticStateTracker
{
    // Maps startLine (1-based) → list of diagnostics at that line
    private volatile List<DiagnosticEntry> _current = new List<DiagnosticEntry>();

    public event EventHandler? StateChanged;

    public void ProcessEvent(SseEvent ev)
    {
        if (ev.Type != "diagnostics") return;
        ProcessDiagnosticsEvent(ev.Data);
    }

    private void ProcessDiagnosticsEvent(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return;

            var entries = new List<DiagnosticEntry>();
            foreach (var codeHashEl in root.EnumerateArray())
            {
                if (!codeHashEl.TryGetProperty("diagnostics", out var diags)) continue;
                foreach (var diag in diags.EnumerateArray())
                {
                    var message = diag.TryGetProperty("message", out var msgEl)
                        ? msgEl.GetString() ?? "" : "";
                    var severity = DiagnosticSeverity.Error;
                    if (diag.TryGetProperty("severity", out var sevEl))
                        severity = ParseSeverity(sevEl.GetString());

                    int startLine = 0, startCol = 0, endLine = 0, endCol = 0;
                    if (diag.TryGetProperty("range", out var range))
                    {
                        if (range.TryGetProperty("startLine", out var sl)) startLine = sl.GetInt32();
                        if (range.TryGetProperty("startColumn", out var sc)) startCol = sc.GetInt32();
                        if (range.TryGetProperty("endLine", out var el)) endLine = el.GetInt32();
                        if (range.TryGetProperty("endColumn", out var ec)) endCol = ec.GetInt32();
                    }

                    if (startLine > 0)
                        entries.Add(new DiagnosticEntry(message, severity, startLine, startCol, endLine, endCol));
                }
            }

            _current = entries;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[DiagnosticStateTracker] JSON parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[DiagnosticStateTracker] Unexpected error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public IReadOnlyList<DiagnosticEntry> GetDiagnosticsForLine(int line)
    {
        var all = _current;
        var result = new List<DiagnosticEntry>();
        foreach (var d in all)
            if (d.StartLine == line)
                result.Add(d);
        return result;
    }

    public IReadOnlyList<DiagnosticEntry> GetAll() => _current;

    private static DiagnosticSeverity ParseSeverity(string? s) =>
        s switch
        {
            "error"   => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            "info"    => DiagnosticSeverity.Info,
            "hidden"  => DiagnosticSeverity.Hidden,
            _         => DiagnosticSeverity.Error
        };
}

internal enum DiagnosticSeverity { Error, Warning, Info, Hidden }

internal readonly struct DiagnosticEntry
{
    public readonly string Message;
    public readonly DiagnosticSeverity Severity;
    public readonly int StartLine;
    public readonly int StartColumn;
    public readonly int EndLine;
    public readonly int EndColumn;

    public DiagnosticEntry(string message, DiagnosticSeverity severity,
        int startLine, int startColumn, int endLine, int endColumn)
    {
        Message = message;
        Severity = severity;
        StartLine = startLine;
        StartColumn = startColumn;
        EndLine = endLine;
        EndColumn = endColumn;
    }
}
