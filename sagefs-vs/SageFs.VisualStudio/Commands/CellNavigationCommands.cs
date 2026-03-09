namespace SageFs.VisualStudio.Commands;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Editor;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

// ── Block detection mode ──────────────────────────────────────────────────────

internal enum BlockMode { SemicolonMode, BlankLineMode }

// ── Shared block helpers ──────────────────────────────────────────────────────

internal static class BlockHelpers
{
    /// <summary>
    /// Returns <see cref="BlockMode.SemicolonMode"/> if the text contains any <c>;;</c>,
    /// otherwise <see cref="BlockMode.BlankLineMode"/>.
    /// </summary>
    public static BlockMode DetectBlockMode(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == ';' && text[i + 1] == ';')
                return BlockMode.SemicolonMode;
        }
        return BlockMode.BlankLineMode;
    }

    /// <summary>
    /// Finds the code block surrounding <paramref name="cursorOffset"/>, respecting
    /// the detected <see cref="BlockMode"/> of the file.
    /// </summary>
    public static string FindBlockAroundCursor(string text, int cursorOffset)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return DetectBlockMode(text) switch
        {
            BlockMode.SemicolonMode => FindBlockBySemicolon(text, cursorOffset),
            _                       => FindBlockByBlankLines(text, cursorOffset),
        };
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
        return text[blockStart..blockEnd].Trim();
    }

    private static string FindBlockByBlankLines(string text, int cursorOffset)
    {
        // Scan backward: stop at a blank line (two consecutive newlines) or start of file
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

        // Scan forward: stop at a blank line or end of file
        var blockEnd = text.Length;
        for (var i = cursorOffset; i < text.Length - 1; i++)
        {
            if (IsBlankLineBoundary(text, i)) { blockEnd = i; break; }
        }

        if (blockStart >= blockEnd) return "";
        return text[blockStart..blockEnd].Trim();
    }

    /// <summary>
    /// Returns all non-empty blocks in document order, for both semicolon mode
    /// and blank-line mode. Empty and whitespace-only blocks are filtered out.
    /// </summary>
    public static System.Collections.Generic.List<string> FindAllBlocks(string text)
    {
        var result = new System.Collections.Generic.List<string>();
        if (string.IsNullOrEmpty(text)) return result;

        if (DetectBlockMode(text) == BlockMode.SemicolonMode)
            CollectSemicolonBlocks(text, result);
        else
            CollectBlankLineBlocks(text, result);

        return result;
    }

    private static void CollectSemicolonBlocks(string text, System.Collections.Generic.List<string> result)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var endSemi = -1;
            for (var i = pos; i < text.Length - 1; i++)
            {
                if (text[i] == ';' && text[i + 1] == ';') { endSemi = i; break; }
            }

            string block;
            int nextPos;
            if (endSemi >= 0)
            {
                block   = text[pos..(endSemi + 2)].Trim();
                nextPos = endSemi + 2;
            }
            else
            {
                block   = text[pos..].Trim();
                nextPos = text.Length;
            }

            var stripped = block.TrimEnd(';').Trim();
            if (!string.IsNullOrWhiteSpace(stripped))
                result.Add(block);

            pos = nextPos;
            while (pos < text.Length && (text[pos] == '\r' || text[pos] == '\n'))
                pos++;
        }
    }

    private static void CollectBlankLineBlocks(string text, System.Collections.Generic.List<string> result)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var end = text.Length;
            for (var i = pos; i < text.Length - 1; i++)
            {
                if (IsBlankLineBoundary(text, i)) { end = i; break; }
            }

            var block = text[pos..end].Trim();
            if (!string.IsNullOrWhiteSpace(block))
                result.Add(block);

            pos = end + 1;
            while (pos < text.Length && (text[pos] == '\r' || text[pos] == '\n'))
                pos++;
        }
    }

    private static bool IsBlankLineBoundary(string text, int pos)
    {
        // A blank line boundary is \n\n or \n\r\n
        if (text[pos] != '\n') return false;
        var next = pos + 1;
        if (next < text.Length && text[next] == '\r') next++;
        return next < text.Length && text[next] == '\n';
    }
}

