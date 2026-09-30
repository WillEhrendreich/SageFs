/// One way to serialize and deserialize, in one place. .NET 11's System.Text.Json writes an F#
/// union and .NET 10's throws, and sixteen separate options objects (six with the F# converter,
/// at differing union encodings, ten without) meant each call site got whichever behavior its
/// author happened to configure. `SageFs.Json` names the few real differences (naming, layout,
/// nulls, union encoding) as a closed set of profiles, and every profile carries the F# converter,
/// so the same value produces the same text on every runtime. These tests run in the net11 tier
/// and in the net10 tier, which is how "the same on every runtime" is checked.
module SageFs.Tests.JsonTests

open System
open System.Text.Json
open Expecto
open Expecto.Flip
open FsCheck
open SageFs

type Kind =
  | Plain
  | Fancy of level: int
  | Named of label: string * weight: float

type Sample =
  { Name: string
    Kind: Kind
    Maybe: int option
    Items: string list
    Lookup: Map<string, int> }

let private sample =
  { Name = "alpha"
    Kind = Fancy 3
    Maybe = None
    Items = [ "a"; "b" ]
    Lookup = Map.ofList [ "x", 1 ] }

[<Tests>]
let tests =
  testList "SageFs.Json" [

    testCase "WHY — a union, an option, a map and a list serialize instead of throwing, on every runtime, because every profile carries the F# converter" <| fun _ ->
      let text = Json.serialize Json.standard sample
      text |> Expect.stringContains "the union case is named" "Fancy"
      text |> Expect.stringContains "the map is written" "\"x\""

    testCase "WHY — an anonymous record with a union inside is fine too, which is exactly the shape that broke get_session_status on .NET 10" <| fun _ ->
      let text = Json.serialize Json.standard {| target = [ Plain; Fancy 1 ]; state = "Ready" |}
      text |> Expect.stringContains "both cases are written" "Fancy"

    testCase "WHY — camelCase profile writes camelCase keys, as-written keeps the F# names" <| fun _ ->
      Json.serialize Json.camelCase sample |> Expect.stringContains "camel key" "\"name\""
      Json.serialize Json.standard sample |> Expect.stringContains "as-written key" "\"Name\""

    testCase "WHY — the indented layout has newlines and the compact one has none" <| fun _ ->
      (Json.serialize Json.standard sample).Contains "\n" |> Expect.isFalse "compact is one line"
      (Json.serialize (Json.indented Json.standard) sample).Contains "\n" |> Expect.isTrue "indented spans lines"

    testCase "WHY — omit-nulls drops a None, write-nulls keeps it, so a client can rely on which one it gets" <| fun _ ->
      (Json.serialize Json.standard sample) |> Expect.stringContains "null kept" "\"Maybe\":null"
      (Json.serialize (Json.omitNulls Json.standard) sample).Contains "Maybe" |> Expect.isFalse "None dropped"

    testCase "WHY — the snake-case profile exists because the Jupyter wire needs it, and is a named profile rather than a hand-built options object" <| fun _ ->
      Json.serialize Json.snakeCase {| someKey = 1 |} |> Expect.stringContains "snake key" "some_key"

    testCase "WHY — the worker wire keeps its adjacent-tag union encoding, because daemon and worker must agree byte for byte" <| fun _ ->
      let text = Json.serialize Json.workerWire (Fancy 3)
      text |> Expect.stringContains "tag name" "\"type\""
      text |> Expect.stringContains "fields name" "\"value\""

    testCase "WHY — deserialize returns an Error that says why, never throws, because a bad payload is an expected input" <| fun _ ->
      match Json.deserialize<Sample> Json.standard "{ not json" with
      | Error reason -> reason |> Expect.isNotEmpty "the reason is carried"
      | Ok value -> failtestf "expected an error, got %A" value

    testCase "WHY — a value survives a round trip, union and option included" <| fun _ ->
      let text = Json.serialize Json.standard sample
      match Json.deserialize<Sample> Json.standard text with
      | Ok back -> back |> Expect.equal "same value" sample
      | Error reason -> failtestf "round trip failed: %s" reason

    testProperty "WHY — every union case round-trips through every profile, so a profile can never lose a case" <| fun (level: int) (label: NonEmptyString) (weight: NormalFloat) ->
      let cases = [ Plain; Fancy level; Named (label.Get, weight.Get) ]
      [ Json.standard; Json.camelCase; Json.indented Json.standard; Json.omitNulls Json.standard; Json.workerWire ]
      |> List.forall (fun profile ->
        cases
        |> List.forall (fun case ->
          match Json.deserialize<Kind> profile (Json.serialize profile case) with
          | Ok back -> back = case
          | Error _ -> false))

    testCase "WHY — the same profile gives the same text every time it is asked for, so two call sites never disagree" <| fun _ ->
      Json.serialize Json.standard sample |> Expect.equal "stable" (Json.serialize Json.standard sample)
  ]

/// The exact text each profile writes, pinned. This test runs in the net11 tier and in the net10
/// tier; if either runtime wrote a byte differently, one of the two tiers goes red.
[<Tests>]
let goldenTests =
  testList "SageFs.Json golden text" [

    testCase "WHY — the standard profile's text for a record with a union, an option, a list and a map is fixed" <| fun _ ->
      Json.serialize Json.standard sample
      |> Expect.equal "standard" """{"Name":"alpha","Kind":{"Case":"Fancy","Fields":[3]},"Maybe":null,"Items":["a","b"],"Lookup":{"x":1}}"""

    testCase "WHY — camelCase changes the keys and nothing else" <| fun _ ->
      Json.serialize Json.camelCase sample
      |> Expect.equal "camel" """{"name":"alpha","kind":{"Case":"Fancy","Fields":[3]},"maybe":null,"items":["a","b"],"lookup":{"x":1}}"""

    testCase "WHY — omit-nulls drops exactly the None" <| fun _ ->
      Json.serialize (Json.omitNulls Json.standard) sample
      |> Expect.equal "omit" """{"Name":"alpha","Kind":{"Case":"Fancy","Fields":[3]},"Items":["a","b"],"Lookup":{"x":1}}"""

    testCase "WHY — the worker wire's adjacent-tag encoding is fixed, because the daemon and the worker must agree byte for byte" <| fun _ ->
      Json.serialize Json.workerWire sample
      |> Expect.equal "worker" """{"name":"alpha","kind":{"type":"Fancy","value":[3]},"maybe":null,"items":["a","b"],"lookup":{"x":1}}"""

    testCase "WHY — a fieldless case and a multi-field case have fixed shapes" <| fun _ ->
      Json.serialize Json.standard Plain |> Expect.equal "fieldless" """{"Case":"Plain"}"""
      Json.serialize Json.standard (Named ("n", 1.5)) |> Expect.equal "multi-field" """{"Case":"Named","Fields":["n",1.5]}"""

    testCase "WHY — an anonymous record with a union inside has a fixed shape, which is the get_session_status payload that broke on .NET 10" <| fun _ ->
      Json.serialize Json.standard {| target = [ Plain; Fancy 1 ]; state = "Ready" |}
      |> Expect.equal "anonymous" """{"state":"Ready","target":[{"Case":"Plain"},{"Case":"Fancy","Fields":[1]}]}"""

    testCase "WHY — the snake-case profile writes snake_case keys" <| fun _ ->
      Json.serialize Json.snakeCase {| someKey = 1 |} |> Expect.equal "snake" """{"some_key":1}"""
  ]
