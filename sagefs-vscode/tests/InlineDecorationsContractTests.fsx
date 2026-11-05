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
      r.ContentText |> Expect.equal "all 4 lines shown, joined with a semicolon" "  // → a; b; c; d"
      r.HoverText |> Expect.equal "hover is the untruncated original" text

    testCase "5 lines truncates to the first line plus a count, but the hover keeps everything" <| fun _ ->
      let text = "a\nb\nc\nd\ne"
      let r = renderInlineResult None text |> Option.get
      r.ContentText |> Expect.equal "summary form says how many lines there are" "  // → a … (5 lines)"
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

// ── linesStaledByEval ─────────────────────────────────────────────
//
// WHY: issue #132's GIF. The daemon says "an eval started" for ANY eval of the file, and the extension marked
// EVERY result in the file stale and then painted only the block that evaluated. Evaluate the block under the
// cursor and every other block's result flipped to "stale" with no edit anywhere, so which block showed a result
// followed the cursor. A result is stale when ITS source changed; an eval somewhere else does not change it.

let linesStaledByEvalTests =
  testList "linesStaledByEval" [
    testCase "WHY — evaluating block 2 leaves block 1's result alone" <| fun _ ->
      // block 1 = lines 0-1 (result at 1), block 2 = lines 3-4 (result at 4)
      linesStaledByEval [ 1; 4 ] 3 4 |> Expect.equal "only block 2's result" [ 4 ]
      linesStaledByEval [ 1; 4 ] 0 1 |> Expect.equal "only block 1's result" [ 1 ]

    testCase "a result inside the block being re-evaluated is staled, wherever in the block it sits" <| fun _ ->
      linesStaledByEval [ 0; 2 ] 0 2 |> Expect.equal "both bounds are inside" [ 0; 2 ]

    testCase "an eval of a block with no result yet stales nothing" <| fun _ ->
      linesStaledByEval [ 1 ] 3 4 |> Expect.isEmpty "nothing in that block"

    testCase "no decorations, nothing to stale" <| fun _ ->
      linesStaledByEval [] 0 10 |> Expect.isEmpty "empty in, empty out"

    // The reporter's GIF as a sequence. check.fsx has two blocks, lines 0-1 and 3-4. Shift+Enter evaluates a block
    // and moves on, so the session was: block 1, block 2, block 1, block 2, about two seconds apart. Frame by frame,
    // each fresh result coincided with the OTHER block going stale. This replays the sequence through the same
    // pure decisions the extension runs (`linesStaledByEval` when an eval starts, a fresh result on the block's
    // last line when it ends) and asks what is on screen after each step.
    testCase "WHY — evaluating block 1, block 2, block 1, block 2 leaves both results fresh, where it used to leave the last one alone" <| fun _ ->
      let blocks = [ 0, 1; 3, 4 ]
      // line -> fresh? (true = a result, false = a stale marker); only decorated lines are present
      let evalBlock (screen: Map<int, bool>) (start: int, last: int) =
        let staled = linesStaledByEval (screen |> Map.toList |> List.map fst) start last
        let afterStart = staled |> List.fold (fun m line -> Map.add line false m) screen
        Map.add last true afterStart
      let oldEvalBlock (screen: Map<int, bool>) (_, last: int) =
        // the old rule: everything on screen goes stale when an eval starts
        let allStale = screen |> Map.map (fun _ _ -> false)
        Map.add last true allStale
      let run step = [ 0; 1; 0; 1 ] |> List.map (fun i -> List.item i blocks) |> List.fold step Map.empty
      let freshLines (screen: Map<int, bool>) = screen |> Map.toList |> List.filter snd |> List.map fst
      run evalBlock |> freshLines |> Expect.equal "both blocks keep their results" [ 1; 4 ]
      run oldEvalBlock |> freshLines |> Expect.equal "the old rule left only the block evaluated last" [ 4 ]
  ]

