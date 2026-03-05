module SageFs.Tests.ResultCeTests

open Expecto
open Expecto.Flip
open FsToolkit.ErrorHandling
open SageFs

[<Tests>]
let resultCeTests = testList "ResultCE" [

  test "result CE returns Ok for pure value" {
    let r = result { return 42 }
    r |> Expect.equal "should be Ok 42" (Ok 42)
  }

  test "result CE short-circuits on Error" {
    let r = result {
      let! _ = Error "boom"
      return 42
    }
    r |> Expect.equal "should be Error" (Error "boom")
  }

  test "result CE chains multiple let! bindings" {
    let step1 x = match x > 0 with true -> Ok x | false -> Error "non-positive"
    let step2 x = match x < 100 with true -> Ok (x * 2) | false -> Error "too large"
    let r = result {
      let! a = step1 42
      let! b = step2 a
      return b
    }
    r |> Expect.equal "should chain successfully" (Ok 84)
  }

  test "result CE with SageFsError type" {
    let validate (id: string) : Result<string, SageFsError> =
      result {
        do! match System.String.IsNullOrEmpty(id) with
            | true -> Error SageFsError.NoActiveSessions
            | false -> Ok ()
        return id
      }
    validate "sess-1"
    |> Expect.equal "valid id" (Ok "sess-1")
    validate ""
    |> Expect.equal "empty id" (Error SageFsError.NoActiveSessions)
  }

  test "result CE with do! for validation" {
    let validatePositive x =
      match x > 0 with true -> Ok () | false -> Error "non-positive"
    let validateEven x =
      match x % 2 = 0 with true -> Ok () | false -> Error "odd"
    let validate x = result {
      do! validatePositive x
      do! validateEven x
      return x
    }
    validate 4 |> Expect.equal "4 is valid" (Ok 4)
    validate (-1) |> Expect.equal "-1 fails positive" (Error "non-positive")
    validate 3 |> Expect.equal "3 fails even" (Error "odd")
  }

  testProperty "result CE preserves Ok identity" <| fun (x: int) ->
    let r = result { return x }
    r = Ok x

  testProperty "result CE Error short-circuits regardless of subsequent steps" <| fun (msg: string) ->
    let r = result {
      let! _ = Error msg
      let! _ = Ok 999
      return 42
    }
    r = Error msg

  testProperty "result CE let! maps Ok values through pipeline" <| fun (x: int) ->
    let r = result {
      let! a = Ok x
      let! b = Ok (a + 1)
      return b
    }
    r = Ok (x + 1)

  test "Result.map replaces match Ok/Error boilerplate" {
    // Before FsToolkit: match x with Ok v -> Ok (f v) | Error e -> Error e
    // After: Result.map f x  (or result { let! v = x; return f v })
    let loadAndTransform () =
      Ok 42 |> Result.map (fun v -> v * 2)
    loadAndTransform ()
    |> Expect.equal "should map" (Ok 84)
  }

  test "result CE with return! for delegation" {
    let inner () : Result<int, string> = Ok 42
    let outer () = result {
      return! inner ()
    }
    outer () |> Expect.equal "should delegate" (Ok 42)
  }
]
