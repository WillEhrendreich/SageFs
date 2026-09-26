module SageFs.Tests.RestartSubjectDecisionTests

/// WHY — the scope was computed, put in the user-facing message, and then
/// DROPPED. `AppRunner.requireRestart` takes `ReloadChange`s, never
/// `RestartReason`s, so the actor never saw a `Scoped` scope and restarted the
/// whole worker anyway — while telling the user "the rest of the app keeps
/// running". A claim with no consequence.
///
/// The invariant, and it is the one that matters: a single `Everything` reason
/// forces a whole-worker restart, ALWAYS. Not a preference — one unscopable
/// reason means some value laid out by the old definition is still live, and a
/// partial restart would leave it in place. The safe direction is also the
/// correct one.

open System
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Core.Features.RestartSubjectDecision

let private scoped t = ReloadOutcome.RestartReason.TypeShapeChanged(t, RestartScope.Scoped "Shop.Orders")
let private every t = ReloadOutcome.RestartReason.TypeShapeChanged(t, RestartScope.Everything)

[<Tests>]
let restartSubjectDecisionTests =
  testList "which subject a restart must act on" [

    testCase "WHY — one scoped reason narrows to that unit, which is the whole point" <| fun _ ->
      ofReasons [ scoped "Order" ]
      |> Expect.equal "restart only the owning unit" (Decision.OneUnit "Shop.Orders")

    testCase "WHY — a narrowed decision reports that the worker is NOT restarting" <| fun _ ->
      ofReasons [ scoped "Order" ]
      |> restartsWholeWorker
      |> Expect.isFalse "the whole point is that the rest keeps running"

    testCase "WHY — ONE unscopable reason forces the whole worker, no matter what else is scoped" <| fun _ ->
      // The sharp case. A per-reason 'best effort' implementation would restart
      // Shop.Orders here and leave a live old-layout value somewhere else.
      ofReasons [ scoped "Order"; every "Todo" ]
      |> restartsWholeWorker
      |> Expect.isTrue "one Everything is a whole-worker restart"

    testCase "WHY — live mutable state is never scoped: it IS the running state" <| fun _ ->
      ofReasons [ scoped "Order"; ReloadOutcome.RestartReason.MutableModuleState "counter" ]
      |> restartsWholeWorker
      |> Expect.isTrue "a mutable is the app's own state, so a partial restart would not reset it"

    testCase "WHY — a value the app COPIED forces the whole worker, because the copy survives a partial restart" <| fun _ ->
      ofReasons [ ReloadOutcome.RestartReason.ValueCopiedByApp("config", "app") ]
      |> restartsWholeWorker
      |> Expect.isTrue "a copy the app kept cannot be reached by a scoped restart"

    testCase "WHY — two DIFFERENT units are reported honestly, never narrowed to the first" <| fun _ ->
      let d = ofReasons [ scoped "Order"; ReloadOutcome.RestartReason.TypeShapeChanged("Cart", RestartScope.Scoped "Shop.Cart") ]
      d
      |> Expect.equal "both units are named" (Decision.SeveralUnits [ "Shop.Orders"; "Shop.Cart" ])
      d
      |> restartsWholeWorker
      |> Expect.isTrue "SageFs rebuilds a project, not one registration"

    testCase "WHY — the EMPTY reason list is the safe answer, not a vacuous pass" <| fun _ ->
      ofReasons []
      |> restartsWholeWorker
      |> Expect.isTrue "no reason means no scoping decision was made"

    testCase "WHY — the subject handed to the restart policy is the narrowed one when, and only when, it is safe" <| fun _ ->
      ofReasons [ scoped "Order" ]
      |> toSubject
      |> Expect.equal "a unit subject" (SageFs.GranularRestart.RestartSubject.UnitScope "Shop.Orders")

      ofReasons [ scoped "Order"; every "Todo" ]
      |> toSubject
      |> Expect.equal "the worker when anything is unscopable" SageFs.GranularRestart.RestartSubject.Worker

    testCase "WHY — a narrowed decision SAYS it is narrowed, so a partial restart is visible" <| fun _ ->
      let d = ofReasons [ scoped "Order" ]
      let text = describe d
      (text.Contains "Shop.Orders") |> Expect.isTrue "it names the unit"
      (text.Contains "keeps running") |> Expect.isTrue "it states the consequence"

    testCase "WHY — a whole-worker decision names WHY, so the user is not left guessing" <| fun _ ->
      let text = describe (ofReasons [ every "Todo" ])
      (text.Contains "Todo") |> Expect.isTrue "it names the type that forced it"
  ]
