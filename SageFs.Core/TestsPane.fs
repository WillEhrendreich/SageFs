namespace SageFs

open System
open SageFs.Features.LiveTesting

/// Renders the live test list into a DrawTarget with status icons and per-row coloring.
/// Both TUI (AnsiEmitter) and Raylib (RaylibEmitter) share this renderer because they
/// both consume the same Cell[,] grid abstraction.
module TestsPane =

  let private formatDuration (d: TimeSpan) =
    match d.TotalMilliseconds < 1000.0 with
    | true  -> sprintf "%dms" (int d.TotalMilliseconds)
    | false -> sprintf "%.1fs" d.TotalSeconds

  /// Unicode icon character for a test run status.
  let iconChar (status: TestRunStatus) : char =
    match status with
    | TestRunStatus.Passed _       -> '\u25CF' // ●
    | TestRunStatus.Failed _       -> '\u2717' // ✗
    | TestRunStatus.Running        -> '\u27F3' // ⟳
    | TestRunStatus.Stale          -> '\u25CC' // ◌
    | TestRunStatus.Queued         -> '\u25CC' // ◌
    | TestRunStatus.Detected       -> '\u25CC' // ◌
    | TestRunStatus.Skipped _      -> '\u25CB' // ○
    | TestRunStatus.PolicyDisabled -> '\u2500' // ─

  /// Foreground color (packed RGB) for the status icon.
  let iconFgColor (theme: ThemeConfig) (status: TestRunStatus) : uint32 =
    match status with
    | TestRunStatus.Passed _       -> Theme.hexToRgb theme.ColorPass
    | TestRunStatus.Failed _       -> Theme.hexToRgb theme.ColorFail
    | TestRunStatus.Running        -> Theme.hexToRgb theme.ColorWarn
    | TestRunStatus.Stale
    | TestRunStatus.Queued
    | TestRunStatus.Detected       -> Theme.hexToRgb theme.FgDim
    | TestRunStatus.Skipped _      -> Theme.hexToRgb theme.FgDim
    | TestRunStatus.PolicyDisabled -> Theme.hexToRgb theme.FgDim

  let private durationOf (status: TestRunStatus) : TimeSpan option =
    match status with
    | TestRunStatus.Passed d        -> Some d
    | TestRunStatus.Failed (_, d)   -> Some d
    | _                             -> None

  /// Truncate s to at most maxLen chars, appending '…' if truncated.
  let truncate (maxLen: int) (s: string) : string =
    match s.Length <= maxLen with
    | true  -> s
    | false -> s.[..maxLen - 2] + "\u2026" // …

  /// Format a single entry as a fixed-width line: "<icon> <name…> <duration>".
  let formatEntry (paneWidth: int) (entry: TestStatusEntry) : string =
    let icon      = string (iconChar entry.Status)
    let durStr    = durationOf entry.Status |> Option.map formatDuration |> Option.defaultValue ""
    // icon(1) + space(1) + name + space(1) + duration
    let nameWidth = max 1 (paneWidth - 3 - durStr.Length)
    let name      = truncate nameWidth entry.DisplayName |> fun n -> n.PadRight(nameWidth)
    sprintf "%s %s %s" icon name durStr

  /// Build the content string for the tests region (one entry per line).
  /// Uses paneWidth = 80 as a stable default; actual cell coloring is done by
  /// renderContent which reads the icon from the first character of each line.
  let buildContent (paneWidth: int) (entries: TestStatusEntry array) : string =
    match entries.Length with
    | 0 -> "No tests discovered.\nRun tests with Expecto to see results here."
    | _ -> entries |> Array.map (formatEntry paneWidth) |> String.concat "\n"

  /// Render the tests pane lines with colored status icons into a DrawTarget.
  ///
  /// Parameters:
  ///   inner        – DrawTarget for the pane content area (inside the border)
  ///   visibleLines – already-scrolled lines from the region content
  ///   cursorIdx    – absolute line index that is selected (-1 = none)
  ///   scrollOffset – number of lines skipped at the top (for cursor math)
  ///   theme        – active theme configuration
  let renderContent
    (inner: DrawTarget)
    (visibleLines: string array)
    (cursorIdx: int)
    (scrollOffset: int)
    (theme: ThemeConfig) : unit =

    let fg    = Theme.hexToRgb theme.FgDefault
    let bg    = Theme.hexToRgb theme.BgPanel
    let selBg = Theme.hexToRgb theme.BgSelection
    let dimFg = Theme.hexToRgb theme.FgDim

    visibleLines |> Array.iteri (fun row line ->
      let lineIdx  = scrollOffset + row
      let isSelected = lineIdx = cursorIdx
      let rowBg    = match isSelected with | true -> selBg | false -> bg

      // Fill row background so the whole row is highlighted, not just the text
      Draw.hline inner row dimFg rowBg ' '

      match line.Length > 0 with
      | false -> ()
      | true ->
        // Determine icon color from the first character of the line
        let firstCh   = line.[0]
        let iconColor =
          match firstCh with
          | '\u25CF' -> Theme.hexToRgb theme.ColorPass  // ●
          | '\u2717' -> Theme.hexToRgb theme.ColorFail  // ✗
          | '\u27F3' -> Theme.hexToRgb theme.ColorWarn  // ⟳
          | _        -> dimFg
        Draw.text inner row 0 iconColor rowBg CellAttrs.None (string firstCh)
        match line.Length > 1 with
        | true  -> Draw.text inner row 1 fg rowBg CellAttrs.None line.[1..]
        | false -> ())
