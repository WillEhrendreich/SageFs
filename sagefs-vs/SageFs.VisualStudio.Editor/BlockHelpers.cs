using System;
using System.Collections.Generic;

namespace SageFs.VisualStudio.Editor;

/// <summary>
/// Pure block-parsing helpers for the Editor (MEF net472) layer.
/// These contain no VS API dependencies and are fully testable without a VS host.
/// The net8.0 layer has a parallel <c>BlockHelpers</c> with the same algorithms.
/// </summary>
internal static class BlockHelpers
{
  /// <summary>
  /// Returns the 0-based (startLine, endLine) line range of the block containing
  /// <paramref name="cursorOffset"/>. Returns (0, 0) for empty input.
  /// </summary>
  public static (int startLine, int endLine) FindBlockLineRange(string text, int cursorOffset)
  {
    if (string.IsNullOrEmpty(text)) return (0, 0);

    FindBlockBounds(text, cursorOffset, out var blockStart, out var blockEnd);
    if (blockStart >= blockEnd) return (0, 0);

    var startLine = CountNewlines(text, 0, blockStart);
    var endLine   = CountNewlines(text, 0, blockEnd);
    return (startLine, endLine);
  }

  /// <summary>
  /// Returns all non-empty blocks in document order, for both semicolon mode and blank-line mode.
  /// Empty and whitespace-only blocks are filtered out.
  /// </summary>
  public static List<string> FindAllBlocks(string text)
  {
    var result = new List<string>();
    if (string.IsNullOrEmpty(text)) return result;

    if (DetectSemicolonMode(text))
      CollectSemicolonBlocks(text, result);
    else
      CollectBlankLineBlocks(text, result);

    return result;
  }

  /// <summary>
  /// Pure testable version: returns true if a code element with the given kind, text
  /// and attribute names should be labelled "▶ Run Test" instead of "▶ Eval".
  /// </summary>
  public static bool IsTestElement(string elementKind, string elementText, string[] attributeNames)
  {
    if (elementKind is not ("Function" or "Method"))
      return false;

    foreach (var attr in attributeNames)
    {
      if (attr is "Test" or "Fact" or "Theory" or "Property" or "EntryPoint")
        return true;
    }

    return elementText.Contains("testCase ")
        || elementText.Contains("testProperty ")
        || elementText.Contains("testList ");
  }

  // ── Private helpers ───────────────────────────────────────────────────────

  private static bool DetectSemicolonMode(string text)
  {
    for (var i = 0; i < text.Length - 1; i++)
    {
      if (text[i] == ';' && text[i + 1] == ';')
        return true;
    }
    return false;
  }

  private static void FindBlockBounds(string text, int cursorOffset, out int start, out int end)
  {
    if (DetectSemicolonMode(text))
      FindSemicolonBounds(text, cursorOffset, out start, out end);
    else
      FindBlankLineBounds(text, cursorOffset, out start, out end);
  }

  private static void FindSemicolonBounds(string text, int cursor, out int start, out int end)
  {
    start = 0;
    for (var i = Math.Min(cursor, text.Length - 1); i >= 1; i--)
    {
      if (text[i] == ';' && text[i - 1] == ';')
      {
        start = i + 1;
        while (start < text.Length && (text[start] == '\r' || text[start] == '\n'))
          start++;
        break;
      }
    }
    end = text.Length;
    for (var i = cursor; i < text.Length - 1; i++)
    {
      if (text[i] == ';' && text[i + 1] == ';') { end = i + 2; break; }
    }
  }

  private static void FindBlankLineBounds(string text, int cursor, out int start, out int end)
  {
    start = 0;
    for (var i = Math.Min(cursor, text.Length - 1); i >= 1; i--)
    {
      if (IsBlankLineBoundary(text, i))
      {
        start = i + 1;
        while (start < text.Length && (text[start] == '\r' || text[start] == '\n'))
          start++;
        break;
      }
    }
    end = text.Length;
    for (var i = cursor; i < text.Length - 1; i++)
    {
      if (IsBlankLineBoundary(text, i)) { end = i; break; }
    }
  }

  private static bool IsBlankLineBoundary(string text, int pos)
  {
    if (pos >= text.Length || text[pos] != '\n') return false;
    var next = pos + 1;
    if (next < text.Length && text[next] == '\r') next++;
    return next < text.Length && text[next] == '\n';
  }

  private static void CollectSemicolonBlocks(string text, List<string> result)
  {
    var pos = 0;
    while (pos < text.Length)
    {
      var endSemi = -1;
      for (var i = pos; i < text.Length - 1; i++)
      {
        if (text[i] == ';' && text[i + 1] == ';') { endSemi = i; break; }
      }

      int nextPos;
      string block;
      if (endSemi >= 0)
      {
        block   = text.Substring(pos, endSemi + 2 - pos).Trim();
        nextPos = endSemi + 2;
      }
      else
      {
        block   = text.Substring(pos).Trim();
        nextPos = text.Length;
      }

      // Filter blocks that are empty or consist only of ;; terminators
      var stripped = block.TrimEnd(';').Trim();
      if (!string.IsNullOrWhiteSpace(stripped))
        result.Add(block);

      pos = nextPos;
      while (pos < text.Length && (text[pos] == '\r' || text[pos] == '\n'))
        pos++;
    }
  }

  private static void CollectBlankLineBlocks(string text, List<string> result)
  {
    var pos = 0;
    while (pos < text.Length)
    {
      var end = text.Length;
      for (var i = pos; i < text.Length - 1; i++)
      {
        if (IsBlankLineBoundary(text, i)) { end = i; break; }
      }

      var block = text.Substring(pos, end - pos).Trim();
      if (!string.IsNullOrWhiteSpace(block))
        result.Add(block);

      pos = end + 1;
      while (pos < text.Length && (text[pos] == '\r' || text[pos] == '\n'))
        pos++;
    }
  }

  private static int CountNewlines(string text, int from, int to)
  {
    var count = 0;
    for (var i = from; i < Math.Min(to, text.Length); i++)
    {
      if (text[i] == '\n') count++;
    }
    return count;
  }
}
