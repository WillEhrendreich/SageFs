using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// Integration tests for <see cref="TestStateTracker"/> JSON parsing.
///
/// These tests feed raw SSE event payloads matching the daemon's
/// <c>test_results_batch</c> format and assert the correct line→status mapping.
/// They protect against silent regressions when the daemon JSON format changes.
/// </summary>
public sealed class TestStateTrackerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static TestStateTracker Track(string eventType, string json)
    {
        var tracker = new TestStateTracker();
        tracker.ProcessEvent(new SseEvent(eventType, json));
        return tracker;
    }

    // ── test_results_batch — happy path ─────────────────────────────────────

    [Fact]
    public void SinglePassedEntry_MapsCorrectLineAndStatus()
    {
        const string json = """
            {
              "Entries": [{
                "TestId":  { "Fields": ["my-test-id"] },
                "Origin":  { "Case": "SourceMapped", "Fields": ["C:/code/Tests.fs", 42] },
                "Status":  { "Case": "Passed", "Fields": ["00:00:00.012"] }
              }],
              "Freshness": "Fresh"
            }
            """;

        var tracker = Track("test_results_batch", json);
        tracker.GetStatusForLine("C:\\code\\Tests.fs", 42).Should().Be(TestStatus.Passed);
    }

    [Fact]
    public void SingleFailedEntry_MapsCorrectStatus()
    {
        const string json = """
            {
              "Entries": [{
                "TestId":  { "Fields": ["fail-test"] },
                "Origin":  { "Case": "SourceMapped", "Fields": ["src/Specs.fs", 10] },
                "Status":  { "Case": "Failed", "Fields": ["Expected 1 but got 2"] }
              }],
              "Freshness": "Fresh"
            }
            """;

        var tracker = Track("test_results_batch", json);
        tracker.GetStatusForLine("src/Specs.fs", 10).Should().Be(TestStatus.Failed);
    }

    [Fact]
    public void RunningStatus_MapsCorrectly()
    {
        const string json = """
            {
              "Entries": [{
                "TestId": { "Fields": ["running-test"] },
                "Origin": { "Case": "SourceMapped", "Fields": ["test/Run.fs", 7] },
                "Status": { "Case": "Running", "Fields": [] }
              }],
              "Freshness": "Fresh"
            }
            """;

        var tracker = Track("test_results_batch", json);
        tracker.GetStatusForLine("test/Run.fs", 7).Should().Be(TestStatus.Running);
    }

    [Fact]
    public void MultipleEntries_AllMapped()
    {
        const string json = """
            {
              "Entries": [
                {
                  "TestId": { "Fields": ["t1"] },
                  "Origin": { "Case": "SourceMapped", "Fields": ["Suite.fs", 5] },
                  "Status": { "Case": "Passed", "Fields": [] }
                },
                {
                  "TestId": { "Fields": ["t2"] },
                  "Origin": { "Case": "SourceMapped", "Fields": ["Suite.fs", 15] },
                  "Status": { "Case": "Failed", "Fields": [] }
                },
                {
                  "TestId": { "Fields": ["t3"] },
                  "Origin": { "Case": "SourceMapped", "Fields": ["Suite.fs", 25] },
                  "Status": { "Case": "Running", "Fields": [] }
                }
              ],
              "Freshness": "Fresh"
            }
            """;

        var tracker = Track("test_results_batch", json);
        tracker.GetStatusForLine("Suite.fs", 5).Should().Be(TestStatus.Passed);
        tracker.GetStatusForLine("Suite.fs", 15).Should().Be(TestStatus.Failed);
        tracker.GetStatusForLine("Suite.fs", 25).Should().Be(TestStatus.Running);
    }

    // ── Path normalization ────────────────────────────────────────────────

    [Fact]
    public void ForwardSlashPaths_NormalizedToBackslash()
    {
        const string json = """
            {
              "Entries": [{
                "TestId": { "Fields": ["t1"] },
                "Origin": { "Case": "SourceMapped", "Fields": ["src/tests/Suite.fs", 20] },
                "Status": { "Case": "Passed", "Fields": [] }
              }],
              "Freshness": "Fresh"
            }
            """;

        var tracker = Track("test_results_batch", json);
        // Both slash styles should resolve to the same entry.
        tracker.GetStatusForLine("src/tests/Suite.fs", 20).Should().Be(TestStatus.Passed);
        tracker.GetStatusForLine("src\\tests\\Suite.fs", 20).Should().Be(TestStatus.Passed);
    }

    [Fact]
    public void CaseInsensitivePathLookup()
    {
        const string json = """
            {
              "Entries": [{
                "TestId": { "Fields": ["t1"] },
                "Origin": { "Case": "SourceMapped", "Fields": ["C:/Code/Tests.FS", 1] },
                "Status": { "Case": "Passed", "Fields": [] }
              }],
              "Freshness": "Fresh"
            }
            """;

        var tracker = Track("test_results_batch", json);
        tracker.GetStatusForLine("c:\\code\\tests.fs", 1).Should().Be(TestStatus.Passed);
        tracker.GetStatusForLine("C:\\Code\\Tests.FS", 1).Should().Be(TestStatus.Passed);
    }

    // ── Unknown / missing origin ──────────────────────────────────────────

    [Fact]
    public void NonSourceMappedOrigin_EntryIsIgnored()
    {
        const string json = """
            {
              "Entries": [{
                "TestId": { "Fields": ["t1"] },
                "Origin": { "Case": "Unknown", "Fields": [] },
                "Status": { "Case": "Passed", "Fields": [] }
              }],
              "Freshness": "Fresh"
            }
            """;

        var tracker = Track("test_results_batch", json);
        tracker.GetAllStatuses().Should().BeEmpty();
    }

    [Fact]
    public void MissingOrigin_EntryIsIgnored()
    {
        const string json = """
            {
              "Entries": [{
                "TestId": { "Fields": ["t1"] },
                "Status": { "Case": "Passed", "Fields": [] }
              }],
              "Freshness": "Fresh"
            }
            """;

        var tracker = Track("test_results_batch", json);
        tracker.GetAllStatuses().Should().BeEmpty();
    }

    [Fact]
    public void EntryWithOriginButNoStatus_RecordedAsNotRun()
    {
        const string json = """
            {
              "Entries": [{
                "TestId": { "Fields": ["t1"] },
                "Origin": { "Case": "SourceMapped", "Fields": ["Test.fs", 5] }
              }],
              "Freshness": "Fresh"
            }
            """;

        var tracker = Track("test_results_batch", json);
        tracker.GetStatusForLine("Test.fs", 5).Should().Be(TestStatus.NotRun);
    }

    // ── Status mapping edge cases ──────────────────────────────────────────

    [Fact]
    public void Skipped_MapsToNotRun()
    {
        const string json = """
            {
              "Entries": [{
                "TestId": { "Fields": ["t1"] },
                "Origin": { "Case": "SourceMapped", "Fields": ["Test.fs", 3] },
                "Status": { "Case": "Skipped", "Fields": [] }
              }],
              "Freshness": "Fresh"
            }
            """;

        var tracker = Track("test_results_batch", json);
        tracker.GetStatusForLine("Test.fs", 3).Should().Be(TestStatus.NotRun);
    }

    [Fact]
    public void PolicyDisabled_MapsToNotRun()
    {
        const string json = """
            {
              "Entries": [{
                "TestId": { "Fields": ["t1"] },
                "Origin": { "Case": "SourceMapped", "Fields": ["Test.fs", 9] },
                "Status": { "Case": "PolicyDisabled", "Fields": [] }
              }],
              "Freshness": "Fresh"
            }
            """;

        var tracker = Track("test_results_batch", json);
        tracker.GetStatusForLine("Test.fs", 9).Should().Be(TestStatus.NotRun);
    }

    [Fact]
    public void UnknownStatusCase_DoesNotUpdateEntry()
    {
        const string json = """
            {
              "Entries": [{
                "TestId": { "Fields": ["t1"] },
                "Origin": { "Case": "SourceMapped", "Fields": ["Test.fs", 2] },
                "Status": { "Case": "SomeFutureStatus", "Fields": [] }
              }],
              "Freshness": "Fresh"
            }
            """;

        var tracker = Track("test_results_batch", json);
        // Location is recorded as NotRun (test discovered), but unknown status leaves it as-is.
        tracker.GetStatusForLine("Test.fs", 2).Should().Be(TestStatus.NotRun);
    }

    // ── Session reset ──────────────────────────────────────────────────────

    [Fact]
    public void SessionReset_ClearsAllState()
    {
        const string batch = """
            {
              "Entries": [{
                "TestId": { "Fields": ["t1"] },
                "Origin": { "Case": "SourceMapped", "Fields": ["Test.fs", 1] },
                "Status": { "Case": "Passed", "Fields": [] }
              }],
              "Freshness": "Fresh"
            }
            """;

        var tracker = new TestStateTracker();
        tracker.ProcessEvent(new SseEvent("test_results_batch", batch));
        tracker.GetStatusForLine("Test.fs", 1).Should().Be(TestStatus.Passed);

        tracker.ProcessEvent(new SseEvent("session_reset", ""));
        tracker.GetStatusForLine("Test.fs", 1).Should().Be(TestStatus.Unknown);
        tracker.GetAllStatuses().Should().BeEmpty();
    }

    [Fact]
    public void SessionHardReset_ClearsAllState()
    {
        const string batch = """
            {
              "Entries": [{
                "TestId": { "Fields": ["t1"] },
                "Origin": { "Case": "SourceMapped", "Fields": ["Test.fs", 1] },
                "Status": { "Case": "Passed", "Fields": [] }
              }],
              "Freshness": "Fresh"
            }
            """;

        var tracker = new TestStateTracker();
        tracker.ProcessEvent(new SseEvent("test_results_batch", batch));
        tracker.ProcessEvent(new SseEvent("session_hard_reset", ""));
        tracker.GetAllStatuses().Should().BeEmpty();
    }

    // ── StateChanged event ────────────────────────────────────────────────

    [Fact]
    public void StateChanged_FiredAfterSuccessfulBatch()
    {
        int fired = 0;
        var tracker = new TestStateTracker();
        tracker.StateChanged += (_, _) => fired++;

        const string json = """
            {
              "Entries": [{
                "TestId": { "Fields": ["t1"] },
                "Origin": { "Case": "SourceMapped", "Fields": ["Test.fs", 1] },
                "Status": { "Case": "Passed", "Fields": [] }
              }],
              "Freshness": "Fresh"
            }
            """;

        tracker.ProcessEvent(new SseEvent("test_results_batch", json));
        fired.Should().Be(1);
    }

    [Fact]
    public void StateChanged_FiredOnReset()
    {
        int fired = 0;
        var tracker = new TestStateTracker();
        tracker.StateChanged += (_, _) => fired++;
        tracker.ProcessEvent(new SseEvent("session_reset", ""));
        fired.Should().Be(1);
    }

    [Fact]
    public void StateChanged_NotFiredForUnrecognisedEvent()
    {
        int fired = 0;
        var tracker = new TestStateTracker();
        tracker.StateChanged += (_, _) => fired++;
        tracker.ProcessEvent(new SseEvent("some_other_event", "{}"));
        fired.Should().Be(0);
    }

    // ── Resilience ────────────────────────────────────────────────────────

    [Fact]
    public void MalformedJson_DoesNotThrow()
    {
        var tracker = new TestStateTracker();
        Action act = () => tracker.ProcessEvent(new SseEvent("test_results_batch", "not-json"));
        act.Should().NotThrow();
    }

    [Fact]
    public void EmptyJson_DoesNotThrow()
    {
        var tracker = new TestStateTracker();
        Action act = () => tracker.ProcessEvent(new SseEvent("test_results_batch", "{}"));
        act.Should().NotThrow();
        tracker.GetAllStatuses().Should().BeEmpty();
    }

    [Fact]
    public void EmptyEntriesArray_ProducesNoStatuses()
    {
        const string json = """{"Entries": [], "Freshness": "Fresh"}""";
        var tracker = Track("test_results_batch", json);
        tracker.GetAllStatuses().Should().BeEmpty();
    }
}
