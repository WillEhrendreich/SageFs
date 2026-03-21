module SageFs.Tests.LiveTestingDecisionPropertyTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.LiveTesting

let mkDecision precision selected deferred =
  LiveTestingDecision.fromSelection
    (RerunCause.FileSaved "src/Test.fs")
    precision
    [ "Module.symbol" ]
    selected
    deferred
    "property test"

[<Tests>]
let tests =
  testList "Live testing decision properties" [
    testProperty "exact decisions never degrade to approximate trust" <| fun (NonEmptyString name) ->
      let decision = mkDecision SelectionPrecision.ExactDependencyMatch [| name |] [||]
      decision.Trust = FreshnessTrust.FreshExact

    testProperty "suppressed decisions never claim rerun selections" <| fun (NonEmptyString name) ->
      let decision = mkDecision SelectionPrecision.SuppressedByPolicy [||] [| name |]
      decision.Explanation.SelectedTests.Length = 0

    testProperty "no impacted tests never claim exact trust" <| fun () ->
      let decision = mkDecision SelectionPrecision.NoImpactedTests [||] [||]
      decision.Trust <> FreshnessTrust.FreshExact
  ]
