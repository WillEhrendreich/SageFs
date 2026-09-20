// WHY — sagefs-ux-roast.md Island E: `InlineDecorations.fs` computed inline
// result summarisation, diagnostic truncation, eval-in-progress ghost text,
// stale-marker bookkeeping, and the binding-value target-line arithmetic
// (`blockStartLine + bv.SourceLine - 1`) inline, next to the Fable-only
// `vscode.window` calls, with ZERO test references. This is the eval path —
// the reason the extension exists — and until `InlineDecorationsPure.fs`
// landed, it had no pure-decision coverage at all (`TestDecorationsPure.fs`
// covers test/coverage gutters only). `TestCodeLensProvider.fs`'s per-test
// title formatting duplicated the outcome-to-text decision a second time,
// independently, with its own message-truncation quirk; it is folded in
// here rather than left untested.
//
// Mirrors `TestDecorationsContractTests.fsx` exactly:
//   - an exhaustive, named example per decision, including the boundary
//     cases the sweep calls out by name (the 4-line summary cutoff, the
//     60-character CodeLens truncation, and above all the
//     `blockStartLine + sourceLine - 1` off-by-one);
//   - a property, over ALL of `VscTestOutcome` via FsCheck generation, that
//     `formatCodeLensTitle` never throws and is total;
//   - a property that `bindingTargetLine` only ever accepts an index inside
//     `[0, lineCount)` and rejects everything outside it.
//
// Runs under plain `dotnet fsi` (no Fable, no VS Code, no Electron host) —
// mirroring the other 15 `sagefs-vscode/tests/*.fsx` contract tests.
#r "nuget: Expecto, 11.0.0-alpha8"
#r "nuget: Expecto.FsCheck, 11.0.0-alpha8"
#r "nuget: FsCheck, 3.3.2"
#load "../src/LiveTestingTypes.fs"
#load "../src/InlineDecorationsPure.fs"

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Vscode.LiveTestingTypes
open SageFs.Vscode.InlineDecorationsPure

// ── formatDuration ──────────────────────────────────────────────────

let durationFormatting =
  testList "formatDuration" [
    testCase "under one second renders as milliseconds" <| fun _ ->
      formatDuration 123.0 |> Expect.equal "ms form" "123ms"

    testCase "exactly one second renders as seconds, not milliseconds" <| fun _ ->
      formatDuration 1000.0 |> Expect.equal "1.0s at the boundary" "1.0s"

    testCase "just under one second still renders as milliseconds" <| fun _ ->
      formatDuration 999.0 |> Expect.equal "999ms" "999ms"

    testCase "multi-second durations render to one decimal place" <| fun _ ->
      formatDuration 20468.0 |> Expect.equal "20.5s" "20.5s"
  ]

// ── renderInlineResult ────────────────────────────────────────────

let inlineResultRendering =
  testList "renderInlineResult" [
    testCase "blank text renders nothing" <| fun _ ->
      renderInlineResult None "" |> Expect.isNone "empty string"
      renderInlineResult None "   \n  " |> Expect.isNone "whitespace only"

    testCase "single line, no duration" <| fun _ ->
      let r = renderInlineResult None "42" |> Option.get
      r.ContentText |> Expect.equal "no duration suffix" "  // → 42"
      r.HoverText |> Expect.equal "hover carries the full text" "42"

    testCase "single line with duration appends a duration suffix" <| fun _ ->
      let r = renderInlineResult (Some 250.0) "42" |> Option.get
      r.ContentText |> Expect.equal "duration suffix present" "  // → 42  250ms"
      r.HoverText.Contains("ms") |> Expect.isFalse "hover never carries the duration suffix"

    testCase "exactly 4 lines joins every line with the separator, no truncation" <| fun _ ->
      let text = "a\nb\nc\nd"
      let r = renderInlineResult None text |> Option.get
      r.ContentText |> Expect.equal "all 4 lines shown" "  // → a  │  b  │  c  │  d"
      r.HoverText |> Expect.equal "hover is the untruncated original" text

    testCase "5 lines truncates to the first line plus a count, but the hover keeps everything" <| fun _ ->
      let text = "a\nb\nc\nd\ne"
      let r = renderInlineResult None text |> Option.get
      r.ContentText |> Expect.equal "summary form" "  // → a  │  ... (5 lines)"
      r.HoverText |> Expect.equal "hover has all 5 lines, reachable via hoverMessage" text
      r.HoverText |> Expect.stringContains "the truncated remainder is reachable" "e"

    testCase "leading/trailing whitespace is trimmed before rendering" <| fun _ ->
      let r = renderInlineResult None "  42  \n" |> Option.get
      r.HoverText |> Expect.equal "trimmed" "42"
  ]

// ── renderInlineDiagnostic ──────────────────────────────────────────

