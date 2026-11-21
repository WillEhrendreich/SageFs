namespace SageFs

open System
open System.Threading.Tasks
open SageFs.McpRouteError
open SageFs.McpSessionRouting
open SageFs.McpTools

/// The FCS tooltip for one named candidate at a caret. The completion wire carries names and kinds but
/// no description closure (a closure cannot be serialized), so ask the one process that holds FCS for
/// the single tooltip the signature tool needs. None means the name was not in the set or FCS had no
/// description; it is an answer, not a failure to reach the worker.
module McpCompletionDescription =
  /// Single-case wrappers: the three strings below are not interchangeable. Swapping code for name, or
  /// either for a session id, would compile cleanly and fail at runtime; the wrappers make it a type error.
  type CompletionSource = CompletionSource of string
  type Candidate = Candidate of string
  type Session = Session of string

  let get
    (ctx: McpContext)
    (CompletionSource code)
    (cursorPosition: int)
    (Candidate name)
    (Session sessionId)
    : Task<string option> =
    task {
      let! routeResult =
        routeToSession ctx sessionId
          (fun replyId ->
            WorkerProtocol.WorkerMessage.DescribeCompletion(
              code, cursorPosition, name, WorkerProtocol.SessionId.value replyId))
      match routeResult with
      | Ok (WorkerProtocol.WorkerResponse.DescriptionResult(_, description)) ->
        return if String.IsNullOrEmpty description then None else Some description
      | Ok other ->
        Utils.Log.warn "[signature] a describe reply came back as %s" (other.GetType().Name)
        return None
      | Error _ -> return None
    }
