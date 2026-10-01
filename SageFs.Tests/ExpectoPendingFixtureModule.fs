/// A module-level Expecto tree no runner registers (no [<Tests>]), found the way the live-testing
/// executor finds module bindings: by reading the getter that returns an Expecto Test. Two tests
/// that pass and one pending test. ExpectoPendingTests discovers and runs it through the real
/// Discover and RunTest path.
module SageFs.Tests.ExpectoPendingFixtureModule

open Expecto

type Marker = class end

let tests =
  testList "pending fixture" [
    test "first passes" { () }
    test "second passes" { () }
    ptest "pending is not a pass" { () }
  ]
