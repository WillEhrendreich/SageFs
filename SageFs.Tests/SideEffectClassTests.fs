/// Coverage for `SideEffectClass`: `Live` only for literals, arithmetic,
/// comparisons, booleans, reads and the known-pure allow-list; everything
/// else, including anything this classifier doesn't recognize, is
/// `OnRelease`.
module SageFs.Tests.SideEffectClassTests

open Expecto
open Expecto.Flip
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.SideEffectClass

let private addr name : TweakAddress = { ModulePath = [ "M" ]; BindingName = name; Path = [] }

let private classifyExpr (code: string) =
  let source = sprintf "module M\nlet x = %s\n" code
  classifyAt source (addr "x") |> Expect.wantOk "should resolve"

let private isLive =
  function
  | SideEffectClass.Live -> true
  | SideEffectClass.OnRelease _ -> false

[<Tests>]
let sideEffectClassTests =
  testList "SideEffectClass" [

    testCase "a literal is Live" <| fun _ -> classifyExpr "1.0" |> isLive |> Expect.isTrue "a literal has no side effect"

    testCase "arithmetic on two literals is Live" <| fun _ ->
      classifyExpr "gravity * 2.0" |> isLive |> Expect.isTrue "arithmetic is pure"

    testCase "a comparison is Live" <| fun _ -> classifyExpr "x > 3" |> isLive |> Expect.isTrue "comparisons are pure"

    testCase "boolean operators are Live" <| fun _ -> classifyExpr "hardMode && not easyMode" |> isLive |> Expect.isTrue "booleans are pure"

    testCase "if/then/else over pure branches is Live" <| fun _ ->
      classifyExpr "if hardMode then 80 else 100" |> isLive |> Expect.isTrue "a pure conditional is pure"

    testCase "a tuple of literals is Live" <| fun _ -> classifyExpr "(1.0, 2.0)" |> isLive |> Expect.isTrue "tuple construction is pure"

    testCase "record construction over pure fields is Live" <| fun _ ->
      classifyExpr "{ JumpVelocity = gravity * 2.0; Gravity = 9.8 }" |> isLive |> Expect.isTrue "record construction is pure"

    testCase "a read of a plain binding is Live" <| fun _ -> classifyExpr "gravity" |> isLive |> Expect.isTrue "a read is pure"

    testCase "an allow-listed BCL call is Live" <| fun _ -> classifyExpr "Math.Sqrt 4.0" |> isLive |> Expect.isTrue "Math.Sqrt is on the allow-list"

    testCase "abs is Live" <| fun _ -> classifyExpr "abs -1.0" |> isLive |> Expect.isTrue "abs is on the allow-list"

    testCase "a call NOT on the allow-list is OnRelease" <| fun _ ->
      match classifyExpr "someFunction 1 2" with
      | SideEffectClass.OnRelease _ -> ()
      | live -> failtestf "expected OnRelease, got %A" live

    testCase "printfn is OnRelease" <| fun _ ->
      match classifyExpr "printfn \"hi\"" with
      | SideEffectClass.OnRelease _ -> ()
      | live -> failtestf "printing is a side effect, expected OnRelease, got %A" live

    testCase "an if/then/else whose branch calls an unknown function is OnRelease" <| fun _ ->
      match classifyExpr "if hardMode then someFunction 1 else 100" with
      | SideEffectClass.OnRelease _ -> ()
      | live -> failtestf "expected OnRelease, got %A" live

    testCase "a lambda is OnRelease (the classifier doesn't know it's pure)" <| fun _ ->
      match classifyExpr "(fun x -> x + 1)" with
      | SideEffectClass.OnRelease _ -> ()
      | live -> failtestf "expected OnRelease, got %A" live

    testCase "when unsure, the reason is carried, never a silent default" <| fun _ ->
      match classifyExpr "someFunction 1 2" with
      | SideEffectClass.OnRelease reason -> reason |> Expect.isNotEmpty "the reason must say why"
      | SideEffectClass.Live -> failtest "expected OnRelease"

    testCase "the allow-list is data that can be inspected" <| fun _ ->
      knownPureCalls |> Expect.contains "Math.Sqrt is listed" "Math.Sqrt"
      knownPureCalls |> Expect.contains "abs is listed" "abs"

    testProperty "PROPERTY, every literal is always Live" <|
      fun (n: int) -> classifyExpr (string n) |> isLive

    testProperty "PROPERTY, a call whose name isn't on the allow-list is never Live" <|
      fun () ->
        match classifyExpr "definitelyNotOnTheList 1 2 3" with
        | SideEffectClass.OnRelease _ -> true
        | SideEffectClass.Live -> false
  ]
