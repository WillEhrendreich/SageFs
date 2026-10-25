namespace SageFs

open SageFs.Features
open SageFs.Features.LiveTesting

/// Reads of live-testing state for the surfaces that show it, each answering about the right
/// thing.
///
/// A daemon holds one live-testing cycle per session: the primary session's (`model.LiveTesting`)
/// and one for every other session (`model.PerSessionLiveTesting`). Reading `model.LiveTesting`
/// answers for the primary session only. That is the right answer to a question about the
/// primary session and the wrong one to two others:
///   * what the client is LOOKING AT, which is the session it views, primary or not (the
///     dashboard header's status bar and its failure narratives);
///   * what the DAEMON is doing, which is every session's cycle together (the health summary).
/// A session with live testing on while the primary has it off was reported as off, and its
/// failures as none, by the reads that asked the primary.
///
/// Pure: model in, answer out. The daemon wires these to the Elm runtime's current model.
module LiveTestViewReads =

  /// The cycle of the session being looked at.
  let cycleOf (viewing: WorkerProtocol.SessionId) (model: SageFsModel) : LiveTestCycleState =
    SageFsModel.cycleForSession (WorkerProtocol.SessionId.value viewing) model

  /// The failure narratives of the viewed session's tests, with each test's display name. Empty
  /// while that session has live testing off.
  let failureNarrativesFor
    (viewing: WorkerProtocol.SessionId)
    (model: SageFsModel)
    : (string * FailureNarrative) list =
    let state = (cycleOf viewing model).TestState
    match state.Activation with
    | LiveTestingActivation.Inactive -> []
    | LiveTestingActivation.Active ->
      state.Cached.FailureNarratives
      |> Map.toList
      |> List.choose (fun (testId, narrative) ->
        state.DiscoveredTests
        |> Array.tryFind (fun tc -> tc.Id = testId)
        |> Option.map (fun tc -> tc.DisplayName, narrative))

  /// The status-bar text of the viewed session: its own rebuild, timing, test summary and
  /// decision hint.
  let statusBarFor (viewing: WorkerProtocol.SessionId) (model: SageFsModel) : string =
    LiveTestCycleState.liveTestingStatusBarForSession
      (WorkerProtocol.SessionId.value viewing)
      (cycleOf viewing model)

  /// What the daemon as a whole is doing with live testing.
  [<RequireQualifiedAccess>]
  type DaemonLiveTesting =
    /// No session has live testing on.
    | NoSessionHasItOn
    /// At least one session has it on and none has a test yet.
    | OnWithNoTestsYet
    /// The tests of every session that has it on, added together.
    | Totals of LiveTestHealthSummary

  /// Every cycle the daemon holds: the primary session's and each other session's.
  let allCycles (model: SageFsModel) : LiveTestCycleState list =
    model.LiveTesting :: (model.PerSessionLiveTesting |> Map.toList |> List.map snd)

  /// The daemon-wide live-testing summary: the sessions that have it on, together. A session
  /// that has it off adds nothing, whichever session is primary.
  let daemonLiveTesting (model: SageFsModel) : DaemonLiveTesting =
    let withItOn =
      allCycles model
      |> List.filter (fun cycle ->
        match cycle.TestState.Activation with
        | LiveTestingActivation.Active -> true
        | LiveTestingActivation.Inactive -> false)
    match withItOn with
    | [] -> DaemonLiveTesting.NoSessionHasItOn
    | _ ->
      let statuses =
        withItOn
        |> List.collect (fun cycle ->
          LiveTestState.statusEntriesForSession "" cycle.TestState
          |> Array.map (fun entry -> entry.Status)
          |> Array.toList)
      match statuses with
      | [] -> DaemonLiveTesting.OnWithNoTestsYet
      | _ ->
        let summary = TestSummary.fromStatuses LiveTestingActivation.Active (Array.ofList statuses)
        DaemonLiveTesting.Totals
          { TotalTests = summary.Total
            Passed = summary.Passed
            Failed = summary.Failed
            Running = summary.Running }

  /// One session's live-testing activity, as every surface shows it.
  type SessionActivity = LiveTestActivity.LiveTestActivity

  let addTallies(a: LiveTestActivity.TestTally) (b: LiveTestActivity.TestTally) : LiveTestActivity.TestTally =
    { Passed = a.Passed + b.Passed
      Failed = a.Failed + b.Failed
      Running = a.Running + b.Running
      Stale = a.Stale + b.Stale
      NotYetRun = a.NotYetRun + b.NotYetRun
      Skipped = a.Skipped + b.Skipped
      Disabled = a.Disabled + b.Disabled }

  /// The tests a session's activity reports, none when it reports no tests.
  let tallyOfActivity (activity: SessionActivity) : LiveTestActivity.TestTally =
    match activity with
    | SessionActivity.Off
    | SessionActivity.Discovering
    | SessionActivity.DiscoveryFailed _
    | SessionActivity.NoTestsFound _ -> LiveTestActivity.TestTally.empty
    | SessionActivity.BlockedByCompileErrors (_, _, tally) -> tally
    | SessionActivity.BlockedByFailedRebuild (_, tally) -> tally
    | SessionActivity.Rebuilding (_, tally) -> tally
    | SessionActivity.Running tally -> tally
    | SessionActivity.Settled tally -> tally

  /// How much a session's activity has to say to someone looking at the whole daemon. The order
  /// is `LiveTestActivity.decide`'s: states about tests that are known outrank states about
  /// finding them, and a held-back run (compile errors, a rebuild) outranks a quiet one.
  let activityPrecedence (activity: SessionActivity) : int =
    match activity with
    | SessionActivity.Off -> 0
    | SessionActivity.NoTestsFound _ -> 1
    | SessionActivity.DiscoveryFailed _ -> 2
    | SessionActivity.Discovering -> 3
    | SessionActivity.Settled _ -> 4
    | SessionActivity.Running _ -> 5
    | SessionActivity.BlockedByFailedRebuild _ -> 6
    | SessionActivity.Rebuilding _ -> 7
    | SessionActivity.BlockedByCompileErrors _ -> 8

  let testsBeingRebuilt(activity: SessionActivity) : int =
    match activity with
    | SessionActivity.Rebuilding (testCount, _) -> testCount
    | SessionActivity.Off
    | SessionActivity.Discovering
    | SessionActivity.DiscoveryFailed _
    | SessionActivity.NoTestsFound _
    | SessionActivity.BlockedByCompileErrors _
    | SessionActivity.BlockedByFailedRebuild _
    | SessionActivity.Running _
    | SessionActivity.Settled _ -> 0

  let frameworksDetected(activity: SessionActivity) : string list =
    match activity with
    | SessionActivity.NoTestsFound frameworks -> frameworks
    | SessionActivity.Off
    | SessionActivity.Discovering
    | SessionActivity.DiscoveryFailed _
    | SessionActivity.BlockedByCompileErrors _
    | SessionActivity.BlockedByFailedRebuild _
    | SessionActivity.Rebuilding _
    | SessionActivity.Running _
    | SessionActivity.Settled _ -> []

  /// The one activity of the daemon as a whole, from the activity of each session. A session with
  /// live testing off adds nothing, so a primary with it off cannot hide a session that is running
  /// tests. The tests of the sessions that have it on are added together, and the most pressing
  /// state among them leads (see `activityPrecedence`). With none on, live testing is off.
  let rollUpActivity (activities: SessionActivity list) : SessionActivity =
    let withItOn = activities |> List.filter (fun activity -> activityPrecedence activity > 0)
    match withItOn with
    | [] -> SessionActivity.Off
    | _ ->
      let tally = withItOn |> List.map tallyOfActivity |> List.fold addTallies LiveTestActivity.TestTally.empty
      match List.maxBy activityPrecedence withItOn with
      | SessionActivity.Off -> SessionActivity.Off
      | SessionActivity.NoTestsFound _ ->
        SessionActivity.NoTestsFound (withItOn |> List.collect frameworksDetected |> List.distinct)
      | SessionActivity.DiscoveryFailed reason -> SessionActivity.DiscoveryFailed reason
      | SessionActivity.Discovering -> SessionActivity.Discovering
      | SessionActivity.Settled _ -> SessionActivity.Settled tally
      | SessionActivity.Running _ -> SessionActivity.Running tally
      | SessionActivity.BlockedByFailedRebuild (reason, _) -> SessionActivity.BlockedByFailedRebuild (reason, tally)
      | SessionActivity.Rebuilding _ -> SessionActivity.Rebuilding (withItOn |> List.sumBy testsBeingRebuilt, tally)
      | SessionActivity.BlockedByCompileErrors (file, errorCount, _) ->
        SessionActivity.BlockedByCompileErrors (file, errorCount, tally)
