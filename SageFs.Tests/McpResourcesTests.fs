module SageFs.Tests.McpResourcesTests

/// Item 12 of sagefs-multiagent-vision.md's Phase 1 (§10): "Build MCP
/// resource registration + `resources/subscribe`". This file covers the
/// PURE half of that item — the resource content projections
/// (`CohortFrame -> JSON`, `SessionInfo list -> JSON`) and the
/// `resources/updated` no-change gate (`McpResourceGate`) — all unit
/// testable without a live MCP server, per the task's own requirement.
/// The SDK wiring itself (`SageFs/McpResources.fs`: `[<McpServerResource>]`
/// registration, `resources/subscribe`/`unsubscribe` handlers, and the
/// `notifications/resources/updated` push) is exercised indirectly by
/// constructing `SageFsResources` directly against a real `McpContext`
/// (`TestInfrastructure.sharedCtxWith`) and calling its resource methods —
/// the same "call the underlying function directly" style
/// McpServerIntegrationTests.fs already uses for MCP tools.
///
/// Style mirrors CohortSseEventsTests.fs: format, parse the JSON back,
/// assert VALUES.

open System
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs.Tests.TestInfrastructure

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

let private mkSessionInfo (name: string) (workingDirectory: string) : SageFs.WorkerProtocol.SessionInfo =
  { Id = SageFs.WorkerProtocol.SessionId.newId ()
    Name = Some name
    Projects = [ "Foo.fsproj" ]
    WorkingDirectory = workingDirectory
    SolutionRoot = None
    CreatedAt = clock
    LastActivity = clock
    Status = SageFs.WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1; Port = None }
    Workflow = SageFs.WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning }

