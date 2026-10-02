module SageFs.Tests.CohortIdentityLeakTests

/// The public identity of a cohort member must not be the MCP SDK's bearer
/// handle. Today `memberIdFor` builds `MemberId.Mcp <Mcp-Session-Id>`, and
/// `get_cohort_status`, the cohort frame, the SSE feed, the ledger export and
/// the lease listing all print it. Anyone who can read any of them can present
/// the handle as `Mcp-Session-Id` and act as that member, the conductor
/// included (nehemiah-cohort-requests-response-2026-10-02.md, "Hazards none of
/// the asks mention"). The fix is an opaque, one-way public id; the handle
/// stays inside the connection.

open System
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.MemberTable
open SageFs.McpTools
open SageFs.Tests.TestInfrastructure

/// Handles in the shape the SDK mints (22 base64url characters, 128 bits).
let private handleA = "tJj5NFu4OCqmI2WIWkiBFA"
let private handleB = "Y5FMEWuygbQVjRTam-Cmkg"

let private jsonOpts = SageFs.Json.optionsOf SageFs.Json.camelCase

/// Run `body` as the connection that holds `handle`, the way the call filter
/// binds it: an AsyncLocal that flows through the awaited work.
let private asConnection (handle: string) (body: unit -> Threading.Tasks.Task<'a>) : Threading.Tasks.Task<'a> =
  task {
    let previous = currentTransportSessionId.Value
    currentTransportSessionId.Value <- Some handle
    try
      return! body ()
    finally
      currentTransportSessionId.Value <- previous
  }

let private mentionsAny (handles: string list) (text: string) : string list =
  handles |> List.filter (fun h -> text.Contains(h, StringComparison.Ordinal))

[<Tests>]
let tests =
  testList "Cohort identity: no output carries a connection's bearer handle" [

    testCase "WHY - a connection member's public id is not its handle" <| fun () ->
      let shown = MemberId.display (MemberId.ofConnectionHandle handleA)
      shown |> Expect.stringStarts "keeps the mcp: prefix so ids stay distinguishable from names" "mcp:"
      shown.Contains handleA |> Expect.isFalse "the public id must not contain the handle"

    testCase "WHY - the public id is stable per connection and distinct across connections" <| fun () ->
      MemberId.ofConnectionHandle handleA
      |> Expect.equal "same handle, same member id" (MemberId.ofConnectionHandle handleA)
      (MemberId.ofConnectionHandle handleA <> MemberId.ofConnectionHandle handleB)
      |> Expect.isTrue "two connections are two members"

    testCase "WHY - a raw handle wrapped by hand still never prints as the handle" <| fun () ->
      // The legacy shape: ledger rows written before this fix hold MemberId.Mcp <raw handle>.
      MemberId.display (MemberId.Mcp handleA)
      |> fun shown -> shown.Contains handleA |> Expect.isFalse "display re-derives the public id from a raw handle"

    testCase "WHY - memberIdFor under a bound connection yields the opaque id" <| fun () ->
      let previous = currentTransportSessionId.Value
      currentTransportSessionId.Value <- Some handleA
      try
        let who = memberIdFor "alice"
        (MemberId.display who).Contains handleA |> Expect.isFalse "the member id must not carry the handle"
        resolvedKey "alice" |> fun key -> key.Contains handleA |> Expect.isFalse "the routing key is printed in lease and presence listings, so it must not carry the handle either"
        who |> Expect.equal "the same connection resolves to the same member" (MemberId.ofConnectionHandle handleA)
      finally
        currentTransportSessionId.Value <- previous

    testTask "NO-HANDLE-IN-COHORT-OUTPUT - status, frame, SSE rows, ledger, events and leases never show a handle" {
      let handles = [ handleA; handleB ]
      let ledger = SageFs.Features.CohortLedger.InMemory.create<MemberId> ()
      let epoch = DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc)
      let mutable seed = 0
      use owner =
        SageFs.Features.CohortOwner.start
          (SageFs.Utils.Log.asILogger ())
          ledger
          (fun () -> epoch)
          (fun () -> seed <- seed + 1; BitConverter.GetBytes seed)
          (fun _ -> ([], [], [], 0L))
      let ctx = { sharedCtx () with CohortOwner = Some owner }
      let transcript = ResizeArray<string>()
      let record (label: string) (result: Result<string, SageFsError>) =
        match result with
        | Ok text -> transcript.Add(sprintf "%s: %s" label text)
        | Error err -> transcript.Add(sprintf "%s: %s" label (SageFsError.describeForAgent err))

      let! joinA = asConnection handleA (fun () -> joinCohort ctx "conductor" "Implementer" None)
      record "join A" joinA
      let! joinB = asConnection handleB (fun () -> joinCohort ctx "worker" "Implementer" None)
      record "join B" joinB
      let! claim = asConnection handleB (fun () -> acquireClaim ctx "worker" "file:src/Foo.fs" "editing")
      record "claim B" claim
      let! leaseA = asConnection handleA (fun () -> Threading.Tasks.Task.FromResult(Ok (acquireWorkLease "conductor" "/work/a" SageFs.ExpensiveWorkLease.Kind.FullBuild)))
      record "lease A" leaseA
      let! status = SageFs.McpCohortIntegration.getCohortStatus ctx
      record "status" status

      let frame = owner.ReadFrame()
      transcript.Add(SageFs.SseWriter.cohortFrameJson jsonOpts frame)
      transcript.Add(SageFs.SseWriter.formatCohortMatrixEvent jsonOpts frame)
      transcript.Add(SageFs.Server.McpResources.SageFsResources(ctx).CohortStatus())
      transcript.Add(SageFs.Features.CohortLedgerExport.toJsonl (ledger.ReadAll()))
      // The events every committed command recorded, the way the owner publishes them.
      transcript.Add(SageFs.WorkerProtocol.Serialization.serialize (ledger.ReadAll() |> List.collect (fun entry -> entry.Events)))

      let everything = String.concat "\n" transcript
      everything.Contains "mcp:" |> Expect.isTrue "the outputs must show members at all, or this test proves nothing"
      mentionsAny handles everything
      |> Expect.isEmpty "no cohort output may contain any connection's bearer handle"
    }
  ]