/// <summary>
/// Evaluates the next ;; delimited block after the cursor and reports the result.
/// Use repeatedly to step through an .fsx file block-by-block (Ctrl+Alt+]).
/// This is the VS equivalent of the SageFs TUI/Neovim Ctrl+] cell advance workflow.
/// </summary>
[VisualStudioContribution]
internal class NextBlockCommand : Command
{
  private readonly Core.SageFsClient client;
  private readonly Core.EvalCancellation cancellation;
  private OutputChannel? output;

  public NextBlockCommand(Core.SageFsClient client, Core.EvalCancellation cancellation)
  {
    this.client = client;
    this.cancellation = cancellation;
  }

  public override CommandConfiguration CommandConfiguration => new("%SageFs.NextBlock.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.GoToNext, IconSettings.IconAndText),
    Shortcuts = [new CommandShortcutConfiguration(ModifierKey.ControlLeftAlt, Key.VK_OEM_6)],
    VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveEditorContentType, ".+"),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    using var textView = await context.GetActiveTextViewAsync(ct);
    if (textView is null) return;

    var text = textView.Document.Text.CopyToString();
    var cursorOffset = textView.Selection.Extent.Start.Offset;
    var (nextCode, nextLine) = FindNextBlock(text, cursorOffset);
    if (nextCode is null)
    {
      if (output is not null) await output.WriteLineAsync("○ No next block found");
      return;
    }

    var filePath = textView.Document.Uri.LocalPath;
    using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
      ct, cancellation.StartNew());
    try
    {
      var result = await client.EvalWithContextAsync(nextCode, filePath, "block", nextLine, linked.Token);
      if (output is not null)
      {
        var icon = result.ExitCode == 0 ? "✓" : "✗";
        await output.WriteLineAsync($"{icon} [line {nextLine}] {result.Output.Trim()}");
      }
    }
    catch (OperationCanceledException)
    {
      if (output is not null) await output.WriteLineAsync("⊘ Cancelled");
    }
    finally { cancellation.Done(); }
  }

  private static (string? code, int lineNumber) FindNextBlock(string text, int fromOffset)
  {
    for (var i = fromOffset; i < text.Length - 1; i++)
    {
      if (text[i] == ';' && text[i + 1] == ';')
      {
        var start = i + 2;
        while (start < text.Length && (text[start] == '\r' || text[start] == '\n')) start++;
        if (start >= text.Length) return (null, 0);
        // Find the end of this block
        var end = text.Length;
        for (var j = start; j < text.Length - 1; j++)
        {
          if (text[j] == ';' && text[j + 1] == ';') { end = j + 2; break; }
        }
        var lineNumber = text[..start].Split('\n').Length;
        return (text[start..end].Trim(), lineNumber);
      }
    }
    return (null, 0);
  }
}

/// <summary>
/// Evaluates the previous ;; delimited block before the cursor and reports the result.
/// Use to re-run an earlier block without scrolling (Ctrl+Alt+[).
/// Mirrors the SageFs TUI Ctrl+[ binding.
/// </summary>
[VisualStudioContribution]
internal class PrevBlockCommand : Command
{
  private readonly Core.SageFsClient client;
  private readonly Core.EvalCancellation cancellation;
  private OutputChannel? output;

  public PrevBlockCommand(Core.SageFsClient client, Core.EvalCancellation cancellation)
  {
    this.client = client;
    this.cancellation = cancellation;
  }

  public override CommandConfiguration CommandConfiguration => new("%SageFs.PrevBlock.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.GoToPrevious, IconSettings.IconAndText),
    Shortcuts = [new CommandShortcutConfiguration(ModifierKey.ControlLeftAlt, Key.VK_OEM_4)],
    VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveEditorContentType, ".+"),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    using var textView = await context.GetActiveTextViewAsync(ct);
    if (textView is null) return;

    var text = textView.Document.Text.CopyToString();
    var cursorOffset = textView.Selection.Extent.Start.Offset;
    var (prevCode, prevLine) = FindPrevBlock(text, cursorOffset);
    if (prevCode is null)
    {
      if (output is not null) await output.WriteLineAsync("○ No previous block found");
      return;
    }

