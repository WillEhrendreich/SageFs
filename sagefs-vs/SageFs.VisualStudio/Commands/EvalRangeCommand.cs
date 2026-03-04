namespace SageFs.VisualStudio.Commands;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Editor;

/// <summary>
/// Evaluates the current ;;-delimited code block around the cursor.
/// If there's a selection, evaluates the selection instead.
/// </summary>
[VisualStudioContribution]
#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW
internal class EvalRangeCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public EvalRangeCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.EvalRange.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.Run, IconSettings.IconAndText),
    Shortcuts = [new CommandShortcutConfiguration(ModifierKey.ControlLeftAlt, Key.Enter)],
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

    string code;
    if (!textView.Selection.IsEmpty)
    {
      code = textView.Selection.Extent.CopyToString();
    }
    else
    {
      // Find the ;; delimited block around the cursor
      var fullText = textView.Document.Text.CopyToString();
      var cursorOffset = textView.Selection.Extent.Start.Offset;
      code = FindBlockAroundCursor(fullText, cursorOffset);
    }

    if (string.IsNullOrWhiteSpace(code)) return;

    var filePath = textView.Document.Uri.LocalPath;
    var startLine = textView.Selection.Extent.Start.GetContainingLine().LineNumber + 1; // 0-based → 1-based

    if (output is not null)
      await output.WriteLineAsync($"▶ Evaluating block ({code.Length} chars)...");

    var result = await client.EvalWithContextAsync(code, filePath, "block", startLine, ct);
    if (output is not null)
    {
      if (result.ExitCode == 0)
      {
        await output.WriteLineAsync($"✓ {result.Output}");
      }
      else
      {
        await output.WriteLineAsync($"✗ Exit code {result.ExitCode}");
        if (!string.IsNullOrEmpty(result.Output))
          await output.WriteLineAsync(result.Output);
        foreach (var diag in result.Diagnostics)
          await output.WriteLineAsync($"  ⚠ {diag}");
      }
      await output.WriteLineAsync("───────────────────────────────────────");
    }
  }

  /// <summary>
  /// Finds the ;; delimited code block surrounding the cursor position.
  /// Scans backwards and forwards from cursor to find ;; boundaries.
  /// </summary>
  private static string FindBlockAroundCursor(string text, int cursorOffset)
  {
    if (string.IsNullOrEmpty(text)) return "";

    // Find the start: scan backwards for ;; or beginning of file
    var blockStart = 0;
    for (var i = Math.Min(cursorOffset, text.Length - 1); i >= 1; i--)
    {
      if (text[i] == ';' && text[i - 1] == ';')
      {
        // Found ;;, block starts after it (skip whitespace)
        blockStart = i + 1;
        while (blockStart < text.Length && (text[blockStart] == '\r' || text[blockStart] == '\n'))
          blockStart++;
        break;
      }
    }

    // Find the end: scan forward for ;; or end of file
    var blockEnd = text.Length;
    for (var i = cursorOffset; i < text.Length - 1; i++)
    {
      if (text[i] == ';' && text[i + 1] == ';')
      {
        blockEnd = i + 2; // Include the ;;
        break;
      }
    }

    if (blockStart >= blockEnd) return "";
    return text[blockStart..blockEnd].Trim();
  }
}
#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
