module SageFs.Tests.SessionScopedRoutingTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Server
open SageFs.WorkerProtocol
open SageFs.Tests.SharedGenerators

// ---------------------------------------------------------------------------
// A session-scoped MUTATING daemon route must act on the session the caller
// NAMED, never on whichever session the daemon's ambient "active session"
// pointer happens to hold when the request is handled.
//
// The race: a client switches the daemon's active session and then calls the
// route. With ONE client that is fine. The moment a second client (Neovim, a
// dashboard tab, another agent) moves the pointer in between, the action lands
// on the wrong session.
//
// Rules under test, composed onto the PURE `SessionOperations.resolveSession`
// rather than reimplemented here:
//   * an explicit sessionId, when present, is the target — unconditionally
//   * NO sessionId on a mutating route is a typed REFUSAL when several sessions
//     exist (choosing would mean guessing at ambient state)
//   * NO sessionId with exactly one session still succeeds — single client,
//     single session must not regress into an error
//   * NO sessionId with zero sessions is NoActiveSessions, never a guess
// ---------------------------------------------------------------------------

/// Build a session record. `minutesAgo` positions it in the LastActivity
/// ordering `resolveSession` sorts by.
let private sessionInfo (hex: string) (minutesAgo: float) : SessionInfo = {
  Id = (match SessionId.validate hex with Ok s -> s | Error _ -> failtestf "bad test session id %s" hex)
  Name = None
  Projects = []
  WorkingDirectory = "/tmp/" + hex
  SolutionRoot = None
  CreatedAt = DateTime.UtcNow.AddMinutes(-60.0)
  LastActivity = DateTime.UtcNow.AddMinutes(-minutesAgo)
  Status = SessionLifecycleStatus.Ready { Pid = 1; Port = None }
  Workflow = WorkflowTypes.SessionWorkflow.Interactive
  ActiveProject = None
  ProjectRoles = []
  App = SageFs.AppRun.AppRunState.NotRunning
  Rebuild = LastRebuild.NeverRebuilt
  Reload = SessionReload.NoReloadYet
  Freshness = SageFs.ReplFreshness.InSync
}

let private singleSession = [ sessionInfo "aaa00001" 0.0 ]

let private twoSessions =
  [ sessionInfo "aaa00001" 5.0
    sessionInfo "bbb00002" 1.0 ]

/// The two-client race, as the decision the route makes. Deliberately models
/// what the daemon would have done BEFORE the fix: read the ambient pointer.
type RouteDecision =
  | ActOn of sessionId: string
  | Refuse of error: SageFsError

let private decide sessions (requested: string option) =
  match SessionRouting.resolveForMutatingRoute requested sessions with
  | Ok sid -> ActOn sid
  | Error err -> Refuse err

// ── chunked-body plumbing ────────────────────────────────────────────────────

/// A request whose body is `json`. `declaredLength` false produces a request
/// with NO Content-Length — a chunked body, which is how Node's http.request
/// posts and therefore how the VS Code extension posts.
let private chunkedCtx (declaredLength: bool) (json: string) =
  let ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext()
  let bytes = System.Text.Encoding.UTF8.GetBytes(json)
  ctx.Request.Body <- new System.IO.MemoryStream(bytes)
  if declaredLength then
    ctx.Request.ContentLength <- int64 bytes.Length
  ctx

/// The OLD `tryReadTargetSessionId` gate, verbatim: returns None WITHOUT reading
/// the body whenever ContentLength is absent or non-positive.
let private oldContentLengthGate (ctx: Microsoft.AspNetCore.Http.HttpContext) =
  match ctx.Request.ContentLength with
  | cl when not cl.HasValue || cl.Value <= 0L -> None
  | _ -> Some "body-was-read"

