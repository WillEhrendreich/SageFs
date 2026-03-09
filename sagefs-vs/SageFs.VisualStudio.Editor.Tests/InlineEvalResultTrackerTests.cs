using System;
using FluentAssertions;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// Tests for <see cref="InlineEvalResultTracker"/> state transitions.
/// </summary>
public sealed class InlineEvalResultTrackerTests
{
    // ── SetResult ────────────────────────────────────────────────────────────

    [Fact]
    public void SetResult_TransitionsToActive()
    {
        var tracker = new InlineEvalResultTracker();
        tracker.SetResult("C:\\test.fs", 10, "42");

        tracker.Get("C:\\test.fs", 10).Should().BeOfType<AdornmentState.ActiveState>()
            .Which.Result.Should().Be("42");
    }

    [Fact]
    public void SetResult_ReplacesExistingActiveResult()
    {
        var tracker = new InlineEvalResultTracker();
        tracker.SetResult("C:\\test.fs", 5, "old");
        tracker.SetResult("C:\\test.fs", 5, "new");

        tracker.Get("C:\\test.fs", 5).Should().BeOfType<AdornmentState.ActiveState>()
            .Which.Result.Should().Be("new");
    }

    [Fact]
    public void SetResult_ReplacesStaleWithActive()
    {
        var tracker = new InlineEvalResultTracker();
        tracker.SetResult("C:\\test.fs", 3, "old");
        tracker.MarkStale("C:\\test.fs", 3);
        tracker.SetResult("C:\\test.fs", 3, "fresh");

        tracker.Get("C:\\test.fs", 3).Should().BeOfType<AdornmentState.ActiveState>()
            .Which.Result.Should().Be("fresh");
    }

    // ── MarkStale ─────────────────────────────────────────────────────────────

    [Fact]
    public void MarkStale_ActiveBecomesStale_PreservesResult()
    {
        var tracker = new InlineEvalResultTracker();
        tracker.SetResult("C:\\test.fs", 7, "hello");
        tracker.MarkStale("C:\\test.fs", 7);

        tracker.Get("C:\\test.fs", 7).Should().BeOfType<AdornmentState.StaleState>()
            .Which.Result.Should().Be("hello");
    }

    [Fact]
    public void MarkStale_AlreadyStale_NoChange()
    {
        var tracker = new InlineEvalResultTracker();
        tracker.SetResult("C:\\test.fs", 2, "val");
        tracker.MarkStale("C:\\test.fs", 2);

        int fired = 0;
        tracker.StateChanged += (_, _) => fired++;
        tracker.MarkStale("C:\\test.fs", 2); // already stale — no event expected

        fired.Should().Be(0);
    }

    [Fact]
    public void MarkStale_Gone_NoChange()
    {
        var tracker = new InlineEvalResultTracker();
        var act = () => tracker.MarkStale("C:\\test.fs", 99);
        act.Should().NotThrow();
        tracker.Get("C:\\test.fs", 99).Should().BeOfType<AdornmentState.GoneState>();
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_RemovesResult()
    {
        var tracker = new InlineEvalResultTracker();
        tracker.SetResult("C:\\test.fs", 1, "x");
        tracker.Clear("C:\\test.fs", 1);

        tracker.Get("C:\\test.fs", 1).Should().BeOfType<AdornmentState.GoneState>();
    }

    // ── ClearFile ─────────────────────────────────────────────────────────────

    [Fact]
    public void ClearFile_RemovesAllResultsForFile()
    {
        var tracker = new InlineEvalResultTracker();
        tracker.SetResult("C:\\test.fs", 1, "a");
        tracker.SetResult("C:\\test.fs", 5, "b");
        tracker.SetResult("C:\\other.fs", 3, "c");

        tracker.ClearFile("C:\\test.fs");

        tracker.HasAnyForFile("C:\\test.fs").Should().BeFalse();
        tracker.HasAnyForFile("C:\\other.fs").Should().BeTrue();
    }

    // ── Path normalisation ────────────────────────────────────────────────────

    [Fact]
    public void Get_PathNormalisation_ForwardSlashEqualsBackslash()
    {
        var tracker = new InlineEvalResultTracker();
        tracker.SetResult("C:/test.fs", 1, "val");

        tracker.Get("C:\\test.fs", 1).Should().BeOfType<AdornmentState.ActiveState>();
    }

    [Fact]
    public void Get_PathNormalisation_CaseInsensitive()
    {
        var tracker = new InlineEvalResultTracker();
        tracker.SetResult("C:\\TEST.FS", 1, "val");

        tracker.Get("c:\\test.fs", 1).Should().BeOfType<AdornmentState.ActiveState>();
    }

    // ── StateChanged event ────────────────────────────────────────────────────

    [Fact]
    public void StateChanged_FiredOnSetResult()
    {
        var tracker = new InlineEvalResultTracker();
        string? firedPath = null;
        int firedLine = 0;
        tracker.StateChanged += (p, l) => { firedPath = p; firedLine = l; };

        tracker.SetResult("C:\\test.fs", 8, "ok");

        firedPath.Should().NotBeNull();
        firedLine.Should().Be(8);
    }

    [Fact]
    public void StateChanged_FiredOnMarkStale()
    {
        var tracker = new InlineEvalResultTracker();
        tracker.SetResult("C:\\test.fs", 4, "val");

        int fired = 0;
        tracker.StateChanged += (_, _) => fired++;
        tracker.MarkStale("C:\\test.fs", 4);

        fired.Should().Be(1);
    }

    // ── GetAllForFile ─────────────────────────────────────────────────────────

    [Fact]
    public void GetAllForFile_OnlyReturnsMatchingFile()
    {
        var tracker = new InlineEvalResultTracker();
        tracker.SetResult("C:\\a.fs", 1, "x");
        tracker.SetResult("C:\\b.fs", 2, "y");

        var results = new System.Collections.Generic.List<(int, AdornmentState)>(
            tracker.GetAllForFile("C:\\a.fs"));

        results.Should().HaveCount(1);
        results[0].Item1.Should().Be(1);
    }

    [Fact]
    public void GetAllForFile_SkipsGoneEntries()
    {
        var tracker = new InlineEvalResultTracker();
        tracker.SetResult("C:\\test.fs", 1, "live");
        tracker.SetResult("C:\\test.fs", 2, "gone");
        tracker.Clear("C:\\test.fs", 2);

        var results = new System.Collections.Generic.List<(int, AdornmentState)>(
            tracker.GetAllForFile("C:\\test.fs"));

        results.Should().HaveCount(1);
    }
}
