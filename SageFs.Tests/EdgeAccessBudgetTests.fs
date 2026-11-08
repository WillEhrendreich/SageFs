module SageFs.Tests.EdgeAccessBudgetTests

/// A ratchet on how many of the daemon's routes still take every caller. Slice 0 of
/// docs/plans/a-token-for-every-daemon-call.md moves one route, the member mint, to the operator credential; each
/// route moved later lowers the budget, and the budget never goes up. A route added with no declared access counts as
/// Open, so adding one without deciding fails here.

open Expecto
open Expecto.Flip
open SageFs
open SageFs.EdgeCredential
open SageFs.EndpointContracts

let openRoutes () : int =
  all |> List.filter (fun endpoint -> endpoint.access = EdgeAccess.Open) |> List.length

let operatorOnlyRoutes () : (HttpMethod * string) list =
  all
  |> List.filter (fun endpoint -> endpoint.access = EdgeAccess.OperatorOnly)
  |> List.map (fun endpoint -> endpoint.method, endpoint.path)

/// The table as data, so `--ratchets --tighten` can lower it.
let budgets : (string * int) list =
  [ "routes open to every caller", 63 ]

let _table =
  TestInfrastructure.Ratchet.table
    { Name = "edge access budgets"
      SourceFile = "SageFs.Tests/EdgeAccessBudgetTests.fs"
      Budgets = budgets
      Actual = fun () -> budgets |> List.map (fun (key, _) -> key, openRoutes ()) }

[<Tests>]
let tests =
  testList "Edge access" [
    testList "budgets" [
      test "WHY - no more routes take every caller than the budget says, so a route is only ever moved toward the operator" {
        for (key, budget) in budgets do
          (openRoutes (), budget)
          |> Expect.isLessThanOrEqual (sprintf "%s may not exceed %d: decide the new route's access, or move one" key budget)
      }

      test "WHY - a budget above the real count is stale, so room that was won back cannot be spent again" {
        for (_, budget) in budgets do
          openRoutes () |> Expect.equal "lower the budget with --ratchets --tighten" budget
      }
    ]
    |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant

    test "WHY - the member mint, the cohort seat and the local-data clear are the operator-only routes" {
      operatorOnlyRoutes ()
      |> Expect.equal
        "only the mint, the seat and the clear"
        [ POST, "/api/members/mint"; GET, "/api/cohort/seat"; POST, "/api/cohort/seat"; POST, "/api/local-data/clear" ]
    }
  ]
