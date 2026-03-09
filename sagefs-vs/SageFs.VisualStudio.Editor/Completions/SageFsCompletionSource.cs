using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SageFs.VisualStudio.Editor.Completions;

// ── Provider: MEF export ──────────────────────────────────────────────────────

[Export(typeof(IAsyncCompletionSourceProvider))]
[Name("SageFs FSI Completions")]
[ContentType("text")]
[Order(Before = "default")]
internal sealed class SageFsCompletionSourceProvider : IAsyncCompletionSourceProvider
{
    private readonly string? _baseUrl;
    private readonly HttpClient _http;

    [Import]
    internal ITextDocumentFactoryService? TextDocumentFactoryService { get; set; }

    [ImportingConstructor]
    public SageFsCompletionSourceProvider()
    {
        _baseUrl = PortConfig.TryGetDaemonUrl();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public IAsyncCompletionSource? GetOrCreate(ITextView textView)
    {
        if (_baseUrl == null) return null;
        string? workingDir = null;
        if (TextDocumentFactoryService is { } factory &&
            factory.TryGetTextDocument(textView.TextBuffer, out var doc) &&
            doc?.FilePath is { } filePath)
        {
            workingDir = Path.GetDirectoryName(filePath);
        }
        return new SageFsCompletionSource(_http, _baseUrl, workingDir);
    }
}

// ── Source: produces completion items for the current trigger ─────────────────

internal sealed class SageFsCompletionSource : IAsyncCompletionSource
{
    internal const int WindowHalfSize = 1024;   // 2048 char window total
    internal const int MinTriggerLength = 2;    // don't trigger on 0-1 char word

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _workingDirectory;

    public SageFsCompletionSource(HttpClient http, string baseUrl, string? workingDirectory = null)
    {
        _http = http;
        _baseUrl = baseUrl;
        _workingDirectory = workingDirectory;
    }

    // ── InitializeCompletion ─────────────────────────────────────────────────

    public CompletionStartData InitializeCompletion(
        CompletionTrigger trigger,
        SnapshotPoint triggerLocation,
        CancellationToken token)
    {
        // Always participate on explicit invoke
        if (trigger.Reason == CompletionTriggerReason.Invoke ||
            trigger.Reason == CompletionTriggerReason.InvokeAndCommitIfUnique)
            return CompletionStartData.ParticipatesInCompletionIfAny;

        // Always trigger on dot
        if (trigger.Character == '.')
            return CompletionStartData.ParticipatesInCompletionIfAny;

        // Trigger when the current word is long enough
        var wordText = GetWordBeforeCursor(triggerLocation);
        return ShouldTriggerForText(wordText)
            ? CompletionStartData.ParticipatesInCompletionIfAny
            : CompletionStartData.DoesNotParticipateInCompletion;
    }

    // ── GetCompletionContextAsync ─────────────────────────────────────────────

    public async Task<CompletionContext> GetCompletionContextAsync(
        IAsyncCompletionSession session,
        CompletionTrigger trigger,
        SnapshotPoint triggerLocation,
        SnapshotSpan applicableToSpan,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return CompletionContext.Empty;

        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? linkedCts = null;
        try
        {
            var snapshot = triggerLocation.Snapshot;
            var fullText = snapshot.GetText();
            var cursor = triggerLocation.Position;

            var (start, cursorInWindow) = ComputeWindow(cursor, fullText.Length);
            var end = Math.Min(fullText.Length, start + 2 * WindowHalfSize);
            var window = fullText.Substring(start, end - start);

            var requestBody = BuildRequestBody(window, cursorInWindow, _workingDirectory);
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            (linkedCts, timeoutCts) = ComposeLinkedTimeout(cancellationToken, TimeSpan.FromSeconds(3));

            var response = await _http.PostAsync(
                _baseUrl.TrimEnd('/') + "/dashboard/completions",
                content,
                linkedCts.Token).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var items = ParseCompletionItems(body, this);

            return items.Length > 0
                ? new CompletionContext(items)
                : CompletionContext.Empty;
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            // 3-second timeout fired — return empty silently
            return CompletionContext.Empty;
        }
        catch (HttpRequestException) { return CompletionContext.Empty; }
        catch (TaskCanceledException) { return CompletionContext.Empty; }
        catch (OperationCanceledException) { return CompletionContext.Empty; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SageFsCompletionSource] Error fetching completions: {ex.GetType().Name}: {ex.Message}");
            return CompletionContext.Empty;
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }
    }

