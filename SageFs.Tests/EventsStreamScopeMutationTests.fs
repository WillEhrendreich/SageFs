/// ## /events scoping mutation tests
///
/// The example and property tests in EventsStreamScopeTests say the scoping works. These ask
/// the other question: if one decision in it were wrong, would anything notice? Each mutant
/// breaks exactly one decision (a daemon frame hidden, a session's frame shown to everyone, a
/// bad id read as the firehose, an event filed under the wrong scope), and each case passes
/// only when the mutant's answer differs from the real one.
module EventsStreamScopeMutationTests

open Expecto
open SageFs
open SageFs.Server
open SageFs.WorkerProtocol
open MutationTestingFramework

let idA = "0a0b0c0d"
let idB = "deadbeef"

// ── SseFrame.visibleTo ───────────────────────────────────────────────────

let visible (stream: StreamScope, frame: SseFrame) = SseFrame.visibleTo stream frame

let hideDaemonFromScoped : Mutant<(StreamScope * SseFrame) -> bool> =
  { Name = "visibleTo_scoped_stream_hides_daemon_frames"
    Description = "a connection for one session must still get what is true for the whole daemon (cohort, alarms)"
    Apply = fun real (stream, frame) ->
      match stream, frame.Scope with
      | StreamScope.OnlySession _, FrameScope.Daemon -> false
      | _ -> real (stream, frame) }

let scopedSeesEverySession : Mutant<(StreamScope * SseFrame) -> bool> =
  { Name = "visibleTo_scoped_stream_ignores_the_session"
    Description = "a connection for A must never be handed B's frames"
    Apply = fun _ (_, _) -> true }

let ownerComparisonInverted : Mutant<(StreamScope * SseFrame) -> bool> =
  { Name = "visibleTo_owner_comparison_inverted"
    Description = "the session it asked for must be the one it gets"
    Apply = fun real (stream, frame) ->
      match stream, frame.Scope with
      | StreamScope.OnlySession _, FrameScope.Session _ -> not (real (stream, frame))
      | _ -> real (stream, frame) }

let firehoseFiltersLikeScoped : Mutant<(StreamScope * SseFrame) -> bool> =
  { Name = "visibleTo_firehose_filtered"
    Description = "a connection with no sessionId is the editors' stream and must keep every frame"
    Apply = fun real (_, frame) ->
      match frame.Scope with
      | FrameScope.Session _ -> real (StreamScope.OnlySession "00000000", frame)
      | FrameScope.Daemon -> real (StreamScope.EverySession, frame) }

let visibleMutants = [
  detectsOutputMutant hideDaemonFromScoped (StreamScope.OnlySession idA, SseFrame.daemon "x") visible id
  detectsOutputMutant scopedSeesEverySession (StreamScope.OnlySession idA, SseFrame.session idB "x") visible not
  detectsOutputMutant ownerComparisonInverted (StreamScope.OnlySession idA, SseFrame.session idA "x") visible id
  detectsOutputMutant ownerComparisonInverted (StreamScope.OnlySession idA, SseFrame.session idB "x") visible not
  detectsOutputMutant firehoseFiltersLikeScoped (StreamScope.EverySession, SseFrame.session idB "x") visible id
]

// ── SseFrame.ofSession ───────────────────────────────────────────────────

let scopeOfOptional (sessionId: string option) = (SseFrame.ofSession sessionId "x").Scope

let blankIdIsASession : Mutant<string option -> FrameScope> =
  { Name = "ofSession_blank_id_is_a_session"
    Description = "no session id (or a blank one) is not any session's news, so a scoped stream must still get it"
    Apply = fun real id ->
      match id with
      | Some blank when System.String.IsNullOrWhiteSpace blank -> FrameScope.Session blank
      | _ -> real id }

let noIdIsASession : Mutant<string option -> FrameScope> =
  { Name = "ofSession_none_is_a_session"
    Description = "an absent id must be the daemon's, never an invented session"
    Apply = fun real id ->
      match id with
      | None -> FrameScope.Session ""
      | _ -> real id }

