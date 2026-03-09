namespace SageFs.VisualStudio.Core

open System

/// Simplified glyph status for editor gutter rendering
type GlyphStatus =
  | GlyphPassed
  | GlyphFailed
  | GlyphRunning
  | GlyphStale
  | GlyphNotRun

/// A projected glyph entry for a specific test line in an editor
type GlyphEntry = {
  FilePath: string
  Line: int  // 1-based, as provided by daemon/source-mapper
  Outcome: TestOutcome
  Status: GlyphStatus
  DisplayName: string
}

/// Pure projection functions for test glyph state.
/// Used by both the MEF glyph provider (net472) and the test suite (net8.0).
/// All functions are pure and deterministic.
[<RequireQualifiedAccess>]
module GlyphProjection =

  /// Classify a TestOutcome into a glyph rendering category
  let classify (outcome: TestOutcome) : GlyphStatus =
    match outcome with
    | TestOutcome.Passed _     -> GlyphPassed
    | TestOutcome.Failed _
    | TestOutcome.Errored _    -> GlyphFailed
    | TestOutcome.Running      -> GlyphRunning
    | TestOutcome.Stale        -> GlyphStale
    | TestOutcome.Detected
    | TestOutcome.Skipped _
    | TestOutcome.PolicyDisabled -> GlyphNotRun

  /// Get all glyph entries for a specific file from the live test state.
  /// Path comparison is case-insensitive (Windows paths can differ in case
  /// between the daemon's source-mapped path and VS's ITextDocument.FilePath).
  let forFile (state: LiveTestState) (filePath: string) : GlyphEntry list =
    state.Tests
    |> Map.toSeq
    |> Seq.choose (fun (id, info) ->
      match info.FilePath, info.Line with
      | Some fp, Some ln
        when String.Equals(fp, filePath, StringComparison.OrdinalIgnoreCase) ->
        let outcome =
          state.Results
          |> Map.tryFind id
          |> Option.map (fun r -> r.Outcome)
          |> Option.defaultValue TestOutcome.Detected
        Some {
          FilePath = fp
          Line = ln
          Outcome = outcome
          Status = classify outcome
          DisplayName = info.DisplayName
        }
      | _ -> None)
    |> List.ofSeq
    |> List.sortBy (fun e -> e.Line)

  /// Get all glyph entries across all files, sorted by file then line.
  let allEntries (state: LiveTestState) : GlyphEntry list =
    state.Tests
    |> Map.toSeq
    |> Seq.choose (fun (id, info) ->
      match info.FilePath, info.Line with
      | Some fp, Some ln ->
        let outcome =
          state.Results
          |> Map.tryFind id
          |> Option.map (fun r -> r.Outcome)
          |> Option.defaultValue TestOutcome.Detected
        Some {
          FilePath = fp
          Line = ln
          Outcome = outcome
          Status = classify outcome
          DisplayName = info.DisplayName
        }
      | _ -> None)
    |> List.ofSeq
    |> List.sortBy (fun e -> e.FilePath, e.Line)
