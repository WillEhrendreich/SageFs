using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SageFs.VisualStudio.Editor;

/// <summary>
/// Minimal SSE client for consuming /events from the SageFs daemon.
/// Designed for net472 (no async streams). Runs on background thread.
/// </summary>
internal sealed class SseClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    public SseClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
        // Required header for SSE streams — without this, some servers return JSON instead
        _http.DefaultRequestHeaders.Add("Accept", "text/event-stream");
        _http.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
    }

    public event EventHandler<SseEvent>? EventReceived;

    public void Start(string baseUrl, string endpoint = "/events")
    {
        _ = Task.Run(() => LoopAsync(baseUrl, endpoint, _cts.Token));
    }

    private async Task LoopAsync(string baseUrl, string endpoint, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var response = await _http.GetAsync(
                    baseUrl.TrimEnd('/') + endpoint,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                delay = TimeSpan.FromSeconds(1);

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                string? eventType = null;
                var dataLines = new List<string>();

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;

                    if (line.StartsWith("event:"))
                        eventType = line.Substring(6).Trim();
                    else if (line.StartsWith("data:"))
                        dataLines.Add(line.Substring(5).Trim());
                    else if (line == "" && (eventType != null || dataLines.Count > 0))
                    {
                        var ev = new SseEvent(eventType ?? "message", string.Join("\n", dataLines));
                        EventReceived?.Invoke(this, ev);
                        eventType = null;
                        dataLines.Clear();
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch { /* swallow and reconnect */ }

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }

            if (delay < TimeSpan.FromSeconds(30))
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _cts.Dispose();
    }
}

internal readonly struct SseEvent
{
    public readonly string Type;
    public readonly string Data;

    public SseEvent(string type, string data)
    {
        Type = type;
        Data = data;
    }
}
