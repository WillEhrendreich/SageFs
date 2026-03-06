// ============================================================
//  🧘  About .NET Collections — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutDotNetCollections.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  F# works seamlessly with .NET's mutable collection types.
//  Dictionary<K,V>, List<T>, and Seq (IEnumerable<T>) are all
//  available alongside F#'s immutable list/array/map.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip
open System.Collections.Generic

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── .NET List<T> ─────────────────────────────────────────────
let fruits = new List<string>()
fruits.Add("apple")
fruits.Add("pear")
fruits.[0]    // → "apple"
fruits.[1]    // → "pear"

// ── .NET Dictionary<K,V> ─────────────────────────────────────
let addressBook = new Dictionary<string, string>()
addressBook.["Chris"]        <- "Ann Arbor"
addressBook.["SkillsMatter"] <- "London"
addressBook.["Chris"]           // → "Ann Arbor"
addressBook.["SkillsMatter"]    // → "London"

// ── Seq module works on any IEnumerable ──────────────────────
// NOTE: `seq` in F# is an alias for .NET's IEnumerable<T>
// You can pipe Dictionary, List<T>, arrays, lists — anything IEnumerable.

let verboseBook =
  addressBook
  |> Seq.map (fun kvp -> sprintf "Name: %s - City: %s" kvp.Key kvp.Value)
  |> Seq.toArray
// → [|"Name: Chris - City: Ann Arbor"; "Name: SkillsMatter - City: London"|]
// (order may vary — Dictionary doesn't guarantee order)

// Seq.skip and Seq.max:
let skipped = Seq.skip 2 [0..5] |> Seq.toList   // → [2; 3; 4; 5]

let values = new List<int>()
values.Add(11); values.Add(20); values.Add(4); values.Add(2); values.Add(3)
Seq.max values    // → 20

let names = [| "Harry"; "Lloyd"; "Nicholas"; "Mary"; "Joe" |]
Seq.maxBy (fun (s: string) -> s.Length) names    // → "Nicholas" (longest)

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about dot net collections" [

  test "CreatingDotNetLists — index 0" {
    fruits.[0] |> Expect.equal "first fruit" __
  }

  test "CreatingDotNetLists — index 1" {
    fruits.[1] |> Expect.equal "second fruit" __
  }

  test "CreatingDotNetDictionaries — Chris" {
    addressBook.["Chris"] |> Expect.equal "Chris lives in Ann Arbor" __
  }

  test "CreatingDotNetDictionaries — SkillsMatter" {
    addressBook.["SkillsMatter"] |> Expect.equal "SkillsMatter is in London" __
  }

  test "YouUseCombinatorsWithDotNetTypes — length" {
    // We can't test order of verboseBook (Dictionary is unordered).
    // But we CAN test that it has the right number of entries:
    verboseBook.Length |> Expect.equal "two entries in address book" __
  }

  test "SkippingElements" {
    let result = Seq.skip 2 [0..5] |> Seq.toList
    result |> Expect.equal "skip first 2 from [0..5]" __
  }

  test "FindingTheMax" {
    let vals = new List<int>()
    vals.Add(11); vals.Add(20); vals.Add(4); vals.Add(2); vals.Add(3)
    let result = Seq.max vals
    result |> Expect.equal "max of the list" __
  }

  test "FindingTheMaxByLength" {
    let result = Seq.maxBy (fun (s: string) -> s.Length) names
    result |> Expect.equal "longest name" __
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `addressBook.["Chris"]` — see "Ann Arbor"
// 2. Try `Seq.min`, `Seq.sort`, `Seq.distinct` on a list
// 3. Convert between types: `Seq.toList`, `Seq.toArray`, `Array.toSeq`
// 4. Try `Map.ofList [("a", 1); ("b", 2)]` — F#'s immutable map
// ============================================================