    // ── GetDescriptionAsync ──────────────────────────────────────────────────

    public Task<object> GetDescriptionAsync(
        IAsyncCompletionSession session,
        CompletionItem item,
        CancellationToken token)
    {
        // Return the stored description string as the description object;
        // VS renders string objects as plain text in the completion description popup.
        if (item.Properties.TryGetProperty("description", out string detail) &&
            !string.IsNullOrEmpty(detail))
            return Task.FromResult<object>(detail);

        return Task.FromResult<object>(item.DisplayText);
    }

    // ── Pure helpers (internal for testability) ──────────────────────────────

    /// <summary>
    /// Computes the sliding 2048-char window around the cursor.
    /// Returns (windowStart, cursorInWindow).
    /// </summary>
    internal static (int start, int cursorInWindow) ComputeWindow(int cursor, int bufferLen)
    {
        var start = Math.Max(0, cursor - WindowHalfSize);
        var cursorInWindow = cursor - start;
        return (start, cursorInWindow);
    }

    /// <summary>
    /// Builds the JSON request body for the completions endpoint.
    /// Omits <c>working_directory</c> when it is null or empty.
    /// </summary>
    internal static string BuildRequestBody(string code, int cursorPosition, string? workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory))
            return JsonSerializer.Serialize(new { code, cursor_position = cursorPosition });
        return JsonSerializer.Serialize(new
        {
            code,
            cursor_position = cursorPosition,
            working_directory = workingDirectory,
        });
    }

    /// <summary>
    /// Creates a linked <see cref="CancellationTokenSource"/> that fires when either
    /// <paramref name="outer"/> is cancelled or <paramref name="timeoutDuration"/> elapses.
    /// Both returned sources must be disposed by the caller.
    /// </summary>
    internal static (CancellationTokenSource linked, CancellationTokenSource timeout)
        ComposeLinkedTimeout(CancellationToken outer, TimeSpan timeoutDuration)
    {
        var timeoutCts = new CancellationTokenSource(timeoutDuration);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(outer, timeoutCts.Token);
        return (linkedCts, timeoutCts);
    }

    /// <summary>
    /// Returns true when the trigger text should initiate completion.
    /// Triggers on "." (dot) or text of length ≥ <see cref="MinTriggerLength"/>.
    /// </summary>
    internal static bool ShouldTriggerForText(string? triggerText)
    {
        if (string.IsNullOrEmpty(triggerText)) return false;
        if (triggerText == ".") return true;
        return triggerText.Length >= MinTriggerLength;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static string GetWordBeforeCursor(SnapshotPoint point)
    {
        var position = point.Position;
        var snapshot = point.Snapshot;
        var start = position;
        while (start > 0 && (char.IsLetterOrDigit(snapshot[start - 1]) || snapshot[start - 1] == '_'))
            start--;
        return position > start ? snapshot.GetText(start, position - start) : string.Empty;
    }

    private static ImmutableArray<CompletionItem> ParseCompletionItems(
        string body, SageFsCompletionSource source)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Response shape: { "completions": [...] }
            if (!root.TryGetProperty("completions", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return ImmutableArray<CompletionItem>.Empty;

            var builder = ImmutableArray.CreateBuilder<CompletionItem>();
            foreach (var el in arr.EnumerateArray())
            {
                var label       = GetString(el, "label");
                var insertText  = GetString(el, "insertText");
                var description = GetString(el, "description");

                if (string.IsNullOrEmpty(label)) continue;

                var item = new CompletionItem(
                    displayText: label,
                    source: source);

                if (!string.IsNullOrEmpty(description))
                    item.Properties.AddProperty("description", description);

                builder.Add(item);
            }
            return builder.ToImmutable();
        }
        catch
        {
            return ImmutableArray<CompletionItem>.Empty;
        }
    }

    private static string GetString(JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
    }
}
