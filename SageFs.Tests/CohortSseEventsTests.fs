module SageFs.Tests.CohortSseEventsTests

/// Round-trip coverage for item 15a's three cohort SSE wire rows
/// (`cohort_matrix` / `claim_changed` / `landing_changed`, SseWriter.fs).
/// Mirrors SseContractComplianceTests.fs's "JSON shape contracts" style —
/// format, parse the JSON back, assert VALUES (not just property presence).
/// Fully qualifies every `SageFs.Cohort`/`SageFs.MemberTable` reference:
/// `SageFs.Cohort.TestId` would otherwise collide with
/// `SageFs.Features.LiveTesting.TestId` if this file ever opens both.

open System
open System.Text.Json
open Expecto
open Expecto.Flip

// ── Scenario builder ──

let private jsonOpts =
  let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
  opts.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
  opts

let private clock = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private noEntropy : byte[] = [||]
let private alice = SageFs.MemberTable.MemberId.Minted "alice"

let private applyOk state cmd =
  match SageFs.Cohort.decide clock noEntropy state cmd with
  | Ok(s, _, _) -> s
  | Error e -> failwithf "unexpected cohort decide error: %A" e

let private joinAndClaim () =
  SageFs.Cohort.CohortState.empty ()
  |> fun s -> applyOk s (SageFs.Cohort.CohortCommand.Join(alice, SageFs.Cohort.JoinableRole.Implementer, Some "sess-1"))
  |> fun s -> applyOk s (SageFs.Cohort.CohortCommand.AcquireClaim(alice, SageFs.Cohort.ClaimScope.File "src/Foo.fs", "testing"))

let private getProp (name: string) (el: JsonElement) = el.GetProperty(name)

// ── Tests ──

