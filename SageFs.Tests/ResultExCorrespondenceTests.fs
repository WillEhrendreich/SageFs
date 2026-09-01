/// ## ResultEx Correspondence Tests
///
/// Validates that the F# ResultEx satisfies the same properties proved in
/// `formal-verification/lean/FVSquad/ResultEx.lean`. Each test maps 1-to-1
/// to a Lean theorem (functor/monad laws).
module ResultExCorrespondenceTests

open Expecto
open Expecto.Flip
open SageFs

// ── Test Fixtures ──────────────────────────────────────────────────────────

let ok42 : Result<int, string> = Ok 42
let errMsg : Result<int, string> = Error "fail"

// ── Group 1: resMap — Lean: resMap_id, resMap_comp, resMap_eq_bind ─────────

let mapTests =
  testList "resMap" [
    test "WHY — resMap_id — map id is identity (Lean: resMap_id)" {
      ResultEx.map id ok42
      |> Expect.equal "map id should be identity" ok42
    }

    test "WHY — resMap_comp — map (g∘f) = map g ∘ map f (Lean: resMap_comp)" {
      let f x = x + 1
      let g x = x * 2
      let direct = ResultEx.map (g << f) ok42
      let composed = ok42 |> ResultEx.map f |> ResultEx.map g
      direct |> Expect.equal "map composition should equal composed maps" composed
    }

    test "WHY — resMap_eq_bind — map f = bind (fun v -> Ok (f v)) (Lean: resMap_eq_bind)" {
      let f x = x + 10
      let viaMap = ResultEx.map f ok42
      let viaBind = ResultEx.bind (fun v -> Ok (f v)) ok42
      viaMap |> Expect.equal "map should equal bind with return" viaBind
    }
  ]

// ── Group 2: resBind — Lean: resBind_left_id, resBind_right_id, resBind_assoc ─

let bindTests =
  testList "resBind" [
    test "WHY — resBind_left_id — bind (Ok v) f = f v (Lean: resBind_left_id)" {
      let f x = Ok (x * 3)
      ResultEx.bind f ok42
      |> Expect.equal "bind on Ok should apply f" (f 42)
    }

    test "WHY — resBind_right_id — bind r (Ok) = r (Lean: resBind_right_id)" {
      ResultEx.bind (fun (v: int) -> Ok v) ok42
      |> Expect.equal "bind with Ok return should be identity" ok42
    }

    test "WHY — resBind_assoc — bind associativity (Lean: resBind_assoc)" {
      let f x = Ok (x + 1)
      let g x = Ok (x * 2)
      let left = ok42 |> ResultEx.bind f |> ResultEx.bind g
      let right = ResultEx.bind (fun x -> ResultEx.bind g (f x)) ok42
      left |> Expect.equal "bind should be associative" right
    }
  ]

// ── Group 3: ofOption/toOption — Lean: toOption_ofOption_id, ofOption_toOption_ok ─

let optionTests =
  testList "ofOption/toOption" [
    test "WHY — toOption_ofOption_id — toOption ∘ ofOption = id (Lean: toOption_ofOption_id)" {
      let original = Some "hello"
      original
      |> ResultEx.ofOption "err"
      |> ResultEx.toOption
      |> Expect.equal "toOption(ofOption(x)) should be x" original
    }

    test "WHY — ofOption_toOption_ok — ofOption(e, Some v) = Ok v (Lean: ofOption_toOption_ok)" {
      ResultEx.ofOption "err" (Some 42)
      |> Expect.equal "ofOption with Some should give Ok" (Ok 42)
    }
  ]

// ── Group 4: resSequence — Lean: resSequence_nil, resSequence_single_* ────

let sequenceTests =
  testList "resSequence" [
    test "WHY — resSequence_nil — sequence [] = Ok [] (Lean: resSequence_nil)" {
      ResultEx.sequence ([] : Result<int, string> list)
      |> Expect.equal "sequence [] should be Ok []" (Ok [])
    }

    test "WHY — resSequence_single_ok — sequence [Ok v] = Ok [v] (Lean: resSequence_single_ok)" {
      ResultEx.sequence [Ok 42]
      |> Expect.equal "sequence [Ok 42] should be Ok [42]" (Ok [42])
    }

    test "WHY — resSequence_single_error — sequence [Error e] = Error e (Lean: resSequence_single_error)" {
      ResultEx.sequence [Error "fail"]
      |> Expect.equal "sequence [Error] should be Error" (Error "fail")
    }

    test "WHY — resSequence_length — length of result list matches (Lean: resSequence_length)" {
      let result = ResultEx.sequence [Ok 1; Ok 2; Ok 3] : Result<int list, string>
      match result with
      | Ok xs -> xs.Length |> Expect.equal "length should be 3" 3
      | Error _ -> failwith "Expected Ok"
    }
  ]

// ── Group 5: resPartition — Lean: resPartition_length ──────────────────────

let partitionTests =
  testList "resPartition" [
    test "WHY — resPartition_length — sums to input length (Lean: resPartition_length)" {
      let input = [Ok 1; Error "a"; Ok 2; Error "b"; Ok 3]
      let oks, errs = ResultEx.partition input
      (oks.Length + errs.Length) |> Expect.equal "sum should equal input length" input.Length
    }
  ]

// ── Group 6: isOk/isError — Lean: isOk_iff, isOk_isError_complement ────────

let isOkTests =
  testList "isOk/isError" [
    test "WHY — isOk_iff — isOk r ↔ r is Ok (Lean: isOk_iff)" {
      ResultEx.isOk ok42 |> Expect.isTrue "isOk(Ok 42) should be true"
      ResultEx.isOk errMsg |> Expect.isFalse "isOk(Error) should be false"
    }

    test "WHY — isOk_isError_complement — isOk r ≠ isError r (Lean: isOk_isError_complement)" {
      [ok42; errMsg] |> List.iter (fun r ->
        (ResultEx.isOk r <> ResultEx.isError r)
        |> Expect.isTrue $"isOk and isError should be complementary for %A{r}")
    }
  ]

// ── Group 7: resZip — Lean: resZip_ok_ok, resZip_error_left ───────────────

let zipTests =
  testList "resZip" [
    test "WHY — resZip_ok_ok — zip (Ok a) (Ok b) = Ok (a, b) (Lean: resZip_ok_ok)" {
      ResultEx.zip (Ok 1 : Result<int, string>) (Ok 2)
      |> Expect.equal "zip of two Oks should be Ok tuple" (Ok (1, 2))
    }

    test "WHY — resZip_error_left — zip (Error e) r = Error e (Lean: resZip_error_left)" {
      ResultEx.zip (Error "left" : Result<int, string>) (Ok 2)
      |> Expect.equal "zip with left error should be left error" (Error "left")
    }
  ]

// ── All tests combined ──────────────────────────────────────────────────────

let resultExCorrespondenceTests =
  testList "ResultEx Correspondence (F# vs Lean)" [
    mapTests
    bindTests
    optionTests
    sequenceTests
    partitionTests
    isOkTests
    zipTests
  ]
