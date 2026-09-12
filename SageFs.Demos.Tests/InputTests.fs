/// Tests for `Input.plan` (demo-gif-plan.md §4.3, §5): a Click ends in a
/// button-up whose preceding motion ends exactly at the target's centre; a
/// Chord presses every key down in order (modifiers held) and releases in
/// reverse; Type clicks then delivers key taps for every planned char.
module SageFs.Demos.Tests.InputTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Domain

let private boundTo (maxExclusive: int) (v: int) : int = (abs v) % maxExclusive

let private mkRect (rawX: int) (rawY: int) (rawW: int) (rawH: int) : ScreenRect =
  { X = boundTo 1000 rawX
    Y = boundTo 1000 rawY
    W = 1 + boundTo 400 rawW
    H = 1 + boundTo 400 rawH }

let private centreOf (rect: ScreenRect) : Point =
  { X = rect.X + rect.W / 2
    Y = rect.Y + rect.H / 2 }

[<Tests>]
let tests =
  testList "Input" [

    testProperty "a Click ends in a button-up, whose preceding motion ends at the rect centre"
    <| fun (rawX: int) (rawY: int) (rawW: int) (rawH: int) ->
      let target = mkRect rawX rawY rawW rawH
      let requests = SageFs.Demos.Input.plan (Action.Click(Target.WindowCenter ActorId.Dashboard)) target
      let last = List.last requests
      let motions = requests |> List.choose (function | X11Request.FakeMotion(x, y) -> Some { X = x; Y = y } | _ -> None)
      last = X11Request.FakeButton(Button.Left, Pressed.Up)
      && (List.last motions) = centreOf target

    testCase "a Click's button-down immediately precedes its button-up" <| fun _ ->
      let target = { X = 100; Y = 100; W = 40; H = 20 }
      let requests = SageFs.Demos.Input.plan (Action.Click(Target.WindowCenter ActorId.Dashboard)) target
      let lastTwo = requests |> List.rev |> List.take 2 |> List.rev
      lastTwo
      |> Expect.equal
        "down immediately before up"
        [ X11Request.FakeButton(Button.Left, Pressed.Down); X11Request.FakeButton(Button.Left, Pressed.Up) ]

    testCase "a Chord presses every key down in order, then releases in reverse (§4.3)" <| fun _ ->
      // Lowercase 's' deliberately, not the `Key.S` alias (= `Key.Char 'S'`,
      // uppercase) — capitals implying a held Shift is a `Keymap.resolve`
      // rule for *typed text*; mixing it into a chord key's own identity
      // would silently add an extra Shift the chord didn't ask for.
      let target = { X = 0; Y = 0; W = 10; H = 10 }
      let requests = SageFs.Demos.Input.plan (Action.Chord [ Key.Ctrl; Key.Char 's' ]) target
      let pressed =
        requests
        |> List.choose (function
          | X11Request.FakeKey(kc, Pressed.Down) -> Some kc
          | _ -> None)
      let released =
        requests
        |> List.choose (function
          | X11Request.FakeKey(kc, Pressed.Up) -> Some kc
          | _ -> None)
      pressed.Length |> Expect.equal "two keys pressed down (Ctrl, then s)" 2
      pressed |> List.distinct |> List.length |> Expect.equal "the two keycodes are distinct" 2
      released |> Expect.equal "released in exactly the reverse order they were pressed" (List.rev pressed)
      // Every Up must come after every Down (the modifier is held for the
      // whole chord, not released early).
      let firstUpIndex = requests |> List.findIndex (function X11Request.FakeKey(_, Pressed.Up) -> true | _ -> false)
      let lastDownIndex =
        requests |> List.findIndexBack (function X11Request.FakeKey(_, Pressed.Down) -> true | _ -> false)
      (firstUpIndex, lastDownIndex) |> Expect.isGreaterThan "no release starts before every key is pressed"

    testCase "Chord produces no mouse motion or button events" <| fun _ ->
      let target = { X = 0; Y = 0; W = 10; H = 10 }
      let requests = SageFs.Demos.Input.plan (Action.Chord [ Key.Ctrl; Key.S ]) target
      requests
      |> List.forall (function
        | X11Request.FakeKey _ -> true
        | _ -> false)
      |> Expect.isTrue "a chord is keys only"

    testCase "Type clicks then taps a key pair (down/up) per planned character" <| fun _ ->
      let target = { X = 200; Y = 200; W = 20; H = 20 }
      let text = Text.mk "hi"
      let action = Action.Type(Target.WindowCenter ActorId.VsCode, text, CadenceSeed.ofId "seed")
      let requests = SageFs.Demos.Input.plan action target
      let keyEvents = requests |> List.filter (function X11Request.FakeKey _ -> true | _ -> false)
      // "hi" is two plain ASCII chars, each one keycode, each tapped
      // down-then-up: 4 key events.
      keyEvents.Length |> Expect.equal "2 chars * (down + up)" 4
      let hasButtonUp = requests |> List.exists (function X11Request.FakeButton(Button.Left, Pressed.Up) -> true | _ -> false)
      hasButtonUp |> Expect.isTrue "Type still clicks the target before typing"

    testCase "Setup and Await produce no fake input" <| fun _ ->
      let target = { X = 0; Y = 0; W = 10; H = 10 }
      SageFs.Demos.Input.plan (Action.Setup ClientCommand.SaveAll) target
      |> Expect.isEmpty "Setup is API-level, not captured"
      SageFs.Demos.Input.plan (Action.Await Signal.appOutputChanged) target
      |> Expect.isEmpty "Await only waits for a signal"

    testCase "the same Click on the same target plans identically every run (§1, §9)" <| fun _ ->
      let target = { X = 50; Y = 60; W = 30; H = 15 }
      let a = SageFs.Demos.Input.plan (Action.Click(Target.WindowCenter ActorId.Dashboard)) target
      let b = SageFs.Demos.Input.plan (Action.Click(Target.WindowCenter ActorId.Dashboard)) target
      a |> Expect.equal "deterministic plan for the same target" b
  ]
