module SageFs.JumpToTest

open System
open System.Diagnostics

/// Open a file at a specific line in the user's editor.
/// Respects the $EDITOR environment variable (vim/emacs-style `+N` arg);
/// falls back to `code --goto` (VS Code) when $EDITOR is not set.
let openInEditor (file: string) (line: int) =
  let editor = Environment.GetEnvironmentVariable("EDITOR")
  let exe, args =
    match editor with
    | null | "" ->
      // VS Code: code --goto "file:line"
      "code", sprintf "--goto \"%s:%d\"" file line
    | e ->
      // Vim / Emacs / nano style: editor +N "file"
      e, sprintf "+%d \"%s\"" line file
  try
    Process.Start(exe, args) |> ignore
  with ex ->
    // F12 is non-critical — log but don't crash the TUI/GUI loop.
    eprintfn "[SageFs] openInEditor failed: %s" ex.Message

/// Extract the test full-name from a LineAnnotation tooltip.
/// Tooltip format: "testName ✓ 42ms", "testName ✗ msg (42ms)",
/// "testName (detected)", "testName (queued)", etc.
let private extractTestNameFromTooltip (tooltip: string) : string option =
  match String.IsNullOrWhiteSpace tooltip with
  | true -> None
  | false ->
    let markers = [| " \u2713"; " \u2717"; " (detected)"; " (queued)"; " (running...)"; " (skipped:"; " (stale"; " (disabled" |]
    let name =
      markers
      |> Array.tryPick (fun m ->
        let idx = tooltip.IndexOf(m, StringComparison.Ordinal)
        match idx > 0 with
        | true -> Some (tooltip.Substring(0, idx))
        | false -> None)
      |> Option.defaultValue (tooltip.Trim())
    match name.Length > 0 with
    | true -> Some name
    | false -> None

/// Parse a display name from a Tests-pane content line.
/// Line format: "[icon] [name padded] [duration]" e.g. "✗ My.Test 42ms"
let private extractTestNameFromLine (line: string) : string option =
  match line.Length > 2 with
  | true ->
    // Skip icon char + space, then trim trailing whitespace/duration
    let raw = line.Substring(2).TrimEnd()
    // Strip trailing duration like "42ms", "1.2s", "0ms"
    let stripped =
      match raw.Length with
      | 0 -> raw
      | _ ->
        let lastSpace = raw.LastIndexOf(' ')
        match lastSpace > 0 with
        | true ->
          let suffix = raw.Substring(lastSpace + 1)
          match suffix.EndsWith("ms", StringComparison.Ordinal) || suffix.EndsWith("s", StringComparison.Ordinal) with
          | true -> raw.Substring(0, lastSpace).TrimEnd()
          | false -> raw
        | false -> raw
    match stripped.Length > 0 with
    | true -> Some stripped
    | false -> None
  | false -> None

/// Try to find (file, line) for the test at the given scroll offset in the regions.
///
/// Checks two panes:
///  • Tests pane — parses the display name from the content line at scrollOffset
///  • Editor pane — extracts the test name from the nearest LineAnnotation tooltip
///
/// Then looks up the test name in sourceLocations (testName → file * line).
let getSelectedTestLocation
  (regions: RenderRegion list)
  (focusedPane: PaneId)
  (scrollOffset: int)
  (sourceLocations: Map<string, string * int>)
  : (string * int) option =
  match sourceLocations.IsEmpty with
  | true -> None
  | false ->
    let tryLookup (name: string) =
      sourceLocations
      |> Map.tryFind name
      |> Option.orElseWith (fun () ->
        // Fallback: display name may be a suffix of the full name
        sourceLocations
        |> Map.tryPick (fun fullName loc ->
          match fullName.EndsWith(name, StringComparison.Ordinal)
                || fullName.EndsWith("/" + name, StringComparison.Ordinal) with
          | true -> Some loc
          | false -> None))

    let findInTestsPane () =
      regions
      |> List.tryFind (fun r -> r.Id = PaneId.toRegionId PaneId.Tests)
      |> Option.bind (fun r ->
        let lines = r.Content.Split('\n')
        match scrollOffset >= 0 && scrollOffset < lines.Length with
        | true -> extractTestNameFromLine lines.[scrollOffset]
        | false -> None)
      |> Option.bind tryLookup

    let findInEditorPane () =
      regions
      |> List.tryFind (fun r -> r.Id = PaneId.toRegionId PaneId.Editor)
      |> Option.bind (fun r ->
        // Find the test annotation closest to the scroll position
        let testAnnotations =
          r.LineAnnotations
          |> Array.filter (fun a ->
            match a.Icon with
            | Features.LiveTesting.GutterIcon.TestPassed
            | Features.LiveTesting.GutterIcon.TestFailed
            | Features.LiveTesting.GutterIcon.TestRunning
            | Features.LiveTesting.GutterIcon.TestDiscovered
            | Features.LiveTesting.GutterIcon.TestSkipped
            | Features.LiveTesting.GutterIcon.TestFlaky -> true
            | _ -> false)
        match testAnnotations.Length with
        | 0 -> None
        | _ ->
          testAnnotations
          |> Array.minBy (fun a -> abs (a.Line - scrollOffset))
          |> fun a -> extractTestNameFromTooltip a.Tooltip)
      |> Option.bind tryLookup

    match focusedPane with
    | PaneId.Tests -> findInTestsPane ()
    | PaneId.Editor -> findInEditorPane ()
    | _ -> None
