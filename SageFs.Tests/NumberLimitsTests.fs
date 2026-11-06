/// What a person chose for one number: where it may go and how far one step moves it. These are the guarantees the slider stands on.
/// A number is never written outside the range the person set or the range its own type holds, and the slider's ends say where those
/// are, so the handle's position means something. The arithmetic is pure, so every claim here is checked without a file or a page.
module SageFs.Tests.NumberLimitsTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.Tweak.LiteralEdit
open SageFs.Features.Tweak
open SageFs.Features.Tweak.BindingTweak

let private limits (lowest: float) (highest: float) (step: float) : NumberLimits =
  NumberLimits.ofNumbers (Edge.At lowest) (Edge.At highest) (StepChoice.Every step) |> Expect.wantOk "valid limits"

let private lowestOnly (lowest: float) : NumberLimits =
  NumberLimits.ofNumbers (Edge.At lowest) Edge.Unlimited StepChoice.FromSpelling |> Expect.wantOk "valid limits"

let private reachOf (typeRange: NumberRange) (chosen: NumberLimits) (control: Control) : Reach =
  match Stepping.reachOf typeRange chosen control with
  | Dial.Dial reach -> reach
  | Dial.NoDial -> failtest "the control has a dial"

let private noTypeLimit : NumberRange = { Smallest = -Double.MaxValue; Largest = Double.MaxValue }

let private texts (lowest: string) (highest: string) (step: string) : LimitsTexts =
  { LowestText = lowest; HighestText = highest; StepText = step }

