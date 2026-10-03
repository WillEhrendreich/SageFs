module SageFs.Tests.SessionScopedRoutingTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Server

/// `SessionRouting` is nested inside `SageFs.Server.DashboardTypes`, which is a bare `module`, so it needs its parent
/// qualified even with both namespaces open — there is no way to `open` a nested module from outside it.
module SessionRouting = SageFs.Server.DashboardTypes.SessionRouting
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
      joined |> Expect.stringContains "the refusal names the first session" "aaa00001"
      joined |> Expect.stringContains "and the second" "bbb00002"
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

// ── The live-testing routes: which session's OWN state does a request act on? ──
//
// The resolution tests above prove the DECISION. These prove the decision is
// what the Elm runtime then acts ON, through a real `SageFsUpdate.update`
// against a real model, with two sessions live. The assertion reads the paused
// flag back off each session's own cycle — which session changed is the whole
// question, so the test would fail against the ambient-pointer code even if it
// returned "something".

/// One session's OWN live-testing cycle, as the read path resolves it
/// (`cycleForSession`). This is the state a caller is asking about when it
/// names a session: the active session's own state IS Primary, so probing
/// `cycleOwnedBySession` here would read Primary for both and hide the bug.
let private pausedFor (model: SageFsModel) (sessionId: string) =
  (SageFsModel.cycleForSession sessionId model).TestState.Pause

/// A model with the ambient active session on `ambientId` (what another client
/// would have switched the daemon to), then the ONE request under test applied.
let private modelAfter (ambientId: string) (request: SageFs.SageFsMsg) =
  let start = SageFsModel.initial ()
  let switched = SageFsUpdate.update (SageFsMsg.Event (TuiEvent.SessionSwitched (None, ambientId))) start
  let after = SageFsUpdate.update request (fst switched)
  fst after

/// THE LIVE-TESTING ROUTES THAT ACT ON ONE SESSION'S STATE, as data.
///
/// Each row carries the route and the three questions every one of them must
/// answer the same way, expressed against the route's OWN resolve-then-dispatch
/// shape (`resolveForMutatingRoute`, then dispatch the resolved id) rather than
/// a hand-picked id — so the row is the route, not a restatement of it.
///
/// `DecideTwoClients` / `DecideOneClient` are the decision the route reaches
/// with two sessions / one session. `DispatchTwoClients` / `DispatchOneClient`
/// are the message that decision puts on the Elm loop.
type LiveRoute = {
  Route: string
  /// The decision with the two-client registry (`aaa00001`, `bbb00002`).
  DecideTwoClients: string option -> RouteDecision
  /// The decision with the single-client registry (`aaa00001`).
  DecideOneClient: string option -> RouteDecision
  /// The msg the route dispatches after that decision, against the two-client
  /// registry (the ambient active session is `bbb00002`).
  DispatchTwoClients: string option -> SageFs.SageFsMsg
  /// The same, against the single-client registry.
  DispatchOneClient: string option -> SageFs.SageFsMsg
}

/// The pause/resume family. Both routes are `TuiEvent.LivePauseChanged`, so one
/// builder serves both and the pair differs only in the pause value.
let private pauseRoute (pause: Features.LiveTesting.LivePause) : LiveRoute = {
  Route =
    match pause with
    | Features.LiveTesting.LivePause.Paused -> "POST /api/live-testing/pause"
    | Features.LiveTesting.LivePause.Live -> "POST /api/live-testing/resume"
  // Resolving is what every session-scoped live-testing route does first, and
  // what it must do BEFORE it dispatches: the id it dispatches is the resolved
  // one, never whatever the ambient pointer holds.
  DecideTwoClients = fun requested -> decide twoSessions requested
  DecideOneClient = fun requested -> decide singleSession requested
  DispatchTwoClients = fun requested ->
    match decide twoSessions requested with
    | ActOn sid -> SageFsMsg.Event (TuiEvent.LivePauseChanged (Some sid, pause))
    | Refuse _ ->
      // A refusal dispatches NOTHING. `LivePauseChanged (None, _)` is the
      // ambient-pointer write the fix exists to remove; it must be unreachable
      // from a route, so the builder refuses to produce it.
      failtestf "%s must not dispatch a session-less pause" "pause route"
  DispatchOneClient = fun requested ->
    match decide singleSession requested with
    | ActOn sid -> SageFsMsg.Event (TuiEvent.LivePauseChanged (Some sid, pause))
    | Refuse _ -> failtestf "%s must not dispatch a session-less pause" "pause route"
}

/// Every session-scoped live-testing mutation, in one table, so the sweep is
/// auditable: adding a route to the family means adding a row here.
let private liveScopedRoutes: LiveRoute list = [
  pauseRoute Features.LiveTesting.LivePause.Paused
  pauseRoute Features.LiveTesting.LivePause.Live
]

