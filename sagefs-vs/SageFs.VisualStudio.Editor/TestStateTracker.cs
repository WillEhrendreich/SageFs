using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

namespace SageFs.VisualStudio.Editor;

/// <summary>
/// Tracks test positions and results from SSE events.
/// Thread-safe. Used by the glyph tagger to determine glyph per line.
/// </summary>
internal sealed class TestStateTracker
{
    // Maps (normalizedFilePath, 1-based lineNumber) → TestStatus
    private readonly ConcurrentDictionary<(string, int), TestStatus> _lineStatus = new ConcurrentDictionary<(string, int), TestStatus>();
    // Maps testId → (filePath, line) for join with TestResultBatch
    private readonly ConcurrentDictionary<string, (string FilePath, int Line)> _testLocations = new ConcurrentDictionary<string, (string FilePath, int Line)>();

    public event EventHandler? StateChanged;

    public void ProcessEvent(SseEvent ev)
    {
        switch (ev.Type)
        {
            // test_results_batch contains both location (Origin) and outcome (Status) per entry.
            // A single event replaces both tests_discovered + test_result_batch in one shot.
            case "test_results_batch":
                ProcessResultsBatch(ev.Data);
                break;
            case "session_reset":
            case "session_hard_reset":
                _lineStatus.Clear();
                _testLocations.Clear();
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>
    /// Parses the daemon's test_results_batch event.
    /// JSON shape (mirrors SageFs.VisualStudio.Core.LiveTestingParser):
    /// {
    ///   "Entries": [{
    ///     "TestId":  { "Fields": ["test-id"] },
    ///     "Origin":  { "Case": "SourceMapped", "Fields": ["path/to/File.fs", 42] },
    ///     "Status":  { "Case": "Passed", "Fields": ["00:00:00.045"] }
    ///   }],
    ///   "Freshness": "Fresh"
    /// }
    /// </summary>
    private void ProcessResultsBatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Entries", out var entries)) return;

            var changed = false;
            foreach (var entry in entries.EnumerateArray())
            {
                // ── extract test ID ──────────────────────────────────────────
                if (!entry.TryGetProperty("TestId", out var testIdEl)) continue;
                var id = ExtractTestId(testIdEl);
                if (id == null) continue;

                // ── extract file path + line from Origin ─────────────────────
                string? filePath = null;
                int line = 0;
                if (entry.TryGetProperty("Origin", out var origin) &&
                    origin.TryGetProperty("Case", out var originCase) &&
                    originCase.GetString() == "SourceMapped" &&
                    origin.TryGetProperty("Fields", out var originFields) &&
                    originFields.ValueKind == JsonValueKind.Array &&
                    originFields.GetArrayLength() >= 2)
                {
                    if (originFields[0].ValueKind == JsonValueKind.String)
                        filePath = originFields[0].GetString();
                    if (originFields[1].ValueKind == JsonValueKind.Number)
                        line = originFields[1].GetInt32();
                }

                // Record location (even if Status is missing — test was at least discovered)
                if (filePath != null && line > 0)
                {
                    var normalized = NormalizePath(filePath);
                    _testLocations[id] = (normalized, line);
                    _lineStatus.TryAdd((normalized, line), TestStatus.NotRun);
                }

                // ── extract outcome from Status ──────────────────────────────
                if (entry.TryGetProperty("Status", out var status) &&
                    status.TryGetProperty("Case", out var statusCase))
                {
                    var outcome = statusCase.GetString() switch
                    {
                        "Passed"        => TestStatus.Passed,
                        "Failed"        => TestStatus.Failed,
                        "Running"       => TestStatus.Running,
                        "Stale"         => TestStatus.NotRun,
                        "Skipped"       => TestStatus.NotRun,
                        "PolicyDisabled"=> TestStatus.NotRun,
                        _               => (TestStatus?)null
                    };

                    if (outcome.HasValue && _testLocations.TryGetValue(id, out var loc))
                    {
                        _lineStatus[loc] = outcome.Value;
                        changed = true;
                    }
                }
            }

            if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[TestStateTracker] JSON parse error in test_results_batch: {ex.Message}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[TestStateTracker] Unexpected error processing test_results_batch: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? ExtractTestId(JsonElement el)
    {
        // Shape: {"Fields": ["the-id"]}
        if (el.TryGetProperty("Fields", out var fields) &&
            fields.ValueKind == JsonValueKind.Array &&
            fields.GetArrayLength() >= 1 &&
            fields[0].ValueKind == JsonValueKind.String)
            return fields[0].GetString();
        // Fallback: bare string
        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    public TestStatus GetStatusForLine(string filePath, int lineNumber)
    {
        var normalized = NormalizePath(filePath);
        return _lineStatus.TryGetValue((normalized, lineNumber), out var status)
            ? status
            : TestStatus.Unknown;
    }

    public IEnumerable<((string FilePath, int Line) Key, TestStatus Status)> GetAllStatuses()
    {
        foreach (var kvp in _lineStatus)
            yield return (kvp.Key, kvp.Value);
    }

    private static string NormalizePath(string path) =>
        path.Replace('/', '\\').ToLowerInvariant();
}

public enum TestStatus { Unknown, NotRun, Running, Passed, Failed }