let inlineDiagnosticRendering =
  testList "renderInlineDiagnostic" [
    testCase "blank first line renders nothing, even with content on later lines" <| fun _ ->
      renderInlineDiagnostic "" |> Expect.isNone "empty string"
      renderInlineDiagnostic "\nsecond line has content" |> Expect.isNone "blank first line"

    testCase "single-line diagnostic" <| fun _ ->
      let r = renderInlineDiagnostic "type mismatch" |> Option.get
      r.ContentText |> Expect.equal "content text" "  // ❌ type mismatch"
      r.HoverText |> Expect.equal "hover carries the full text" "type mismatch"

    testCase "multi-line diagnostic shows only the first line inline, but the hover keeps the rest" <| fun _ ->
      let text = "type mismatch\nExpected: int\nActual: string"
      let r = renderInlineDiagnostic text |> Option.get
      r.ContentText |> Expect.equal "first line only, inline" "  // ❌ type mismatch"
      r.HoverText |> Expect.equal "hover has every line" text
      r.HoverText |> Expect.stringContains "the truncated remainder is reachable via hoverMessage" "Expected: int"
  ]

// ── evalInProgressLabel ───────────────────────────────────────────

let evalInProgressLabelling =
  testList "evalInProgressLabel" [
    testCase "no elapsed time yet -> the initial label, no timer" <| fun _ ->
      evalInProgressLabel None |> Expect.equal "initial label" "  // ⏳ evaluating…"

    testCase "elapsed time renders one decimal place of seconds" <| fun _ ->
      evalInProgressLabel (Some 2500L) |> Expect.equal "2.5s" "  // ⏳ evaluating… 2.5s"

    testCase "sub-second elapsed still renders as a decimal-second suffix" <| fun _ ->
      evalInProgressLabel (Some 300L) |> Expect.equal "0.3s" "  // ⏳ evaluating… 0.3s"
  ]

// ── staleTransition ───────────────────────────────────────────────

let staleTransitionTests =
  testList "staleTransition" [
    testCase "no lines already stale -> every block line becomes newly stale" <| fun _ ->
      staleTransition [ 3; 7; 10 ] Set.empty |> Expect.equal "all pass through" [ 3; 7; 10 ]

    testCase "a line already marked stale is not re-created" <| fun _ ->
      staleTransition [ 3; 7; 10 ] (Set.ofList [ 7 ]) |> Expect.equal "7 excluded" [ 3; 10 ]

    testCase "every line already stale -> nothing newly transitions" <| fun _ ->
      staleTransition [ 3; 7 ] (Set.ofList [ 3; 7 ]) |> Expect.isEmpty "none left"

    testCase "order is preserved" <| fun _ ->
      staleTransition [ 10; 3; 7 ] Set.empty |> Expect.equal "input order kept" [ 10; 3; 7 ]
  ]

// ── isVisibleBinding / bindingTargetLine ─────────────────────────────
//
// The roast names this "a second uncentralised off-by-one" — the exact
// defect class `TestDecorationsPure`'s bucket-routing property was written
// to catch, one layer over. These tests are written to fail loudly if the
// `- 1` in `bindingTargetLine` is ever dropped, moved, or flipped in sign.

let bindingVisibility =
  testList "isVisibleBinding" [
    testCase "a function value is never visible, even with a known source line" <| fun _ ->
      isVisibleBinding true 5 |> Expect.isFalse "functions render as <fn>, not positioned"

    testCase "an unknown source line (0) is never visible" <| fun _ ->
      isVisibleBinding false 0 |> Expect.isFalse "SourceLine = 0 means unknown"

    testCase "a non-function binding with a known line is visible" <| fun _ ->
      isVisibleBinding false 5 |> Expect.isTrue "eligible for ghost text"
  ]

let bindingTargeting =
  testList "bindingTargetLine" [
    testCase "the first line of the block (sourceLine = 1) targets the block's own start line" <| fun _ ->
      bindingTargetLine 10 1 100 |> Expect.equal "start line itself, not start+1" (Some 10)

    testCase "the second line of the block targets one past the start" <| fun _ ->
      bindingTargetLine 10 2 100 |> Expect.equal "start + 1" (Some 11)

    testCase "a block starting at line 0 with sourceLine 1 targets line 0" <| fun _ ->
      bindingTargetLine 0 1 100 |> Expect.equal "no negative index" (Some 0)

    testCase "a target exactly at the last valid line is in bounds" <| fun _ ->
      bindingTargetLine 0 10 10 |> Expect.equal "index 9 is the last valid line of a 10-line doc" (Some 9)

    testCase "a target one past the last line is out of bounds" <| fun _ ->
      bindingTargetLine 0 11 10 |> Expect.equal "index 10 is out of bounds for a 10-line doc" None

    testProperty "the target is always exactly blockStartLine + sourceLine - 1 when in bounds" <|
      Prop.forAll (Arb.fromGen (Gen.choose (0, 1000))) (fun (blockStartLine: int) ->
        Prop.forAll (Arb.fromGen (Gen.choose (1, 1000))) (fun (sourceLine: int) ->
          let lineCount = blockStartLine + sourceLine + 1 // always enough room
          bindingTargetLine blockStartLine sourceLine lineCount = Some (blockStartLine + sourceLine - 1)))

    testProperty "the result is always None outside [0, lineCount) and Some inside it" <|
      Prop.forAll
        (Arb.fromGen (gen {
          let! blockStartLine = Gen.choose (0, 500)
          let! sourceLine = Gen.choose (1, 500)
          let! lineCount = Gen.choose (0, 1000)
          return blockStartLine, sourceLine, lineCount
        }))
        (fun (blockStartLine, sourceLine, lineCount) ->
          let idx = blockStartLine + sourceLine - 1
          match bindingTargetLine blockStartLine sourceLine lineCount with
          | Some got -> got = idx && idx >= 0 && idx < lineCount
          | None -> idx < 0 || idx >= lineCount)
  ]

