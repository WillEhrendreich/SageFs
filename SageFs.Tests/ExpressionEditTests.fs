/// Coverage for `ExpressionEdit`: replacing a whole expression, checked
/// against a hash so a save never silently overwrites something that
/// changed underneath it.
module SageFs.Tests.ExpressionEditTests

open Expecto
open Expecto.Flip
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.ExpressionEdit

let private addr name : TweakAddress = { ModulePath = [ "M" ]; BindingName = name; Path = [] }

[<Tests>]
let expressionEditTests =
  testList "ExpressionEdit" [

    testCase "replaces the expression and formats the single-line result" <| fun _ ->
      let source = "module M\nlet x = 1.0\n"
      let result = setExpression source (addr "x") None "gravity*2.0" |> Expect.wantOk "should succeed"
      result |> Expect.equal "the operator gets Fantomas's normal spacing" "module M\nlet x = gravity * 2.0\n"

    testCase "everything outside the replaced range stays byte for byte" <| fun _ ->
      let source = "module M\nlet a = 1\nlet x = 1.0\nlet b = 2\n"
      let result = setExpression source (addr "x") None "3.0 + 4.0" |> Expect.wantOk "should succeed"
      result |> Expect.equal "unrelated bindings untouched" "module M\nlet a = 1\nlet x = 3.0 + 4.0\nlet b = 2\n"

    testCase "a hash that still matches lets the edit through" <| fun _ ->
      let source = "module M\nlet x = 1.0\n"
      let resolved = resolve source (addr "x") |> Expect.wantOk "resolves"
      setExpression source (addr "x") (Some resolved.Hash) "2.0"
      |> Expect.equal "matched hash applies" (Ok "module M\nlet x = 2.0\n")

    testCase "a stale hash refuses the edit rather than guessing" <| fun _ ->
      let source = "module M\nlet x = 1.0\n"
      match setExpression source (addr "x") (Some "stale-hash-value") "2.0" with
      | Error(ExpressionEditError.HashMismatch(expected, actual, currentText)) ->
        expected |> Expect.equal "the caller's stale hash" "stale-hash-value"
        currentText |> Expect.equal "shows what's really there" "1.0"
        actual |> Expect.notEqual "the real current hash differs from the stale one" expected
      | other -> failtestf "expected HashMismatch, got %A" other

    testCase "text that doesn't parse as an expression is refused" <| fun _ ->
      let source = "module M\nlet x = 1.0\n"
      match setExpression source (addr "x") None "let ( broken" with
      | Error(ExpressionEditError.ParseFailed _) -> ()
      | other -> failtestf "expected ParseFailed, got %A" other

    testCase "an address that's gone is refused, not silently written somewhere else" <| fun _ ->
      let source = "module M\nlet x = 1.0\n"
      let gone : TweakAddress = { ModulePath = [ "M" ]; BindingName = "notThere"; Path = [] }
      match setExpression source gone None "2.0" with
      | Error(ExpressionEditError.Gone _) -> ()
      | other -> failtestf "expected Gone, got %A" other

    testProperty "PROPERTY, the result of a successful edit always parses" <|
      fun () ->
        let source = "module M\nlet x = 1.0\n"
        match setExpression source (addr "x") None "1.0 + 2.0 * 3.0" with
        | Ok newSource -> SageFs.Features.Tweak.TweakAddress.resolve newSource (addr "x") |> Result.isOk
        | Error _ -> false

    testProperty "PROPERTY, everything before and after the target binding is untouched" <|
      fun () ->
        let source = "module M\nlet a = 111\nlet x = 1.0\nlet b = 222\n"
        match setExpression source (addr "x") None "9.0" with
        | Ok newSource -> newSource.StartsWith "module M\nlet a = 111\nlet x = " && newSource.EndsWith "\nlet b = 222\n"
        | Error _ -> false
  ]
