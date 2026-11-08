namespace SageFs

open System
open System.Threading.Tasks

/// THE ONE APPLICATION of `SessionAccess.decide`: refuse stopping, resetting, hard-resetting, switching the workflow of or
/// stopping the app of a session unless the caller may act on it, with the refusal and its next action. The tool gate
/// calls it for the four guarded tools that route to a session, `stop_session`'s body calls it, and the HTTP stop,
/// stop-app and workflow routes call it with who the request was bound to. Nobody is ever asked: a caller is allowed or
/// refused. The rule is `SessionAccess.decide`; this is what feeds it.
module McpSessionAccess =

  /// The two lookups a refusal may need, passed in so this module reads neither the registry nor the cohort itself.
  type Lookups =
    {
      /// The session's record, to find the directory (and so the cohort) it sits in.
      SessionInfo: string -> Task<WorkerProtocol.SessionInfo option>
      /// What `who` is in the cohort that owns `workingDirectory`.
      AuthorityIn: string -> MemberTable.MemberId -> Cohort.Authority<MemberTable.MemberId>
    }

  /// Refuse `action` on `sessionId` for a caller that is the operator (or not) and bound to `bound` (or to nothing).
  ///
  /// Only a conductor of the session's cohort changes a refusal, and finding that cohort costs a session lookup, so the rule
  /// runs first with no authority and again with the real one only when it refused.
  let refusal
    (lookups: Lookups)
    (operator: bool)
    (bound: MemberTable.MemberId option)
    (action: SessionAction)
    (sessionId: string)
    : Task<SageFsError option> =
    task {
      let maker =
        match SessionOwners.ownerOf sessionId with
        | Some owner -> SessionMaker.MadeBy owner
        | None -> SessionMaker.NoRecord
      let decideWith (authority: Cohort.Authority<MemberTable.MemberId>) =
        SessionAccess.decide maker (SessionCaller.resolve operator bound authority)
      match decideWith Cohort.Authority.Anonymous with
      | SessionAccess.Allowed _ -> return None
      | SessionAccess.Refused _ ->
        let! info = lookups.SessionInfo sessionId
        let authority =
          match bound, info with
          | Some who, Some session when not (String.IsNullOrWhiteSpace session.WorkingDirectory) ->
            lookups.AuthorityIn session.WorkingDirectory who
          | _ -> Cohort.Authority.Anonymous
        match decideWith authority with
        | SessionAccess.Allowed _ -> return None
        | SessionAccess.Refused maker -> return Some(SessionAccess.refusal action sessionId maker)
    }
