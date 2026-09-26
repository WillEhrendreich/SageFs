module SageFs.Tests.RestartAttributionRegistryTests

/// WHY — `RestartAttribution` is what makes granular restart REACHABLE. The
/// planner already parsed every watched file, so the module that owns each type
/// was in hand and unread: `KnownUnit` had no constructor in production at all,
/// and the live path at `SageFs.Host/WorkerMain.fs` called the unattributed
/// translation, so every type change restarted the whole app.
///
/// These tests pin the derivation from real parsed source, and pin the failure
/// direction as hard as the success: a type nobody declares must be
/// `Everything`, because a wrongly-narrowed restart leaves a half-restarted app
/// holding a value laid out by the old type.

open System
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.ReloadOutcome
open SageFs.Features.ReloadPlanning
open SageFs.Features.RestartAttribution

let private shopOrders = "namespace Shop\n\nmodule Orders =\n  type Order = { Id: int }\n"
let private shopCart = "namespace Shop\n\nmodule Cart =\n  type Cart = { Items: int list }\n"
let private noNamespace = "module Solo =\n  type Solo = { X: int }\n"

let private parsed src =
  match extractDecls src with
  | Ok d -> d
  | Error e -> failtestf "fixture should parse: %s" e

[<Tests>]
let restartAttributionRegistryTests =
  testList "unit attribution from the planner's own parse" [

    testCase "WHY — a type is attributed to the module that declares it, namespace-qualified" <| fun _ ->
      let units = knownUnitsOf [ parsed shopOrders; parsed shopCart ]
      units
      |> List.exists (fun u -> u.Name = "Shop.Orders" && u.DeclaresType = "Order")
      |> Expect.isTrue (sprintf "expected Shop.Orders to declare Order, got %A" units)

    testCase "WHY — two modules declaring two types produce two independent units" <| fun _ ->
      let units = knownUnitsOf [ parsed shopOrders; parsed shopCart ]
      units |> List.length |> Expect.equal "one unit per module that declares a type" 2

    testCase "WHY — a file with no namespace attributes to its module alone, not an empty name" <| fun _ ->
      let units = knownUnitsOf [ parsed noNamespace ]
      units
      |> List.iter (fun u -> (u.Name.Trim().Length > 0) |> Expect.isTrue "a unit must be named")
      units |> List.head |> fun u -> u.Name |> Expect.equal "the module path is the whole name" "Solo"

    testCase "WHY — the end to end: a declared type narrows, an undeclared one does not" <| fun _ ->
      let units = knownUnitsOf [ parsed shopOrders; parsed shopCart ]
      RestartScope.infer units "Order"
      |> Expect.equal "declared in Shop.Orders" (RestartScope.Scoped "Shop.Orders")
      RestartScope.infer units "Cart"
      |> Expect.equal "declared in Shop.Cart" (RestartScope.Scoped "Shop.Cart")
      RestartScope.infer units "Todo"
      |> Expect.equal "declared nowhere, so the safe direction" RestartScope.Everything

    testCase "WHY — an EMPTY registry yields Everything, because 'we found nothing' is not a licence to narrow" <| fun _ ->
      let units = knownUnitsOf []
      units |> List.length |> Expect.equal "nothing was declared" 0
      RestartScope.infer units "Order"
      |> Expect.equal "no attribution means the whole app" RestartScope.Everything

    testCase "WHY — a unit that exists does NOT capture a type it does not declare" <| fun _ ->
      // The sharp case. A name-substring or first-match implementation would
      // scope 'Todo' to 'Shop.Orders' here and leave a half-restarted app.
      let units = knownUnitsOf [ parsed shopOrders; parsed shopCart ]
      RestartScope.infer units "Todo"
      |> Expect.equal "presence of other units must not capture an unrelated type" RestartScope.Everything

    testCase "WHY — a file that fails to parse contributes nothing, and does not fail the registry" <| fun _ ->
      let good = knownUnitsOf [ parsed shopOrders ]
      let withBad = knownUnitsOfSources [ shopOrders; "this is not F# at all {{{" ]
      good
      |> Expect.equal "the good file still attributes" [ { Name = "Shop.Orders"; DeclaresType = "Order" } ]
      withBad
      |> List.exists (fun u -> u.DeclaresType = "Order")
      |> Expect.isTrue "a bad file must not erase the good attribution"

    testCase "WHY — the same type declared in two files yields BOTH units, never a silent pick" <| fun _ ->
      let dup = "namespace Shop\n\nmodule Orders2 =\n  type Order = { Id: int }\n"
      let units = knownUnitsOf [ parsed shopOrders; parsed dup ]
      units
      |> List.filter (fun u -> u.DeclaresType = "Order")
      |> List.length
      |> Expect.equal "both owners are reported; the caller decides" 2

    testCase "WHY — the translation the live path will call is exactly the attributed one" <| fun _ ->
      let units = knownUnitsOf [ parsed shopOrders ]
      restartReasonFor units (ReloadChange.TypeChanged "Order")
      |> Expect.equal "the reason carries the narrowed scope" (RestartReason.TypeShapeChanged("Order", RestartScope.Scoped "Shop.Orders"))
  ]
