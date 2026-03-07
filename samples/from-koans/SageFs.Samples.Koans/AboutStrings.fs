module SageFs.Samples.Koans.AboutStrings

open Expecto
open Expecto.Flip

let tests = testList "about strings" [

  test "StringValue" {
    let s = "hello"
    s |> Expect.equal "should be hello" "hello"
  }

  test "StringConcatenation" {
    let s = "hello " + "world"
    s |> Expect.equal "concatenation with +" "hello world"
  }

  test "FormattingWithSprintf — int" {
    let s = sprintf "F# turns it to %d!" 11
    s |> Expect.equal "sprintf with %d" "F# turns it to 11!"
  }

  test "FormattingWithSprintf — string" {
    let s = sprintf "hello %s" "world"
    s |> Expect.equal "sprintf with %s" "hello world"
  }

  test "FormattingAnythingWithPercA" {
    let s = sprintf "Formatting other types is as easy as: %A" (1, 2, 3)
    s |> Expect.equal "sprintf with %A formats any type" "Formatting other types is as easy as: (1, 2, 3)"
  }

  test "StringInterpolation" {
    let lang = "F#"
    let s = $"Hello, {lang}!"
    s |> Expect.equal "interpolated string" "Hello, F#!"
  }

  test "ExtractFirstChar" {
    let s = "hello world"
    s.[0] |> Expect.equal "first char of 'hello world'" 'h'
  }

  test "ExtractFifthChar" {
    let s = "hello world"
    s.[4] |> Expect.equal "fifth char (index 4) of 'hello world'" 'o'
  }

  test "ApplyWhatYouLearned" {
    let getFunFacts x =
      sprintf "%d doubled is %d, and %d tripled is %d!" x (x*2) x (x*3)

    (getFunFacts 3) |> Expect.equal "fun facts about 3" "3 doubled is 6, and 3 tripled is 9!"
    (getFunFacts 6) |> Expect.equal "fun facts about 6" "6 doubled is 12, and 6 tripled is 18!"
  }

]
