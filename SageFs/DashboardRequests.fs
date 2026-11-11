/// What the dashboard's page-level handlers decide before they touch a session: why an Evaluate click cannot run. Pure values and
/// sentences, so a test reads them and `Dashboard.fs` (which is over its file-size budget) does not have to grow to hold them.
module SageFs.Server.DashboardRequests

open System
open Falco.Markup
open SageFs

/// What the Evaluate click carried: the code box and the session the page believes it is viewing.
type EvalSignals = { Code: string; ViewingSessionId: string }

/// Why an Evaluate click did not run. Each case has its own sentence (`EvalRefusal.describe`), so the box can keep the
/// person's code and the page can say what to do.
[<RequireQualifiedAccess>]
type EvalRefusal =
  | BlankCode
  | NoSessionInView
  | SessionIdInvalid of reason: string

module EvalRefusal =
  /// The sentence the page shows. Every case says what happened, that nothing ran, and the next action.
  let describe (refusal: EvalRefusal) : string =
    match refusal with
    | EvalRefusal.BlankCode ->
      "The code box is empty. Nothing was run. Type an expression, then press Eval."
    | EvalRefusal.NoSessionInView ->
      "No session is open to run this in. Nothing was run, and your code is still in the box. Start one with Quick Start or + New, then press Eval again."
    | EvalRefusal.SessionIdInvalid reason ->
      sprintf "This page's session id was not accepted (%s). Nothing was run, and your code is still in the box. Pick a session in the sidebar to retarget the page, then press Eval again." reason

module EvalRequest =
  /// Decide what an Evaluate click asks for: the typed session id and the code with its `;;` terminator, or the one
  /// reason it cannot run.
  let decide (signals: EvalSignals) : Result<WorkerProtocol.SessionId * string, EvalRefusal> =
    match String.IsNullOrWhiteSpace signals.Code with
    | true -> Result.Error EvalRefusal.BlankCode
    | false ->
      match String.IsNullOrEmpty signals.ViewingSessionId with
      | true -> Result.Error EvalRefusal.NoSessionInView
      | false ->
        match WorkerProtocol.SessionId.validate signals.ViewingSessionId with
        | Result.Error reason -> Result.Error(EvalRefusal.SessionIdInvalid reason)
        | Result.Ok sessionId ->
          let trimmed = signals.Code.TrimEnd()
          match trimmed.EndsWith(";;") with
          | true -> Result.Ok(sessionId, signals.Code)
          | false -> Result.Ok(sessionId, sprintf "%s;;" trimmed)
