/// What a lease decision looks like to an agent or to the guard hook, as JSON. The old text said
/// "you already hold 1/1 leases" to a caller whose sibling held the lease, so three agents spent
/// up to 35 minutes looking for a lease they did not have. The refusal now carries the holder, the
/// kind of work, when it was granted and when it lapses, the caller's place in line, and what to do.
/// Decision tokens the guard hook and `scripts/local-gate.fsx` already read (`granted`, `wait`, `refused`)
/// keep their meaning.
module SageFs.Tests.LeaseWireTests

open System
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.ExpensiveWorkLease

let private epoch = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

let private siblingOne = Holder.make "mcp:shared" "sub-agent-one" "/work/one"
let private siblingTwo = Holder.make "mcp:shared" "sub-agent-two" "/work/two"

let private parse (text: string) : JsonElement = (JsonDocument.Parse text).RootElement

[<Tests>]
let tests =
  testList "McpLeaseWire.decisionJson" [

    testCase "WHY — a queued ask carries the holder, the kind, both times, the place in line and what to do" <| fun () ->
      let held, _ = request epoch MemoryPressure.Tight empty siblingOne Kind.FullBuild
      let now = epoch.AddSeconds 30.0
      let _, decision = request now MemoryPressure.Tight held siblingTwo Kind.FullBuild
      let json = parse (McpLeaseWire.decisionJson now siblingTwo Kind.FullBuild decision)
      json.GetProperty("decision").GetString() |> Expect.equal "the token the guard hook already reads" "wait"
      json.GetProperty("kind").GetString() |> Expect.equal "kind" "full_build"
      json.GetProperty("position").GetInt32() |> Expect.equal "place in line" 1
      (json.GetProperty("retryAfterSeconds").GetDouble() > 0.0) |> Expect.isTrue "a positive retry-after"
      let holder = json.GetProperty("heldBy").EnumerateArray() |> Seq.exactlyOne
      holder.GetProperty("agentName").GetString() |> Expect.equal "the holder's agent name" "sub-agent-one"
      holder.GetProperty("connection").GetString() |> Expect.equal "its connection" "mcp:shared"
      holder.GetProperty("workingDirectory").GetString() |> Expect.equal "its working directory" "/work/one"
      holder.GetProperty("kind").GetString() |> Expect.equal "what it is doing" "full_build"
      holder.GetProperty("grantedAt").GetDateTimeOffset() |> Expect.equal "when it was granted" epoch
      holder.GetProperty("expiresAt").GetDateTimeOffset() |> Expect.equal "when it lapses" (epoch + Kind.defaultTtl Kind.FullBuild)
      (holder.GetProperty("expiresInSeconds").GetInt32() > 0) |> Expect.isTrue "how long it has left"
      json.GetProperty("reason").GetString() |> Expect.stringContains "the same facts in words" "sub-agent-one"

    testCase "a holder's own second kind is refused with the lease id to release" <| fun () ->
      let held, first = request epoch MemoryPressure.Normal empty siblingOne Kind.FullBuild
      let id = match first with Decision.Granted(id, _) -> LeaseId.value id | other -> failtestf "expected Granted, got %A" other
      let _, decision = request epoch MemoryPressure.Normal held siblingOne Kind.TestSuiteRun
      let json = parse (McpLeaseWire.decisionJson epoch siblingOne Kind.TestSuiteRun decision)
      json.GetProperty("decision").GetString() |> Expect.equal "refused" "refused"
      json.GetProperty("heldLease").GetProperty("leaseId").GetString() |> Expect.equal "the lease it holds" id
      json.GetProperty("reason").GetString() |> Expect.stringContains "says to release it" "release_work_lease"

    testCase "an ask for a kind the holder already holds is granted again with grant already_held" <| fun () ->
      let held, first = request epoch MemoryPressure.Normal empty siblingOne Kind.FullBuild
      let id = match first with Decision.Granted(id, _) -> LeaseId.value id | other -> failtestf "expected Granted, got %A" other
      let _, again = request (epoch.AddMinutes 1.0) MemoryPressure.Normal held siblingOne Kind.FullBuild
      let json = parse (McpLeaseWire.decisionJson (epoch.AddMinutes 1.0) siblingOne Kind.FullBuild again)
      json.GetProperty("decision").GetString() |> Expect.equal "still a grant, so callers that proceed on granted keep working" "granted"
      json.GetProperty("grant").GetString() |> Expect.equal "and it says it is the one it already had" "already_held"
      json.GetProperty("leaseId").GetString() |> Expect.equal "the same id" id

    testCase "a fresh grant says it is new" <| fun () ->
      let _, decision = request epoch MemoryPressure.Normal empty siblingOne Kind.RunApp
      let json = parse (McpLeaseWire.decisionJson epoch siblingOne Kind.RunApp decision)
      json.GetProperty("grant").GetString() |> Expect.equal "new" "new"
      json.GetProperty("heldBy").GetProperty("agentName").GetString() |> Expect.equal "attributed from the first moment" "sub-agent-one"
  ]
