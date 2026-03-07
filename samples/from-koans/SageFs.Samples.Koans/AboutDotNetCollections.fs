module SageFs.Samples.Koans.AboutDotNetCollections

open Expecto
open Expecto.Flip
open System.Collections.Generic

let fruits = List<string>()
do fruits.Add("apple")
do fruits.Add("pear")

let addressBook = Dictionary<string, string>()
do addressBook.["Chris"] <- "Ann Arbor"
do addressBook.["SkillsMatter"] <- "London"

let verboseBook =
  addressBook
  |> Seq.map (fun kvp -> sprintf "Name: %s - City: %s" kvp.Key kvp.Value)
  |> Seq.toArray

let names = [| "Harry"; "Lloyd"; "Nicholas"; "Mary"; "Joe" |]

let tests = testList "about dot net collections" [

  test "CreatingDotNetLists — index 0" {
    fruits.[0] |> Expect.equal "first fruit" "apple"
  }

  test "CreatingDotNetLists — index 1" {
    fruits.[1] |> Expect.equal "second fruit" "pear"
  }

  test "CreatingDotNetDictionaries — Chris" {
    addressBook.["Chris"] |> Expect.equal "Chris lives in Ann Arbor" "Ann Arbor"
  }

  test "CreatingDotNetDictionaries — SkillsMatter" {
    addressBook.["SkillsMatter"] |> Expect.equal "SkillsMatter is in London" "London"
  }

  test "YouUseCombinatorsWithDotNetTypes — length" {
    verboseBook.Length |> Expect.equal "two entries in address book" 2
  }

  test "SkippingElements" {
    let result = Seq.skip 2 [0..5] |> Seq.toList
    result |> Expect.equal "skip first 2 from [0..5]" [2; 3; 4; 5]
  }

  test "FindingTheMax" {
    let vals = List<int>()
    vals.Add(11); vals.Add(20); vals.Add(4); vals.Add(2); vals.Add(3)
    let result = Seq.max vals
    result |> Expect.equal "max of the list" 20
  }

  test "FindingTheMaxByLength" {
    let result = Seq.maxBy (fun (s: string) -> s.Length) names
    result |> Expect.equal "longest name" "Nicholas"
  }

]
