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

    // Per-file: line → coverage glyph kind
    private readonly ConcurrentDictionary<string, Dictionary<int, CoverageGlyphKind>>
        _coverageByFile = new ConcurrentDictionary<string, Dictionary<int, CoverageGlyphKind>>(StringComparer.OrdinalIgnoreCase);

    // Failure narratives keyed by TestId
    private readonly ConcurrentDictionary<string, FailureNarrativeEntry>
        _narratives = new ConcurrentDictionary<string, FailureNarrativeEntry>(StringComparer.Ordinal);

    public event EventHandler<string>? FileAnnotationsUpdated; // arg = normalized filePath
    public event EventHandler<string>? CoverageUpdated;        // arg = normalized filePath

    public void ProcessEvent(SseEvent ev)
    {
        if (ev.Type == "file_annotations")
            ProcessFileAnnotations(ev.Data);
        else if (ev.Type == "failure_narratives")
            ProcessFailureNarratives(ev.Data);
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
                    var testId = "";
                    if (f.TryGetProperty("TestId", out var tidEl))
                    {
                        testId = tidEl.ValueKind == JsonValueKind.String
                            ? (tidEl.GetString() ?? "")
                            : tidEl.ValueKind == JsonValueKind.Object
                              && tidEl.TryGetProperty("Fields", out var tidFields)
                              && tidFields.ValueKind == JsonValueKind.Array
                              && tidFields.GetArrayLength() > 0
                                ? (tidFields[0].GetString() ?? "")
                                : "";
                    }
                    var presentation = ParseFailurePresentation(f);

                    if (!lineMap.TryGetValue(line, out var list))
                    {
                        list = new List<InlineFailureDisplay>();
                        lineMap[line] = list;
                    }
                    list.Add(new InlineFailureDisplay(testName, presentation, testId));
                }
            }

            _byFile[filePath] = lineMap;
            FileAnnotationsUpdated?.Invoke(this, filePath);

            // ── Coverage annotations ─────────────────────────────────────
            if (root.TryGetProperty("CoverageAnnotations", out var coverageAnns))
            {
                var coverageMap = new Dictionary<int, CoverageGlyphKind>();
                foreach (var ann in coverageAnns.EnumerateArray())
                {
                    int covLine = ann.TryGetProperty("Line", out var covLineEl) ? covLineEl.GetInt32() : -1;
                    if (covLine > 0)
                    {
                        var kind = ParseCoverageGlyphKind(ann);
                        if (kind != CoverageGlyphKind.None)
                        {
                            coverageMap[covLine] = kind;
                        }
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

    public CoverageGlyphKind GetCoverageForLine(string filePath, int line)
    {
        var key = NormalizePath(filePath);
        return _coverageByFile.TryGetValue(key, out var map) && map.TryGetValue(line, out var kind)
            ? kind
            : CoverageGlyphKind.None;
    }

    public bool HasAnyCoverageForFile(string filePath) =>
        _coverageByFile.TryGetValue(NormalizePath(filePath), out var m) && m.Count > 0;

    /// <summary>Look up a failure narrative by test display name.</summary>
    public FailureNarrativeEntry? GetNarrativeForTest(string testName)
    {
        return _narratives.TryGetValue(testName, out var n) ? n : null;
    }

    private void ProcessFailureNarratives(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var testId = item.TryGetProperty("TestId", out var tid) ? tid.GetString() ?? "" : "";
                var summary = item.TryGetProperty("Summary", out var s) ? s.GetString() ?? "" : "";
                var timeSince = item.TryGetProperty("TimeSinceLastPass", out var ts) ? ts.GetString() ?? "" : "";

                if (!string.IsNullOrEmpty(testId))
                    _narratives[testId] = new FailureNarrativeEntry(testId, summary, timeSince);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[FileAnnotationTracker] failure_narratives parse error: {ex.Message}");
        }
    }

    private static string NormalizePath(string path) =>
        path.Replace('/', '\\').ToLowerInvariant();

    private static CoverageGlyphKind ParseCoverageGlyphKind(JsonElement annotation)
    {
        var branchKind = ParseBranchCoverage(annotation);
        if (branchKind != CoverageGlyphKind.None)
            return branchKind;

        if (annotation.TryGetProperty("Detail", out var detail))
        {
            var detailCase = TryGetDuCase(detail);
            return detailCase switch
            {
                "Covered" => ParseCoveredDetail(detail),
                "NotCovered" => CoverageGlyphKind.LineUncovered,
                "Pending" => CoverageGlyphKind.LineUncovered,
                _ => CoverageGlyphKind.None
            };
        }

        if (annotation.TryGetProperty("Health", out var health))
        {
            return (health.GetString() ?? "") switch
            {
                "AllPassing" => CoverageGlyphKind.LinePassing,
                "SomeFailing" => CoverageGlyphKind.LineFailing,
                "NoCoverage" => CoverageGlyphKind.LineUncovered,
                _ => CoverageGlyphKind.None
            };
        }

        return CoverageGlyphKind.None;
    }

    private static CoverageGlyphKind ParseBranchCoverage(JsonElement annotation)
    {
        if (!annotation.TryGetProperty("BranchCoverage", out var branch)
            || branch.ValueKind != JsonValueKind.Object)
        {
            return CoverageGlyphKind.None;
        }

        return TryGetDuCase(branch) switch
        {
            "FullyCovered" => CoverageGlyphKind.BranchFullyCovered,
            "PartiallyCovered" => CoverageGlyphKind.BranchPartiallyCovered,
            "NotCovered" => CoverageGlyphKind.BranchNotCovered,
            _ => CoverageGlyphKind.None
        };
    }

    private static CoverageGlyphKind ParseCoveredDetail(JsonElement detail)
    {
        if (!detail.TryGetProperty("Fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array
            || fields.GetArrayLength() < 2)
        {
            return CoverageGlyphKind.None;
        }

        return TryGetDuCase(fields[1]) switch
        {
            "AllPassing" => CoverageGlyphKind.LinePassing,
            "SomeFailing" => CoverageGlyphKind.LineFailing,
            _ => CoverageGlyphKind.None
        };
    }

    private static string? TryGetDuCase(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when value.TryGetProperty("Case", out var caseEl) => caseEl.GetString(),
            _ => null
        };
    }
}

internal readonly struct InlineFailureDisplay
{
    public readonly string TestName;
    public readonly string Presentation;
    public readonly string TestId;

    public InlineFailureDisplay(string testName, string presentation, string testId = "")
    {
        TestName = testName;
        Presentation = presentation;
        TestId = testId;
    }

    /// <summary>One-line inline text, e.g. "⊘ myTest — Expected: 1  Actual: 2"</summary>
    public string ToInlineText() =>
        string.IsNullOrEmpty(Presentation)
            ? $"⊘ {TestName}"
            : $"⊘ {TestName} — {Presentation}";

    /// <summary>One-line inline text enriched with narrative context when available.</summary>
    public string ToInlineText(FailureNarrativeEntry? narrative)
    {
        var baseText = ToInlineText();
        if (narrative == null || string.IsNullOrEmpty(narrative.Value.Summary))
            return baseText;
        return $"{baseText}  ℹ️ {narrative.Value.Summary}";
    }
}

internal readonly struct FailureNarrativeEntry
{
    public readonly string TestId;
    public readonly string Summary;
    public readonly string TimeSinceLastPass;

    public FailureNarrativeEntry(string testId, string summary, string timeSinceLastPass)
    {
        TestId = testId;
        Summary = summary;
        TimeSinceLastPass = timeSinceLastPass;
    }
}