let private pausedFlagTests = testList "live-testing routes act on the session the caller named" [

  testCase "PROVEN: with no sessionId the pause lands on the AMBIENT session, not the caller's" <| fun _ ->
    // Two sessions. `bbb00002` is what the daemon currently considers active
    // (another client switched it). A pause carrying NO id lands on Primary,
    // which is bbb00002's cycle — so the caller who meant aaa00001 gets it
    // applied to somebody else's session. This is the defect, pinned: it is
    // what `dispatchLivePause` dispatches today for an unnamed request.
    let model = modelAfter "bbb00002" (SageFsMsg.Event (TuiEvent.LivePauseChanged (None, Features.LiveTesting.LivePause.Paused)))
    pausedFor model "aaa00001"
    |> Expect.equal "the session the caller meant must not be the one paused" Features.LiveTesting.LivePause.Live
    pausedFor model "bbb00002"
    |> Expect.equal "the ambient session is what a no-id request really hits" Features.LiveTesting.LivePause.Paused

  testCase "two clients, two sessions: each pause lands on its OWN session" <| fun _ ->
    // Client A pauses aaa00001 naming it; client B pauses bbb00002 naming it.
    // Both are live at once and neither may touch the other. Each goes through
    // the route's own resolve-then-dispatch, not through a hand-picked id.
    let pause = liveScopedRoutes |> List.head
    let modelA = modelAfter "bbb00002" (pause.DispatchTwoClients (Some "aaa00001"))
    let modelB = modelAfter "bbb00002" (pause.DispatchTwoClients (Some "bbb00002"))
    pausedFor modelA "aaa00001"
    |> Expect.equal "client A must pause its OWN session" Features.LiveTesting.LivePause.Paused
    pausedFor modelA "bbb00002"
    |> Expect.equal "client A must not touch client B's session" Features.LiveTesting.LivePause.Live
    pausedFor modelB "bbb00002"
    |> Expect.equal "client B must pause its OWN session" Features.LiveTesting.LivePause.Paused
    pausedFor modelB "aaa00001"
    |> Expect.equal "client B must not touch client A's session" Features.LiveTesting.LivePause.Live

  testCase "two sessions and no id REFUSES, so nothing is dispatched to either" <| fun _ ->
    // The refusal half. With two sessions the route must not guess, and a
    // refusal dispatches no message at all — neither session may move.
    let pause = liveScopedRoutes |> List.head
    match pause.DecideTwoClients None with
    | Refuse (SageFsError.AmbiguousSessions _) -> ()
    | other -> failtestf "expected a typed refusal, got %A" other
    let untouched = modelAfter "bbb00002" (SageFsMsg.Event (TuiEvent.LivePauseChanged (None, Features.LiveTesting.LivePause.Paused)))
    pausedFor untouched "aaa00001"
    |> Expect.equal "no dispatch means no session changed" Features.LiveTesting.LivePause.Live
    pausedFor untouched "bbb00002"
    |> Expect.equal "no dispatch means no session changed" Features.LiveTesting.LivePause.Live

  testCase "the single-client case: one session, no id, still pauses that session" <| fun _ ->
    // THE REGRESSION GUARD, and it runs the route's own resolver: one session
    // and no id must resolve `DefaultSingle` and dispatch, not refuse. Naming
    // the only session must give the identical outcome, so adding the id is a
    // no-op for a single-client caller.
    let pause = liveScopedRoutes |> List.head
    match pause.DecideOneClient None with
    | ActOn sid -> sid |> Expect.equal "one session is unambiguous" "aaa00001"
    | Refuse err -> failtestf "one client and one session must not be refused: %A" err
    let unnamed = modelAfter "aaa00001" (pause.DispatchOneClient None)
    let named = modelAfter "aaa00001" (pause.DispatchOneClient (Some "aaa00001"))
    pausedFor unnamed "aaa00001"
    |> Expect.equal "one session and no id still pauses it (single client)" Features.LiveTesting.LivePause.Paused
    pausedFor named "aaa00001"
    |> Expect.equal "naming the only session pauses the same one" Features.LiveTesting.LivePause.Paused

  testCase "every session-scoped live-testing route resolves its session the same way" <| fun _ ->
    // The family is enumerated as data, so a new session-scoped live-testing
    // route cannot join without appearing here — and every row is held to the
    // identical three rules: two sessions route distinctly, two and no id
    // refuses, one and no id succeeds.
    liveScopedRoutes
    |> List.map (fun r -> r.Route)
    |> Expect.hasLength "the sweep is complete and non-empty" 2
    liveScopedRoutes
    |> List.iter (fun route ->
      match route.DecideTwoClients (Some "aaa00001") with
      | ActOn "aaa00001" -> ()
      | other -> failtestf "%s must act on the named session, got %A" route.Route other
      match route.DecideTwoClients None with
      | Refuse (SageFsError.AmbiguousSessions _) -> ()
      | other -> failtestf "%s must refuse two sessions with no id, got %A" route.Route other
      match route.DecideOneClient None with
      | ActOn _ -> ()
      | Refuse err -> failtestf "%s must not refuse a single client: %A" route.Route err)
]

[<Tests>]
let private liveTestingRouteTargeting = pausedFlagTests

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