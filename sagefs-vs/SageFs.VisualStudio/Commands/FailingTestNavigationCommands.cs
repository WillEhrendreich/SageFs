namespace SageFs.VisualStudio.Commands;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

// ── Shared navigation state ──────────────────────────────────────────────────

/// <summary>
/// Shared state for cycling through failed tests across Next/Prev commands.
/// Resets the navigation index when the set of failed tests changes (count mismatch).
/// </summary>
internal static class FailingTestNavigator
{
    private static int currentIndex = -1;
    private static int lastFailedCount = -1;

    /// <summary>
    /// Collects all failed/errored tests from the live subscriber state,
    /// sorted by file path then line number for stable iteration order.
    /// </summary>
    public static List<(Core.TestInfo Info, Core.TestResult Result)> GetFailedTests(
        Core.LiveTestingSubscriber subscriber)
    {
        var state = subscriber.CurrentState;
        var failed = new List<(Core.TestInfo Info, Core.TestResult Result)>();

        foreach (var kv in state.Tests)
        {
            var info = kv.Value;
            var resultOpt = subscriber.ResultFor(info.Id);
            if (resultOpt is null) continue;

            var result = resultOpt.Value;
            if (result.Outcome.IsFailed || result.Outcome.IsErrored)
                failed.Add((info, result));
        }

        failed.Sort((a, b) =>
        {
            var fileA = a.Info.FilePath is not null ? a.Info.FilePath.Value : "";
            var fileB = b.Info.FilePath is not null ? b.Info.FilePath.Value : "";
            var cmp = string.Compare(fileA, fileB, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            var lineA = a.Info.Line is not null ? a.Info.Line.Value : 0;
            var lineB = b.Info.Line is not null ? b.Info.Line.Value : 0;
            return lineA.CompareTo(lineB);
        });

        return failed;
    }

    public static int NavigateNext(int failedCount)
    {
        if (failedCount != lastFailedCount)
        {
            currentIndex = -1;
            lastFailedCount = failedCount;
        }
        currentIndex = (currentIndex + 1) % failedCount;
        return currentIndex;
    }

    public static int NavigatePrev(int failedCount)
    {
        if (failedCount != lastFailedCount)
        {
            currentIndex = 0;
            lastFailedCount = failedCount;
        }
        currentIndex = (currentIndex - 1 + failedCount) % failedCount;
        return currentIndex;
    }
}

// ── Next Failing Test ─────────────────────────────────────────────────────────

/// <summary>
/// Cycles forward through failed tests and navigates to their source location.
/// Keybinding: <b>Shift+Alt+]</b>.
///
/// <para>Failed tests are collected from the <see cref="Core.LiveTestingSubscriber"/>
/// SSE state. Source locations come from <see cref="Core.TestInfo.FilePath"/> and
/// <see cref="Core.TestInfo.Line"/>, enriched by <see cref="Core.TestSourceLocation"/>
/// when available.</para>
///
/// <para>The navigation index is shared with <see cref="PrevFailingTestCommand"/>
/// and resets when the set of failing tests changes.</para>
/// </summary>
[VisualStudioContribution]
internal class NextFailingTestCommand : Command
{
    private readonly Core.LiveTestingSubscriber subscriber;
    private OutputChannel? output;

    public NextFailingTestCommand(Core.LiveTestingSubscriber subscriber)
    {
        this.subscriber = subscriber;
    }

    public override CommandConfiguration CommandConfiguration =>
        new("%SageFs.NextFailingTest.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
        Icon = new(ImageMoniker.KnownValues.StatusError, IconSettings.IconAndText),
        Shortcuts = [new CommandShortcutConfiguration(ModifierKey.ShiftLeftAlt, Key.VK_OEM_6)],
    };

    public override async Task InitializeAsync(CancellationToken ct)
    {
        output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
        await base.InitializeAsync(ct);
    }

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        var failed = FailingTestNavigator.GetFailedTests(subscriber);
        if (failed.Count == 0)
        {
            if (output is not null)
                await output.WriteLineAsync("✓ No failing tests.");
            return;
        }

        var index = FailingTestNavigator.NavigateNext(failed.Count);
        var target = failed[index];

        await NavigateToFailedTestAsync(context, target.Info, target.Result, index, failed.Count, ct);
    }

    private async Task NavigateToFailedTestAsync(
        IClientContext context,
        Core.TestInfo info,
        Core.TestResult result,
        int index,
        int total,
        CancellationToken ct)
    {
        var (filePath, line) = ResolveSourceLocation(info);

        if (filePath is null)
        {
            if (output is not null)
                await output.WriteLineAsync(
                    $"✗ [{index + 1}/{total}] {info.DisplayName} — no source location available");
            return;
        }

        // Open the file in the editor
        var opened = await TryOpenDocumentAsync(filePath, ct);

        // Format the failure message
        var fileName = Path.GetFileName(filePath);
        var lineInfo = line.HasValue ? $":{line.Value}" : "";
        var failMsg = FormatFailureMessage(result);

        if (output is not null)
        {
            var openStatus = opened ? "" : " (could not open file)";
            await output.WriteLineAsync(
                $"✗ [{index + 1}/{total}] {info.DisplayName} — {fileName}{lineInfo}{openStatus}");
            if (!string.IsNullOrEmpty(failMsg))
                await output.WriteLineAsync($"  {failMsg}");
        }
    }

