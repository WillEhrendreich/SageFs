using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SageFs.VisualStudio.Editor;

// ── Adornment state: Active / Stale / Gone ────────────────────────────────────

/// <summary>
/// Three-state lifecycle for an inline eval result adornment.
///
/// <list type="bullet">
/// <item><description><see cref="Active"/>: result is fresh, shown at full opacity (75%)</description></item>
/// <item><description><see cref="Stale"/>: the line was edited after eval — result is dimmed to 35% opacity</description></item>
/// <item><description><see cref="Gone"/>: no result; adornment is not rendered</description></item>
/// </list>
///
/// The <c>Active → Stale</c> transition happens when the buffer line changes.
/// The <c>* → Gone</c> transition happens on explicit clear or re-eval with a new result
/// (which goes directly to <c>Active</c>).
///
/// Transitions are a pure function: <see cref="InlineEvalResultTracker"/> applies them;
/// the adornment manager only reads state and renders.
/// </summary>
internal abstract class AdornmentState
{
    public static AdornmentState Active(string result) => new ActiveState(result);
    public static AdornmentState Stale(string result)  => new StaleState(result);
    public static readonly AdornmentState Gone = new GoneState();

    internal sealed class ActiveState : AdornmentState
    {
        public string Result { get; }
        internal ActiveState(string r) => Result = r;
    }

    internal sealed class StaleState : AdornmentState
    {
        public string Result { get; }
        internal StaleState(string r) => Result = r;
    }

    internal sealed class GoneState : AdornmentState { }
}

// ── Tracker: holds eval results per (file, line) ─────────────────────────────

/// <summary>
/// Thread-safe store of inline eval results, keyed by (normalised file path, 1-based line).
///
/// Lifecycle:
/// <list type="bullet">
/// <item>Call <see cref="SetResult"/> from the command that receives an /exec response.</item>
/// <item>The buffer-change handler calls <see cref="MarkStale"/> for any edited line.</item>
/// <item>The adornment manager calls <see cref="GetAllForFile"/> during render.</item>
/// </list>
///
/// Thread safety: <see cref="ConcurrentDictionary{TKey,TValue}"/> for all state.
/// Event <see cref="StateChanged"/> is raised on the calling thread; callers must
/// marshal to the UI thread before touching WPF elements.
/// </summary>
internal sealed class InlineEvalResultTracker
{
    private readonly ConcurrentDictionary<(string normPath, int line), AdornmentState> _states =
        new ConcurrentDictionary<(string, int), AdornmentState>();

    /// <summary>Raised when a result is set, staled, or cleared. Args: (normalisedFilePath, 1-based line).</summary>
    public event Action<string, int>? StateChanged;

    /// <summary>
    /// Set or replace the eval result for a line. Transitions any state to <see cref="AdornmentState.Active"/>.
    /// Call this from the command/CodeLens that receives the /exec response.
    /// </summary>
    public void SetResult(string filePath, int oneBasedLine, string result)
    {
        var key = (Normalize(filePath), oneBasedLine);
        _states[key] = AdornmentState.Active(result);
        StateChanged?.Invoke(key.Item1, oneBasedLine);
    }

    /// <summary>
    /// Dim the result for a line that was just edited.
    /// Only transitions Active → Stale; already-Stale and Gone are left unchanged.
    /// </summary>
    public void MarkStale(string filePath, int oneBasedLine)
    {
        var key = (Normalize(filePath), oneBasedLine);
        if (_states.TryGetValue(key, out var s) && s is AdornmentState.ActiveState a)
        {
            _states[key] = AdornmentState.Stale(a.Result);
            StateChanged?.Invoke(key.Item1, oneBasedLine);
        }
    }

    /// <summary>Remove the result entirely. The adornment disappears.</summary>
    public void Clear(string filePath, int oneBasedLine)
    {
        var key = (Normalize(filePath), oneBasedLine);
        if (_states.TryRemove(key, out _))
            StateChanged?.Invoke(key.Item1, oneBasedLine);
    }

    /// <summary>Clear all results for every line in a file (e.g., on hard reset).</summary>
    public void ClearFile(string filePath)
    {
        var norm = Normalize(filePath);
        foreach (var key in _states.Keys)
        {
            if (key.normPath == norm && _states.TryRemove(key, out _))
                StateChanged?.Invoke(norm, key.line);
        }
    }

    /// <summary>Get the state for a specific line. Returns <see cref="AdornmentState.Gone"/> if absent.</summary>
    public AdornmentState Get(string filePath, int oneBasedLine)
    {
        var key = (Normalize(filePath), oneBasedLine);
        return _states.TryGetValue(key, out var s) ? s : AdornmentState.Gone;
    }

    /// <summary>Enumerate all (line, state) pairs for a file. Skips Gone entries.</summary>
    public IEnumerable<(int line, AdornmentState state)> GetAllForFile(string filePath)
    {
        var norm = Normalize(filePath);
        foreach (var kv in _states)
        {
            if (kv.Key.normPath == norm && !(kv.Value is AdornmentState.GoneState))
                yield return (kv.Key.line, kv.Value);
        }
    }

    /// <summary>True if this file has any active or stale results.</summary>
    public bool HasAnyForFile(string filePath)
    {
        var norm = Normalize(filePath);
        foreach (var kv in _states)
            if (kv.Key.normPath == norm && !(kv.Value is AdornmentState.GoneState))
                return true;
        return false;
    }

    private static string Normalize(string p) =>
        string.IsNullOrEmpty(p) ? string.Empty : p.Replace('/', '\\').ToLowerInvariant();
}

// ── Shared tracker singleton (connects SSE eval_result events to all views) ──

/// <summary>
/// Holds the single <see cref="InlineEvalResultTracker"/> instance shared across all views.
/// Subscribes to daemon eval_result SSE events and updates the tracker.
/// </summary>
internal static class SharedEvalResultTracker
{
    private static readonly Lazy<InlineEvalResultTracker> _instance =
        new Lazy<InlineEvalResultTracker>(CreateTracker);

    public static InlineEvalResultTracker Instance => _instance.Value;

    private static InlineEvalResultTracker CreateTracker()
    {
        var tracker = new InlineEvalResultTracker();

        var url = PortConfig.TryGetDaemonUrl();
        if (url != null)
        {
            SseConnectionHub.Initialize(url);
            SseConnectionHub.Subscribe("/events", ev => ProcessEvalResultEvent(tracker, ev));
        }

        return tracker;
    }

    private static void ProcessEvalResultEvent(InlineEvalResultTracker tracker, SseEvent ev)
    {
        if (ev.Type != "eval_result") return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ev.Data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("filePath", out var fp)) return;
            if (!root.TryGetProperty("blockStartLine", out var bsl)) return;
            if (!root.TryGetProperty("output", out var output)) return;
            var filePath = fp.GetString();
            var line = bsl.GetInt32();
            var result = output.GetString() ?? "";
            if (string.IsNullOrEmpty(filePath)) return;
            const int MaxDisplayLength = 80;
            var display = result.Length > MaxDisplayLength
                ? result.Substring(0, MaxDisplayLength - 3) + "..."
                : result;
            tracker.SetResult(filePath!, line, display);
        }
        catch { /* parse errors are non-fatal */ }
    }
}
