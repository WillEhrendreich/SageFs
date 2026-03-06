// ============================================================
//  🧘  About Arrays — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutArrays.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  Arrays in F# are the standard .NET arrays — mutable, fixed-size,
//  and fast for random access. Lists are immutable and linked.
//  Know when to use each.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Exploring arrays ──────────────────────────────────────────
// Arrays use [| ... |] syntax (note the pipe characters):

let fruits = [| "apple"; "pear"; "peach" |]

fruits.[0]     // → "apple"  (0-indexed)
fruits.[1]     // → "pear"
fruits.[2]     // → "peach"

// Arrays ARE the .NET System.Array — not the F# List type:
fruits.GetType()                           // → System.String[]
System.Array.CreateInstance(typeof<string>, 0).GetType()  // → same type

// Arrays are MUTABLE (unlike lists):
let mutable mutableFruits = [| "apple"; "pear" |]
mutableFruits.[1] <- "peach"    // mutate in place
mutableFruits                   // → [|"apple"; "peach"|]

// Array comprehensions work just like list comprehensions:
let evenNumbers =
  [| for i in 0..10 do
       if i % 2 = 0 then yield i |]
// → [|0; 2; 4; 6; 8; 10|]

// Array.map (same shape as List.map):
let cube x = x * x * x
let original = [| 0..5 |]
let cubed    = Array.map cube original
// original is UNCHANGED (Array.map returns a new array)

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about arrays" [

  test "CreatingArrays — index 0" {
    fruits.[0] |> Expect.equal "first fruit" __
  }

  test "CreatingArrays — index 1" {
    fruits.[1] |> Expect.equal "second fruit" __
  }

  test "CreatingArrays — index 2" {
    fruits.[2] |> Expect.equal "third fruit" __
  }

  test "ArraysAreDotNetArrays" {
    let dotNetType = System.Array.CreateInstance(typeof<string>, 0).GetType()
    (fruits.GetType()) |> Expect.equal "F# arrays are .NET System.Array" dotNetType
  }

  test "ArraysAreMutable" {
    let arr = [| "apple"; "pear" |]
    arr.[1] <- "peach"
    arr |> Expect.equal "mutation in place" __
  }

  test "YouCanCreateArraysWithComprehensions" {
    let nums = [| for i in 0..10 do if i % 2 = 0 then yield i |]
    nums |> Expect.equal "even numbers 0..10 as array" __
  }

  test "ArrayOperations — original unchanged" {
    original |> Expect.equal "Array.map doesn't mutate original" __
  }

  test "ArrayOperations — cubed result" {
    cubed |> Expect.equal "cubes of 0..5" __
  }

]

// ── List vs Array — when to use which ────────────────────────
// List:  functional, immutable, prepend O(1), index O(n)
//        best for: building/processing sequences functionally
//
// Array: mutable, random access O(1), cache-friendly
//        best for: performance-sensitive code, interop with .NET APIs
//
// In practice: use List by default; switch to Array when profiling shows it matters.
// ============================================================
