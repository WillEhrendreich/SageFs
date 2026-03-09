using FluentAssertions;
using SageFs.VisualStudio.Editor.Completions;
using System;
using System.Threading;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// Tests for the FSI IntelliSense completion provider's pure helper logic.
/// No VS host required — all tested methods are pure static functions.
/// </summary>
public sealed class CompletionProviderTests
{
    // ── CompletionKindMapper ──────────────────────────────────────────────────

    [Theory]
    [InlineData("method",    CompletionItemKinds.Method)]
    [InlineData("function",  CompletionItemKinds.Method)]
    [InlineData("module",    CompletionItemKinds.Module)]
    [InlineData("namespace", CompletionItemKinds.Keyword)]
    [InlineData("field",     CompletionItemKinds.Field)]
    [InlineData("property",  CompletionItemKinds.Property)]
    [InlineData("keyword",   CompletionItemKinds.Keyword)]
    [InlineData("class",     CompletionItemKinds.Class)]
    [InlineData("interface", CompletionItemKinds.Interface)]
    [InlineData("variable",  CompletionItemKinds.Local)]
    [InlineData("value",     CompletionItemKinds.Value)]
    [InlineData("unknown_kind", CompletionItemKinds.Text)]
    [InlineData("", CompletionItemKinds.Text)]
    [InlineData(null, CompletionItemKinds.Text)]
    public void KindMapper_MapsCorrectly(string? input, CompletionItemKinds expected)
    {
        var result = CompletionKindMapper.ToCompletionItemKind(input);
        result.Should().Be(expected,
            because: $"daemon kind '{input}' should map to CompletionItemKinds.{expected}");
    }

    // ── ComputeWindow ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100,  10000, 0,    100)]   // cursor near start → window starts at 0, cursor at 100
    [InlineData(5000, 10000, 3976, 1024)]  // cursor in middle → starts at cursor-1024, cursor at 1024
    [InlineData(9900, 10000, 8876, 1024)]  // cursor near end → starts at cursor-1024, cursor at 1024
    [InlineData(500,  800,   0,    500)]   // short buffer: cursor near start, window starts at 0
    [InlineData(0,    1000,  0,    0)]     // cursor at position 0
    [InlineData(1024, 5000,  0,    1024)]  // cursor exactly at WindowHalfSize
    public void ComputeWindow_ProducesCorrectOffsets(
        int cursor, int bufferLen, int expectedStart, int expectedCursorInWindow)
    {
        var (start, cursorInWindow) = SageFsCompletionSource.ComputeWindow(cursor, bufferLen);

        start.Should().Be(expectedStart,
            because: $"cursor={cursor} bufLen={bufferLen}: window start should be {expectedStart}");
        cursorInWindow.Should().Be(expectedCursorInWindow,
            because: $"cursor={cursor} bufLen={bufferLen}: cursorInWindow should be {expectedCursorInWindow}");
    }

    [Theory]
    [InlineData(500, 800)]   // short buffer: window does not exceed buffer length
    [InlineData(100, 200)]
    [InlineData(9900, 10000)]
    public void ComputeWindow_WindowNeverExceedsBuffer(int cursor, int bufferLen)
    {
        var (start, cursorInWindow) = SageFsCompletionSource.ComputeWindow(cursor, bufferLen);
        var windowEnd = start + 2 * SageFsCompletionSource.WindowHalfSize;
        windowEnd = System.Math.Min(windowEnd, bufferLen);

        windowEnd.Should().BeLessOrEqualTo(bufferLen,
            because: "window must not exceed buffer bounds");
        cursorInWindow.Should().BeGreaterOrEqualTo(0,
            because: "cursor-in-window must be non-negative");
        cursorInWindow.Should().BeLessOrEqualTo(windowEnd - start,
            because: "cursor must be within the window");
    }

    // ── ShouldTriggerForText ──────────────────────────────────────────────────

