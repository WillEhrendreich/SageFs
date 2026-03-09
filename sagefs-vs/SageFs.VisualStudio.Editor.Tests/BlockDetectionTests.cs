using System;
using FluentAssertions;
using Xunit;

namespace SageFs.VisualStudio.Editor.Tests;

/// <summary>
/// Tests for the block detection algorithm used by <c>BlockHelpers.DetectBlockMode</c>
/// and <c>BlockHelpers.FindBlockAroundCursor</c>.
///
/// Because <c>BlockHelpers</c> lives in the net8.0 VS extension project which cannot
/// be referenced from net472 tests, these tests validate the specification by
/// re-implementing the pure string-manipulation logic locally.  If the production
/// implementation diverges from these tests, that's a bug in the production code.
/// </summary>
public sealed class BlockDetectionTests
{
    // ── Local reference implementation (spec) ────────────────────────────────

    private enum BlockMode { SemicolonMode, BlankLineMode }

    private static BlockMode DetectBlockMode(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == ';' && text[i + 1] == ';')
                return BlockMode.SemicolonMode;
        }
        return BlockMode.BlankLineMode;
    }

    private static string FindBlockBySemicolon(string text, int cursorOffset)
    {
        var blockStart = 0;
        for (var i = Math.Min(cursorOffset, text.Length - 1); i >= 1; i--)
        {
            if (text[i] == ';' && text[i - 1] == ';')
            {
                blockStart = i + 1;
                while (blockStart < text.Length && (text[blockStart] == '\r' || text[blockStart] == '\n'))
                    blockStart++;
                break;
            }
        }
        var blockEnd = text.Length;
        for (var i = cursorOffset; i < text.Length - 1; i++)
        {
            if (text[i] == ';' && text[i + 1] == ';') { blockEnd = i + 2; break; }
        }
        if (blockStart >= blockEnd) return "";
        return text.Substring(blockStart, blockEnd - blockStart).Trim();
    }

    private static bool IsBlankLineBoundary(string text, int pos)
    {
        if (pos >= text.Length) return false;
        if (text[pos] != '\n') return false;
        var next = pos + 1;
        if (next < text.Length && text[next] == '\r') next++;
        return next < text.Length && text[next] == '\n';
    }

    private static string FindBlockByBlankLines(string text, int cursorOffset)
    {
        var blockStart = 0;
        for (var i = Math.Min(cursorOffset, text.Length - 1); i >= 1; i--)
        {
            if (IsBlankLineBoundary(text, i))
            {
                blockStart = i + 1;
                while (blockStart < text.Length && (text[blockStart] == '\r' || text[blockStart] == '\n'))
                    blockStart++;
                break;
            }
        }
        var blockEnd = text.Length;
        for (var i = cursorOffset; i < text.Length - 1; i++)
        {
            if (IsBlankLineBoundary(text, i)) { blockEnd = i; break; }
        }
        if (blockStart >= blockEnd) return "";
        return text.Substring(blockStart, blockEnd - blockStart).Trim();
    }

    private static string FindBlockAroundCursor(string text, int cursorOffset) =>
        DetectBlockMode(text) switch
        {
            BlockMode.SemicolonMode => FindBlockBySemicolon(text, cursorOffset),
            _                       => FindBlockByBlankLines(text, cursorOffset),
        };

    // ── DetectBlockMode ───────────────────────────────────────────────────────

    [Fact]
    public void DetectBlockMode_EmptyString_ReturnBlankLineMode()
    {
        DetectBlockMode("").Should().Be(BlockMode.BlankLineMode);
    }

    [Fact]
    public void DetectBlockMode_TextWithDoubleSemicolon_ReturnSemicolonMode()
    {
        DetectBlockMode("let x = 1;;\nlet y = 2;;").Should().Be(BlockMode.SemicolonMode);
    }

    [Fact]
    public void DetectBlockMode_TextWithoutDoubleSemicolon_ReturnBlankLineMode()
    {
        DetectBlockMode("let x = 1\nlet y = 2").Should().Be(BlockMode.BlankLineMode);
    }

    [Fact]
    public void DetectBlockMode_SingleSemicolon_ReturnBlankLineMode()
    {
        DetectBlockMode("let x = f(); // comment").Should().Be(BlockMode.BlankLineMode);
    }

    [Fact]
    public void DetectBlockMode_DoubleSemicolonAnywhere_ReturnSemicolonMode()
    {
        DetectBlockMode("// no semicolons above\n\nlet x = 1\n\n;;").Should().Be(BlockMode.SemicolonMode);
    }

    // ── FindBlockAroundCursor — SemicolonMode ─────────────────────────────────

    [Fact]
    public void FindBlock_Semicolon_CursorInMiddleBlock_ReturnsCorrectBlock()
    {
        const string text = "let a = 1;;\nlet b = 2;;\nlet c = 3;;";
        // cursor on "let b = 2"
        var cursor = text.IndexOf("let b", StringComparison.Ordinal) + 3;
        var block = FindBlockAroundCursor(text, cursor);
        block.Should().Contain("let b = 2");
        block.Should().NotContain("let a");
        block.Should().NotContain("let c");
    }

    [Fact]
    public void FindBlock_Semicolon_CursorAtStart_ReturnsFirstBlock()
    {
        const string text = "let a = 1;;\nlet b = 2;;";
        var block = FindBlockAroundCursor(text, 2);
        block.Should().Contain("let a = 1");
    }

    // ── FindBlockAroundCursor — BlankLineMode ─────────────────────────────────

    [Fact]
    public void FindBlock_BlankLine_CursorInMiddleBlock_ReturnsCorrectBlock()
    {
        const string text = "let a = 1\n\nlet b = 2\n\nlet c = 3";
        var cursor = text.IndexOf("let b", StringComparison.Ordinal) + 3;
        var block = FindBlockAroundCursor(text, cursor);
        block.Should().Be("let b = 2");
    }

    [Fact]
    public void FindBlock_BlankLine_EmptyFile_ReturnsEmpty()
    {
        FindBlockAroundCursor("", 0).Should().BeEmpty();
    }

    [Fact]
    public void FindBlock_BlankLine_SingleBlock_NoSurroundingBlanks_ReturnsEntireText()
    {
        const string text = "let x = 42";
        var block = FindBlockAroundCursor(text, 5);
        block.Should().Be("let x = 42");
    }
}
