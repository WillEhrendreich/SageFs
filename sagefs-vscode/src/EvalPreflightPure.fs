/// What the eval commands do when there is no session to evaluate in.
///
/// WHY this module exists: with the daemon up and no session bound, the first Alt+Enter on a
/// fresh install ended in an inline red "No active session selected" with no path forward
/// (roast-5 finding 1). The decision is a pure function of the daemon's state and the
/// window's sessions, and each step that stops the eval carries a notice whose button runs
/// a command the extension already registers.
///
/// No Fable dependency; tested under `dotnet fsi` (tests/EvalPreflightContractTests.fsx).
module SageFs.Vscode.EvalPreflightPure

type DaemonState =
  | Down
  | Up

type SessionState =
  /// This window has a session bound.
  | Bound
  /// No session is bound; the daemon lists this many sessions in total.
  | NoneBound of sessionsOnDaemon: int

type EvalStep =
  | Proceed
  | StartDaemon
  | OfferCreateSession
  | OfferCreateOrSwitchSession

type NoticeAction = { Label: string; Command: string }

type Notice = { Text: string; Actions: NoticeAction list }

let nextStep (daemon: DaemonState) (sessions: SessionState) : EvalStep =
  match daemon, sessions with
  | DaemonState.Down, _ -> EvalStep.StartDaemon
  | DaemonState.Up, SessionState.Bound -> EvalStep.Proceed
  | DaemonState.Up, SessionState.NoneBound 0 -> EvalStep.OfferCreateSession
  | DaemonState.Up, SessionState.NoneBound _ -> EvalStep.OfferCreateOrSwitchSession

let createAction = { Label = "Create Session"; Command = "sagefs.createSession" }

/// A no-session message from a command other than eval, with the two buttons that fix it. The caller
/// keeps its own words; the buttons are the same ones the eval notice offers.
let noSessionNotice (text: string) : Notice =
  { Text = text
    Actions = [ createAction; { Label = "Switch Session"; Command = "sagefs.switchSession" } ] }

/// The notice that replaces the dead end, for the steps that stop the eval.
let notice (step: EvalStep) : Notice option =
  match step with
  | EvalStep.Proceed | EvalStep.StartDaemon -> None
  | EvalStep.OfferCreateSession ->
    Some { Text = "SageFs has no session for this workspace yet."; Actions = [ createAction ] }
  | EvalStep.OfferCreateOrSwitchSession ->
    Some
      { Text = "No SageFs session is selected for this workspace."
        Actions = [ createAction; { Label = "Switch Session"; Command = "sagefs.switchSession" } ] }