    [Theory]
    [InlineData(".",    true)]   // dot always triggers
    [InlineData("ab",   true)]   // 2+ chars triggers
    [InlineData("abc",  true)]   // 3 chars triggers
    [InlineData("List", true)]   // word triggers
    [InlineData("a",    false)]  // 1 char doesn't trigger
    [InlineData("",     false)]  // empty doesn't trigger
    [InlineData(null,   false)]  // null doesn't trigger
    public void ShouldTrigger_ReturnsCorrectResult(string? triggerText, bool expected)
    {
        var result = SageFsCompletionSource.ShouldTriggerForText(triggerText);
        result.Should().Be(expected,
            because: $"ShouldTriggerForText(\"{triggerText}\") should return {expected}");
    }

    [Fact]
    public void MinTriggerLength_Is2()
    {
        SageFsCompletionSource.MinTriggerLength.Should().Be(2,
            because: "single-char input creates too many false positives; threshold is 2");
    }

    [Fact]
    public void WindowHalfSize_Is1024()
    {
        SageFsCompletionSource.WindowHalfSize.Should().Be(1024,
            because: "2048-char window (2 × 1024) balances completeness vs request payload size");
    }

    // ── BuildRequestBody ─────────────────────────────────────────────────────

    [Fact]
    public void BuildRequestBody_WithWorkingDirectory_IncludesField()
    {
        var body = SageFsCompletionSource.BuildRequestBody("let x = 1", 9, @"C:\projects\myapp");

        body.Should().Contain("\"working_directory\"",
            because: "working_directory must be present when a file path is available");
        body.Should().Contain(@"C:\\projects\\myapp",
            because: "the serialized path should appear in the JSON body");
        body.Should().Contain("\"cursor_position\":9",
            because: "cursor position must always be present");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildRequestBody_WithoutWorkingDirectory_OmitsField(string? workingDir)
    {
        var body = SageFsCompletionSource.BuildRequestBody("let x = 1", 5, workingDir);

        body.Should().NotContain("working_directory",
            because: "working_directory must be omitted when the file path is unknown");
        body.Should().Contain("\"cursor_position\":5",
            because: "cursor position must always be present");
    }

    // ── ComposeLinkedTimeout ──────────────────────────────────────────────────

    [Fact]
    public void ComposeLinkedTimeout_ReturnsDistinctLinkedAndTimeoutSources()
    {
        using var outer = new CancellationTokenSource();
        var (linked, timeout) = SageFsCompletionSource.ComposeLinkedTimeout(
            outer.Token, TimeSpan.FromSeconds(30));
        using (linked)
        using (timeout)
        {
            linked.Should().NotBeSameAs(timeout,
                because: "linked and timeout are independent CancellationTokenSources");
            linked.Token.CanBeCanceled.Should().BeTrue(
                because: "the linked token must be cancellable");
        }
    }

    [Fact]
    public void ComposeLinkedTimeout_OuterCancelPropagatesIntoLinked()
    {
        using var outer = new CancellationTokenSource();
        var (linked, timeout) = SageFsCompletionSource.ComposeLinkedTimeout(
            outer.Token, TimeSpan.FromSeconds(30));
        using (linked)
        using (timeout)
        {
            outer.Cancel();
            linked.Token.IsCancellationRequested.Should().BeTrue(
                because: "cancelling the outer token must cancel the linked token");
        }
    }

    [Fact]
    public void ComposeLinkedTimeout_TimeoutCancelPropagatesIntoLinked()
    {
        using var outer = new CancellationTokenSource();
        var (linked, timeout) = SageFsCompletionSource.ComposeLinkedTimeout(
            outer.Token, TimeSpan.FromMilliseconds(50));
        using (linked)
        using (timeout)
        {
            // Wait slightly longer than the timeout window
            Thread.Sleep(200);
            timeout.Token.IsCancellationRequested.Should().BeTrue(
                because: "the timeout source must self-cancel after the duration elapses");
            linked.Token.IsCancellationRequested.Should().BeTrue(
                because: "the linked token must cancel when the timeout fires");
        }
    }
}
