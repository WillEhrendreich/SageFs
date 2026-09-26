namespace SageFs

// ── Recovering a client whose session id is dead ───────────────────────
//
// WHY this is its own module: it is a POLICY with several honest outcomes, not
// a branch. It was one branch inside `Mcp.fs`'s `resolveSessionId`, which is a
// 4,400-line accretion hub, and the file-size ratchet was right that it did not
// belong there. Extracting it also means the policy can be read without
// reading the whole session-routing stack.
//
// The bug it fixes, which cost a working agent its entire SageFs access: a
// client that outlived a daemon restart was PERMANENTLY bricked. `resolveSessionId`
// given a session id checked ONLY that id, while the branch for a client that
// sent no id at all resolved by working directory and re-bound. So the one case
// that most needs recovery — a stale id, which happens on EVERY restart — was
// the one case that could not recover. The error made it worse by telling the
// caller to use `create_project_session` through a tool surface that could not
// resolve a session at all.
//
// The recovery is deliberately NOT a silent rebind. An ambiguous directory is
// refused, because guessing which session to attach to is the same class of
// error as guessing a restart scope: a wrong answer is worse than an honest
// refusal. And "we could not check" is kept distinct from "there is nothing
// there", so the caller can tell them apart.

open System
open System.Threading.Tasks

module StaleSessionRecovery =

  /// The outcome of trying to route a client whose session id no longer exists.
  [<RequireQualifiedAccess>]
  type Recovery =
    /// A live session serves the requested working directory; re-bind to it.
    | ReBind of sessionId: string * because: string
    /// Nothing recoverable. `why` names what evidence was missing, so the
    /// caller can act on it rather than retry the same call.
    | Gone of why: string

  /// Whether a working directory is actually supplied. An empty or whitespace
  /// string is the same as none, and treating it as "supplied" would send the
  /// caller down a recovery path with nothing to recover by.
  let private usable (wd: string option) =
    match wd with
    | Some w when not (String.IsNullOrWhiteSpace w) -> Some w
    | _ -> None

  /// The one question that decides everything: can exactly ONE live session
  /// serve this directory? Zero or several are both refusals, for different
  /// reasons, and both must be refusals.
  let private uniqueSessionFor
      (sessions: WorkerProtocol.SessionId list)
      (wd: string)
      =
    // The session-matching rule lives in the caller, which owns the registry
    // lookup; this takes an already-narrowed candidate list so the policy is
    // testable without a daemon.
    match sessions with
    | [ only ] -> Ok only
    | [] -> Error "no session serves that working directory"
    | many ->
      Error(
        sprintf
          "several sessions serve that directory (%s)"
          (many |> List.map WorkerProtocol.SessionId.value |> String.concat ", "))

  /// Decide what to do about a dead session id. `candidatesFor` resolves a
  /// working directory to the session ids that serve it; injecting it keeps
  /// this pure and lets the tests drive every branch.
  let decide
      (staleId: string)
      (workingDirectory: string option)
      (candidatesFor: string -> WorkerProtocol.SessionId list)
      : Recovery =
    match usable workingDirectory with
    | None ->
      // No evidence to recover by, and the message says so SPECIFICALLY rather
      // than pretending a working directory was never part of the request.
      Recovery.Gone(sprintf "session '%s' is gone; pass working_directory so SageFs can re-bind you to a live session" staleId)
    | Some wd ->
      match uniqueSessionFor (candidatesFor wd) wd with
      | Error why -> Recovery.Gone(sprintf "session '%s' is gone and %s" staleId why)
      | Ok matchedId when WorkerProtocol.SessionId.value matchedId = staleId ->
        Recovery.Gone(sprintf "session '%s' is gone and nothing else serves that working directory" staleId)
      | Ok matchedId ->
        let matched = WorkerProtocol.SessionId.value matchedId
        Recovery.ReBind(matched, sprintf "session '%s' is gone; '%s' serves the same working directory" staleId matched)
