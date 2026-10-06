// WHY — VS Code's roadmap item asks for "Alt-drag in VS Code … writing through the nudge door",
// and the extension API cannot deliver a drag: `TextEditorSelectionChangeEvent` carries only
// `textEditor`, `selections` and `kind` (its own index.d.ts has no `modifiers`), and there is
// no editor mouse event anywhere in the typings. The gesture this extension decodes is therefore
// an INFERENCE — a multi-cursor mouse drag, which is the fingerprint of Alt under VS Code's
// default `editor.multiCursorModifier = "alt"` — and an inference with arithmetic on top is
// exactly the kind of thing that must be pinned before the glue is wired to it.
//
// These also pin the stepping arithmetic against the dashboard knob's own rules
// (`SageFs.Core/Features/Tweak/BindingTweak.fs` `Step` / `Stepping.literalAfter`): one step is
// one unit in the literal's last decimal place, the result is rounded to the literal's own
// decimals, an out-of-range step is refused rather than wrapped, and one settle carries at most
// the dashboard's `MostSteps`.
//
// Runs under plain `dotnet fsi` (no Fable, no VS Code), mirroring DensityContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/NudgeKnobPure.fs"

open Expecto
open Expecto.Flip
open SageFs.Vscode.NudgeKnobPure

let private knob () =
  { File = "/w/Tuning.fs"
    Address = "Game.Tuning.tuning/{JumpVelocity}"
    Hash = String.replicate 64 "a"
    Text = "9.87"
    Line = 1
    StartCol = 10
    EndLine = 1
    EndCol = 14
    Scrub = Scrub.Fraction(9.87, 2) }

let private tracking line col pending resync knob =
  { Knob = knob
    LastLine = line
    LastCol = col
    Pending = pending
    Resync = resync }

let private altDrag file positions knobs =
  { File = file
    IsMouse = true
    IsAltGesture = true
    Positions = positions
    Knobs = knobs }

let private plainClick file positions knobs =
  { File = file
    IsMouse = true
    IsAltGesture = false
    Positions = positions
    Knobs = knobs }

let private okOf (what: string) (r: Result<string, StepRefusal>) =
  match r with
  | Ok v -> v
  | Error refusal -> failtestf "%s: expected Ok, got %A" what refusal

