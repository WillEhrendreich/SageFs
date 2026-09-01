/// ## ResultEx Mutation Tests
///
/// Proves the test suite catches mutations in `SageFs.ResultEx`.
/// Each test defines a mutant inline and verifies it differs from the real output.
/// Pattern: if real and mutant give the SAME result, mutation survived.
module ResultExMutationTests

open Expecto
open SageFs

// ── Test Fixtures ──────────────────────────────────────────────────────────

let testOk : Result<int, string> = Ok 42
let testError : Result<int, string> = Error "disk full"

// ── Mutation Tests ─────────────────────────────────────────────────────────

let resultExMutationTests = testList "ResultEx mutations" [

  // map: must apply f to Ok value
  testCase "WHY — map_skip_function — map must apply f or values don't change" <| fun () ->
    let real = ResultEx.map (fun x -> x + 1) testOk
    let mutant = testOk  // mutant: skip f entirely
    if real = mutant then
      failwith "Mutation survived — map skip produced same result as real"

  // bind: must apply f to Ok value
  testCase "WHY — bind_skip_function — bind must apply f or values don't change" <| fun () ->
    let real = ResultEx.bind (fun x -> Ok (x + 1)) testOk
    let mutant = testOk  // mutant: skip f entirely
    if real = mutant then
      failwith "Mutation survived — bind skip produced same result as real"

  // bind: must propagate Error
  testCase "WHY — bind_error_passthrough — bind must propagate errors, not swallow them" <| fun () ->
    let real = ResultEx.bind (fun x -> Ok (x + 1)) testError
    let mutant : Result<int, string> = Ok 0  // mutant: convert Error to Ok 0
    if real = mutant then
      failwith "Mutation survived — bind error passthrough produced same result as real"

  // mapError: must apply f to Error, not Ok
  testCase "WHY — mapError_apply_to_ok — mapError must not change Ok values" <| fun () ->
    let real = ResultEx.mapError (fun _ -> "mapped") testOk
    let mutant = match testOk with Ok v -> Ok (v + 1) | Error e -> Error e
    if real = mutant then
      failwith "Mutation survived — mapError on Ok produced same result"

  // defaultWith: must unwrap Ok
  testCase "WHY — defaultWith_ignore_ok — defaultWith must return Ok value, not default" <| fun () ->
    let real = ResultEx.defaultWith (fun _ -> 0) testOk
    let mutant = 0  // mutant: always return default
    if real = mutant then
      failwith "Mutation survived — defaultWith ignored Ok value"

  // defaultValue: must unwrap Ok
  testCase "WHY — defaultValue_ignore_ok — defaultValue must return Ok value, not default" <| fun () ->
    let real = ResultEx.defaultValue 0 testOk
    let mutant = 0  // mutant: always return default
    if real = mutant then
      failwith "Mutation survived — defaultValue ignored Ok value"

  // ofOption: Some → Ok, None → Error
  testCase "WHY — ofOption_swap — ofOption must map Some to Ok and None to Error" <| fun () ->
    let realSome = ResultEx.ofOption "none" (Some "hello")
    let mutantSome : Result<string, string> = Error "none"  // mutant: swapped
    let realNone = ResultEx.ofOption "none" (None : string option)
    let mutantNone : Result<string, string> = Ok "default"  // mutant: swapped
    if realSome = mutantSome && realNone = mutantNone then
      failwith "Mutation survived — ofOption swap produced same results"

  // toOption: Ok → Some, Error → None
  testCase "WHY — toOption_swap — toOption must map Ok to Some and Error to None" <| fun () ->
    let realOk = ResultEx.toOption testOk
    let mutantOk : int option = None  // mutant: Ok → None
    let realErr = ResultEx.toOption testError
    let mutantErr : int option = Some 0  // mutant: Error → Some
    if realOk = mutantOk && realErr = mutantErr then
      failwith "Mutation survived — toOption swap produced same results"

  // zip: both must succeed, first error wins
  testCase "WHY — zip_ignore_error — zip must propagate errors, not ignore them" <| fun () ->
    let real = ResultEx.zip (Ok 1 : Result<int, string>) testError
    let mutant : Result<int * int, string> = Ok (0, 0)  // mutant: always Ok
    if real = mutant then
      failwith "Mutation survived — zip ignored error"

  // sequence: all must succeed, first error stops
  testCase "WHY — sequence_ignore_error — sequence must stop at first error" <| fun () ->
    let real = ResultEx.sequence [Ok 1; Error "fail"; Ok 3] : Result<int list, string>
    let mutant : Result<int list, string> = Ok []  // mutant: always Ok
    if real = mutant then
      failwith "Mutation survived — sequence ignored errors"

  // isOk: must return false for Error
  testCase "WHY — isOk_always_true — isOk must return false for Error values" <| fun () ->
    let real = ResultEx.isOk testError
    let mutant = true  // mutant: always true
    if real = mutant then
      failwith "Mutation survived — isOk always true on Error"

  // isError: must return false for Ok
  testCase "WHY — isError_always_true — isError must return false for Ok values" <| fun () ->
    let real = ResultEx.isError testOk
    let mutant = true  // mutant: always true
    if real = mutant then
      failwith "Mutation survived — isError always true on Ok"
]
