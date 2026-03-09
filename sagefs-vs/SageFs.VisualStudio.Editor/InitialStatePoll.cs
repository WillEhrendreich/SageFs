using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SageFs.VisualStudio.Editor;

/// <summary>
/// Seeds the <see cref="TestStateTracker"/> with the current daemon test state on startup.
///
/// Without this, glyphs are blank until the first SSE event arrives (e.g. next test run).
/// The initial poll fires immediately after the SSE subscription is established and races
/// to populate state before any editor opens. If the daemon isn't running, the exception
/// is swallowed silently and SSE handles the state once the daemon starts.
/// </summary>
internal static class InitialStatePoll
{
    /// <summary>
    /// Calls <paramref name="fetchJson"/> to retrieve the current test state JSON,
    /// then feeds it to <paramref name="tracker"/> as a <c>test_results_batch</c> SSE event.
    /// Returns normally (no throws) on <see cref="HttpRequestException"/> or cancellation.
    /// </summary>
    internal static async Task RunAsync(
        Func<CancellationToken, Task<string>> fetchJson,
        TestStateTracker tracker,
        CancellationToken ct = default)
    {
        try
        {
            var json = await fetchJson(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json))
                tracker.ProcessEvent(new SseEvent("test_results_batch", json));
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[InitialStatePoll] Unexpected error during initial state poll: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fire-and-forget: fetches the current test trace from the daemon and seeds the tracker.
    /// Uses <c>/api/live-testing/test-trace</c>.
    /// </summary>
    internal static void FireAndForget(string baseUrl, TestStateTracker tracker)
    {
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = baseUrl.TrimEnd('/') + "/api/live-testing/test-trace";
            await RunAsync(ct => http.GetStringAsync(url), tracker).ConfigureAwait(false);
        });
    }
}