    private (string? filePath, int? line) ResolveSourceLocation(Core.TestInfo info)
    {
        var state = subscriber.CurrentState;

        // Prefer TestSourceLocation (has precise start/end lines) over TestInfo
        foreach (var kv in state.SourceLocations)
        {
            if (kv.Key == info.FullName)
                return (kv.Value.FilePath, kv.Value.StartLine);
        }

        // Fall back to TestInfo.FilePath / TestInfo.Line (FSharpOption<T>)
        string? filePath = info.FilePath is not null ? info.FilePath.Value : null;
        int? line = info.Line is not null ? (int?)info.Line.Value : null;
        return (filePath, line);
    }

    /// <summary>
    /// Best-effort document opening. The VS Extensibility SDK's out-of-process model
    /// may not support opening arbitrary documents in all versions. Returns true if
    /// the open succeeded.
    /// </summary>
    private async Task<bool> TryOpenDocumentAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(filePath);
            await Extensibility.Documents().OpenTextDocumentAsync(uri, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string FormatFailureMessage(Core.TestResult result)
    {
        if (result.Outcome is Core.TestOutcome.Failed failed)
            return Truncate(failed.message, 120);
        if (result.Outcome is Core.TestOutcome.Errored errored)
            return Truncate(errored.message, 120);
        return "";
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "…";
}

// ── Prev Failing Test ─────────────────────────────────────────────────────────

/// <summary>
/// Cycles backward through failed tests and navigates to their source location.
/// Keybinding: <b>Shift+Alt+[</b>.
///
/// <para>Shares navigation state with <see cref="NextFailingTestCommand"/>.
/// See that command's doc comment for the full algorithm description.</para>
/// </summary>
[VisualStudioContribution]
internal class PrevFailingTestCommand : Command
{
    private readonly Core.LiveTestingSubscriber subscriber;
    private OutputChannel? output;

    public PrevFailingTestCommand(Core.LiveTestingSubscriber subscriber)
    {
        this.subscriber = subscriber;
    }

    public override CommandConfiguration CommandConfiguration =>
        new("%SageFs.PrevFailingTest.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
        Icon = new(ImageMoniker.KnownValues.StatusError, IconSettings.IconAndText),
        Shortcuts = [new CommandShortcutConfiguration(ModifierKey.ShiftLeftAlt, Key.VK_OEM_4)],
    };

    public override async Task InitializeAsync(CancellationToken ct)
    {
        output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
        await base.InitializeAsync(ct);
    }

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        var failed = FailingTestNavigator.GetFailedTests(subscriber);
        if (failed.Count == 0)
        {
            if (output is not null)
                await output.WriteLineAsync("✓ No failing tests.");
            return;
        }

        var index = FailingTestNavigator.NavigatePrev(failed.Count);
        var target = failed[index];

        await NavigateToFailedTestAsync(context, target.Info, target.Result, index, failed.Count, ct);
    }

    private async Task NavigateToFailedTestAsync(
        IClientContext context,
        Core.TestInfo info,
        Core.TestResult result,
        int index,
        int total,
        CancellationToken ct)
    {
        var (filePath, line) = ResolveSourceLocation(info);

        if (filePath is null)
        {
            if (output is not null)
                await output.WriteLineAsync(
                    $"✗ [{index + 1}/{total}] {info.DisplayName} — no source location available");
            return;
        }

        var opened = await TryOpenDocumentAsync(filePath, ct);

        var fileName = Path.GetFileName(filePath);
        var lineInfo = line.HasValue ? $":{line.Value}" : "";
        var failMsg = NextFailingTestCommand.FormatFailureMessage(result);

        if (output is not null)
        {
            var openStatus = opened ? "" : " (could not open file)";
            await output.WriteLineAsync(
                $"✗ [{index + 1}/{total}] {info.DisplayName} — {fileName}{lineInfo}{openStatus}");
            if (!string.IsNullOrEmpty(failMsg))
                await output.WriteLineAsync($"  {failMsg}");
        }
    }

    private (string? filePath, int? line) ResolveSourceLocation(Core.TestInfo info)
    {
        var state = subscriber.CurrentState;

        foreach (var kv in state.SourceLocations)
        {
            if (kv.Key == info.FullName)
                return (kv.Value.FilePath, kv.Value.StartLine);
        }

        string? filePath = info.FilePath is not null ? info.FilePath.Value : null;
        int? line = info.Line is not null ? (int?)info.Line.Value : null;
        return (filePath, line);
    }

    private async Task<bool> TryOpenDocumentAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(filePath);
            await Extensibility.Documents().OpenTextDocumentAsync(uri, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
