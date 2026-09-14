module SageFs.Server.McpResources

/// MCP resources for cohort/session state (Phase 1 item 12,
/// sagefs-multiagent-vision.md §5.6/§8.2/§10): "Build MCP resource
/// registration + `resources/subscribe`". Agents read — and, once
/// subscribed (`configureMcpProtocol` in McpServer.fs wires
/// `resources/subscribe`/`unsubscribe` and pushes
/// `notifications/resources/updated`), watch — this instead of polling
/// `get_cohort_status`/`list_sessions`.
///
/// Content is produced by the same PURE read-model projections the existing
/// SSE wire rows already use (`SseWriter.cohortFrameJson`,
/// `SessionOperations.sessionsToJson`) — §5.6's "one read model, no new
/// channel": a resource is a second VIEW onto state that already has a
/// single source of truth, never a second copy of it.

open System.ComponentModel
open System.Text.Json
open System.Threading.Tasks
open ModelContextProtocol.Server
open SageFs.McpTools

/// The daemon's single per-daemon cohort (cohort-integration-plan.md D1) —
/// a direct resource (no URI parameters), not a template.
[<Literal>]
let CohortStatusUri = "cohort://status"

/// The daemon's current session list — a direct resource.
[<Literal>]
let SessionsListUri = "sessions://list"

/// CamelCase for resource content: this is a fresh, agent-facing JSON
/// surface (unlike `McpServer.fs`'s `sseJsonOpts`, which predates this item
/// and is reused as-is for the existing `cohort_matrix` SSE row rather than
/// changed here). Same `JsonFSharpConverter` the rest of the codebase's JSON
/// wire rows use.
let private jsonOpts =
  let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
  opts.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
  opts

/// A `CohortFrame` for a cohort that has never had a command applied to it —
/// used only when no `CohortOwner` is wired (most daemons/tests). "No
/// cohort" is a legitimate, stable state to report, not a failure to read
/// the resource.
let private emptyCohortFrame () : SageFs.Cohort.CohortFrame<SageFs.MemberTable.MemberId> =
  let head : SageFs.Cohort.LedgerHead<SageFs.MemberTable.MemberId> =
    { Seq = 0L<SageFs.Measures.ledgerSeq>; State = SageFs.Cohort.CohortState.empty () }
  SageFs.Cohort.project head [||]

type SageFsResources(ctx: McpContext) =

  [<McpServerResource(UriTemplate = CohortStatusUri, Name = "cohort_status", MimeType = "application/json")>]
  [<Description("The daemon's current cohort state (members, claims, test matrix) as JSON — the same read model get_cohort_status reports. Subscribe (resources/subscribe) to be pushed notifications/resources/updated whenever the cohort changes, instead of polling.")>]
  member _.CohortStatus() : string =
    match ctx.CohortOwner with
    | Some cohortOwner -> SageFs.SseWriter.cohortFrameJson jsonOpts (cohortOwner.ReadFrame())
    | None -> SageFs.SseWriter.cohortFrameJson jsonOpts (emptyCohortFrame ())

  [<McpServerResource(UriTemplate = SessionsListUri, Name = "sessions_list", MimeType = "application/json")>]
  [<Description("The daemon's current session list as JSON — the same read model list_sessions reports.")>]
  member _.SessionsList() : Task<string> =
    task {
      let! sessions = ctx.SessionOps.GetAllSessions()
      return SageFs.SessionOperations.sessionsToJson jsonOpts sessions
    }
