namespace SageFs.VisualStudio.Commands;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

/// <summary>
/// Runs the test function nearest the cursor. Keybinding: <b>Shift+Alt+T</b>.
///
/// <para><b>Algorithm</b> (Mark Seemann's, Sprint 6 deliberation):
/// Find all test annotations where <c>Line &lt;= cursorLine</c>,
/// then take the one with the maximum line number. This correctly handles
/// the case where the cursor is inside a multi-line test body — it finds
/// the function that *contains* the cursor rather than the one *after* it.</para>
///
/// <para><b>Daemon endpoint</b>:
/// Posts <c>{ "pattern": testInfo.FullName }</c> to <c>/api/live-testing/run</c>
/// via <see cref="Core.SageFsClient.RunTestsAsync"/>. The daemon's pattern
/// parameter supports exact full names. Empty = run all.</para>
///
/// <para><b>Latency</b>:
/// Keystroke → POST → daemon eval → SSE <c>file_annotations</c> event → glyph/adornment refresh.
/// The Output pane records the wall-clock time for profiling.</para>
///
/// <para><b>Edge cases</b>:
/// <list type="bullet">
/// <item>No tests in file → "No test found near cursor" message.</item>
/// <item>Cursor above all test lines → same no-test message.</item>
/// <item>Daemon unreachable → swallowed, error written to Output pane.</item>
/// <item>No active text view → returns immediately (not an F# file).</item>
/// </list></para>
/// </summary>
[VisualStudioContribution]
internal class RunTestAtCursorCommand : Command
{
  private readonly Core.SageFsClient client;
  private readonly Core.LiveTestingSubscriber subscriber;
  private OutputChannel? output;

  public RunTestAtCursorCommand(Core.SageFsClient client, Core.LiveTestingSubscriber subscriber)
  {
    this.client = client;
    this.subscriber = subscriber;
  }

  public override CommandConfiguration CommandConfiguration => new("%SageFs.RunTestAtCursor.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.TestRun, IconSettings.IconAndText),
    Shortcuts = [new CommandShortcutConfiguration(ModifierKey.ShiftLeftAlt, Key.T)],
    VisibleWhen = ActivationConstraint.ClientContext(
      ClientContextKey.Shell.ActiveEditorContentType, ".+"),
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

    var filePath = textView.Document.Uri.LocalPath;
    // GetContainingLine().LineNumber is 0-based; daemon uses 1-based.
    var cursorLine = textView.Selection.Extent.Start.GetContainingLine().LineNumber + 1;

    var testInfo = FindTestContainingLine(filePath, cursorLine);

    if (testInfo is null)
    {
      if (output is not null)
        await output.WriteLineAsync(
          $"⊘ No test found at or above line {cursorLine} in {System.IO.Path.GetFileName(filePath)}. " +
          "Position the cursor inside a test function and try again.");
      return;
    }

    var startTime = DateTimeOffset.UtcNow;
    if (output is not null)
      await output.WriteLineAsync(
        $"▶ Running [{testInfo.DisplayName}] at line {testInfo.Line?.Value}… [{startTime:HH:mm:ss.fff}]");

    try
    {
      var ok = await client.RunTestsAsync(testInfo.FullName, ct);
      if (!ok)
      {
        if (output is not null)
          await output.WriteLineAsync(
            $"✗ Failed to start test run for [{testInfo.DisplayName}] — is the daemon running?");
        return;
      }
      // Result arrives via SSE → inline adornments and glyph margin update automatically.
      // The elapsed time to SSE result is visible in the test tooltip once it arrives.
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      if (output is not null)
        await output.WriteLineAsync($"✗ Failed to run test: {ex.Message}");
    }
  }

  /// <summary>
  /// Returns the test function that *contains* the cursor line.
  /// Uses Seemann's algorithm: last test annotation with Line &lt;= cursorLine.
  /// </summary>
  private Core.TestInfo? FindTestContainingLine(string filePath, int cursorLine)
  {
    var tests = subscriber.TestsForFile(filePath);

    // FSharpList<TestInfo> implements IEnumerable<TestInfo> directly.
    // TestInfo.Line is FSharpOption<int>: null = None, non-null = Some(n).
    return tests
      .Where(t => t.Line is not null && t.Line.Value <= cursorLine)
      .OrderByDescending(t => t.Line!.Value)
      .FirstOrDefault();
  }
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
