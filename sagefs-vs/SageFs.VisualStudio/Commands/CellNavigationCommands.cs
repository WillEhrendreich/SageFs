namespace SageFs.VisualStudio.Commands;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Editor;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

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

  private static string FindBlockAroundCursor(string text, int cursorOffset)
  {
    if (string.IsNullOrEmpty(text)) return "";
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
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW

