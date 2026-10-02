/// What an analysis tool can honestly say: a measurement, or that it cannot know yet and why.
///
/// A tool that answers with an empty list when it has nothing to read is telling the agent
/// "zero". An agent (or a policy engine) that reads zero as "measured, nothing there" acts on
/// a number nobody counted. So the tools answer `Measured` only when the data was there, and
/// `NotAvailable` with a reason otherwise. A `Measured` empty list is a real zero.
module SageFs.Features.ToolAnswers

open SageFs.Features.LiveTesting

/// The workflows a session can run in, as an answer names them.
[<RequireQualifiedAccess>]
type WorkflowKind =
  | Interactive
  | LiveTesting
  | HotReload

module WorkflowKind =
  let ofSession (workflow: SageFs.WorkflowTypes.SessionWorkflow) : WorkflowKind =
    match workflow with
    | SageFs.WorkflowTypes.SessionWorkflow.Interactive -> WorkflowKind.Interactive
    | SageFs.WorkflowTypes.SessionWorkflow.LiveTesting -> WorkflowKind.LiveTesting
    | SageFs.WorkflowTypes.SessionWorkflow.HotReload _ -> WorkflowKind.HotReload

  let token (kind: WorkflowKind) : string =
    match kind with
    | WorkflowKind.Interactive -> "Interactive"
    | WorkflowKind.LiveTesting -> "LiveTesting"
    | WorkflowKind.HotReload -> "HotReload"

  /// The word `switch_workflow` (as `target`) and the create tools (as `workflow`) take for this workflow.
  let switchWord (kind: WorkflowKind) : string =
    match kind with
    | WorkflowKind.Interactive -> "interactive"
    | WorkflowKind.LiveTesting -> "livetesting"
    | WorkflowKind.HotReload -> "hotreload"

/// Why a session cannot take a call right now, when the cause is the session and not the tool.
[<RequireQualifiedAccess>]
type SessionReadiness =
  | WarmingUp
  | Faulted
  | NotRoutable

module SessionReadiness =
  let token (readiness: SessionReadiness) : string =
    match readiness with
    | SessionReadiness.WarmingUp -> "WarmingUp"
    | SessionReadiness.Faulted -> "Faulted"
    | SessionReadiness.NotRoutable -> "NotRoutable"

/// Every reason an analysis tool can have for not answering. Closed on purpose: a reason an
/// agent can branch on is a case here, with one token, one sentence and one action.
[<RequireQualifiedAccess>]
type NotAvailableReason =
  /// The call named no session and none could be found for the caller. `why` is the routing
  /// message (no session, several sessions in that directory, ...).
  | NoSessionResolved of why: string
  /// The session exists but cannot answer yet.
  | SessionNotReady of SessionReadiness
  /// The session has recorded no evals, so there is no cell history to read.
  | NoEvalsYet
  /// No test result is recorded for this session: nothing has run, so nothing is known about its tests.
  | NoTestRunYet
  /// Neither an eval nor a test result is recorded for this session.
  | NothingObservedYet
  /// The cell ids asked about are not in this session's history.
  | CellsNotInHistory of cellIds: int list
  /// The call carried no cell id that could be read, or one that could not.
  | NoUsableCellIds
  /// The binding asked about is not bound in this session.
  | BindingNotInScope of name: string
  /// The data only exists in a workflow this session is not in.
  | NeedsAWorkflow of required: WorkflowKind
  /// The session is in the right workflow but live testing is switched off, and coverage only
  /// comes from live testing's instrumented runs.
  | NeedsLiveTesting
  /// Live testing is on, but no instrumented run has recorded coverage for this session yet.
  | NoCoverageRecorded

module NotAvailableReason =
  /// The wire token for the reason, one per case.
  let token (reason: NotAvailableReason) : string =
    match reason with
    | NotAvailableReason.NoSessionResolved _ -> "NoSessionResolved"
    | NotAvailableReason.SessionNotReady _ -> "SessionNotReady"
    | NotAvailableReason.NoEvalsYet -> "NoEvalsYet"
    | NotAvailableReason.NoTestRunYet -> "NoTestRunYet"
    | NotAvailableReason.NothingObservedYet -> "NothingObservedYet"
    | NotAvailableReason.CellsNotInHistory _ -> "CellsNotInHistory"
    | NotAvailableReason.NoUsableCellIds -> "NoUsableCellIds"
    | NotAvailableReason.BindingNotInScope _ -> "BindingNotInScope"
    | NotAvailableReason.NeedsAWorkflow _ -> "NeedsAWorkflow"
    | NotAvailableReason.NeedsLiveTesting -> "NeedsLiveTesting"
    | NotAvailableReason.NoCoverageRecorded -> "NoCoverageRecorded"

  /// One plain sentence: what is missing.
  let describe (reason: NotAvailableReason) : string =
    match reason with
    | NotAvailableReason.NoSessionResolved why ->
      sprintf "No session could be found for this call, so there is nothing to read. %s" why
    | NotAvailableReason.SessionNotReady readiness ->
      sprintf "The session is not ready to answer (%s)." (SessionReadiness.token readiness)
    | NotAvailableReason.NoEvalsYet ->
      "This session has recorded no evals, so there is no cell history to analyse."
    | NotAvailableReason.NoTestRunYet ->
      "No test result is recorded for this session, so nothing is known about its tests."
    | NotAvailableReason.NothingObservedYet ->
      "This session has recorded no evals and no test results, so there is nothing to diagnose."
    | NotAvailableReason.CellsNotInHistory ids ->
      sprintf "Cell %s not in this session's eval history." (ids |> List.map string |> String.concat ", ")
    | NotAvailableReason.NoUsableCellIds ->
      "No usable cell id was given: expected comma-separated integers."
    | NotAvailableReason.BindingNotInScope name ->
      sprintf "'%s' is not bound in this session." name
    | NotAvailableReason.NeedsAWorkflow required ->
      sprintf "This data is recorded by the %s workflow, and this session is not in it." (WorkflowKind.token required)
    | NotAvailableReason.NeedsLiveTesting ->
      "Coverage comes from live testing's instrumented runs, and live testing is switched off for this session."
    | NotAvailableReason.NoCoverageRecorded ->
      "Live testing is on, but no instrumented test run has recorded coverage for this session yet."

  /// One action an agent can take to make the answer available.
  let whatToDo (reason: NotAvailableReason) : string =
    match reason with
    | NotAvailableReason.NoSessionResolved _ ->
      "Pass session_id (see list_sessions) or working_directory, or create a session with create_project_session or create_bare_session."
    | NotAvailableReason.SessionNotReady _ ->
      "Call get_session_status with wait_seconds=60 and retry once it reports Ready. Do not create a second session."
    | NotAvailableReason.NoEvalsYet ->
      "Evaluate something with send_fsharp_code in this session, then ask again."
    | NotAvailableReason.NoTestRunYet ->
      "Call run_tests for this session, then ask again."
    | NotAvailableReason.NothingObservedYet ->
      "Evaluate something with send_fsharp_code or call run_tests for this session, then ask again."
    | NotAvailableReason.CellsNotInHistory _ ->
      "Read the cell ids from get_cell_dependencies and pass ids it lists."
    | NotAvailableReason.NoUsableCellIds ->
      "Pass changed_cells as comma-separated integers, for example '0,2'."
    | NotAvailableReason.BindingNotInScope _ ->
      "Evaluate the binding in this session first, or check the name against get_cell_dependencies."
    | NotAvailableReason.NeedsAWorkflow required ->
      sprintf "Call switch_workflow with target='%s', which opens a new session in that workflow (use its session_id from then on), then call run_tests." (WorkflowKind.switchWord required)
    | NotAvailableReason.NeedsLiveTesting ->
      "Turn live testing back on from the dashboard or an editor (there is no MCP tool for it), then run_tests."
    | NotAvailableReason.NoCoverageRecorded ->
      "Call run_tests so live testing records an instrumented run, then ask again."

