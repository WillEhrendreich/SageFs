module SageFs.Tests.ExpectoModuleBindingFixture

open Expecto
open Expecto.Flip

type Marker = class end

let tests =
  testList "Expecto module binding fixture" [
    test "module let binding test is runnable" {
      true |> Expect.isTrue "fixture test should pass"
    }
  ]