let tests =
  testList "VS Code value knob - the gesture decodes and the steps are the dashboard's" [

    testCase "WHY - a plain click on a number starts NO scrub: normal selection must survive" <| fun _ ->
      decide ScrubState.Idle (plainClick "/w/Tuning.fs" [ (0, 12) ] [ knob () ])
      |> Expect.equal "one selection is a click, not a drag" ScrubState.Idle

    testCase "WHY - a keypress or a second editor re-anchors, so a caret jump is never a step" <| fun _ ->
      let g = tracking 0 12 7 false (knob ())
      decide (ScrubState.Tracking g) { File = "/w/Tuning.fs"; IsMouse = false; IsAltGesture = false; Positions = [ (0, 40) ]; Knobs = [ knob () ] }
      |> function
        | ScrubState.Tracking g' ->
          g'.Pending |> Expect.equal "pending steps are held, not counted" 7
          g'.Resync |> Expect.isTrue "the next drag event only re-anchors"
          g'.LastCol |> Expect.equal "the caret's new column is not taken as movement" 12
        | ScrubState.Idle -> failtest "the gesture must survive a non-drag event"

    testCase "WHY - an Alt-gesture over a knob starts it anchored where the pointer is" <| fun _ ->
      decide ScrubState.Idle (altDrag "/w/Tuning.fs" [ 0, 12; 0, 3 ] [ knob () ])
      |> function
        | ScrubState.Tracking g ->
          g.Pending |> Expect.equal "nothing counted at the anchor" 0
          g.LastCol |> Expect.equal "anchored at the drag position" 12
          g.Knob.Address |> Expect.equal "the knob under the pointer" (knob ()).Address
        | ScrubState.Idle -> failtest "a drag over a knob must start the scrub"

    testCase "WHY - dragging right counts columns up, dragging back gives them back" <| fun _ ->
      let start = decide ScrubState.Idle (altDrag "/w/Tuning.fs" [ (0, 12) ] [ knob () ])
      let right = decide start (altDrag "/w/Tuning.fs" [ (0, 15) ] [ knob () ])
      let back = decide right (altDrag "/w/Tuning.fs" [ (0, 14) ] [ knob () ])
      match back with
      | ScrubState.Tracking g -> g.Pending |> Expect.equal "+3 then -1 is 2 steps" 2
      | ScrubState.Idle -> failtest "still dragging"

    testCase "WHY - the multi-cursor is what carries the gesture: the stationary second cursor never counts" <| fun _ ->
      // During an Alt+drag the event holds BOTH carets — the dragged one at the pointer and the
      // one the Alt+click added where it was. The tracked caret is whichever sits nearest where
      // the scrub last saw the pointer; a far-away stationary caret cannot inject steps.
      let start = decide ScrubState.Idle (altDrag "/w/Tuning.fs" [ 0, 12; 0, 3 ] [ knob () ])
      let moved = decide start (altDrag "/w/Tuning.fs" [ 0, 3; 0, 14 ] [ knob () ])
      match moved with
      | ScrubState.Tracking g -> g.Pending |> Expect.equal "only the dragged caret counted" 2
      | ScrubState.Idle -> failtest "still dragging"

    testCase "WHY - a drag that leaves the line re-anchors instead of counting a wild jump" <| fun _ ->
      let start = decide ScrubState.Idle (altDrag "/w/Tuning.fs" [ (0, 14) ] [ knob () ])
      let jumped = decide start (altDrag "/w/Tuning.fs" [ (3, 0) ] [ knob () ])
      match jumped with
      | ScrubState.Tracking g ->
        g.Pending |> Expect.equal "no step from a line jump" 0
        g.LastLine |> Expect.equal "re-anchored on the new line" 3
        g.Resync |> Expect.isFalse "the re-anchor is immediate"
      | ScrubState.Idle -> failtest "still dragging"

    testCase "WHY - a gesture can hold at most the dashboard's MostSteps, so a long drag lands where the dashboard's would" <| fun _ ->
      let mutable state = decide ScrubState.Idle (altDrag "/w/Tuning.fs" [ (0, 0) ] [ knob () ])
      for col in 1 .. 250 do
        state <- decide state (altDrag "/w/Tuning.fs" [ (0, col) ] [ knob () ])
      match state with
      | ScrubState.Tracking g ->
        g.Pending |> Expect.equal "clamped at MostSteps" maxPendingSteps
        maxPendingSteps |> Expect.equal "the knob's own cap" 100
      | ScrubState.Idle -> failtest "still dragging"

    testCase "WHY - an Alt-gesture anywhere else starts nothing: only a real knob takes the drag" <| fun _ ->
      decide ScrubState.Idle (altDrag "/w/Tuning.fs" [ (0, 3) ] [ knob () ])
      |> Expect.equal "left of the span" ScrubState.Idle
      decide ScrubState.Idle (altDrag "/w/Tuning.fs" [ (0, 40) ] [ knob () ])
      |> Expect.equal "right of the span" ScrubState.Idle
      decide ScrubState.Idle (altDrag "/w/Other.fs" [ (0, 12) ] [ knob () ])
      |> Expect.equal "another file's knobs are not this file's" ScrubState.Idle

    testCase "WHY - a non-number literal has no drag, matching the dashboard: numbers get the handle" <| fun _ ->
      let textKnob = { knob () with Scrub = Scrub.NotANumber; Text = "\"wind\""; EndCol = 16 }
      decide ScrubState.Idle (altDrag "/w/Tuning.fs" [ (0, 12) ] [ textKnob ])
      |> Expect.equal "a string under the drag is just a drag" ScrubState.Idle

    testCase "WHY - the span is line/col from the door, both ends inclusive of the digit a drag can rest past" <| fun _ ->
      hits (knob ()) 0 10 |> Expect.isTrue "first column"
      hits (knob ()) 0 14 |> Expect.isTrue "last column as the door reports it"
      hits (knob ()) 0 9 |> Expect.isFalse "one left of the span"
      hits (knob ()) 0 15 |> Expect.isFalse "one right of the span"
      hits (knob ()) 1 12 |> Expect.isFalse "the next line"

    testCase "WHY - the settle is ONE write carrying the whole drag, the dashboard's one write per settle" <| fun _ ->
      let g = tracking 0 14 6 false (knob ())
      let state, work = flush (ScrubState.Tracking g)
      state |> Expect.equal "the settle ends the gesture" ScrubState.Idle
      match work with
      | Some (k, steps) ->
        k.Address |> Expect.equal "the knob that was dragged" (knob ()).Address
        steps |> Expect.equal "every pending column in one write" 6
      | None -> failtest "a pending drag must flush"

    testCase "WHY - a settle with nothing pending writes nothing at all" <| fun _ ->
      let state, work = flush (ScrubState.Tracking (tracking 0 12 0 false (knob ())))
      state |> Expect.equal "idle either way" ScrubState.Idle
      work |> Expect.isNone "no steps, no write"

    testCase "WHY - flush never drops steps: a gesture interrupted by a keypress still lands" <| fun _ ->
      let g = { (tracking 0 14 5 false (knob ())) with Resync = true }
      let _, work = flush (ScrubState.Tracking g)
      work |> Expect.equal "the held steps come back" (Some (knob (), 5))

    // ── the arithmetic, against BindingTweak's own rules ──

    testCase "WHY - a real steps by its last decimal place: 1.0 by 0.1, 0.12 by 0.01, 10 by 1" <| fun _ ->
      stepSize (decimalsOf "1.0") |> Expect.equal "one tenth" 0.1
      stepSize (decimalsOf "0.12") |> Expect.equal "one hundredth" 0.01
      stepSize (decimalsOf "10") |> Expect.equal "a whole unit" 1.0
      decimalsOf "5." |> Expect.equal "a bare point spells no decimals" 0
      decimalsOf "1e3" |> Expect.equal "an exponent carries no decimals here" 0
      decimalsOf "1_000.005" |> Expect.equal "an underscore is not a digit" 3

    testCase "WHY - stepping a real lands on the literal's own decimals, like literalAfter" <| fun _ ->
      literalAfter { knob () with Scrub = Scrub.Fraction(1.234, 3) } 1
      |> okOf "steps up"
      |> Expect.equal "+1 in the third decimal" "1.235"
      literalAfter { knob () with Scrub = Scrub.Fraction(1.234, 3) } -2
      |> okOf "steps down"
      |> Expect.equal "-2 in the third decimal" "1.232"
      literalAfter { knob () with Text = "1.0"; Scrub = Scrub.Fraction(1.0, 1) } 1
      |> okOf "steps up"
      |> Expect.equal "1.0 + 0.1" "1.1"
      literalAfter { knob () with Text = "10"; Scrub = Scrub.Fraction(10.0, 0) } 1
      |> okOf "steps up"
      |> Expect.equal "a real spelled without decimals steps whole" "11"

    testCase "WHY - no float dust: the result is rounded to the literal's own decimals" <| fun _ ->
      literalAfter { knob () with Scrub = Scrub.Fraction(0.1, 1) } 2
      |> okOf "0.1 + 0.2"
      |> Expect.equal "rounds to what the author would type" "0.3"

    testCase "WHY - an integer steps by one and keeps whole" <| fun _ ->
      literalAfter { knob () with Scrub = Scrub.Whole 150L } 1
      |> okOf "steps up"
      |> Expect.equal "whole in, whole out" "151"
      literalAfter { knob () with Scrub = Scrub.Whole -3L } -1
      |> okOf "steps down"
      |> Expect.equal "negative too" "-4"

    testCase "WHY - a step out of a literal's range is REFUSED, never wrapped" <| fun _ ->
      literalAfter { knob () with Scrub = Scrub.Whole System.Int64.MaxValue } 1
      |> Expect.equal "past the top" (Error StepRefusal.OutOfRange)
      literalAfter { knob () with Scrub = Scrub.Whole System.Int64.MinValue } -1
      |> Expect.equal "past the bottom" (Error StepRefusal.OutOfRange)
      literalAfter { knob () with Scrub = Scrub.NotANumber } 1
      |> Expect.equal "a non-number has no steps" (Error StepRefusal.OutOfRange)

    // ── what a Written reply does to the knob the next gesture will use ──

    testCase "WHY - a write retargets the knob: new text, new seen hash, span end moved by the growth" <| fun _ ->
      let after = afterWrite (knob ()) (knob ()).Address "9.88" (String.replicate 64 "b")
      after.Hash |> Expect.equal "the next set is seen against THIS hash" (String.replicate 64 "b")
      after.Text |> Expect.equal "what the door wrote" "9.88"
      after.EndCol |> Expect.equal "same length, same end" 14
      let grown = afterWrite (knob ()) (knob ()).Address "1234.56" (String.replicate 64 "b")
      grown.EndCol |> Expect.equal "the span grew with the text" 17
      grown.Scrub |> Expect.equal "the shape re-reads from the written text" (Scrub.Fraction(1234.56, 2))

    testCase "WHY - the door keeps hex, so the shape must read hex back" <| fun _ ->
      let hex = { knob () with Text = "0x1F"; Scrub = Scrub.Whole 31L; EndCol = 13 }
      let after = afterWrite hex hex.Address "0x20" (String.replicate 64 "c")
      after.Scrub |> Expect.equal "0x20 is 32" (Scrub.Whole 32L)
      literalAfter after 1 |> okOf "steps up in hex" |> Expect.equal "0x21" "33"
      tryParseIntLike "0b1010" |> Expect.equal "binary" (Some 10L)
      tryParseIntLike "-0o17" |> Expect.equal "octal with a sign" (Some -15L)
      tryParseIntLike "1_000" |> Expect.equal "underscores gone" (Some 1000L)

    testCase "WHY - text the parser cannot read stops the drag instead of stepping garbage" <| fun _ ->
      scrubOfText (Scrub.Fraction(1.0, 1)) "wind" |> Expect.equal "not a real" Scrub.NotANumber
      scrubOfText (Scrub.Whole 1L) "wind" |> Expect.equal "not an int" Scrub.NotANumber
      tryParseInvariantFloat "1.5e2" |> Expect.equal "exponent" (Some 150.0)
      tryParseInvariantFloat ".5" |> Expect.equal "bare fraction" (Some 0.5)
      tryParseInvariantFloat "5." |> Expect.equal "trailing point" (Some 5.0)
      tryParseInvariantFloat "abc" |> Expect.equal "not a number" None
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1

exit (runTestsWithCLIArgs [] argv tests)
