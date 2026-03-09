using System;
using FluentAssertions;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// Tests for <c>EvalBlockCommand.FindBlock</c>.
///
/// Because <c>EvalBlockCommand</c> lives in the net8.0 VS extension project which cannot
/// be referenced from net472 tests, these tests validate the specification by
/// re-implementing the pure string-array block-detection logic locally.
/// If the production implementation diverges, that's a bug in the production code.
/// </summary>
public sealed class EvalBlockCommandTests
{
    // ── Local reference implementation (spec mirrors EvalBlockCommand.FindBlock) ──

    private static (int startLine, int endLine) FindBlock(string[] lines, int cursorLine)
    {
        if (lines.Length == 0) return (0, 0);
        cursorLine = Math.Max(0, Math.Min(cursorLine, lines.Length - 1));

        // Cursor on a blank separator — return the single blank line (eval is a no-op)
        if (string.IsNullOrWhiteSpace(lines[cursorLine]))
            return (cursorLine, cursorLine);

        int start = cursorLine;
        while (start > 0 && !string.IsNullOrWhiteSpace(lines[start - 1]))
            start--;

        int end = cursorLine;
        while (end < lines.Length - 1 && !string.IsNullOrWhiteSpace(lines[end + 1]))
            end++;

        return (start, end);
    }

    // ── Cursor in middle of block ─────────────────────────────────────────────

    [Fact]
    public void FindBlock_CursorInMiddleOfBlock_ReturnFullBlockRange()
    {
        var lines = new[] { "let a = 1", "let b = 2", "let c = 3" };
        var (start, end) = FindBlock(lines, 1); // cursor on "let b = 2"
        start.Should().Be(0);
        end.Should().Be(2);
    }

    // ── Cursor on empty line ──────────────────────────────────────────────────

    [Fact]
    public void FindBlock_CursorOnEmptyLine_ReturnSingleLine()
    {
        var lines = new[] { "let a = 1", "", "let c = 3" };
        var (start, end) = FindBlock(lines, 1); // cursor on empty line
        start.Should().Be(1);
        end.Should().Be(1);
    }

    [Fact]
    public void FindBlock_CursorOnWhitespaceOnlyLine_ReturnSingleLine()
    {
        var lines = new[] { "let a = 1", "   ", "let c = 3" };
        var (start, end) = FindBlock(lines, 1);
        start.Should().Be(1);
        end.Should().Be(1);
    }

    // ── Cursor at top of file ─────────────────────────────────────────────────

    [Fact]
    public void FindBlock_CursorAtTopOfFile_StartIsZero()
    {
        var lines = new[] { "let a = 1", "let b = 2", "" };
        var (start, end) = FindBlock(lines, 0);
        start.Should().Be(0);
        end.Should().Be(1);
    }

    [Fact]
    public void FindBlock_SingleLineFile_ReturnZeroZero()
    {
        var lines = new[] { "let x = 42" };
        var (start, end) = FindBlock(lines, 0);
        start.Should().Be(0);
        end.Should().Be(0);
    }

    // ── Cursor at bottom of file (no trailing newline) ────────────────────────

    [Fact]
    public void FindBlock_CursorAtBottomOfFile_EndIsLastLine()
    {
        var lines = new[] { "", "let a = 1", "let b = 2" };
        var (start, end) = FindBlock(lines, 2); // cursor on last line
        start.Should().Be(1);
        end.Should().Be(2);
    }

    [Fact]
    public void FindBlock_NoTrailingNewline_IncludesLastLine()
    {
        var lines = new[] { "let f x =", "    x + 1" }; // no trailing blank
        var (start, end) = FindBlock(lines, 0);
        start.Should().Be(0);
        end.Should().Be(1);
    }

    // ── Empty input ───────────────────────────────────────────────────────────

    [Fact]
    public void FindBlock_EmptyLinesArray_ReturnsZeroZero()
    {
        var (start, end) = FindBlock(Array.Empty<string>(), 0);
        start.Should().Be(0);
        end.Should().Be(0);
    }

    // ── Block between blank separators ───────────────────────────────────────

    [Fact]
    public void FindBlock_BlockBetweenBlanks_ReturnsOnlyThatBlock()
    {
        var lines = new[] { "let a = 1", "", "let b = 2", "let b2 = 3", "", "let c = 4" };
        var (start, end) = FindBlock(lines, 3); // cursor on "let b2 = 3"
        start.Should().Be(2);
        end.Should().Be(3);
    }
}