// ── formatCodeLensTitle ───────────────────────────────────────────

let genOutcome : Gen<VscTestOutcome> =
  Gen.oneof [
    Gen.constant VscTestOutcome.Passed
    Gen.map VscTestOutcome.Failed (ArbMap.defaults |> ArbMap.generate<string>)
    Gen.map VscTestOutcome.Skipped (ArbMap.defaults |> ArbMap.generate<string>)
    Gen.constant VscTestOutcome.Running
    Gen.map VscTestOutcome.Errored (ArbMap.defaults |> ArbMap.generate<string>)
    Gen.constant VscTestOutcome.Stale
    Gen.constant VscTestOutcome.PolicyDisabled
    Gen.constant VscTestOutcome.NotYetRun
  ]

let codeLensTitleExamples =
  testList "formatCodeLensTitle" [
    testCase "Passed with a duration shows the duration" <| fun _ ->
      formatCodeLensTitle (Some 42.0) VscTestOutcome.Passed |> Expect.equal "passed+duration" "✓ Passed (42ms)"

    testCase "Passed with no duration omits it" <| fun _ ->
      formatCodeLensTitle None VscTestOutcome.Passed |> Expect.equal "passed, no duration" "✓ Passed"

    testCase "a Failed message of exactly 60 characters is not truncated" <| fun _ ->
      let msg = String.replicate 60 "x"
      formatCodeLensTitle None (VscTestOutcome.Failed msg)
      |> Expect.equal "60 chars fits exactly" (sprintf "✗ Failed: %s" msg)

    testCase "a Failed message of 61 characters is truncated to 60 chars plus an ellipsis" <| fun _ ->
      let msg = String.replicate 61 "x"
      let expected = sprintf "✗ Failed: %s…" (String.replicate 60 "x")
      formatCodeLensTitle None (VscTestOutcome.Failed msg) |> Expect.equal "truncated at 60" expected

    testCase "Errored messages are never truncated, unlike Failed (pre-extraction behaviour, pinned)" <| fun _ ->
      let msg = String.replicate 200 "x"
      formatCodeLensTitle None (VscTestOutcome.Errored msg)
      |> Expect.equal "full message" (sprintf "✗ Error: %s" msg)

    testCase "Running" <| fun _ ->
      formatCodeLensTitle None VscTestOutcome.Running |> Expect.equal "running" "● Running…"

    testCase "Skipped carries the reason" <| fun _ ->
      formatCodeLensTitle None (VscTestOutcome.Skipped "not applicable")
      |> Expect.equal "skipped" "⊘ Skipped: not applicable"

    testCase "Stale" <| fun _ ->
      formatCodeLensTitle None VscTestOutcome.Stale |> Expect.equal "stale" "◌ Stale"

    testCase "PolicyDisabled" <| fun _ ->
      formatCodeLensTitle None VscTestOutcome.PolicyDisabled |> Expect.equal "disabled" "⊘ Disabled"

    testCase "NotYetRun" <| fun _ ->
      formatCodeLensTitle None VscTestOutcome.NotYetRun |> Expect.equal "not yet run" "◆ Not yet run"
  ]

let codeLensTitleProperty =
  testList "formatCodeLensTitle is total over VscTestOutcome" [
    testProperty "every outcome produces a non-null title without throwing" <|
      Prop.forAll (Arb.fromGen genOutcome) (fun outcome ->
        let title = formatCodeLensTitle None outcome
        not (isNull title) && title.Length > 0)
  ]

let tests =
  testList "InlineDecorationsPure contract" [
    durationFormatting
    inlineResultRendering
    inlineDiagnosticRendering
    evalInProgressLabelling
    staleTransitionTests
    bindingVisibility
    bindingTargeting
    codeLensTitleExamples
    codeLensTitleProperty
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