let ofSessionMutants = [
  detectsOutputMutant blankIdIsASession (Some "   ") scopeOfOptional (fun scope -> scope = FrameScope.Daemon)
  detectsOutputMutant noIdIsASession None scopeOfOptional (fun scope -> scope = FrameScope.Daemon)
]

// ── SseEvent.scope ───────────────────────────────────────────────────────

let sidOf (hex: string) =
  match SessionId.validate hex with
  | Ok id -> id
  | Error e -> failwith e

let hotReloadFiledUnderDaemon : Mutant<SseEvent -> FrameScope> =
  { Name = "scope_hot_reload_changed_is_daemon_wide"
    Description = "B's hot reload must not be pushed to a connection that asked for A"
    Apply = fun real evt ->
      match evt with
      | SseEvent.HotReloadChanged _ -> FrameScope.Daemon
      | _ -> real evt }

let globalEventFiledUnderASession : Mutant<SseEvent -> FrameScope> =
  { Name = "scope_model_changed_is_a_session"
    Description = "a model change is true for the whole daemon, so a scoped stream must still get it"
    Apply = fun real evt ->
      match evt with
      | SseEvent.ModelChanged _ -> FrameScope.Session idA
      | _ -> real evt }

let healthFiledUnderWrongSession : Mutant<SseEvent -> FrameScope> =
  { Name = "scope_health_changed_names_the_wrong_session"
    Description = "a verdict belongs to the session whose id it carries"
    Apply = fun real evt ->
      match evt with
      | SseEvent.SessionHealthChanged _ -> FrameScope.Session idA
      | _ -> real evt }

let scopeMutants = [
  detectsOutputMutant hotReloadFiledUnderDaemon (SseEvent.HotReloadChanged (sidOf idB)) SseEvent.scope (fun scope -> scope = FrameScope.Session idB)
  detectsOutputMutant globalEventFiledUnderASession (SseEvent.ModelChanged(1, 2)) SseEvent.scope (fun scope -> scope = FrameScope.Daemon)
  detectsOutputMutant healthFiledUnderWrongSession (SseEvent.SessionHealthChanged(idB, SessionHealth.Healthy)) SseEvent.scope (fun scope -> scope = FrameScope.Session idB)
]

// ── SseEvent.streamScopeOf ───────────────────────────────────────────────

let isRefusal (result: Result<StreamScope, StreamScopeRefusal>) =
  match result with
  | Error _ -> true
  | Ok _ -> false

let badIdReadAsFirehose : Mutant<string -> Result<StreamScope, StreamScopeRefusal>> =
  { Name = "streamScopeOf_bad_id_is_the_firehose"
    Description = "a mistyped id must be refused, never quietly widened to every session"
    Apply = fun real raw ->
      match real raw with
      | Error _ -> Ok StreamScope.EverySession
      | ok -> ok }

let emptyIsRefused : Mutant<string -> Result<StreamScope, StreamScopeRefusal>> =
  { Name = "streamScopeOf_no_parameter_is_refused"
    Description = "no sessionId is the editors' firehose and must keep working"
    Apply = fun real raw ->
      match raw with
      | "" -> Error (StreamScopeRefusal.NotASessionId "empty")
      | _ -> real raw }

let streamScopeOfMutants = [
  detectsOutputMutant badIdReadAsFirehose "not-a-session" SseEvent.streamScopeOf isRefusal
  detectsOutputMutant emptyIsRefused "" SseEvent.streamScopeOf (fun result -> not (isRefusal result))
]

let eventsStreamScopeMutationTests =
  testList "Events stream scope mutations" [
    testList "visibleTo" visibleMutants
    testList "ofSession" ofSessionMutants
    testList "SseEvent.scope" scopeMutants
    testList "streamScopeOf" streamScopeOfMutants
  ]