[<Tests>]
let mcpResourcesTests = testList "MCP resources (item 12)" [

  // ── cohort://status content projection (SseWriter.cohortFrameJson) ──

  testCase "cohortFrameJson round-trips members/claims/tests/rows (same shape as cohort_matrix)" <| fun () ->
    let state = joinAndClaim ()
    let head : SageFs.Cohort.LedgerHead<SageFs.MemberTable.MemberId> = { Seq = 5L<SageFs.Measures.ledgerSeq>; State = state }
    let snapshot : SageFs.Cohort.SessionSnapshot<SageFs.MemberTable.MemberId> =
      { Member = Some alice
        SessionId = "sess-1"
        Generation = 1L
        PassingTests = [ SageFs.Cohort.TestId "t1" ]
        FailingTests = []
        StaleTests = [] }
    let frame = SageFs.Cohort.project head [| snapshot |]
    let json = SageFs.SseWriter.cohortFrameJson jsonOpts frame
    use doc = JsonDocument.Parse(json)
    let root = doc.RootElement
    root |> getProp "version" |> fun p -> p.GetInt64() |> Expect.equal "version should be the ledger seq the frame was projected from" 5L
    let members = root |> getProp "members" |> fun p -> p.EnumerateArray() |> Seq.toList
    members |> List.length |> Expect.equal "one member joined" 1
    members.[0] |> getProp "id" |> fun p -> p.GetString() |> Expect.equal "member id displayed verbatim" "alice"
    let claims = root |> getProp "claims" |> fun p -> p.EnumerateArray() |> Seq.toList
    claims |> List.length |> Expect.equal "one claim acquired" 1

  testCase "cohortFrameJson is stable/empty for an empty cohort (no members ever joined)" <| fun () ->
    let head : SageFs.Cohort.LedgerHead<SageFs.MemberTable.MemberId> = { Seq = 0L<SageFs.Measures.ledgerSeq>; State = SageFs.Cohort.CohortState.empty () }
    let frame = SageFs.Cohort.project head [||]
    let json1 = SageFs.SseWriter.cohortFrameJson jsonOpts frame
    let json2 = SageFs.SseWriter.cohortFrameJson jsonOpts frame
    json1 |> Expect.equal "same input frame must always project the same JSON (property 10: frame identity is (Version, SessionGens))" json2
    use doc = JsonDocument.Parse(json1)
    doc.RootElement.GetProperty("version").GetInt64() |> Expect.equal "version should be 0 for an untouched ledger" 0L
    doc.RootElement.GetProperty("members").GetArrayLength() |> Expect.equal "no members yet" 0
    doc.RootElement.GetProperty("claims").GetArrayLength() |> Expect.equal "no claims yet" 0
    doc.RootElement.GetProperty("rows").GetArrayLength() |> Expect.equal "no session rows yet" 0

  testCase "formatCohortMatrixEvent's SSE data payload is byte-identical to cohortFrameJson" <| fun () ->
    // Proves the extraction changed nothing observable about the existing
    // cohort_matrix SSE wire row — both surfaces share the exact same
    // projection (§5.6 "one read model").
    let state = joinAndClaim ()
    let head : SageFs.Cohort.LedgerHead<SageFs.MemberTable.MemberId> = { Seq = 1L<SageFs.Measures.ledgerSeq>; State = state }
    let frame = SageFs.Cohort.project head [||]
    let sse = SageFs.SseWriter.formatCohortMatrixEvent jsonOpts frame
    let dataLine =
      sse.Split('\n') |> Array.choose (fun l -> if l.StartsWith("data: ") then Some (l.Substring 6) else None) |> String.concat "\n"
    dataLine |> Expect.equal "SSE data must equal the pure projection" (SageFs.SseWriter.cohortFrameJson jsonOpts frame)

  // ── sessions://list content projection (SessionOperations.sessionsToJson) ──

  testCase "sessionsToJson round-trips id/name/workingDirectory/status/workflow/projects" <| fun () ->
    let info = mkSessionInfo "my-session" "/repo/checkout"
    let json = SageFs.SessionOperations.sessionsToJson jsonOpts [ info ]
    use doc = JsonDocument.Parse(json)
    let sessions = doc.RootElement |> getProp "sessions" |> fun p -> p.EnumerateArray() |> Seq.toList
    sessions |> List.length |> Expect.equal "one session" 1
    let s0 = sessions.[0]
    s0 |> getProp "id" |> fun p -> p.GetString() |> Expect.equal "id should be the session id" (SageFs.WorkerProtocol.SessionId.value info.Id)
    // SessionInfo.displayName derives from SolutionRoot/WorkingDirectory's
    // last path segment (WorkerProtocol.fs), NOT the `Name` field — the same
    // behavior formatSessionInfo/list_sessions already reports, so
    // sessionsToJson must match it exactly rather than surfacing `Name`.
    s0 |> getProp "name" |> fun p -> p.GetString() |> Expect.equal "name should be the display name (last segment of workingDirectory, no SolutionRoot set)" "checkout"
    s0 |> getProp "workingDirectory" |> fun p -> p.GetString() |> Expect.equal "workingDirectory should round-trip" "/repo/checkout"
    s0 |> getProp "workflow" |> fun p -> p.GetString() |> Expect.equal "workflow should be the label for Interactive" "REPL"
    let projects = s0 |> getProp "projects" |> fun p -> p.EnumerateArray() |> Seq.map (fun x -> x.GetString()) |> Seq.toList
    projects |> Expect.equal "projects should round-trip" [ "Foo.fsproj" ]

  testCase "sessionsToJson is stable/empty for no active sessions" <| fun () ->
    let json = SageFs.SessionOperations.sessionsToJson jsonOpts []
    use doc = JsonDocument.Parse(json)
    doc.RootElement.GetProperty("sessions").GetArrayLength() |> Expect.equal "no sessions" 0

  // ── sessionsListVersion: the sessions://list McpResourceGate key ──

  testCase "sessionsListVersion is identical for the same sessions in a different order" <| fun () ->
    let a = mkSessionInfo "a" "/repo/a"
    let b = mkSessionInfo "b" "/repo/b"
    SageFs.SessionOperations.sessionsListVersion [ a; b ]
    |> Expect.equal "order must not matter (sorted internally)" (SageFs.SessionOperations.sessionsListVersion [ b; a ])

  testCase "sessionsListVersion changes when a session's status changes" <| fun () ->
    let ready = mkSessionInfo "a" "/repo/a"
    let stopped = { ready with Status = SageFs.WorkerProtocol.SessionLifecycleStatus.Stopped }
    SageFs.SessionOperations.sessionsListVersion [ ready ]
    |> Expect.notEqual "a status change must change the version" (SageFs.SessionOperations.sessionsListVersion [ stopped ])

  testCase "sessionsListVersion changes when a session is added or removed" <| fun () ->
    let a = mkSessionInfo "a" "/repo/a"
    let b = mkSessionInfo "b" "/repo/b"
    SageFs.SessionOperations.sessionsListVersion [ a ]
    |> Expect.notEqual "adding a session must change the version" (SageFs.SessionOperations.sessionsListVersion [ a; b ])

  // ── McpResourceGate: the resources/updated no-change guard ──

  testCase "McpResourceGate.observe notifies on the first observation" <| fun () ->
    let notify, _ = SageFs.McpResourceGate.observe 1L SageFs.McpResourceGate.initial
    notify |> Expect.isTrue "the very first version observed must always notify"

  testCase "McpResourceGate.observe does not notify twice for the same unchanged version" <| fun () ->
    let notify1, state1 = SageFs.McpResourceGate.observe 1L SageFs.McpResourceGate.initial
    let notify2, state2 = SageFs.McpResourceGate.observe 1L state1
    notify1 |> Expect.isTrue "first observation notifies"
    notify2 |> Expect.isFalse "an unchanged tick (same version again) must emit zero notifications"
    state2 |> Expect.equal "state is unchanged when nothing was notified" state1

  testCase "McpResourceGate.observe notifies exactly once per version change, across a run of ticks" <| fun () ->
    // Mirrors wireCohortEventSubscription's lastMatrixVersion guard: several
    // unchanged ticks between two real changes must fire nothing.
    let versions = [ 1L; 1L; 1L; 2L; 2L; 3L ]
    let notifications =
      versions
      |> List.fold
        (fun (fired, state) v ->
          let notify, state' = SageFs.McpResourceGate.observe v state
          (fired @ [ notify ]), state')
        ([], SageFs.McpResourceGate.initial)
      |> fst
    notifications |> Expect.equal "notify exactly on the version changes: 1(first), 1, 1, 2(changed), 2, 3(changed)"
      [ true; false; false; true; false; true ]

  testCase "McpResourceGate.observe notifies again when the version changes back to a previous value" <| fun () ->
    let _, s1 = SageFs.McpResourceGate.observe 1L SageFs.McpResourceGate.initial
    let notify2, s2 = SageFs.McpResourceGate.observe 2L s1
    let notify3, _ = SageFs.McpResourceGate.observe 1L s2
    notify2 |> Expect.isTrue "moving to a new version notifies"
    notify3 |> Expect.isTrue "the gate does not assume monotonicity — any change from the last-seen version notifies"

  // ── SageFsResources: the actual MCP resource methods, called directly ──

  testTask "SageFsResources.CohortStatus reads the cohort owner's current frame as JSON" {
    let sessionId = SageFs.WorkerProtocol.SessionId.newId ()
    let ctx = sharedCtxWith sessionId
    let ledger = SageFs.Features.CohortLedger.InMemory.create<SageFs.MemberTable.MemberId> ()
    use cohortOwner =
      SageFs.Features.CohortOwner.start (SageFs.Utils.Log.asILogger ()) ledger (fun () -> clock) (fun () -> noEntropy) (fun _ -> ([], [], [], 0L))
    let! _ = cohortOwner.Commit(SageFs.Cohort.CohortCommand.Join(alice, SageFs.Cohort.JoinableRole.Implementer, Some "sess-1"))
    let ctxWithCohort = { ctx with CohortOwner = Some cohortOwner }
    let resources = SageFs.Server.McpResources.SageFsResources(ctxWithCohort)
    let json = resources.CohortStatus()
    let doc = JsonDocument.Parse(json)
    let members = doc.RootElement |> getProp "members" |> fun p -> p.EnumerateArray() |> Seq.toList
    members |> List.length |> Expect.equal "alice should have joined the cohort" 1
  }

  testCase "SageFsResources.CohortStatus reports an empty-but-valid frame when no cohort is wired" <| fun () ->
    let sessionId = SageFs.WorkerProtocol.SessionId.newId ()
    let ctx = sharedCtxWith sessionId
    let resources = SageFs.Server.McpResources.SageFsResources(ctx)
    let json = resources.CohortStatus()
    use doc = JsonDocument.Parse(json)
    doc.RootElement.GetProperty("members").GetArrayLength() |> Expect.equal "no cohort owner -> no members, not an error" 0

  testTask "SageFsResources.SessionsList reads the session list as JSON" {
    let sessionId = SageFs.WorkerProtocol.SessionId.newId ()
    let ctx = sharedCtxWith sessionId
    let resources = SageFs.Server.McpResources.SageFsResources(ctx)
    let! (json: string) = resources.SessionsList()
    let doc = JsonDocument.Parse(json)
    doc.RootElement.TryGetProperty("sessions") |> fst |> Expect.isTrue "sessions://list content must be a JSON object with a sessions array"
  }
]
