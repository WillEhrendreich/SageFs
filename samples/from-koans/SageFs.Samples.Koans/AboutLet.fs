module SageFs.Samples.Koans.AboutLet

open Expecto
open Expecto.Flip

let tests = testList "about let" [

  test "LetBindsANameToAValue" {
    let bound = 50
    bound |> Expect.equal "x should equal 50" 50
  }

  test "LetInfersTypesWherePossible — int" {
    let n = 50
    (n.GetType()) |> Expect.equal "n should be an int" typeof<int>
  }

  test "LetInfersTypes — string" {
    let s = "a string"
    (s.GetType()) |> Expect.equal "s should be a string" typeof<string>
  }

  test "YouCanMakeTypesExplicit — int" {
    let (explicit: int) = 42
    (explicit.GetType()) |> Expect.equal "should be typeof<int>" typeof<int>
  }

  test "YouCanMakeTypesExplicit — string" {
    let (explicit: string) = "forty two"
    (explicit.GetType()) |> Expect.equal "should be typeof<string>" typeof<string>
  }

  test "FloatsAndIntsAreDifferentTypes" {
    let intVal = 20
    let floatVal = 20.0
    (intVal.GetType()) |> Expect.equal "should be int" typeof<int>
    (floatVal.GetType()) |> Expect.equal "should be float" typeof<float>
  }

  test "ModifyingMutableValues" {
    let mutable n = 100
    n <- 200
    n |> Expect.equal "n should be 200 after reassignment" 200
  }

  test "ShadowingAllowsReusingNames" {
    let n = 50
    let n = 100
    n |> Expect.equal "n should be the shadowed value 100" 100
  }

]