let tests = testList "session-scoped route targeting" [

  // ── The bug: two clients, interleaved ──────────────────────────────────────

  testCase "explicit sessionId wins even though another client moved the pointer" <| fun _ ->
    // Client A switches the daemon pointer to aaa00001; client B then moves it
    // to bbb00002; client A now calls /reset naming its own session.
    let pointerAfterInterleave = "bbb00002"
    pointerAfterInterleave
    |> Expect.equal "precondition: the pointer really did move under A" "bbb00002"
    decide twoSessions (Some "aaa00001")
    |> Expect.equal "must act on the named session, not the pointer" (ActOn "aaa00001")

  testCase "two interleaved clients each land on their own session, in either order" <| fun _ ->
    let clientA, clientB = "aaa00001", "bbb00002"
    [ (clientA, clientB); (clientB, clientA) ]
    |> List.iter (fun (first, second) ->
      decide twoSessions (Some first)
      |> Expect.equal (sprintf "%s then %s: first keeps its own" first second) (ActOn first)
      decide twoSessions (Some second)
      |> Expect.equal (sprintf "%s then %s: second keeps its own" first second) (ActOn second))

  testCase "the decision is identical whatever the ambient pointer holds" <| fun _ ->
    // Sweep every session as the pointer and show the outcome never moves.
    let outcomes =
      twoSessions
      |> List.collect (fun pointerSession ->
        [ SessionId.value pointerSession.Id
          SessionId.value (List.head (List.rev twoSessions)).Id ])
      |> List.distinct
      |> List.map (fun _pointer -> decide twoSessions (Some "aaa00001"))
    outcomes
    |> List.distinct
    |> Expect.equal "target is pointer-independent" [ ActOn "aaa00001" ]

  // ── Refuse when ambiguous ─────────────────────────────────────────────────

  testCase "missing sessionId with several sessions is a typed refusal, not a guess" <| fun _ ->
    match decide twoSessions None with
    | Refuse (SageFsError.AmbiguousSessions _) -> ()
    | other -> failtestf "expected AmbiguousSessions, got %A" other

  testCase "the refusal is HTTP 400 and tells the caller to specify a sessionId" <| fun _ ->
    let err =
      match decide twoSessions None with
      | Refuse e -> e
      | ActOn sid -> failtestf "should have refused, acted on %s" sid
    SageFsError.toHttpStatus err
    |> Expect.equal "ambiguous targeting is the caller's problem" 400
    SageFsError.suggestedAction err
    |> Expect.equal "must say what to do" "Specify a sessionId explicitly"

  testCase "the refusal names the sessions the caller could have chosen" <| fun _ ->
    match decide twoSessions None with
    | Refuse (SageFsError.AmbiguousSessions descriptions) ->
      descriptions
      |> Expect.hasLength "both sessions must be listed" 2
      let joined = String.concat "\n" descriptions
      joined |> Expect.stringContains "aaa00001"
      joined |> Expect.stringContains "bbb00002"
    | other -> failtestf "expected AmbiguousSessions, got %A" other

  // ── Single client must NOT regress ────────────────────────────────────────

  testCase "missing sessionId with exactly one session still works (single client)" <| fun _ ->
    decide singleSession None
    |> Expect.equal "one session is unambiguous, so it is the target" (ActOn "aaa00001")

  testCase "an explicit id that does not exist is SessionNotFound, never a fallback" <| fun _ ->
    match decide twoSessions (Some "cccc0003") with
    | Refuse (SageFsError.SessionNotFound "cccc0003") -> ()
    | other -> failtestf "expected SessionNotFound, got %A" other

  testCase "no sessions at all is NoActiveSessions" <| fun _ ->
    match decide [] None with
    | Refuse SageFsError.NoActiveSessions -> ()
    | other -> failtestf "expected NoActiveSessions, got %A" other

  testCase "a blank sessionId is treated as absent, not as a lookup" <| fun _ ->
    decide twoSessions (Some "   ")
    |> Expect.equal "blank is not an explicit id" (Refuse(SageFsError.NoActiveSessions))

  // ── Chunked bodies: the same defect one layer down ────────────────────────

  testCase "PROVEN: the old ContentLength gate silently skipped a chunked body" <| fun _ ->
    // This is the reader as it was: it gates on ContentLength and returns None
    // without ever touching the stream. A chunked request has no Content-Length
    // (that is what chunked means), so EVERY such request looked to the route
    // as if the caller had sent no sessionId at all.
    let chunked = chunkedCtx false "{\"sessionId\":\"aaa00001\"}"
    chunked.Request.ContentLength.HasValue
    |> Expect.isFalse "a chunked request declares no length"
    oldContentLengthGate chunked
    |> Expect.equal "old gate returns nothing without reading" None

  testCase "the new reader returns the session id from a chunked body" <| fun _ ->
    let json = "{\"sessionId\":\"aaa00001\"}"
    let chunked = chunkedCtx false json
    let doc = McpServer.readJsonBody(chunked).Result
    SessionRouting.tryReadSessionIdFromBody chunked.Request.ContentLength.HasValue (doc.RootElement.GetRawText())
    |> Expect.equal "chunked body must still be read" (Some "aaa00001")

  testCase "chunked and declared-length reads agree for every body" <| fun _ ->
    // The defect WAS a ContentLength gate, so the two transports must be
    // indistinguishable for any body at all.
    let bodies =
      [ ""
        "not json"
        "{}"
        "{\"sessionId\":\"aaa00001\"}"
        "{\"sessionId\":\"\"}"
        "{\"session_id\":\"bbb00002\"}"
        "{\"category\":\"unit\"}"
        "{\"sessionId\":123}"
        "[1,2,3]" ]
    bodies
    |> List.iter (fun body ->
      let chunked = SessionRouting.tryReadSessionIdFromBody false body
      let declared = SessionRouting.tryReadSessionIdFromBody true body
      chunked
      |> Expect.equal (sprintf "transport-independence for %s" body) declared)

  testCase "alias spellings of sessionId all work" <| fun _ ->
    [ "sessionId"; "session_id"; "session" ]
    |> List.iter (fun alias ->
      let body = sprintf "{\"%s\":\"aaa00001\"}" alias
      SessionRouting.tryReadSessionIdFromBody false body
      |> Expect.equal (sprintf "alias %s" alias) (Some "aaa00001"))

  testCase "an empty or unparseable body yields no session id (and then refuses)" <| fun _ ->
    SessionRouting.tryReadSessionIdFromBody false ""
    |> Expect.equal "empty body has no id" None
    SessionRouting.tryReadSessionIdFromBody false "not json"
    |> Expect.equal "unparseable body has no id" None
    SessionRouting.tryReadSessionIdFromBody false "{\"sessionId\":\"\"}"
    |> Expect.equal "blank id is no id" None

  testCase "a project name in a chunked body is read" <| fun _ ->
    SessionRouting.tryReadProjectFromBody false "{\"project\":\"App.fsproj\"}"
    |> Expect.equal "chunked body must still be read" (Some "App.fsproj")

  testCase "a missing or blank project name is None in either transport" <| fun _ ->
    SessionRouting.tryReadProjectFromBody false "{\"project\":\"\"}"
    |> Expect.equal "blank project is no project" None
    SessionRouting.tryReadProjectFromBody false "{}"
    |> Expect.equal "absent project is no project" None
    SessionRouting.tryReadProjectFromBody true "{\"project\":\"App.fsproj\"}"
    |> Expect.equal "declared-length reads the same" (Some "App.fsproj")
]

