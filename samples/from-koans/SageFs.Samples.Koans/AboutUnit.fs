module SageFs.Samples.Koans.AboutUnit

open Expecto
open Expecto.Flip

let sendData (data: string) =
  ()

let sayHello () =
  "hello"

let tests = testList "about unit" [

  test "UnitIsUsedWhenThereIsNoReturnValue" {
    let r = sendData "data"
    r |> Expect.equal "sendData returns unit" ()
  }

  test "ParameterlessFunctionsTakeUnit" {
    let r = sayHello ()
    r |> Expect.equal "sayHello should return 'hello'" "hello"
  }

  test "UnitIsAType" {
    typeof<unit>.Name |> Expect.equal "unit type is called Unit" "Unit"
  }

]
