module SageFs.Tests.LiveTestingCycleRefusalTests

// `GET /api/live-testing/test-trace` and `POST /api/live-testing/mark-all-stale` work on the one cycle the daemon
// holds. Asked about a session that does not own it they answer 409, and the sentence told the caller to "read
// <the route it just refused> for per-session state". The remedy has to be something that answers: the owning
// session, and the query that asks for it.

open Expecto
open Expecto.Flip
open SageFs.Server.McpServer

let private occurrences (needle: string) (text: string) : int =
  text.Split(needle).Length - 1

let private refusedRoutes =
  [ "GET /api/live-testing/test-trace"
    "POST /api/live-testing/mark-all-stale" ]

let private owner = "aaaa1111"
let private asked = "bbbb2222"

[<Tests>]
let cycleOwnerRefusalTests = testList "a live-testing route refused for a session that does not own the cycle" [
  for route in refusedRoutes do
    testCase (sprintf "%s is named once, as what refused, and never as the remedy" route) <| fun _ ->
      cycleOwnerRefusal route owner asked
      |> occurrences route
      |> Expect.equal "the refused route appears as the subject only, so it is not offered back as the way out" 1

    testCase (sprintf "%s points at the owning session through the query that routes to it" route) <| fun _ ->
      let text = cycleOwnerRefusal route owner asked
      text |> Expect.stringContains "names the session that owns the cycle" owner
      text |> Expect.stringContains "names the session that was refused" asked
      text |> Expect.stringContains "gives the query that asks for the owner" (sprintf "session=%s" owner)
]
