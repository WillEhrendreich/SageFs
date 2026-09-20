/// Pure decision logic for VS Code inline eval decorations and the
/// per-test CodeLens title.
///
/// WHY — `sagefs-ux-roast.md` Island E: `InlineDecorations.fs` computed
/// inline-result summarisation, diagnostic truncation, eval-in-progress
/// ghost text, stale-marker transitions, and the binding-value target-line
/// arithmetic (`blockStartLine + bv.SourceLine - 1`) inline, next to the
/// Fable-only `vscode.window` calls — with zero test references. The eval
/// path is the one this extension exists for, and until this file it had
/// no pure-decision coverage at all: `TestDecorationsPure.fs` covers test
/// gutters and coverage gutters, nothing covered the eval ghost text or the
/// binding-value line math ("a second uncentralised off-by-one", per the
/// roast). `TestCodeLensProvider.fs`'s per-test title formatting duplicated
/// the same outcome-to-text decision `TestDecorationsPure.bucketForOutcome`
/// already made once, with its own independent (and slightly different —
/// message-truncated) rendering; it is folded in here rather than left as a
/// second untested copy.
///
/// This module extracts every one of those decisions so they are testable
/// under plain `dotnet fsi`, mirroring `TestDecorationsPure.fs` /
/// `SessionsTreePure.fs` / `StatusBarPure.fs` / `ContextKeysPure.fs` /
/// `CoverageViewPure.fs`. No Fable dependency — deliberately not opening
/// `FeatureTypes` (which pulls in `JsHelpers`/`SafeInterop`/`Vscode` and so
/// cannot load under plain `fsi`); the one field this module needs from
/// `ClientBindingValue` (`SourceLine`, `IsFunctionValue`) is taken as plain
/// arguments instead.
/// Tested by `tests/InlineDecorationsContractTests.fsx`.
module SageFs.Vscode.InlineDecorationsPure

open SageFs.Vscode.LiveTestingTypes

// ── Duration formatting ──────────────────────────────────────────

/// `123ms` under one second, `1.2s` at or above it. Shared by inline
/// results and the CodeLens duration suffix.
let formatDuration (ms: float) : string =
  match ms < 1000.0 with
  | true -> sprintf "%dms" (int ms)
  | false -> sprintf "%.1fs" (ms / 1000.0)

// ── Inline eval-result rendering ─────────────────────────────────

/// A rendered inline result: the short ghost text VS Code shows after the
/// line, plus the full untruncated text for `hoverMessage` — so a
/// multi-line or long result is never only reachable through the
/// summarised form. `None` from `renderInlineResult` means "render
/// nothing" (blank output), the pre-extraction behaviour.
type InlineResultRender = {
  ContentText: string
  HoverText: string
}

/// How many lines of a multi-line result are shown verbatim before the
/// summary collapses to "first line … (N lines)". Matches the
/// pre-extraction constant exactly (`InlineDecorations.fs` used a bare `4`).
let [<Literal>] MaxInlineLines = 4

/// The inline "// → ..." ghost text and its full-text hover companion for
/// one eval result, or `None` when there is nothing to show (blank/
/// whitespace-only output — the pre-extraction early return). `durationMs`
/// is appended as a `"  123ms"` suffix on the summary line only, never on
/// the hover text (the hover already carries the full result; there is
/// nothing to disambiguate).
let renderInlineResult (durationMs: float option) (text: string) : InlineResultRender option =
  let trimmed = text.Trim()
  match trimmed with
  | "" -> None
  | _ ->
    let lines = trimmed.Split('\n')
    let firstLine = match lines.Length with 0 -> "" | _ -> lines.[0]
    let durSuffix =
      match durationMs with
      | Some ms -> sprintf "  %s" (formatDuration ms)
      | None -> ""
    let summary =
      match lines.Length with
      | 0 | 1 -> firstLine
      | n when n <= MaxInlineLines -> lines |> String.concat "  │  "
      | n -> sprintf "%s  │  ... (%d lines)" firstLine n
    Some {
      ContentText = sprintf "  // → %s%s" summary durSuffix
      HoverText = trimmed
    }

// ── Inline diagnostic rendering ──────────────────────────────────

