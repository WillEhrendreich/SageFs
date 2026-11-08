namespace SageFs

open System.Threading.Tasks
open SageFs.Features
open SageFs.McpRouteError
open SageFs.McpTools

/// The session-routed half of `list_references`: ask the session's worker for every definition and use of a qualified
/// name, and say how far its compiler looked. A reply that is not a references answer, or no reply at all, is an
/// unreachable session with the reason named, and never an empty list.
module McpReferencesRoute =

  /// What the worker's reply to `GetReferences` says, as the reply a caller is shown.
  let replyOf (routeResult: Result<WorkerProtocol.WorkerResponse, RouteError>) : ReferenceReply =
    match routeResult with
    | Ok(WorkerProtocol.WorkerResponse.ReferencesResult(_, answer)) -> ReferenceReply.ofAnswer answer
    | Ok other ->
      ReferenceReply.NotCounted(
        ReferenceUnavailable.SessionUnreachable(
          sprintf
            "the worker answered a references request with %s"
            (fst (Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(other, typeof<WorkerProtocol.WorkerResponse>))).Name))
    | Error msg -> ReferenceReply.NotCounted(ReferenceUnavailable.SessionUnreachable(routeErrorMessage msg))

  let referencesOf (ctx: McpContext) (sid: string) (name: string) : Task<ReferenceReply> =
    task {
      let! routeResult =
        routeToSession ctx sid (fun replyId ->
          WorkerProtocol.WorkerMessage.GetReferences(name, WorkerProtocol.SessionId.value replyId))
      return replyOf routeResult
    }

  /// The page for a name. The name path is walked first, so a name that stops resolving is answered at once with where
  /// it stops, and the compiler (whose budget covers every project) is asked only for a name that may resolve. A
  /// member list that was cut at the wire ceiling, or that could not be fetched, does not show the name is absent, so
  /// those still go to the compiler.
  let referencesPage
    (membersOf: string -> Task<Features.AutoCompletion.CompletionAnswer<Features.AutoCompletion.CompletionEntry>>)
    (referencesOf: string -> Task<ReferenceReply>)
    (name: string)
    (maxItems: int)
    (offset: int)
    : Task<McpReferencesPage.Page> =
    task {
      let! resolution = McpNamePath.resolve SageFsConfig.NearestNamesMax membersOf name
      let counted (nameCheck: McpReferencesPage.NameCheck) = task {
        let! reply = referencesOf name
        return
          match McpReferencesPage.build name maxItems offset reply with
          | McpReferencesPage.Page.Counted c -> McpReferencesPage.Page.Counted { c with NameCheck = nameCheck }
          | other -> other
      }
      match resolution with
      | McpNamePath.NamePathResolution.StopsAt(resolvedPrefix, missing, nearest) ->
        return McpReferencesPage.Page.NameStopsAt(name, resolvedPrefix, missing, nearest)
      | McpNamePath.NamePathResolution.NothingResolves first ->
        return McpReferencesPage.Page.NameNotListed(name, first)
      | McpNamePath.NamePathResolution.Resolved _
      | McpNamePath.NamePathResolution.ResolvedRoot -> return! counted McpReferencesPage.NameCheck.Resolves
      | McpNamePath.NamePathResolution.StopsAtCutList _
      | McpNamePath.NamePathResolution.Unknown _ -> return! counted McpReferencesPage.NameCheck.NotChecked
    }

  let listReferences
    (ctx: McpContext)
    (agent: string)
    (QualifiedName qualifiedName)
    (offset: int)
    (target: McpSessionRouting.RouteTarget)
    : Task<string> =
    if System.String.IsNullOrWhiteSpace qualifiedName then
      task { return "Give a qualified_name to find references to, e.g. 'SageFs.Features.CallerCheck.callersAsync'." }
    else
      let name = qualifiedName.Trim().TrimEnd '.'
      withSessionWd ctx agent target (fun sid -> task {
        let! page =
          referencesPage (McpCompletions.membersOf ctx sid) (referencesOf ctx sid) name SageFsConfig.ListReferencesMax offset
        return McpReferencesPage.render page
      })
