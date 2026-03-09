module SageFs.Tests.ResultExTests

open Expecto
open Expecto.Flip
open SageFs
open FsCheck

// ── Helpers ──

let ok v = Ok v
let err msg = Error (SageFsError.EvalFailed msg)
let errNotFound id = Error (SageFsError.SessionNotFound id)

// ── Tests ──

[<Tests>]
let resultExTests =
  testList "ResultEx" [

    testList "map" [
      test "maps Ok value" {
        ok 42 |> ResultEx.map (fun x -> x * 2)
        |> Expect.equal "doubled" (Ok 84)
      }
      test "preserves Error" {
        err "fail" |> ResultEx.map (fun x -> x * 2)
        |> Expect.equal "error" (err "fail")
      }
    ]

    testList "bind" [
      test "chains Ok to Ok" {
        ok 10 |> ResultEx.bind (fun x -> Ok (x + 5))
        |> Expect.equal "chained" (Ok 15)
      }
      test "chains Ok to Error" {
        ok 10 |> ResultEx.bind (fun _ -> err "nope")
        |> Expect.equal "failed" (err "nope")
      }
      test "short-circuits Error" {
        err "early" |> ResultEx.bind (fun x -> Ok (x + 5))
        |> Expect.equal "short-circuit" (err "early")
      }
    ]

    testList "mapError" [
      test "transforms Error" {
        err "x"
        |> ResultEx.mapError (fun _ -> SageFsError.DaemonNotRunning)
        |> Expect.equal "mapped" (Error SageFsError.DaemonNotRunning)
      }
      test "preserves Ok" {
        ok 42 |> ResultEx.mapError (fun _ -> SageFsError.DaemonNotRunning)
        |> Expect.equal "ok" (Ok 42)
      }
    ]

    testList "defaultWith" [
      test "returns Ok value" {
        ok 42 |> ResultEx.defaultWith (fun _ -> 0)
        |> Expect.equal "ok" 42
      }
      test "calls function on Error" {
        err "x" |> ResultEx.defaultWith (fun _ -> 99)
        |> Expect.equal "default" 99
      }
    ]

    testList "defaultValue" [
      test "returns Ok value" {
        ok "hello" |> ResultEx.defaultValue "default"
        |> Expect.equal "ok" "hello"
      }
      test "returns default on Error" {
        err "x" |> ResultEx.defaultValue "default"
        |> Expect.equal "default" "default"
      }
    ]

    testList "ofOption" [
      test "Some to Ok" {
        Some 42 |> ResultEx.ofOption (SageFsError.NoActiveSessions)
        |> Expect.equal "ok" (Ok 42)
      }
      test "None to Error" {
        None |> ResultEx.ofOption (SageFsError.NoActiveSessions)
        |> Expect.equal "error" (Error SageFsError.NoActiveSessions)
      }
    ]

    testList "toOption" [
      test "Ok to Some" {
        ok 42 |> ResultEx.toOption
        |> Expect.equal "some" (Some 42)
      }
      test "Error to None" {
        err "x" |> ResultEx.toOption
        |> Expect.equal "none" None
      }
    ]

    testList "zip" [
      test "both Ok" {
        ResultEx.zip (Ok 1) (Ok "a")
        |> Expect.equal "zipped" (Ok (1, "a"))
      }
      test "first Error" {
        ResultEx.zip (err "fail") (Ok "a")
        |> Expect.equal "first error" (err "fail")
      }
      test "second Error" {
        ResultEx.zip (Ok 1) (errNotFound "s1")
        |> Expect.equal "second error" (errNotFound "s1")
      }
    ]

    testList "apply" [
      test "applies Ok function to Ok value" {
        ResultEx.apply (Ok (fun x -> x + 1)) (Ok 41)
        |> Expect.equal "applied" (Ok 42)
      }
      test "Error function" {
        ResultEx.apply (err "f") (Ok 1)
        |> Expect.equal "error fn" (err "f")
      }
      test "Error value" {
        ResultEx.apply (Ok (fun x -> x + 1)) (err "v")
        |> Expect.equal "error val" (err "v")
      }
    ]

    testList "tap" [
      test "calls function on Ok, returns same result" {
        let mutable called = false
        let r = ok 42 |> ResultEx.tap (fun v -> called <- true)
        r |> Expect.equal "result" (Ok 42)
        called |> Expect.isTrue "called"
      }
      test "skips function on Error" {
        let mutable called = false
        let r = err "x" |> ResultEx.tap (fun _ -> called <- true)
        called |> Expect.isFalse "not called"
      }
    ]

    testList "tapError" [
      test "calls function on Error, returns same result" {
        let mutable called = false
        let r = err "x" |> ResultEx.tapError (fun _ -> called <- true)
        called |> Expect.isTrue "called"
      }
      test "skips function on Ok" {
        let mutable called = false
        let r = ok 42 |> ResultEx.tapError (fun _ -> called <- true)
        called |> Expect.isFalse "not called"
      }
    ]

    testList "sequence" [
      test "all Ok" {
        [ Ok 1; Ok 2; Ok 3 ]
        |> ResultEx.sequence
        |> Expect.equal "all ok" (Ok [ 1; 2; 3 ])
      }
      test "first Error stops" {
        [ Ok 1; err "stop"; Ok 3 ]
        |> ResultEx.sequence
        |> Expect.equal "stopped" (err "stop")
      }
      test "empty list" {
        ([] : Result<int, SageFsError> list)
        |> ResultEx.sequence
        |> Expect.equal "empty" (Ok [])
      }
    ]

    testList "traverse" [
      test "all succeed" {
        [ 1; 2; 3 ]
        |> ResultEx.traverse (fun x -> Ok (x * 10))
        |> Expect.equal "mapped" (Ok [ 10; 20; 30 ])
      }
      test "one fails" {
        [ 1; 2; 3 ]
        |> ResultEx.traverse (fun x ->
          match x with
          | 2 -> err "bad"
          | n -> Ok n)
        |> Expect.equal "failed" (err "bad")
      }
    ]

    testList "partition" [
      test "separates successes and failures" {
        let (oks, errs) =
          [ Ok 1; err "a"; Ok 3; err "b" ]
          |> ResultEx.partition
        oks |> Expect.equal "oks" [ 1; 3 ]
        errs |> Expect.hasLength "errs" 2
      }
      test "all Ok" {
        let (oks, errs) =
          [ Ok 1; Ok 2 ] |> ResultEx.partition
        oks |> Expect.equal "oks" [ 1; 2 ]
        errs |> Expect.hasLength "no errors" 0
      }
    ]

    testList "isOk and isError" [
      test "isOk on Ok" {
        ok 42 |> ResultEx.isOk |> Expect.isTrue "ok"
      }
      test "isOk on Error" {
        err "x" |> ResultEx.isOk |> Expect.isFalse "not ok"
      }
      test "isError on Error" {
        err "x" |> ResultEx.isError |> Expect.isTrue "error"
      }
    ]

    testList "describe" [
      test "describes Ok" {
        ok 42
        |> ResultEx.describe string
        |> Expect.stringContains "has value" "42"
      }
      test "describes Error" {
        err "boom"
        |> ResultEx.describe string
        |> Expect.stringContains "has error" "boom"
      }
    ]

    testList "property tests" [
      testProperty "map id = id" <| fun (x: int) ->
        let r: Result<int, SageFsError> = Ok x
        r |> ResultEx.map id = r

      testProperty "bind Ok = id" <| fun (x: int) ->
        let r: Result<int, SageFsError> = Ok x
        r |> ResultEx.bind Ok = r

      testProperty "sequence preserves length on all-Ok" <| fun (xs: int list) ->
        let results = xs |> List.map Ok
        match ResultEx.sequence results with
        | Ok ys -> ys.Length = xs.Length
        | Error _ -> false

      testProperty "partition preserves total count" <| fun (xs: Result<int, string> list) ->
        let mapped =
          xs |> List.map (Result.mapError (fun s -> SageFsError.EvalFailed s))
        let (oks, errs) = ResultEx.partition mapped
        oks.Length + errs.Length = xs.Length
    ]
  ]
