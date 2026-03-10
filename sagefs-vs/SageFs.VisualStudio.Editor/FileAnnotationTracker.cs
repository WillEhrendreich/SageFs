using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

namespace SageFs.VisualStudio.Editor;

/// <summary>
/// Tracks per-file inline failure annotations from the <c>file_annotations</c> SSE event.
///
/// JSON shape (matches <c>FileAnnotations</c> in SageFs.Core):
/// <code>
/// event: file_annotations
/// data: {
///   "FilePath": "C:/code/Tests.fs",
///   "TestAnnotations": [
///     { "Line": 10, "TestId": "...", "DisplayName": "myTest", "Status": "Failed", "Freshness": "Current" }
///   ],
///   "InlineFailures": [
///     {
///       "Line": 10,
///       "TestId": "...",
///       "TestName": "myTest",
///       "Duration": "00:00:00.034",
///       "Failure": {
///         "Case": "AssertionDiff",
///         "Fields": ["expected-value", "actual-value"]
///       }
///     }
///   ],
///   "CodeLenses": [...],
///   "CoverageAnnotations": [...]
/// }
/// </code>
/// </summary>
internal sealed class FileAnnotationTracker
{
    // Per-file: line → list of inline failure messages
    private readonly ConcurrentDictionary<string, Dictionary<int, List<InlineFailureDisplay>>>
        _byFile = new ConcurrentDictionary<string, Dictionary<int, List<InlineFailureDisplay>>>(StringComparer.OrdinalIgnoreCase);

    // Per-file: line → coverage health
    private readonly ConcurrentDictionary<string, Dictionary<int, CoverageHealth>>
        _coverageByFile = new ConcurrentDictionary<string, Dictionary<int, CoverageHealth>>(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<string>? FileAnnotationsUpdated; // arg = normalized filePath
    public event EventHandler<string>? CoverageUpdated;        // arg = normalized filePath

    public void ProcessEvent(SseEvent ev)
    {
        if (ev.Type != "file_annotations") return;
        ProcessFileAnnotations(ev.Data);
    }

    private void ProcessFileAnnotations(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("FilePath", out var fpEl)) return;
            var filePath = NormalizePath(fpEl.GetString() ?? "");
            if (string.IsNullOrEmpty(filePath)) return;

            var lineMap = new Dictionary<int, List<InlineFailureDisplay>>();

            if (root.TryGetProperty("InlineFailures", out var failures))
            {
                foreach (var f in failures.EnumerateArray())
                {
                    if (!f.TryGetProperty("Line", out var lineEl)) continue;
                    var line = lineEl.GetInt32();
                    var testName = f.TryGetProperty("TestName", out var tn)
                        ? tn.GetString() ?? "" : "";
                    var presentation = ParseFailurePresentation(f);

                    if (!lineMap.TryGetValue(line, out var list))
                    {
                        list = new List<InlineFailureDisplay>();
                        lineMap[line] = list;
                    }
                    list.Add(new InlineFailureDisplay(testName, presentation));
                }
            }

            _byFile[filePath] = lineMap;
            FileAnnotationsUpdated?.Invoke(this, filePath);

            // ── Coverage annotations ─────────────────────────────────────
            if (root.TryGetProperty("CoverageAnnotations", out var coverageAnns))
            {
                var coverageMap = new Dictionary<int, CoverageHealth>();
                foreach (var ann in coverageAnns.EnumerateArray())
                {
                    int covLine = ann.TryGetProperty("Line", out var covLineEl) ? covLineEl.GetInt32() : -1;
                    var healthStr = ann.TryGetProperty("Health", out var hEl) ? hEl.GetString() : null;
                    if (covLine > 0)
                    {
                        coverageMap[covLine] = healthStr switch
                        {
                            "AllPassing" => CoverageHealth.AllPassing,
                            "SomeFailing" => CoverageHealth.SomeFailing,
                            _ => CoverageHealth.NoCoverage
                        };
                    }
                }
                _coverageByFile[filePath] = coverageMap;
                CoverageUpdated?.Invoke(this, filePath);
            }
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[FileAnnotationTracker] JSON parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[FileAnnotationTracker] Unexpected error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string ParseFailurePresentation(JsonElement f)
    {
        if (!f.TryGetProperty("Failure", out var failure)) return "";

        var caseStr = failure.TryGetProperty("Case", out var c) ? c.GetString() : null;
        failure.TryGetProperty("Fields", out var fields);

        return caseStr switch
        {
            "AssertionDiff" when fields.ValueKind == JsonValueKind.Array && fields.GetArrayLength() >= 2 =>
                $"Expected: {fields[0].GetString()}  Actual: {fields[1].GetString()}",

            "ExceptionMessage" when fields.ValueKind == JsonValueKind.Array && fields.GetArrayLength() >= 1 =>
                fields[0].GetString() ?? "",

            "Timeout" when fields.ValueKind == JsonValueKind.Array && fields.GetArrayLength() >= 1 =>
                $"Timed out after {fields[0].GetString()}",

            "RawMessage" when fields.ValueKind == JsonValueKind.Array && fields.GetArrayLength() >= 1 =>
                fields[0].GetString() ?? "",

            _ => ""
        };
    }

    public IReadOnlyList<InlineFailureDisplay> GetFailuresForLine(string filePath, int line)
    {
        var key = NormalizePath(filePath);
        return _byFile.TryGetValue(key, out var map) && map.TryGetValue(line, out var list)
            ? list
            : Array.Empty<InlineFailureDisplay>();
    }

    public bool HasAnyForFile(string filePath) =>
        _byFile.TryGetValue(NormalizePath(filePath), out var m) && m.Count > 0;

    public CoverageHealth GetCoverageForLine(string filePath, int line)
    {
        var key = NormalizePath(filePath);
        return _coverageByFile.TryGetValue(key, out var map) && map.TryGetValue(line, out var health)
            ? health
            : CoverageHealth.NoCoverage;
    }

    public bool HasAnyCoverageForFile(string filePath) =>
        _coverageByFile.TryGetValue(NormalizePath(filePath), out var m) && m.Count > 0;

    private static string NormalizePath(string path) =>
        path.Replace('/', '\\').ToLowerInvariant();
}

internal readonly struct InlineFailureDisplay
{
    public readonly string TestName;
    public readonly string Presentation;

    public InlineFailureDisplay(string testName, string presentation)
    {
        TestName = testName;
        Presentation = presentation;
    }

    /// <summary>One-line inline text, e.g. "⊘ myTest — Expected: 1  Actual: 2"</summary>
    public string ToInlineText() =>
        string.IsNullOrEmpty(Presentation)
            ? $"⊘ {TestName}"
            : $"⊘ {TestName} — {Presentation}";
}
