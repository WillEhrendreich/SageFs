/// ## ResultEx Mutation Tests
///
/// Proves the test suite catches mutations in `SageFs.ResultEx`.
/// Each case asserts EXACT equality against the correct value (not merely
/// inequality with one hand-picked wrong value) so a mutant that returns any
/// other wrong value is killed too.
module ResultExMutationTests

open Expecto
open Expecto.Flip
open SageFs

// ── Test Fixtures ──────────────────────────────────────────────────────────

let testOk : Result<int, string> = Ok 42
let testError : Result<int, string> = Error "disk full"

// ── Mutation Tests ─────────────────────────────────────────────────────────

let resultExMutationTests = testList "ResultEx mutations" [

  // map: must apply f to Ok value
  testCase "WHY — map_applies_function_to_ok — map must apply f to the Ok value" <| fun () ->
    ResultEx.map (fun x -> x + 1) testOk
    |> Expect.equal "map (+1) (Ok 42) must be Ok 43" (Ok 43)

  testCase "WHY — map_preserves_error — map must not touch the Error value" <| fun () ->
    ResultEx.map (fun x -> x + 1) testError
    |> Expect.equal "map (+1) on Error must pass the error through unchanged" (Error "disk full")

  // bind: must apply f to Ok value
  testCase "WHY — bind_applies_function_to_ok — bind must apply f to the Ok value" <| fun () ->
    ResultEx.bind (fun x -> Ok (x + 1)) testOk
    |> Expect.equal "bind (fun x -> Ok (x+1)) (Ok 42) must be Ok 43" (Ok 43)

  // bind: must propagate Error
  testCase "WHY — bind_error_passthrough — bind must propagate errors, not swallow them" <| fun () ->
    ResultEx.bind (fun x -> Ok (x + 1)) testError
    |> Expect.equal "bind on Error must return the same Error, not Ok" (Error "disk full")

  // mapError: must apply f to Error, not Ok
  testCase "WHY — mapError_preserves_ok — mapError must not change Ok values" <| fun () ->
    ResultEx.mapError (fun _ -> "mapped") testOk
    |> Expect.equal "mapError on Ok must leave the Ok value unchanged" (Ok 42)

  testCase "WHY — mapError_applies_function_to_error — mapError must apply f to the Error value" <| fun () ->
    ResultEx.mapError (fun e -> e + "!") testError
    |> Expect.equal "mapError (+ \"!\") on Error must transform the error" (Error "disk full!")

  // defaultWith: must unwrap Ok
  testCase "WHY — defaultWith_unwraps_ok — defaultWith must return the Ok value, not the default" <| fun () ->
    ResultEx.defaultWith (fun _ -> 0) testOk
    |> Expect.equal "defaultWith on Ok 42 must return 42" 42

  testCase "WHY — defaultWith_applies_function_on_error — defaultWith must apply f to the Error" <| fun () ->
    ResultEx.defaultWith (fun (e: string) -> e.Length) testError
    |> Expect.equal "defaultWith (fun e -> e.Length) on Error \"disk full\" must return 9" 9

  // defaultValue: must unwrap Ok
  testCase "WHY — defaultValue_unwraps_ok — defaultValue must return the Ok value, not the default" <| fun () ->
    ResultEx.defaultValue 0 testOk
    |> Expect.equal "defaultValue 0 on Ok 42 must return 42" 42

  testCase "WHY — defaultValue_returns_default_on_error — defaultValue must return the default on Error" <| fun () ->
    ResultEx.defaultValue 99 testError
    |> Expect.equal "defaultValue 99 on Error must return 99" 99

  // ofOption: Some → Ok, None → Error
  testCase "WHY — ofOption_some_becomes_ok — ofOption must map Some to Ok" <| fun () ->
    ResultEx.ofOption "none" (Some "hello")
    |> Expect.equal "ofOption on Some \"hello\" must be Ok \"hello\"" (Ok "hello")

  testCase "WHY — ofOption_none_becomes_error — ofOption must map None to the given Error" <| fun () ->
    ResultEx.ofOption "none" (None: string option)
    |> Expect.equal "ofOption on None must be Error \"none\"" (Error "none")

  // toOption: Ok → Some, Error → None
  testCase "WHY — toOption_ok_becomes_some — toOption must map Ok to Some" <| fun () ->
    ResultEx.toOption testOk
    |> Expect.equal "toOption on Ok 42 must be Some 42" (Some 42)

  testCase "WHY — toOption_error_becomes_none — toOption must map Error to None" <| fun () ->
    ResultEx.toOption testError
    |> Expect.equal "toOption on Error must be None" None

  // zip: both must succeed, first error wins
  testCase "WHY — zip_propagates_first_error — zip must propagate the second argument's error" <| fun () ->
    ResultEx.zip (Ok 1: Result<int, string>) testError
    |> Expect.equal "zip (Ok 1) (Error \"disk full\") must be Error \"disk full\"" (Error "disk full")

  testCase "WHY — zip_combines_both_oks — zip must pair up two Ok values" <| fun () ->
    ResultEx.zip (Ok 1: Result<int, string>) (Ok "a": Result<string, string>)
    |> Expect.equal "zip (Ok 1) (Ok \"a\") must be Ok (1, \"a\")" (Ok(1, "a"))

  // sequence: all must succeed, first error stops
  testCase "WHY — sequence_stops_at_first_error — sequence must return the first error" <| fun () ->
    (ResultEx.sequence [Ok 1; Error "fail"; Ok 3] : Result<int list, string>)
    |> Expect.equal "sequence with a middle Error must be Error \"fail\"" (Error "fail")

  testCase "WHY — sequence_collects_all_oks_in_order — sequence must preserve order" <| fun () ->
    (ResultEx.sequence [Ok 1; Ok 2; Ok 3] : Result<int list, string>)
    |> Expect.equal "sequence of all Oks must be Ok [1; 2; 3]" (Ok [1; 2; 3])

  // isOk: must return false for Error
  testCase "WHY — isOk_false_on_error — isOk must return false for Error values" <| fun () ->
    ResultEx.isOk testError
    |> Expect.isFalse "isOk on Error must be false"

  testCase "WHY — isOk_true_on_ok — isOk must return true for Ok values" <| fun () ->
    ResultEx.isOk testOk
    |> Expect.isTrue "isOk on Ok must be true"

  // isError: must return false for Ok
  testCase "WHY — isError_false_on_ok — isError must return false for Ok values" <| fun () ->
    ResultEx.isError testOk
    |> Expect.isFalse "isError on Ok must be false"

  testCase "WHY — isError_true_on_error — isError must return true for Error values" <| fun () ->
    ResultEx.isError testError
    |> Expect.isTrue "isError on Error must be true"
]
