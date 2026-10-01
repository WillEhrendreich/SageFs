module LemDrive.Tests.ChordTests

open Expecto
open Expecto.Flip
open LemDrive

let private playwright (text: string) : Result<string list, string> =
  Chord.parseSequence text |> Result.map (List.map Chord.toPlaywright)

[<Tests>]
let tests =
  testList "chords" [
    testCase "alt+enter reads as Playwright wants it" <| fun _ ->
      playwright "alt+enter" |> Expect.equal "one chord" (Ok [ "Alt+Enter" ])

    testCase "modifiers come out in one order whatever order they were written in" <| fun _ ->
      playwright "shift+ctrl+p" |> Expect.equal "ctrl before shift" (Ok [ "Control+Shift+p" ])

    testCase "a function key" <| fun _ ->
      playwright "f5" |> Expect.equal "F5" (Ok [ "F5" ])

    testCase "a plus key is the key, not an empty word" <| fun _ ->
      playwright "ctrl++" |> Expect.equal "Control plus plus" (Ok [ "Control++" ])

    testCase "a sequence of chords stays in order" <| fun _ ->
      playwright "ctrl+k ctrl+s" |> Expect.equal "two chords" (Ok [ "Control+k"; "Control+s" ])

    testCase "an unknown modifier is named" <| fun _ ->
      match playwright "hyper+p" with
      | Result.Error e -> e |> Expect.stringContains "names the word" "hyper"
      | Ok _ -> failtest "should not parse"

    testCase "an unknown key is named" <| fun _ ->
      match playwright "ctrl+nope" with
      | Result.Error e -> e |> Expect.stringContains "names the word" "nope"
      | Ok _ -> failtest "should not parse"

    testCase "an empty chord is refused" <| fun _ ->
      playwright "" |> Expect.isError "empty"

    testCase "f13 is not a key" <| fun _ ->
      playwright "f13" |> Expect.isError "no such function key"
  ]
