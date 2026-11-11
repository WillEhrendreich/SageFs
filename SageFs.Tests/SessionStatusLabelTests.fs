/// The session-status label the tool replies carry comes from SessionStatusPayload, not from a literal.
module SageFs.Tests.SessionStatusLabelTests

open Expecto
open Expecto.Flip
open SageFs

[<Tests>]
let statusLabelTests =
  testList "session status label" [
    testCase "WHY — stateLabelOf — the faulted and warming labels the tool replies carry are the payload's own, byte for byte" <| fun _ ->
      SessionStatusPayload.stateLabelOf SessionState.Faulted |> Expect.equal "Faulted keeps its wire text" "Faulted"
      SessionStatusPayload.stateLabelOf SessionState.WarmingUp |> Expect.equal "WarmingUp keeps its wire text" "WarmingUp"
      SessionStatusPayload.stateLabelOf SessionState.Uninitialized |> Expect.equal "an uninitialized session reads as warming up" "WarmingUp"
  ]
