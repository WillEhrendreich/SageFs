/// What verifies a landing. Each test says what a handler's message has to look like, not what it says today, so an edit that
/// keeps the shape stays green and one that breaks it goes red.
module CohortTrunkFixture.Tests

open Expecto
open Expecto.Flip

[<Tests>]
let tests =
  testList "CohortTrunkFixture" [
    test "alice speaks as alice" {
      Alice.aliceMessage () |> Expect.stringStarts "alice's message starts with her name" "alice:"
    }
    test "bob speaks as bob" {
      Bob.bobMessage () |> Expect.stringStarts "bob's message starts with his name" "bob:"
    }
    test "the shape speaks as rude" {
      Rude.rudeMessage () |> Expect.stringStarts "the shape's message starts with its name" "rude:"
    }
  ]