/// The inline "// ❌ ..." ghost text and its full-text hover companion for
/// one diagnostic, or `None` when the first line is blank (the
/// pre-extraction early return). Only the first line is shown inline —
/// the hover carries every line, so the truncated remainder stays
/// reachable.
let renderInlineDiagnostic (text: string) : InlineResultRender option =
  let parts = text.Split('\n')
  let firstLine = match parts.Length with 0 -> "" | _ -> parts.[0].Trim()
  match firstLine with
  | "" -> None
  | _ ->
    Some {
      ContentText = sprintf "  // ❌ %s" firstLine
      HoverText = text.Trim()
    }

// ── Eval-in-progress ghost text ───────────────────────────────────

/// The "⏳ evaluating…" ghost text, with or without an elapsed-time
/// suffix. `None` = the initial label (`showEvalInProgress`); `Some ms` =
/// the ticking label (`updateEvalInProgressElapsed`).
let evalInProgressLabel (elapsedMs: int64 option) : string =
  match elapsedMs with
  | None -> "  // ⏳ evaluating…"
  | Some ms -> sprintf "  // ⏳ evaluating… %.1fs" (float ms / 1000.0)

// ── Stale-marker transition ───────────────────────────────────────

/// Which of the currently block-decorated lines newly need a "⏸ stale"
/// marker: every line in `blockLines` that is not already in
/// `alreadyStaleLines` — the exact `if not (Map.containsKey line
/// staleDecorations)` guard `markDecorationsStale` applied inline, so a
/// line already carrying a stale marker is never re-created (and never
/// double-disposed). Order is preserved from `blockLines`.
let staleTransition (blockLines: int list) (alreadyStaleLines: Set<int>) : int list =
  blockLines |> List.filter (fun line -> not (Set.contains line alreadyStaleLines))

// ── Binding-value ghost text targeting ────────────────────────────

/// A binding is a candidate for positioned ghost text only when it is not
/// a function value (functions render as `<fn>` but were never given a
/// line target in the pre-extraction code) and its source line is known
/// (`SourceLine > 0` — `0` means "unknown", per the server convention
/// documented on `FeatureTypes.ClientBindingValue`).
let isVisibleBinding (isFunctionValue: bool) (sourceLine: int) : bool =
  not isFunctionValue && sourceLine > 0

/// Where a binding's ghost text targets, in zero-based VS Code document-line
/// terms, or `None` when the computed target falls outside the document —
/// the exact bounds check `showBindingValues` performed before calling
/// `setDecorations`. `blockStartLine` is zero-based (VS Code); `sourceLine`
/// is one-based within the evaluated block (server convention) — this is
/// the "second uncentralised off-by-one" the roast names: get the `- 1`
/// wrong here and every binding value renders one line off from the
/// binding it describes.
let bindingTargetLine (blockStartLine: int) (sourceLine: int) (lineCount: int) : int option =
  let lineIdx = blockStartLine + sourceLine - 1
  match lineIdx >= 0 && lineIdx < lineCount with
  | true -> Some lineIdx
  | false -> None

// ── Per-test CodeLens title ────────────────────────────────────────

/// How long a `Failed`/`Errored` message can render in a CodeLens title
/// before it is truncated with `…`. CodeLens titles are single-line UI
/// chrome, unlike the inline hover text above, which is never truncated.
let [<Literal>] MaxCodeLensMessageLength = 60

let private truncateForLens (msg: string) : string =
  match msg.Length > MaxCodeLensMessageLength with
  | true -> msg.[.. MaxCodeLensMessageLength - 1] + "…"
  | false -> msg

/// The per-test CodeLens title for one outcome — total over `VscTestOutcome`,
/// matching `TestCodeLensProvider.formatTitle` exactly (including that
/// `Failed` truncates its message and `Errored` does not, which is the
/// pre-extraction behaviour, not a new decision).
let formatCodeLensTitle (durationMs: float option) (outcome: VscTestOutcome) : string =
  match outcome with
  | VscTestOutcome.Passed ->
    match durationMs with
    | Some ms -> sprintf "✓ Passed (%.0fms)" ms
    | None -> "✓ Passed"
  | VscTestOutcome.Failed msg -> sprintf "✗ Failed: %s" (truncateForLens msg)
  | VscTestOutcome.Running -> "● Running…"
  | VscTestOutcome.Skipped reason -> sprintf "⊘ Skipped: %s" reason
  | VscTestOutcome.Errored msg -> sprintf "✗ Error: %s" msg
  | VscTestOutcome.Stale -> "◌ Stale"
  | VscTestOutcome.PolicyDisabled -> "⊘ Disabled"
  | VscTestOutcome.NotYetRun -> "◆ Not yet run"