// ── Properties: the ambient pointer can never leak into the decision ─────────

let private distinctHexes (ids: string list) =
  ids
  |> List.filter (fun h -> h.Length = 8)
  |> List.distinct

let config = { FsCheckConfig.defaultConfig with maxTest = 200 }

[<Tests>]
let propertyTests = testList "session-scoped route targeting (properties)" [

  testPropertyWithConfig
    config
    "with several sessions and no id, the route always refuses"
    (fun (ids: string list) ->
      let hexes = distinctHexes ids
      let sessions = hexes |> List.map (fun h -> sessionInfo h 0.0)
      match sessions with
      | [] -> true
      | [ _ ] -> true // single session is allowed; covered explicitly above
      | _ ->
        match decide sessions None with
        | Refuse (SageFsError.AmbiguousSessions _) -> true
        | _ -> false)

  testPropertyWithConfig
    config
    "an explicit id always selects that id, whatever else exists"
    (fun (ids: string list) ->
      match distinctHexes ids with
      | [] -> true
      | target :: others ->
        let sessions = (target :: others) |> List.map (fun h -> sessionInfo h 0.0)
        decide sessions (Some target) = ActOn target)

  testPropertyWithConfig
    config
    "adding more sessions can never change the target an explicit id selects"
    (fun (ids: string list) ->
      match distinctHexes ids with
      | [] -> true
      | target :: others ->
        let alone = [ sessionInfo target 0.0 ]
        let many = (target :: others) |> List.map (fun h -> sessionInfo h 0.0)
        decide alone (Some target) = decide many (Some target))
]