    var filePath = textView.Document.Uri.LocalPath;
    using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
      ct, cancellation.StartNew());
    try
    {
      var result = await client.EvalWithContextAsync(prevCode, filePath, "block", prevLine, linked.Token);
      if (output is not null)
      {
        var icon = result.ExitCode == 0 ? "✓" : "✗";
        await output.WriteLineAsync($"{icon} [line {prevLine}] {result.Output.Trim()}");
      }
    }
    catch (OperationCanceledException)
    {
      if (output is not null) await output.WriteLineAsync("⊘ Cancelled");
    }
    finally { cancellation.Done(); }
  }

  private static (string? code, int lineNumber) FindPrevBlock(string text, int fromOffset)
  {
    var searchFrom = Math.Min(fromOffset - 1, text.Length - 2);
    // Find the ;; boundary just before the cursor
    var blockEnd = -1;
    for (var i = searchFrom; i >= 1; i--)
    {
      if (text[i] == ';' && text[i - 1] == ';') { blockEnd = i + 1; break; }
    }
    if (blockEnd < 0) return (null, 0);

    // Find the ;; boundary before that one to get the block start
    var blockStart = 0;
    for (var i = blockEnd - 3; i >= 1; i--)
    {
      if (text[i] == ';' && text[i - 1] == ';')
      {
        blockStart = i + 1;
        while (blockStart < text.Length && (text[blockStart] == '\r' || text[blockStart] == '\n'))
          blockStart++;
        break;
      }
    }
    if (blockStart >= blockEnd) return (null, 0);
    var lineNumber = text[..blockStart].Split('\n').Length;
    return (text[blockStart..blockEnd].Trim(), lineNumber);
  }
}

/// <summary>
/// Evaluates the current ;; block and immediately proceeds — the core rapid-iteration loop.
/// Use to "step through" an .fsx file: evaluate one block, glance at the output, continue.
/// Bound to Ctrl+Alt+Shift+Enter to complement Alt+Enter (selection) and Ctrl+Alt+Enter (block).
/// Unlike EvalRange, this command is explicit about the workflow intent: iterate fast.
/// </summary>
[VisualStudioContribution]
internal class EvalAndAdvanceCommand : Command
{
  private readonly Core.SageFsClient client;
  private readonly Core.EvalCancellation cancellation;
  private OutputChannel? output;

  public EvalAndAdvanceCommand(Core.SageFsClient client, Core.EvalCancellation cancellation)
  {
    this.client = client;
    this.cancellation = cancellation;
  }

  public override CommandConfiguration CommandConfiguration => new("%SageFs.EvalAndAdvance.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.PlayStepGroup, IconSettings.IconAndText),
    Shortcuts = [new CommandShortcutConfiguration(ModifierKey.ControlShiftLeftAlt, Key.Enter)],
    VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveEditorContentType, ".+"),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    using var textView = await context.GetActiveTextViewAsync(ct);
    if (textView is null) return;

    var text = textView.Document.Text.CopyToString();
    var cursorOffset = textView.Selection.Extent.Start.Offset;
    var code = FindBlockAroundCursor(text, cursorOffset);
    if (string.IsNullOrWhiteSpace(code)) return;

    var filePath = textView.Document.Uri.LocalPath;
    var startLine = textView.Selection.Extent.Start.GetContainingLine().LineNumber + 1;

    if (output is not null)
      await output.WriteLineAsync($"▶ Evaluating block at line {startLine}…");

    using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
      ct, cancellation.StartNew());
    try
    {
      var result = await client.EvalWithContextAsync(code, filePath, "block", startLine, linked.Token);
      if (output is not null)
      {
        if (result.ExitCode == 0)
          await output.WriteLineAsync($"✓ {result.Output.Trim()}");
        else
        {
          await output.WriteLineAsync($"✗ Exit {result.ExitCode}: {result.Output.Trim()}");
          foreach (var d in result.Diagnostics) await output.WriteLineAsync($"  ⚠ {d}");
        }
        await output.WriteLineAsync("───────────────────────────────────────");
      }
    }
    catch (OperationCanceledException)
    {
      if (output is not null) await output.WriteLineAsync("⊘ Cancelled");
    }
    finally { cancellation.Done(); }
  }

  private static string FindBlockAroundCursor(string text, int cursorOffset) =>
    BlockHelpers.FindBlockAroundCursor(text, cursorOffset);
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW

