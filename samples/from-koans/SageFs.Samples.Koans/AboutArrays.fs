module SageFs.Samples.Koans.AboutArrays

open Expecto
open Expecto.Flip

let fruits = [| "apple"; "pear"; "peach" |]

let cube x = x * x * x
let original = [| 0..5 |]
let cubed = Array.map cube original

let tests = testList "about arrays" [

  test "CreatingArrays — index 0" {
    fruits.[0] |> Expect.equal "first fruit" "apple"
  }

  test "CreatingArrays — index 1" {
    fruits.[1] |> Expect.equal "second fruit" "pear"
  }

  test "CreatingArrays — index 2" {
    fruits.[2] |> Expect.equal "third fruit" "peach"
  }

  test "ArraysAreDotNetArrays" {
    let dotNetType = System.Array.CreateInstance(typeof<string>, 0).GetType()
    (fruits.GetType()) |> Expect.equal "F# arrays are .NET System.Array" dotNetType
  }

  test "ArraysAreMutable" {
    let arr = [| "apple"; "pear" |]
    arr.[1] <- "peach"
    arr |> Expect.equal "mutation in place" [| "apple"; "peach" |]
  }

  test "YouCanCreateArraysWithComprehensions" {
    let nums = [| for i in 0..10 do if i % 2 = 0 then yield i |]
    nums |> Expect.equal "even numbers 0..10 as array" [| 0; 2; 4; 6; 8; 10 |]
  }

  test "ArrayOperations — original unchanged" {
    original |> Expect.equal "Array.map doesn't mutate original" [| 0; 1; 2; 3; 4; 5 |]
  }

  test "ArrayOperations — cubed result" {
    cubed |> Expect.equal "cubes of 0..5" [| 0; 1; 8; 27; 64; 125 |]
  }

]
