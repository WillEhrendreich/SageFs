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
            case "tests_discovered":
                ProcessTestsDiscovered(ev.Data);
                break;
            case "test_result_batch":
                ProcessTestResultBatch(ev.Data);
                break;
            case "session_reset":
            case "session_hard_reset":
                _lineStatus.Clear();
                _testLocations.Clear();
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void ProcessTestsDiscovered(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tests", out var tests)) return;
            foreach (var test in tests.EnumerateArray())
            {
                if (!test.TryGetProperty("id", out var idEl)) continue;
                var id = idEl.GetString();
                if (id == null) continue;

                string? filePath = null;
                int line = 0;
                if (test.TryGetProperty("filePath", out var fp)) filePath = fp.GetString();
                if (test.TryGetProperty("line", out var ln)) line = ln.GetInt32();

                if (filePath != null && line > 0)
                {
                    var normalized = NormalizePath(filePath);
                    _testLocations[id] = (normalized, line);
                    _lineStatus.TryAdd((normalized, line), TestStatus.NotRun);
                }
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { }
    }

    private void ProcessTestResultBatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results)) return;
            var changed = false;
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("testId", out var idEl)) continue;
                var id = idEl.GetString();
                if (id == null || !_testLocations.TryGetValue(id, out var loc)) continue;

                var status = TestStatus.NotRun;
                if (result.TryGetProperty("outcome", out var outcome))
                {
                    status = outcome.GetString() switch
                    {
                        "Passed"  => TestStatus.Passed,
                        "Failed"  => TestStatus.Failed,
                        "Running" => TestStatus.Running,
                        _         => TestStatus.NotRun
                    };
                }
                _lineStatus[loc] = status;
                changed = true;
            }
            if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { }
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
