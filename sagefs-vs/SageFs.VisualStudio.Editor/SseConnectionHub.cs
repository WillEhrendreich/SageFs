using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace SageFs.VisualStudio.Editor;

/// <summary>
/// Deduplicates SSE connections to the SageFs daemon.
///
/// Multiple MEF components (TestGlyphTagger, SquiggleTagger, InlineFailureAdornment)
/// all subscribe to SSE events. Without this hub, each component would open its own
/// HTTP connection to the daemon — wasteful and a potential source of race conditions
/// on startup.
///
/// Usage:
/// <code>
///   var hub = SseConnectionHub.Instance;
///   hub.Subscribe("/events", ev => MyTracker.ProcessEvent(ev));
/// </code>
///
/// The hub opens at most one <see cref="SseClient"/> per endpoint. Late subscribers
/// added after connection are immediately registered (they'll receive future events).
/// </summary>
internal static class SseConnectionHub
{
    // Per-endpoint: the SseClient + list of subscribers
    private static readonly ConcurrentDictionary<string, EndpointConnection> _connections =
        new ConcurrentDictionary<string, EndpointConnection>(StringComparer.OrdinalIgnoreCase);

    private static string? _baseUrl;
    private static readonly object _initLock = new object();

    /// <summary>
    /// Must be called once (e.g., from the first provider that reads PortConfig).
    /// Subsequent calls with the same URL are no-ops; a different URL reconnects.
    /// </summary>
    public static void Initialize(string baseUrl)
    {
        lock (_initLock)
        {
            if (_baseUrl == baseUrl) return;
            _baseUrl = baseUrl;
            // Reconnect all existing endpoints with new URL
            foreach (var conn in _connections.Values)
                conn.Reconnect(baseUrl);
        }
    }

    /// <summary>
    /// Subscribe to an SSE endpoint. If no connection exists for this endpoint,
    /// one is created. Multiple subscribers share the same underlying connection.
    /// </summary>
    public static void Subscribe(string endpoint, Action<SseEvent> handler)
    {
        var conn = _connections.GetOrAdd(endpoint, ep => new EndpointConnection(ep));
        conn.AddSubscriber(handler);

        // Start connecting if we have a URL
        string? url;
        lock (_initLock) { url = _baseUrl; }
        if (url != null)
            conn.EnsureStarted(url);
    }

    private sealed class EndpointConnection
    {
        private readonly string _endpoint;
        private readonly List<Action<SseEvent>> _subscribers = new List<Action<SseEvent>>();
        private readonly object _lock = new object();
        private SseClient? _client;

        public EndpointConnection(string endpoint) => _endpoint = endpoint;

        public void AddSubscriber(Action<SseEvent> handler)
        {
            lock (_lock) { _subscribers.Add(handler); }
        }

        public void EnsureStarted(string baseUrl)
        {
            lock (_lock)
            {
                if (_client != null) return;
                _client = new SseClient();
                _client.EventReceived += Dispatch;
                _client.Start(baseUrl, _endpoint);
            }
        }

        public void Reconnect(string baseUrl)
        {
            lock (_lock)
            {
                _client?.Dispose();
                _client = new SseClient();
                _client.EventReceived += Dispatch;
                _client.Start(baseUrl, _endpoint);
            }
        }

        private void Dispatch(object? sender, SseEvent ev)
        {
            List<Action<SseEvent>> snapshot;
            lock (_lock) { snapshot = new List<Action<SseEvent>>(_subscribers); }
            foreach (var h in snapshot)
            {
                try { h(ev); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SseConnectionHub] Subscriber threw on {_endpoint}: {ex.Message}");
                }
            }
        }
    }
}