[<Tests>]
let cohortSseEventsTests = testList "Cohort SSE events (item 15a)" [

  testCase "claim_changed round-trips claimId/scope/holder/fence/kind" <| fun () ->
    let state = joinAndClaim ()
    let claimId, claim = state.Claims |> Map.toList |> List.exactlyOne
    let (SageFs.Cohort.ClaimId expectedClaimId) = claimId
    let payload =
      SageFs.SseWriter.formatClaimChangedEvent jsonOpts "acquired" claim
      |> fun sse ->
        sse.Split('\n')
        |> Array.choose (fun l -> if l.StartsWith("data: ") then Some (l.Substring 6) else None)
        |> String.concat "\n"
    use doc = JsonDocument.Parse(payload)
    let root = doc.RootElement
    root |> getProp "claimId" |> fun p -> p.GetString()
    |> Expect.equal "claimId should match the acquired claim's id" expectedClaimId
    let scope = root |> getProp "scope"
    scope |> getProp "kind" |> fun p -> p.GetString() |> Expect.equal "scope kind should be file" "file"
    scope |> getProp "path" |> fun p -> p.GetString() |> Expect.equal "scope path should be the claimed file" "src/Foo.fs"
    root |> getProp "holder" |> fun p -> p.GetString()
    |> Expect.equal "holder should be the acquiring member, displayed verbatim (Minted)" "alice"
    root |> getProp "fence" |> fun p -> p.GetInt64()
    |> Expect.equal "fence should be the claim's current fence" (int64 claim.Fence)
    root |> getProp "kind" |> fun p -> p.GetString()
    |> Expect.equal "kind should be the event kind passed in" "acquired"

  testCase "claim_changed reports holder=null for a released claim" <| fun () ->
    let state0 = joinAndClaim ()
    let claimId, claim0 = state0.Claims |> Map.toList |> List.exactlyOne
    let state1 = applyOk state0 (SageFs.Cohort.CohortCommand.ReleaseClaim(alice, claimId, claim0.Fence))
    let released = state1.Claims |> Map.find claimId
    let payload =
      SageFs.SseWriter.formatClaimChangedEvent jsonOpts "released" released
      |> fun sse -> sse.Split('\n') |> Array.choose (fun l -> if l.StartsWith("data: ") then Some (l.Substring 6) else None) |> String.concat "\n"
    use doc = JsonDocument.Parse(payload)
    doc.RootElement.GetProperty("holder").ValueKind
    |> Expect.equal "released claim should carry a null holder, not a stale one" JsonValueKind.Null

  testCase "landing_changed round-trips a Blocked landing's typed blocker + next action" <| fun () ->
    let state0 = joinAndClaim ()
    let claimId, claim = state0.Claims |> Map.toList |> List.exactlyOne
    let state1 = applyOk state0 (SageFs.Cohort.CohortCommand.RequestLanding(alice, [ claimId, claim.Fence ], [ "abc123" ], "land it"))
    let landingId = state1.Landings |> Map.toList |> List.exactlyOne |> fst
    let (SageFs.Cohort.LandingId expectedLandingId) = landingId
    let state2 = applyOk state1 (SageFs.Cohort.CohortCommand.RebaseCompleted(landingId, Ok "def456"))
    let state3 = applyOk state2 (SageFs.Cohort.CohortCommand.AffectedComputed(landingId, [ SageFs.Cohort.TestId "t1" ]))
    let state4 = applyOk state3 (SageFs.Cohort.CohortCommand.TestsCompleted(landingId, [ SageFs.Cohort.TestId "t1" ]))
    let landing = state4.Landings |> Map.find landingId
    let payload =
      SageFs.SseWriter.formatLandingChangedEvent jsonOpts landing
      |> fun sse -> sse.Split('\n') |> Array.choose (fun l -> if l.StartsWith("data: ") then Some (l.Substring 6) else None) |> String.concat "\n"
    use doc = JsonDocument.Parse(payload)
    let root = doc.RootElement
    root |> getProp "landingId" |> fun p -> p.GetString() |> Expect.equal "landingId should match" expectedLandingId
    root |> getProp "requester" |> fun p -> p.GetString() |> Expect.equal "requester should be the landing's requester" "alice"
    root |> getProp "state" |> fun p -> p.GetString() |> Expect.equal "state should be blocked" "blocked"
    let blocker = root |> getProp "blocker"
    blocker |> getProp "kind" |> fun p -> p.GetString() |> Expect.equal "blocker kind should be failing_tests" "failing_tests"
    blocker |> getProp "tests" |> fun p -> p.EnumerateArray() |> Seq.map (fun t -> t.GetString()) |> Seq.toList
    |> Expect.equal "blocker tests should list the failing test" [ "t1" ]
    let nextAction = root |> getProp "nextAction"
    nextAction |> getProp "kind" |> fun p -> p.GetString() |> Expect.equal "nextAction kind should be fix_tests" "fix_tests"
    nextAction |> getProp "tests" |> fun p -> p.EnumerateArray() |> Seq.map (fun t -> t.GetString()) |> Seq.toList
    |> Expect.equal "nextAction tests should list the failing test" [ "t1" ]

  testCase "landing_changed carries no blocker/nextAction for a non-blocked state" <| fun () ->
    let state0 = joinAndClaim ()
    let claimId, claim = state0.Claims |> Map.toList |> List.exactlyOne
    let state1 = applyOk state0 (SageFs.Cohort.CohortCommand.RequestLanding(alice, [ claimId, claim.Fence ], [ "abc123" ], "land it"))
    let landingId = state1.Landings |> Map.toList |> List.exactlyOne |> fst
    let landing = state1.Landings |> Map.find landingId
    let payload =
      SageFs.SseWriter.formatLandingChangedEvent jsonOpts landing
      |> fun sse -> sse.Split('\n') |> Array.choose (fun l -> if l.StartsWith("data: ") then Some (l.Substring 6) else None) |> String.concat "\n"
    use doc = JsonDocument.Parse(payload)
    doc.RootElement.GetProperty("state").GetString()
    |> Expect.equal "landing was queued then immediately rebased (queue was empty)" "rebasing"
    doc.RootElement.GetProperty("blocker").ValueKind
    |> Expect.equal "blocker should be null when not blocked" JsonValueKind.Null
    doc.RootElement.GetProperty("nextAction").ValueKind
    |> Expect.equal "nextAction should be null when not blocked" JsonValueKind.Null

  testCase "cohort_matrix round-trips members/claims/tests/rows" <| fun () ->
    let state = joinAndClaim ()
    let head : SageFs.Cohort.LedgerHead<SageFs.MemberTable.MemberId> = { Seq = 3L<SageFs.Measures.ledgerSeq>; State = state }
    let snapshot : SageFs.Cohort.SessionSnapshot<SageFs.MemberTable.MemberId> =
      { Member = Some alice
        SessionId = "sess-1"
        Generation = 2L
        PassingTests = [ SageFs.Cohort.TestId "t1" ]
        FailingTests = [ SageFs.Cohort.TestId "t2" ]
        StaleTests = [] }
    let frame = SageFs.Cohort.project head [| snapshot |]
    let payload =
      SageFs.SseWriter.formatCohortMatrixEvent jsonOpts frame
      |> fun sse -> sse.Split('\n') |> Array.choose (fun l -> if l.StartsWith("data: ") then Some (l.Substring 6) else None) |> String.concat "\n"
    use doc = JsonDocument.Parse(payload)
    let root = doc.RootElement
    root |> getProp "version" |> fun p -> p.GetInt64() |> Expect.equal "version should be the ledger seq the frame was projected from" 3L
    let members = root |> getProp "members" |> fun p -> p.EnumerateArray() |> Seq.toList
    members |> List.length |> Expect.equal "one member joined" 1
    let m0 = members.[0]
    m0 |> getProp "id" |> fun p -> p.GetString() |> Expect.equal "member id should be displayed verbatim" "alice"
    m0 |> getProp "role" |> fun p -> p.GetString() |> Expect.equal "role should be Implementer" "Implementer"
    m0 |> getProp "conductor" |> fun p -> p.GetBoolean() |> Expect.isTrue "the first joiner becomes conductor"
    let claims = root |> getProp "claims" |> fun p -> p.EnumerateArray() |> Seq.toList
    claims |> List.length |> Expect.equal "one claim acquired" 1
    let c0 = claims.[0]
    c0 |> getProp "scope" |> getProp "path" |> fun p -> p.GetString() |> Expect.equal "claim scope path" "src/Foo.fs"
    c0 |> getProp "holder" |> fun p -> p.GetString() |> Expect.equal "claim holder should be alice" "alice"
    let tests = root |> getProp "tests" |> fun p -> p.EnumerateArray() |> Seq.map (fun t -> t.GetString()) |> Seq.toList
    tests |> Expect.equal "tests should be the union of pass/fail/stale, sorted" [ "t1"; "t2" ]
    let rows = root |> getProp "rows" |> fun p -> p.EnumerateArray() |> Seq.toList
    rows |> List.length |> Expect.equal "one session row (alice's)" 1
    let r0 = rows.[0]
    r0 |> getProp "generation" |> fun p -> p.GetInt64() |> Expect.equal "row generation should match the snapshot" 2L
    let passBits = r0 |> getProp "pass" |> fun p -> p.EnumerateArray() |> Seq.map (fun b -> b.GetBoolean()) |> Seq.toList
    passBits |> Expect.equal "pass bitplane aligned with tests [t1;t2]" [ true; false ]
    let failBits = r0 |> getProp "fail" |> fun p -> p.EnumerateArray() |> Seq.map (fun b -> b.GetBoolean()) |> Seq.toList
    failBits |> Expect.equal "fail bitplane aligned with tests [t1;t2]" [ false; true ]

  testCase "cohort_matrix has no members/claims/rows for an empty cohort" <| fun () ->
    let head : SageFs.Cohort.LedgerHead<SageFs.MemberTable.MemberId> = { Seq = 0L<SageFs.Measures.ledgerSeq>; State = SageFs.Cohort.CohortState.empty () }
    let frame = SageFs.Cohort.project head [||]
    let payload =
      SageFs.SseWriter.formatCohortMatrixEvent jsonOpts frame
      |> fun sse -> sse.Split('\n') |> Array.choose (fun l -> if l.StartsWith("data: ") then Some (l.Substring 6) else None) |> String.concat "\n"
    use doc = JsonDocument.Parse(payload)
    doc.RootElement.GetProperty("members").GetArrayLength() |> Expect.equal "no members yet" 0
    doc.RootElement.GetProperty("claims").GetArrayLength() |> Expect.equal "no claims yet" 0
    doc.RootElement.GetProperty("rows").GetArrayLength() |> Expect.equal "no session rows yet" 0
]