[<Tests>]
let numberLimitsTests =
  testList "NumberLimits" [

    testCase "no texts means no limits, and the step comes from how the author spelled the number" <| fun _ ->
      NumberLimits.ofTexts (texts "" "" "")
      |> Expect.equal "unlimited" (Ok NumberLimits.unlimited)

    testCase "three numbers are a range and a step" <| fun _ ->
      NumberLimits.ofTexts (texts "0" "30" "0.1")
      |> Expect.equal "the range and step" (Ok { Lowest = Edge.At 0.0; Highest = Edge.At 30.0; Step = StepChoice.Every 0.1 })

    testCase "only one end can be set" <| fun _ ->
      NumberLimits.ofTexts (texts "1" "" "")
      |> Expect.equal "a floor and no ceiling" (Ok { Lowest = Edge.At 1.0; Highest = Edge.Unlimited; Step = StepChoice.FromSpelling })

    testCase "a lowest above the highest is refused, and the refusal names both" <| fun _ ->
      match NumberLimits.ofTexts (texts "30" "0" "") with
      | Error(LimitsRefusal.LowestAboveHighest(lowest, highest)) ->
        lowest |> Expect.equal "lowest" 30.0
        highest |> Expect.equal "highest" 0.0
      | other -> failtestf "expected LowestAboveHighest, got %A" other

    testCase "a step of zero or less is refused" <| fun _ ->
      match NumberLimits.ofTexts (texts "" "" "0") with
      | Error(LimitsRefusal.StepNotPositive _) -> ()
      | other -> failtestf "expected StepNotPositive, got %A" other
      match NumberLimits.ofTexts (texts "" "" "-0.5") with
      | Error(LimitsRefusal.StepNotPositive _) -> ()
      | other -> failtestf "expected StepNotPositive, got %A" other

    testCase "text that is not a finite number is refused, and says which field" <| fun _ ->
      for field, entry in
        [ "lowest", ("abc", "", "")
          "highest", ("", "NaN", "")
          "step", ("", "", "Infinity") ] do
        let lowest, highest, step = entry
        match NumberLimits.ofTexts (texts lowest highest step) with
        | Error(LimitsRefusal.NotANumber(named, _)) -> named |> Expect.equal "the field" field
        | other -> failtestf "expected NotANumber for %s, got %A" field other

    testCase "a number is allowed on both edges and refused just outside" <| fun _ ->
      let chosen = limits 0.0 30.0 0.1
      NumberLimits.allows chosen 0.0 |> Expect.isTrue "the lowest"
      NumberLimits.allows chosen 30.0 |> Expect.isTrue "the highest"
      NumberLimits.allows chosen -0.1 |> Expect.isFalse "below"
      NumberLimits.allows chosen 30.1 |> Expect.isFalse "above"
      NumberLimits.allows NumberLimits.unlimited 1e300 |> Expect.isTrue "no limits allows any finite number"

    testCase "a file of limits reads back what was written" <| fun _ ->
      let book : LimitsBook =
        Map.ofList [ "gravity", limits 0.0 30.0 0.1; "maxHealth", lowestOnly 1.0; "rig/Mass", limits 1.0 500.0 5.0 ]
      LimitsBook.parse (LimitsBook.text book) |> Expect.equal "round trip" (Ok book)

    testCase "an empty file is a book with no limits" <| fun _ ->
      LimitsBook.parse "{}" |> Expect.equal "empty" (Ok Map.empty)

    testCase "a file that is not JSON is refused with a reason, not read as empty" <| fun _ ->
      match LimitsBook.parse "not json" with
      | Error fault -> LimitsFileFault.describe fault |> Expect.isNotEmpty "says why"
      | Ok _ -> failtest "a broken file must not be read as no limits"

    testCase "a file with an impossible entry names the row, and nothing from the file is used" <| fun _ ->
      match LimitsBook.parse """{ "gravity": { "min": 30, "max": 0 } }""" with
      | Error fault -> LimitsFileFault.describe fault |> Expect.stringContains "names the row" "gravity"
      | Ok _ -> failtest "min above max must be refused"

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 } "PROPERTY, limits that were made read back from their own file"
      (Prop.forAll
        (Arb.fromGen (
          Gen.zip3 (Gen.choose (-1000, 1000)) (Gen.choose (0, 1000)) (Gen.choose (1, 50))
          |> Gen.map (fun (low, span, step) -> float low, float (low + span), float step / 10.0)))
        (fun (low, high, step) ->
          let book = Map.ofList [ "x", limits low high step ]
          LimitsBook.parse (LimitsBook.text book) = Ok book))

    // ── stepping stays inside the range ──

    testCase "stepping to the highest lands there, and one step more is refused" <| fun _ ->
      let chosen = limits 0.0 30.0 0.1
      let control = Control.RealStepper(9.8, Step.Fraction 1)
      Stepping.literalAfterWithin chosen control 202 |> Expect.equal "30.0 is the ceiling" (Ok "30")
      match Stepping.literalAfterWithin chosen control 203 with
      | Error(StepRefusal.OutsideLimits(attempted, _, _)) -> attempted |> Expect.floatClose "the refused number" Accuracy.high 30.1
      | other -> failtestf "expected OutsideLimits, got %A" other

    testCase "stepping down to the lowest lands there, and one step more is refused" <| fun _ ->
      let chosen = limits 0.0 30.0 0.1
      let control = Control.RealStepper(9.8, Step.Fraction 1)
      Stepping.literalAfterWithin chosen control -98 |> Expect.equal "0 is the floor" (Ok "0")
      match Stepping.literalAfterWithin chosen control -99 with
      | Error(StepRefusal.OutsideLimits _) -> ()
      | other -> failtestf "expected OutsideLimits, got %A" other

    testCase "a chosen step replaces the step the spelling gives" <| fun _ ->
      let chosen = limits 0.0 30.0 0.25
      let control = Control.RealStepper(9.8, Step.Fraction 1)
      Stepping.literalAfterWithin chosen control 1 |> Expect.equal "9.8 plus a quarter" (Ok "10.05")
      Stepping.literalAfterWithin chosen control -4 |> Expect.equal "four quarters down" (Ok "8.8")

    testCase "an integer steps by a chosen whole amount and stays inside its range" <| fun _ ->
      let chosen = limits 1.0 500.0 5.0
      let control = Control.IntegerStepper 100L
      Stepping.literalAfterWithin chosen control 1 |> Expect.equal "five up" (Ok "105")
      Stepping.literalAfterWithin chosen control -19 |> Expect.equal "down to 5" (Ok "5")
      match Stepping.literalAfterWithin chosen control -20 with
      | Error(StepRefusal.OutsideLimits _) -> ()
      | other -> failtestf "0 is below the floor of 1, got %A" other

    testCase "no limits steps exactly as before" <| fun _ ->
      Stepping.literalAfterWithin NumberLimits.unlimited (Control.RealStepper(9.8, Step.Fraction 1)) 3
      |> Expect.equal "same as literalAfter" (Stepping.literalAfter (Control.RealStepper(9.8, Step.Fraction 1)) 3)

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 300 } "PROPERTY, a step never lands outside a range: it lands inside or is refused"
      (Prop.forAll
        (Arb.fromGen (Gen.zip3 (Gen.choose (-500, 500)) (Gen.choose (0, 800)) (Gen.choose (-900, 900))))
        (fun (low, span, steps) ->
          let chosen = limits (float low) (float (low + span)) 1.0
          let start = float (low + span / 2)
          match Stepping.literalAfterWithin chosen (Control.IntegerStepper(int64 start)) steps with
          | Ok text ->
            let landed = Double.Parse(text, Globalization.CultureInfo.InvariantCulture)
            landed >= float low && landed <= float (low + span)
          | Error(StepRefusal.OutsideLimits _) -> true
          | Error StepRefusal.OutOfRange -> false))

    // ── the slider's reach: where it can go each way, and where it stops ──

    testCase "with a range the handle's two sides stop where the range stops" <| fun _ ->
      let reach = reachOf noTypeLimit (limits 0.0 30.0 0.1) (Control.RealStepper(9.8, Step.Fraction 1))
      reach.Below |> Expect.equal "98 steps to 0" (Run.Ends(98, 0.0))
      reach.Above |> Expect.equal "202 steps to 30" (Run.Ends(202, 30.0))
      reach.Size |> Expect.floatClose "a tenth" Accuracy.high 0.1

    testCase "with no range both sides are an open window, not a scale" <| fun _ ->
      let reach = reachOf noTypeLimit NumberLimits.unlimited (Control.RealStepper(9.8, Step.Fraction 1))
      reach.Below |> Expect.equal "open below" (Run.Open Stepping.OpenWindow)
      reach.Above |> Expect.equal "open above" (Run.Open Stepping.OpenWindow)

    testCase "a floor with no ceiling is bounded on one side only" <| fun _ ->
      let reach = reachOf noTypeLimit (lowestOnly 1.0) (Control.IntegerStepper 100L)
      reach.Below |> Expect.equal "99 steps to 1" (Run.Ends(99, 1.0))
      reach.Above |> Expect.equal "open above" (Run.Open Stepping.OpenWindow)

    testCase "a number's own type bounds the handle even when nothing was chosen" <| fun _ ->
      let reach = reachOf (typeRangeOfText "100") NumberLimits.unlimited (Control.IntegerStepper 2147483640L)
      reach.Above |> Expect.equal "seven steps to the top of an int" (Run.Ends(7, 2147483647.0))

    testCase "a byte's handle runs from 0 to 255" <| fun _ ->
      let reach = reachOf (typeRangeOfText "2uy") NumberLimits.unlimited (Control.IntegerStepper 2L)
      reach.Below |> Expect.equal "two steps down" (Run.Ends(2, 0.0))
      reach.Above |> Expect.equal "253 steps up" (Run.Ends(253, 255.0))

    testCase "the tighter of the chosen range and the type's wins" <| fun _ ->
      let reach = reachOf (typeRangeOfText "2uy") (limits 0.0 1000.0 1.0) (Control.IntegerStepper 2L)
      reach.Above |> Expect.equal "255, not 1000" (Run.Ends(253, 255.0))

    testCase "a value already outside the chosen range can only step back toward it" <| fun _ ->
      let reach = reachOf noTypeLimit (limits 0.0 30.0 1.0) (Control.IntegerStepper 50L)
      reach.Above |> Expect.equal "no way up" (Run.Ends(0, 30.0))
      reach.Below |> Expect.equal "back to 0 is fifty steps" (Run.Ends(50, 0.0))

    testCase "a toggle, a field and no control have no dial" <| fun _ ->
      Stepping.reachOf noTypeLimit NumberLimits.unlimited (Control.Toggle true) |> Expect.equal "toggle" Dial.NoDial
      Stepping.reachOf noTypeLimit NumberLimits.unlimited Control.NoControl |> Expect.equal "none" Dial.NoDial

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 300 } "PROPERTY, the handle's reach never carries it outside the range"
      (Prop.forAll
        (Arb.fromGen (Gen.zip3 (Gen.choose (-500, 500)) (Gen.choose (0, 800)) (Gen.choose (1, 9))))
        (fun (low, span, step) ->
          let high = low + span
          let chosen = limits (float low) (float high) (float step)
          let start = int64 (low + span / 2)
          let control = Control.IntegerStepper start
          let reach = reachOf noTypeLimit chosen control
          let fits (steps: int) =
            match Stepping.literalAfterWithin chosen control steps with
            | Ok _ -> true
            | Error _ -> false
          let below = match reach.Below with Run.Ends(n, _) | Run.Open n -> n
          let above = match reach.Above with Run.Ends(n, _) | Run.Open n -> n
          fits -below && fits above))

    // ── a written text is held to the range of its row, whoever writes it ──

    testCase "a write is checked against the row its address names, and a number outside the range is refused with the row's name" <| fun _ ->
      let book : LimitsBook = Map.ofList [ "gravity", limits 0.0 30.0 0.1; "rig/Mass", limits 1.0 500.0 5.0 ]
      let at (name: string) (path: SageFs.Features.Tweak.TweakAddress.PathStep list) : SageFs.Features.Tweak.TweakAddress.TweakAddress =
        { ModulePath = [ "Game" ]; BindingName = name; Path = path }
      LimitsBook.checkWrite book (at "gravity" []) "29.5" |> Expect.equal "inside" (Ok())
      match LimitsBook.checkWrite book (at "gravity" []) "31.0" with
      | Error refusal -> RangeRefusal.describe refusal |> Expect.stringContains "names the row" "gravity"
      | Ok() -> failtest "31 is outside 0 to 30"
      match LimitsBook.checkWrite book (at "rig" [ SageFs.Features.Tweak.TweakAddress.PathStep.RecordField "Mass" ]) "0.5" with
      | Error refusal -> RangeRefusal.describe refusal |> Expect.stringContains "a record field is its own row" "rig/Mass"
      | Ok() -> failtest "0.5 is below 1"

    testCase "text that is not a number, and a path that is not a row, are not the range's business" <| fun _ ->
      let book : LimitsBook = Map.ofList [ "gravity", limits 0.0 30.0 0.1 ]
      let at (path: SageFs.Features.Tweak.TweakAddress.PathStep list) : SageFs.Features.Tweak.TweakAddress.TweakAddress =
        { ModulePath = [ "Game" ]; BindingName = "gravity"; Path = path }
      LimitsBook.checkWrite book (at []) "gravity * 2.0" |> Expect.equal "a formula" (Ok())
      LimitsBook.checkWrite book (at []) "\"high\"" |> Expect.equal "a string" (Ok())
      LimitsBook.checkWrite book (at [ SageFs.Features.Tweak.TweakAddress.PathStep.ListItem 0 ]) "99.0" |> Expect.equal "a list item is no row" (Ok())
  ]
