module SageFs.Samples.Koans.AboutFunctions

open Expecto
open Expecto.Flip

let add x y =
  x + y

let caffeinate (text: string) =
  let suffix = "!!!"
  let exclaimed = text.Trim() + suffix
  let yelled = exclaimed.ToUpper()
  yelled

let sayItLikeAnAuctioneer (text: string) =
  text.Replace(" ", "")

let tests = testList "about functions" [

  test "CreatingFunctionsWithLet — first call" {
    (add 2 2) |> Expect.equal "add 2 2 should be 4" 4
  }

  test "CreatingFunctionsWithLet — second call" {
    (add 5 2) |> Expect.equal "add 5 2 should be 7" 7
  }

  test "NestingFunctions" {
    let quadruple x =
      let double x = x * 2
      double (double x)
    (quadruple 4) |> Expect.equal "quadruple 4 should be 16" 16
  }

  test "AddingTypeAnnotations" {
    let result = sayItLikeAnAuctioneer "going once going twice sold to the lady in red"
    result |> Expect.equal "spaces should be removed" "goingoncegoingtwicesoldtotheladyinred"
  }

  test "VariablesInParentScopeCanBeAccessed" {
    let result = caffeinate "hello there"
    result |> Expect.equal "should be yelled with exclamation" "HELLO THERE!!!"
  }

]
