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

  let listReferences
    (ctx: McpContext)
    (agent: string)
    (qualifiedName: string)
    (offset: int)
    (workingDirectory: string option)
    : Task<string> =
    if System.String.IsNullOrWhiteSpace qualifiedName then
      task { return "Give a qualified_name to find references to, e.g. 'SageFs.Features.CallerCheck.callersAsync'." }
    else
      let name = qualifiedName.Trim().TrimEnd '.'
      withSessionWd ctx agent workingDirectory (fun sid -> task {
        let! reply = referencesOf ctx sid name
        return McpReferencesPage.build name SageFsConfig.ListReferencesMax offset reply |> McpReferencesPage.render
      })
