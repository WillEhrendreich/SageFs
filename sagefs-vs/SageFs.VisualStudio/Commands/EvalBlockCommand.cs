namespace SageFs.VisualStudio.Commands;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Editor;

[VisualStudioContribution]
#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW
internal class EvalBlockCommand : Command
{
    private readonly Core.SageFsClient client;
    private OutputChannel? output;

    public EvalBlockCommand(Core.SageFsClient client) => this.client = client;

    public override CommandConfiguration CommandConfiguration => new("%SageFs.EvalBlock.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
        Icon = new(ImageMoniker.KnownValues.PlayStepGroup, IconSettings.IconAndText),
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

        var fullText = textView.Document.Text.CopyToString();
        var cursorLine = textView.Selection.Extent.Start.GetContainingLine().LineNumber;
        var filePath = textView.Document.Uri.LocalPath;

        var lines = fullText.Split('\n');
        var (startLine, endLine) = FindBlock(lines, cursorLine);

        var blockLines = lines[startLine..(endLine + 1)];
        var code = string.Join("\n", blockLines);

        if (string.IsNullOrWhiteSpace(code)) return;

        if (output is not null)
            await output.WriteLineAsync($"▶ Eval block (lines {startLine + 1}–{endLine + 1}, {code.Length} chars)...");

        var result = await client.EvalWithContextAsync(code, filePath, "block", startLine + 1, ct);
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
    /// Finds the 0-based (startLine, endLine) range of the block surrounding
    /// <paramref name="cursorLine"/>. A block is a run of consecutive non-empty lines.
    /// If the cursor is on an empty/whitespace line, returns just that single line.
    /// Pure static method — no VS API dependencies — fully unit-testable.
    /// </summary>
    internal static (int startLine, int endLine) FindBlock(string[] lines, int cursorLine)
    {
        if (lines.Length == 0) return (0, 0);
        cursorLine = Math.Max(0, Math.Min(cursorLine, lines.Length - 1));

        // Cursor on a blank separator — return the single blank line (eval is a no-op)
        if (string.IsNullOrWhiteSpace(lines[cursorLine]))
            return (cursorLine, cursorLine);

        // Scan up to find the first empty line (or top of file)
        int start = cursorLine;
        while (start > 0 && !string.IsNullOrWhiteSpace(lines[start - 1]))
            start--;

        // Scan down to find the first empty line (or bottom of file)
        int end = cursorLine;
        while (end < lines.Length - 1 && !string.IsNullOrWhiteSpace(lines[end + 1]))
            end++;

        return (start, end);
    }
}
#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
