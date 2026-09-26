module SageFs.Tests.RestartScopeAttributionTests

/// WHY — this is the seam that decides whether the type-migration work is
/// reachable AT ALL. Five modules (granular restart, holder, derived
/// migration, rewrite side conditions, translation validation) are built and
/// tested, and until this file exists none of them can narrow anything: every
/// `TypeChanged` becomes `TypeShapeChanged(name, RestartScope.Everything)`, so
/// a type change restarts the whole app exactly as it did before.
////
/// Proven in the live REPL on the real function, not just asserted here:
////
///   ReloadChange.restartReason (TypeChanged "Order")
///     = TypeShapeChanged ("Order", Everything)
////
/// even though `RestartScope.infer` takes a type NAME and would scope it. The
/// name is right there in the change and is thrown away.
////
/// These tests are the acceptance gate for that gap closing: if the planner
/// cannot attribute, it MUST stay at Everything. The safety direction matters
/// more than the win — a wrongly-narrowed restart leaves a half-restarted app
/// holding a value laid out by the old type.

open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.ReloadOutcome
open SageFs.Features.ReloadPlanning

let private orderUnits =
  [ { Name = "order-store"; DeclaresType = "Order" }
    { Name = "cart"; DeclaresType = "Cart" } ]

[<Tests>]
let restartScopeAttributionTests =
  testList "type-change restart scope attribution" [

    testCase "WHY — a type an owner declares narrows to that owner, instead of restarting everything" <| fun _ ->
      let reason = ReloadChange.restartReasonAttributed orderUnits (ReloadChange.TypeChanged "Order")
      reason
      |> Expect.equal "scoped to the unit that declares it" (RestartReason.TypeShapeChanged("Order", RestartScope.Scoped "order-store"))

    testCase "WHY — a type NO unit declares stays Everything, because attribution is never guessed" <| fun _ ->
      let reason = ReloadChange.restartReasonAttributed orderUnits (ReloadChange.TypeChanged "Todo")
      reason
      |> Expect.equal "unattributable is the safe direction" (RestartReason.TypeShapeChanged("Todo", RestartScope.Everything))

    testCase "WHY — with no units registered, EVERYTHING is the answer: a default that narrowed would be a lie" <| fun _ ->
      let reason = ReloadChange.restartReasonAttributed [] (ReloadChange.TypeChanged "Order")
      reason
      |> Expect.equal "no attribution available" (RestartReason.TypeShapeChanged("Order", RestartScope.Everything))

    testCase "WHY — a unit that does not declare the type does not capture it" <| fun _ ->
      // The sharp case: 'order-store' exists, but it declares Order, not Todo.
      // A name-substring or first-match implementation would scope this wrongly.
      let reason = ReloadChange.restartReasonAttributed orderUnits (ReloadChange.TypeChanged "Todo")
      reason
      |> Expect.equal "presence of other units must not capture an unrelated type" (RestartReason.TypeShapeChanged("Todo", RestartScope.Everything))

    testCase "WHY — attribution changes NOTHING about a non-type change" <| fun _ ->
      // A mutable's remedy is its own decision; scoping must not leak into it.
      let withUnits = ReloadChange.restartReasonAttributed orderUnits (ReloadChange.MutableStateChanged "counter")
      let withoutUnits = ReloadChange.restartReason (ReloadChange.MutableStateChanged "counter")
      withUnits
      |> Expect.equal "attribution is irrelevant to a mutable" withoutUnits

    testCase "WHY — the unattributed translation is still correct on its own, so existing callers are unharmed" <| fun _ ->
      ReloadChange.restartReason (ReloadChange.TypeChanged "Order")
      |> Expect.equal "the old entry point still means Everything" (RestartReason.TypeShapeChanged("Order", RestartScope.Everything))

    testCase "WHY — every declared unit is reachable, not just the first" <| fun _ ->
      [ "Order"; "Cart" ]
      |> List.iter (fun t ->
        let r = ReloadChange.restartReasonAttributed orderUnits (ReloadChange.TypeChanged t)
        match r with
        | RestartReason.TypeShapeChanged(_, RestartScope.Scoped _) -> ()
        | _ -> failtestf "'%s' should be attributable" t)

    testCase "WHY — RestartScope.infer is total: no type name ever throws or produces an invalid scope" <| fun _ ->
      [ "Order"; "Cart"; "Todo"; ""; "order-store" ]
      |> List.iter (fun t ->
        let s = RestartScope.infer orderUnits t
        (s = RestartScope.Scoped "cart" || s = RestartScope.Everything || s = RestartScope.Scoped "order-store")
        |> Expect.isTrue (sprintf "infer returned a valid scope for '%s'" t))
  ]
