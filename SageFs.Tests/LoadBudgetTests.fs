module SageFs.Tests.LoadBudgetTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Tests.LoadBudget

[<Tests>]
let tests =
  testList "LoadBudget — one deadline, load-aware waits" [

    testPropertyWithConfig FsCheckConfig.defaultConfig "WHY — the load factor is never below the declared wait and never above 8x" <|
      fun (NonNegativeInt load) (PositiveInt cores) ->
        let f = factorFor (float load) cores
        f >= 1.0 && f <= 8.0

    testCase "WHY — a quiet machine does not inflate a wait" <| fun () ->
      factorFor 0.1 8 |> Expect.equal "quiet" 1.0

    testCase "WHY — a loaded machine scales the wait, capped" <| fun () ->
      factorFor 64.0 8 |> Expect.equal "capped at 8x" 8.0

    testCase "WHY — an inner budget never exceeds its declared wait" <| fun () ->
      let declared = TestTimeouts.patience
      let budget = innerBudget 0.5 declared
      budget <= TimeSpan.FromTicks(int64 (float declared.Ticks * 8.0))
      |> Expect.isTrue "even at the load cap, a child does not invent time"
  ]
