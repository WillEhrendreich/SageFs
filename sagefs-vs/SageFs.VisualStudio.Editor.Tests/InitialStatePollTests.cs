using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// Tests for <see cref="InitialStatePoll"/> — the component that seeds glyph state
/// on VS startup without waiting for the first SSE event.
/// </summary>
public sealed class InitialStatePollTests
{
    // JSON in test_results_batch format matching what TestStateTracker.ProcessEvent expects
    private const string SampleResultsBatchJson = """
        {
          "Entries": [
            {
              "TestId": { "Fields": ["my-test-id"] },
              "Origin": { "Case": "SourceMapped", "Fields": ["C:\\src\\MyFile.fs", 42] },
              "Status": { "Case": "Passed", "Fields": ["00:00:00.045"] }
            }
          ],
          "Freshness": "Fresh"
        }
        """;

    [Fact]
    public async Task WhenDaemonReturnsState_GlyphsArePreseeded()
    {
        // Arrange
        var tracker = new TestStateTracker();
        Func<CancellationToken, Task<string>> fetchJson =
            ct => Task.FromResult(SampleResultsBatchJson);

        // Act
        await InitialStatePoll.RunAsync(fetchJson, tracker);

        // Assert — tracker should be seeded with the Passed status at line 42
        var status = tracker.GetStatusForLine(@"C:\src\MyFile.fs", 42);
        status.Should().Be(TestStatus.Passed,
            because: "InitialStatePoll should seed the tracker from the daemon response " +
                     "without waiting for an SSE event");
    }

    [Fact]
    public async Task WhenDaemonUnavailable_NoException()
    {
        // Arrange
        var tracker = new TestStateTracker();
        Func<CancellationToken, Task<string>> fetchJson =
            ct => Task.FromException<string>(new HttpRequestException("Connection refused"));

        // Act + Assert — must not throw
        var act = async () => await InitialStatePoll.RunAsync(fetchJson, tracker);
        await act.Should().NotThrowAsync(
            because: "InitialStatePoll must swallow HttpRequestException so VS startup " +
                     "is not affected when the SageFs daemon isn't running");
    }

    [Fact]
    public async Task WhenDaemonReturnsEmpty_TrackerUnchanged()
    {
        var tracker = new TestStateTracker();
        Func<CancellationToken, Task<string>> fetchJson = ct => Task.FromResult(string.Empty);

        await InitialStatePoll.RunAsync(fetchJson, tracker);

        tracker.GetAllStatuses().Should().BeEmpty(
            because: "empty response should not seed any statuses");
    }

    [Fact]
    public async Task WhenFetchThrowsGenericException_NoException()
    {
        var tracker = new TestStateTracker();
        Func<CancellationToken, Task<string>> fetchJson =
            ct => Task.FromException<string>(new InvalidOperationException("unexpected"));

        var act = async () => await InitialStatePoll.RunAsync(fetchJson, tracker);
        await act.Should().NotThrowAsync(
            because: "all exceptions during initial poll must be swallowed to avoid crashing VS");
    }

    [Fact]
    public async Task WhenDaemonReturnsMalformedJson_TrackerUnchanged()
    {
        var tracker = new TestStateTracker();
        Func<CancellationToken, Task<string>> fetchJson = ct => Task.FromResult("not-json");

        await InitialStatePoll.RunAsync(fetchJson, tracker);

        tracker.GetAllStatuses().Should().BeEmpty(
            because: "malformed JSON should be swallowed by TestStateTracker and leave state empty");
    }

    [Fact]
    public async Task WhenMultipleEntries_AllAreSeeded()
    {
        var tracker = new TestStateTracker();
        const string json = """
            {
              "Entries": [
                {
                  "TestId": { "Fields": ["test-a"] },
                  "Origin": { "Case": "SourceMapped", "Fields": ["C:\\src\\A.fs", 10] },
                  "Status": { "Case": "Passed", "Fields": ["00:00:00.010"] }
                },
                {
                  "TestId": { "Fields": ["test-b"] },
                  "Origin": { "Case": "SourceMapped", "Fields": ["C:\\src\\A.fs", 20] },
                  "Status": { "Case": "Failed", "Fields": ["error msg"] }
                }
              ],
              "Freshness": "Fresh"
            }
            """;

        await InitialStatePoll.RunAsync(ct => Task.FromResult(json), tracker);

        tracker.GetStatusForLine(@"C:\src\A.fs", 10).Should().Be(TestStatus.Passed);
        tracker.GetStatusForLine(@"C:\src\A.fs", 20).Should().Be(TestStatus.Failed);
    }
}