// ── one line of ghost text ────────────────────────────────────────
//
// WHY: issue #132's GIF. VS Code keeps only the FIRST LINE of a decoration's `contentText`: its decoration CSS code
// is `contentText.match(/^.*$/m)[0]` (VS Code 1.142, workbench.desktop.main.js), and `.` stops at \n, \r,
// and the two Unicode separators (code points 0x2028 and 0x2029). A Windows daemon ends FSI's lines with \r\n. The
// renderer split on \n only, so every line kept its
// trailing \r and the ghost text was cut after the first binding: a two-binding block showed `val a: int = 3` and
// never `val b: int = 5`, while a one-line result (the printf block) showed in full. The reply was never wrong.

let lineSeparator = string (char 0x2028)
let paragraphSeparator = string (char 0x2029)
let terminators = [| '\r'; '\n'; char 0x2028; char 0x2029 |]
let hasTerminator (s: string) = s.IndexOfAny terminators >= 0

let oneLineGhostTextTests =
  testList "ghost text is one line" [
    testCase "WHY — a Windows reply with two bindings keeps both, and nothing VS Code would cut at" <| fun _ ->
      match renderInlineResult None "Result: val a: int = 3\r\nval b: int = 5\r\n" with
      | None -> failtest "a result with text renders"
      | Some rendered ->
        rendered.ContentText |> Expect.stringContains "the first binding" "val a: int = 3"
        rendered.ContentText |> Expect.stringContains "and the second one, which was cut off" "val b: int = 5"
        hasTerminator rendered.ContentText |> Expect.isFalse "no line terminator is left in the ghost text"
        rendered.HoverText |> Expect.stringContains "the hover still has the whole reply" "val b: int = 5"

    testCase "a Unix reply joins its lines with a semicolon, never a box-drawing bar" <| fun _ ->
      match renderInlineResult None "Result: val a: int = 3\nval b: int = 5\n" with
      | None -> failtest "a result with text renders"
      | Some rendered ->
        rendered.ContentText |> Expect.equal "joined with a semicolon" "  // → Result: val a: int = 3; val b: int = 5"
        rendered.ContentText.Contains "│" |> Expect.isFalse "no box-drawing bar in the ghost text"

    testCase "a diagnostic whose first line ends in \\r\\n renders that line alone" <| fun _ ->
      match renderInlineDiagnostic "error FS0001: bad\r\nmore detail\r\n" with
      | None -> failtest "a diagnostic with text renders"
      | Some rendered ->
        rendered.ContentText |> Expect.equal "only the first line" "  // ❌ error FS0001: bad"
        hasTerminator rendered.ContentText |> Expect.isFalse "no terminator"

    testCase "ghostLine turns every kind of line break into one space and trims the ends" <| fun _ ->
      ghostLine ("a\r\nb\nc\rd" + lineSeparator + "e" + paragraphSeparator + "f") |> Expect.equal "one line" "a b c d e f"
      ghostLine "  \r\n x \r\n " |> Expect.equal "trimmed, runs collapsed" "x"

    testCase "asOneLine replaces line breaks and keeps the leading spaces a ghost text starts with" <| fun _ ->
      asOneLine "  // → a\r\nb" |> Expect.equal "break became a space, indent kept" "  // → a b"
      asOneLine "  x" |> Expect.equal "no break, unchanged" "  x"

    testProperty "asOneLine never leaves a line terminator, whatever it is given" <|
      Prop.forAll (Arb.fromGen (Gen.elements [ "a"; "\r"; "\n"; "\r\n"; lineSeparator; paragraphSeparator; " " ] |> Gen.listOf |> Gen.map (String.concat ""))) (fun text ->
        not (hasTerminator (asOneLine text)))

    testProperty "WHY — whatever a result or a diagnostic says, no ghost text holds a line terminator" <|
      Prop.forAll (Arb.fromGen (Gen.elements [ "a"; "b b"; "\r"; "\n"; "\r\n"; lineSeparator; paragraphSeparator; " "; "→"; "val x: int = 1" ] |> Gen.listOf |> Gen.map (String.concat ""))) (fun text ->
        let result = renderInlineResult (Some 12.0) text
        let diagnostic = renderInlineDiagnostic text
        let clean (rendered: InlineResultRender option) =
          match rendered with
          | None -> true
          | Some r -> not (hasTerminator r.ContentText)
        clean result && clean diagnostic)
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
    linesStaledByEvalTests
    oneLineGhostTextTests
    bindingVisibility
    bindingTargeting
    codeLensTitleExamples
    codeLensTitleProperty
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