/// Something an analysis tool can leave out of a measurement because it could not read it.
[<RequireQualifiedAccess>]
type UnmeasuredScope =
  | Cells
  | Tests

module UnmeasuredScope =
  let token (scope: UnmeasuredScope) : string =
    match scope with
    | UnmeasuredScope.Cells -> "Cells"
    | UnmeasuredScope.Tests -> "Tests"

/// The body of a measurement: JSON text, or prose for the tools that answer in sentences.
[<RequireQualifiedAccess>]
type Rendered =
  | Json of string
  | Prose of string

/// A measurement, and any part of the question it did not measure.
type Measurement =
  { Body: Rendered
    Unmeasured: UnmeasuredScope list }

/// An analysis tool's answer: it measured, or it says why it could not.
[<RequireQualifiedAccess>]
type ToolAnswer<'payload> =
  | Measured of 'payload
  | NotAvailable of NotAvailableReason

module ToolAnswer =
  /// The wire token for the answer's kind.
  let token (answer: ToolAnswer<'payload>) : string =
    match answer with
    | ToolAnswer.Measured _ -> "Measured"
    | ToolAnswer.NotAvailable _ -> "NotAvailable"

  let measured (body: Rendered) : ToolAnswer<Measurement> =
    ToolAnswer.Measured { Body = body; Unmeasured = [] }

/// The plain sentence for a call that could not be answered: what is missing, then what to do.
let notAvailableText (reason: NotAvailableReason) : string =
  sprintf "Not available (%s). %s %s" (NotAvailableReason.token reason) (NotAvailableReason.describe reason) (NotAvailableReason.whatToDo reason)

/// Whether live testing has recorded coverage for a session.
[<RequireQualifiedAccess>]
type CoverageEvidence =
  | Recorded
  | NotRecorded

/// What each analysis tool needs before it may say anything, as pure decisions. A `Result` so
/// a missing input is an `Error` with its reason and the caller cannot read past it.
module Readiness =
  /// Cell-history tools need at least one recorded eval.
  let evalsRecorded (evalCount: int) : Result<unit, NotAvailableReason> =
    match evalCount > 0 with
    | true -> Ok ()
    | false -> Error NotAvailableReason.NoEvalsYet

  /// Test-reading tools need at least one recorded test result.
  let testsRecorded (resultCount: int) : Result<unit, NotAvailableReason> =
    match resultCount > 0 with
    | true -> Ok ()
    | false -> Error NotAvailableReason.NoTestRunYet

  /// `diagnose` reads cells and tests. It may answer when either was observed, and it names the
  /// side it could not read. When neither was, it has nothing to say.
  let diagnosable (evalCount: int) (resultCount: int) : Result<UnmeasuredScope list, NotAvailableReason> =
    match evalCount > 0, resultCount > 0 with
    | true, true -> Ok []
    | true, false -> Ok [ UnmeasuredScope.Tests ]
    | false, true -> Ok [ UnmeasuredScope.Cells ]
    | false, false -> Error NotAvailableReason.NothingObservedYet

  /// `coverage_intel` reads per-test coverage, which only live testing's instrumented runs
  /// produce. Without recorded coverage the reason says which switch is missing.
  let coverageRecorded
    (activation: LiveTestingActivation)
    (workflow: WorkflowKind)
    (evidence: CoverageEvidence)
    : Result<unit, NotAvailableReason> =
    match evidence, activation, workflow with
    | CoverageEvidence.Recorded, _, _ -> Ok ()
    | CoverageEvidence.NotRecorded, LiveTestingActivation.Active, _ -> Error NotAvailableReason.NoCoverageRecorded
    | CoverageEvidence.NotRecorded, LiveTestingActivation.Inactive, WorkflowKind.LiveTesting -> Error NotAvailableReason.NeedsLiveTesting
    | CoverageEvidence.NotRecorded, LiveTestingActivation.Inactive, _ -> Error (NotAvailableReason.NeedsAWorkflow WorkflowKind.LiveTesting)
