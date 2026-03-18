module SageFs.Tests.LiveTestWatcherScopeTests

/// Behavioral tests for per-session file watcher scope (Stream 1) and
/// session watcher lifecycle (Stream 4).
///
/// Stream 1 tests 1–4 all document passing contracts:
///   1. Single-session baseline: file change populates debounce channel.
///   2. Non-active session isolation: B's file change routes to
///      PerSessionLiveTesting, leaving A's primary state untouched.
///   3. Multi-session independence: each session tracks its own changes.
///   4. Debounce coalescing: N rapid saves within 200ms produce exactly
///      one pending operation.
///
/// Stream 4 tests verify watcher lifecycle (created/disposed with session).

open System
open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

// ── helpers ─────────────────────────────────────────────────────────

/// Build a minimal SessionSnapshot for a given session id + working dir.
let private mkSession (idStr: string) (workingDir: string) : SessionSnapshot =
  { Id = WorkerProtocol.SessionId.newId () // placeholder — overridden below
    Name = Some idStr
    Projects = [ sprintf "%s/MyProj.fsproj" workingDir ]
    Status = SessionDisplayStatus.Running
    LastActivity = DateTime.UtcNow
    EvalCount = 0
    UpSince = DateTime.UtcNow
    IsActive = true
    WorkingDirectory = workingDir }

/// Inject a session into the initial model by dispatching SessionCreated.
let private withSession (snap: SessionSnapshot) (model: SageFsModel) =
  let model', _ =
    SageFsUpdate.update
      (SageFsMsg.Event (SageFsEvent.SessionCreated snap))
      model
  model'

/// Activate live testing on the model.
let private withLiveTesting (model: SageFsModel) =
  let model', _ =
    SageFsUpdate.update SageFsMsg.EnableLiveTesting model
  model'

let private unionCaseNameOf (value: obj) =
  let ty = value.GetType()
  let case, _ = FSharpValue.GetUnionFields(value, ty)
  case.Name

let private makeSessionAwareMsg (caseName: string) (sessionId: string option) (payloads: obj array) : SageFsMsg =
  let case =
    FSharpType.GetUnionCases(typeof<SageFsMsg>)
    |> Array.find (fun c -> c.Name = caseName)
  let fields = case.GetFields()
  fields.Length
  |> Expect.equal
      (sprintf "%s should carry a session id plus payload fields" caseName)
      (1 + payloads.Length)
  FSharpValue.MakeUnion(case, Array.append [| box sessionId |] payloads) :?> SageFsMsg

// =====================================================================
// Stream 1 — Per-Session File Watcher Scope
// =====================================================================

[<Tests>]
let stream1Tests =
  testList "Stream 1 — Per-Session File Watcher Scope" [

    // ── Test 1 ──────────────────────────────────────────────────────
    // WHY: The current bug is that the watcher fires for daemon CWD,
    // not project dirs.  After the fix, changes in
    // session.WorkingDir must produce effects — the Elm model's
    // debounce channel should be populated for the session-scoped
    // file path.
    test "file change in session directory dispatches FileContentChanged for that session" {
      let sessionDir = "/users/alice/projects/my-app"
      let snap = mkSession "session-a" sessionDir
      let model =
        SageFsModel.initial ()
        |> withSession snap
        |> withLiveTesting

      let filePath = sprintf "%s/src/Domain.fs" sessionDir
      let model', _effects =
        SageFsUpdate.update
          (SageFsMsg.FileContentChanged (filePath, "let x = 1"))
          model

      // After the fix the message must carry session context so the
      // debounce channel populates for this file path.
      // Currently FileContentChanged has no sessionId — so the model
      // cannot distinguish WHICH session the file belongs to.
      //
      // RED: FileContentChanged currently lacks a sessionId field.
      // The test asserts that the ActiveFile on LiveTesting matches
      // and that debounce was populated — which it does today for a
      // single session.  The real RED assertion is test 2 below, but
      // this test validates the baseline contract: that a file in the
      // session directory actually triggers debounce.
      model'.LiveTesting.ActiveFile
      |> Expect.equal
          "ActiveFile should be the session-scoped path"
          (Some filePath)

      model'.LiveTesting.Debounce.Fcs.Pending
      |> Expect.isSome
          "FCS debounce channel should have a pending op for the changed file"
    }

    // ── Test 2 ──────────────────────────────────────────────────────
    // WHY: Without per-session routing, ANY file change pollutes the
    // single global LiveTestCycleState.  Session A's rebuild fires when
    // Session B edits a file in a completely different project.  After
    // the fix, Session B's change must be isolated in
    // PerSessionLiveTesting and leave Session A's primary state entirely
    // untouched — including its ActiveFile (debounce channel state).
    //
    // NOTE: FileContentChanged only populates the debounce channel;
    // TestCycleEffects are emitted only when TestCycleTick fires.  This
    // test therefore asserts on model state, not on returned effects.
    test "file change in non-active session directory routes to per-session state without touching primary LiveTesting" {
      let snapA = mkSession "session-a" "/projects/alpha"  // created first → becomes active
      let snapB = mkSession "session-b" "/projects/beta"   // created second → background
      let model =
        SageFsModel.initial ()
        |> withSession snapA
        |> withSession snapB
        |> withLiveTesting

      let fileB = "/projects/beta/src/Lib.fs"
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.FileContentChanged (fileB, "module Lib"))
          model

      // Session A is the active session.  Session B's file change must NOT
      // pollute A's primary LiveTesting debounce state.
      model'.LiveTesting.ActiveFile
      |> Expect.isNone
          "primary LiveTesting (session A) should be untouched by session B's file change"

      // Session B's change should instead be routed to PerSessionLiveTesting.
      let bSid = WorkerProtocol.SessionId.value snapB.Id
      model'.PerSessionLiveTesting
      |> Map.tryFind bSid
      |> Option.bind (fun c -> c.ActiveFile)
      |> Expect.equal
          "session B's file change should route to PerSessionLiveTesting"
          (Some fileB)
    }

    // ── Test 3 ──────────────────────────────────────────────────────
    // WHY: Per-session isolation.  Session A's file change should NOT
    // trigger Session B's test cycle.
    test "multiple sessions each track their own file changes independently" {
      let snapA = mkSession "session-a" "/projects/alpha"
      let snapB = mkSession "session-b" "/projects/beta"
      let model =
        SageFsModel.initial ()
        |> withSession snapA
        |> withSession snapB
        |> withLiveTesting

      // File change only in session A's directory
      let fileA = "/projects/alpha/src/Domain.fs"
      let model', _ =
        SageFsUpdate.update
          (SageFsMsg.FileContentChanged (fileA, "module Domain"))
          model

      // RED: Today there is ONE global LiveTestCycleState.  After the
      // fix, each session should have its own debounce/cycle state so
      // that a change in A's directory does not affect B.
      //
      // We need either:
      //   model.PerSessionLiveTesting : Map<string, LiveTestCycleState>
      // or a session filter on the existing state.
      //
      // The assertion below checks that the global debounce was NOT
      // populated when the file is outside session B's directory.
      // Today it WILL be populated (global state), so this test is RED.

      // First: session A should see the change
      model'.LiveTesting.ActiveFile
      |> Expect.equal
          "session A should see the file change"
          (Some fileA)

      // Second: session B should NOT be affected.
      // RED: Today there is no per-session tracking — the single
      // global LiveTesting state treats all changes identically.
      // After the fix, querying session B's cycle state should show
      // no pending debounce.
      //
      // We test this by checking that the Fcs debounce payload
      // references a file in session A's dir, not B's.
      // Then we send a change for B and verify A's state is untouched.
      let fileB = "/projects/beta/src/Other.fs"
      let model'', _ =
        SageFsUpdate.update
          (SageFsMsg.FileContentChanged (fileB, "module Other"))
          model'

      // After the fix, session A's debounce should still reference
      // fileA (not overwritten by fileB).
      // GREEN: PerSessionLiveTesting routing keeps B's change isolated.
      model''.LiveTesting.ActiveFile
      |> Expect.notEqual
          "session A's active file should not be overwritten by session B's change"
          (Some fileB)

      // Session B's isolated change must be tracked in PerSessionLiveTesting.
      let bSid = WorkerProtocol.SessionId.value snapB.Id
      model''.PerSessionLiveTesting
      |> Map.tryFind bSid
      |> Option.bind (fun c -> c.ActiveFile)
      |> Expect.equal
          "session B's file change should be tracked independently in PerSessionLiveTesting"
          (Some fileB)
    }

    // ── Test 4 ──────────────────────────────────────────────────────
    // WHY: N saves in 200ms = 1 pipeline trigger, not N.
    test "debounce coalesces rapid changes within 200ms window" {
      let snap = mkSession "session-a" "/projects/alpha"
      let model =
        SageFsModel.initial ()
        |> withSession snap
        |> withLiveTesting

      let filePath = "/projects/alpha/src/Lib.fs"
      let baseTime = DateTimeOffset.UtcNow

      // Simulate 5 rapid changes within 200ms
      let model' =
        [ 0.0; 40.0; 80.0; 120.0; 160.0 ]
        |> List.fold (fun m offset ->
          let content = sprintf "version-%g" offset
          // Use onKeystroke directly on the cycle state so we can
          // control timestamps (SageFsUpdate.update uses UtcNow)
          let cycle' =
            m.LiveTesting
            |> LiveTestCycleState.onKeystroke
                content filePath
                (baseTime.AddMilliseconds offset)
          { m with LiveTesting = cycle' }
        ) model

      // The debounce generation should reflect coalescing: the
      // generation counter increments on each submit, but only
      // the LATEST payload should be pending (previous ones are stale).
      model'.LiveTesting.Debounce.Fcs.Pending
      |> Expect.isSome
          "debounce should have a pending FCS op after rapid changes"

      let pendingOp = model'.LiveTesting.Debounce.Fcs.Pending |> Option.get

      // The pending payload should be the LAST file path submitted
      pendingOp.Payload
      |> Expect.equal
          "pending payload should be the file path (last wins)"
          filePath

      // Generation should be 5 (one per keystroke)
      model'.LiveTesting.Debounce.Fcs.CurrentGeneration
      |> Expect.equal
          "generation counter should reflect all 5 submits"
          5L

      // The pending op's generation should equal CurrentGeneration
      // (meaning it's the latest, not stale)
      pendingOp.Generation
      |> Expect.equal
          "pending op generation should match current (not stale)"
          model'.LiveTesting.Debounce.Fcs.CurrentGeneration

      // Tick BEFORE the debounce window elapses → nothing fires
      let noFireEffects, cycleNoFire =
        model'.LiveTesting
        |> LiveTestCycleState.tick (baseTime.AddMilliseconds 200.0)
      noFireEffects
      |> Expect.isEmpty
          "tick within debounce window should not fire effects"

      // Tick AFTER the debounce window elapses → FCS request fires
      // The FCS default delay is adaptive (~200ms initial), so we
      // advance well past it.
      let fireEffects, _cycleFired =
        model'.LiveTesting
        |> LiveTestCycleState.tick (baseTime.AddMilliseconds 600.0)
      fireEffects
      |> Expect.isNonEmpty
          "tick after debounce window should fire FCS type-check effect"
    }
  ]

// =====================================================================
// Stream 4 — Session Watcher Lifecycle
// =====================================================================

[<Tests>]
let stream4Tests =
  testList "Session Watcher Lifecycle" [

    // ── Test 5 ──────────────────────────────────────────────────────
    // WHY: Watchers consume OS handles — only create them for sessions
    // that actually need live testing.  When a session is created and
    // live testing is enabled, the model/effects should indicate that
    // a file watcher needs to be registered for that session's dir.
    test "watcher registration effect emitted when session with live testing created" {
      let snap = mkSession "session-a" "/projects/alpha"
      let model =
        SageFsModel.initial ()
        |> withSession snap

      let model', effects =
        SageFsUpdate.update SageFsMsg.EnableLiveTesting model

      // RED: Today EnableLiveTesting does not emit any effect to
      // register a per-session file watcher.  It only activates the
      // TestState and emits RequestInitialDiscovery.
      //
      // After the fix, enabling live testing should produce an effect
      // that tells the effect handler to create a FileSystemWatcher
      // for each running session's WorkingDirectory.
      let hasWatcherRegistration =
        effects
        |> List.exists (fun e ->
          match e with
          | SageFsEffect.TestCycle (TestCycleEffect.RegisterFileWatcher _) -> true
          | _ -> false)

      // Also verify that the model tracks which sessions have watchers.
      // RED: No such tracking exists today.
      model'.LiveTesting.TestState.Activation
      |> Expect.equal
          "live testing should be activated"
          LiveTestingActivation.Active

      // This is the RED assertion: we expect an effect for watcher
      // registration, but no such effect variant exists yet.
      hasWatcherRegistration
      |> Expect.isTrue
          "EnableLiveTesting should emit a watcher registration effect for session directories (not yet implemented)"
    }

    // ── Test 6 ──────────────────────────────────────────────────────
    // WHY: Leaked watchers for stopped sessions dispatch events to
    // nowhere, wasting OS handles and causing spurious rebuilds.
    test "watcher disposal effect emitted when session removed" {
      let snap = mkSession "session-a" "/projects/alpha"
      let model =
        SageFsModel.initial ()
        |> withSession snap
        |> withLiveTesting

      // Remove the session
      let sessionId = WorkerProtocol.SessionId.value snap.Id
      let _model', effects =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.SessionStopped sessionId))
          model

      // RED: Today SessionStopped clears TestSessionMap entries but
      // does NOT emit an effect to dispose the file watcher for that
      // session's directory.
      //
      // After the fix, SessionStopped should produce an effect like:
      //   TestCycleEffect.DisposeFileWatcher of sessionId
      let hasWatcherDisposal =
        effects
        |> List.exists (fun e ->
          match e with
          | SageFsEffect.TestCycle (TestCycleEffect.DisposeFileWatcher _) -> true
          | _ -> false)

      hasWatcherDisposal
      |> Expect.isTrue
          "SessionStopped should emit a watcher disposal effect (not yet implemented)"
    }

    // ── Test 7 ──────────────────────────────────────────────────────
    // WHY: A restarted session may have different project directories
    // (e.g. the user changed the --proj flag).  The old watcher must
    // be disposed and a new one registered for the updated dirs.
    test "session restart disposes old watcher and registers new one" {
      let snapOld = mkSession "session-a" "/projects/alpha-v1"
      let model =
        SageFsModel.initial ()
        |> withSession snapOld
        |> withLiveTesting

      // Simulate session restart: SessionStopped followed by
      // SessionCreated with a potentially different working directory
      let sessionId = WorkerProtocol.SessionId.value snapOld.Id
      let modelAfterStop, stopEffects =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.SessionStopped sessionId))
          model

      let snapNew =
        { snapOld with
            WorkingDirectory = "/projects/alpha-v2"
            IsActive = true }

      let _modelAfterCreate, createEffects =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.SessionCreated snapNew))
          modelAfterStop

      let allEffects = stopEffects @ createEffects

      // RED: Today neither SessionStopped nor SessionCreated emit
      // watcher lifecycle effects.
      //
      // After the fix:
      //   1. SessionStopped emits DisposeFileWatcher for old dir
      //   2. SessionCreated (when live testing is active) emits
      //      RegisterFileWatcher for new dir
      let hasDisposal =
        allEffects
        |> List.exists (fun e ->
          match e with
          | SageFsEffect.TestCycle (TestCycleEffect.DisposeFileWatcher _) -> true
          | _ -> false)

      let hasRegistration =
        allEffects
        |> List.exists (fun e ->
          match e with
          | SageFsEffect.TestCycle (TestCycleEffect.RegisterFileWatcher _) -> true
          | _ -> false)

      hasDisposal
      |> Expect.isTrue
          "session restart should dispose old watcher (not yet implemented)"

      hasRegistration
      |> Expect.isTrue
          "session restart should register new watcher for updated directory (not yet implemented)"
    }

    test "session becoming running after live testing enable registers watcher" {
      let snap =
        { (mkSession "session-a" "/projects/alpha") with
            Status = SessionDisplayStatus.Starting }

      let model =
        SageFsModel.initial ()
        |> withSession snap
        |> withLiveTesting

      let _model', effects =
        SageFsUpdate.update
          (SageFsMsg.Event (
            SageFsEvent.SessionStatusChanged (
              WorkerProtocol.SessionId.value snap.Id,
              SessionDisplayStatus.Running)))
          model

      let hasWatcherRegistration =
        effects
        |> List.exists (fun e ->
          match e with
          | SageFsEffect.TestCycle (TestCycleEffect.RegisterFileWatcher _) -> true
          | _ -> false)

      hasWatcherRegistration
      |> Expect.isTrue
          "when live testing is already enabled, a session transitioning to running should register its watcher"
    }
  ]

// =====================================================================
// Stream 6 — Session-Scoped Buffer Changes
// =====================================================================

[<Tests>]
let stream6Tests =
  testList "Stream 6 — Session-Scoped Buffer Changes" [

    test "background session buffer change updates only background live-testing state with keystroke semantics" {
      let snapA = mkSession "session-a" "/projects/alpha"
      let snapB = mkSession "session-b" "/projects/beta"
      let bSid = WorkerProtocol.SessionId.value snapB.Id
      let fileB = "/projects/beta/src/Lib.fs"
      let model =
        SageFsModel.initial ()
        |> withSession snapA
        |> withSession snapB
        |> withLiveTesting
        |> fun m ->
          { m with
              LiveTesting =
                { LiveTestCycleState.empty with
                    TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Active } }
              PerSessionLiveTesting =
                Map.ofList [
                  bSid,
                  { LiveTestCycleState.empty with
                      TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Active } }
                ] }

      let model', effects =
        SageFsUpdate.update
          (SageFsMsg.BufferContentChanged (Some bSid, fileB, "module Lib\nlet add a b = a + b"))
          model

      model'.LiveTesting.ActiveFile
      |> Expect.isNone
          "primary live-testing state should be untouched by a background session buffer change"

      let backgroundState =
        model'.PerSessionLiveTesting
        |> Map.find bSid

      backgroundState.ActiveFile
      |> Expect.equal "background active file updated" (Some fileB)
      backgroundState.LastTrigger
      |> Expect.equal "background unsaved buffer content should be treated as a keystroke" RunTrigger.Keystroke
      effects
      |> Expect.isEmpty "buffer ingress should only update debounce state; cycle work still waits for ticks"
    }

    test "background session buffer change cancels only that session's pending rebuild" {
      // WHY: Session-scoped buffer ingress is only honest if it invalidates
      // stale rebuild work for the owning session and leaves everyone else alone.
      let snapA = mkSession "session-a" "/projects/alpha"
      let snapB = mkSession "session-b" "/projects/beta"
      let aSid = WorkerProtocol.SessionId.value snapA.Id
      let bSid = WorkerProtocol.SessionId.value snapB.Id
      let fileA = "/projects/alpha/src/Lib.fs"
      let fileB = "/projects/beta/src/Lib.fs"
      let tcA = mkTestCase "Alpha.Tests.one" TestFramework.Expecto TestCategory.Unit
      let tcB = mkTestCase "Beta.Tests.two" TestFramework.Expecto TestCategory.Unit
      let pendingA = {
        Generation = 1L
        Tests = [| tcA |]
        Trigger = RunTrigger.FileSave
        FilePath = fileA
        AnalysisIdentity = None
        TreeSitterElapsed = TimeSpan.Zero
        FcsElapsed = TimeSpan.Zero
        SessionId = Some aSid
        InstrumentationMaps = [||] }
      let pendingB = {
        Generation = 2L
        Tests = [| tcB |]
        Trigger = RunTrigger.FileSave
        FilePath = fileB
        AnalysisIdentity = None
        TreeSitterElapsed = TimeSpan.Zero
        FcsElapsed = TimeSpan.Zero
        SessionId = Some bSid
        InstrumentationMaps = [||] }
      let model =
        SageFsModel.initial ()
        |> withSession snapA
        |> withSession snapB
        |> withLiveTesting
        |> fun m ->
          { m with
              LiveTesting =
                { LiveTestCycleState.empty with
                    NextRebuildGeneration = pendingA.Generation
                    PendingRebuild = Some pendingA
                    TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Active } }
              PerSessionLiveTesting =
                Map.ofList [
                  bSid,
                  { LiveTestCycleState.empty with
                      NextRebuildGeneration = pendingB.Generation
                      PendingRebuild = Some pendingB
                      TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Active } }
                ] }

      let model', effects =
        SageFsUpdate.update
          (SageFsMsg.BufferContentChanged (Some bSid, fileB, "module Lib\nlet add a b = a + b"))
          model

      model'.LiveTesting.PendingRebuild
      |> Expect.isSome
          "primary pending rebuild should survive a background session buffer change"

      model'.PerSessionLiveTesting
      |> Map.find bSid
      |> fun cycle -> cycle.PendingRebuild
      |> Expect.isNone
          "background pending rebuild should clear when that session's buffer changes"

      let cancelFields =
        effects
        |> List.tryPick (function
          | SageFsEffect.TestCycle effect ->
              let case, fields = FSharpValue.GetUnionFields(effect, typeof<TestCycleEffect>)
              match case.Name with
              | "CancelRebuild" -> Some fields
              | _ -> None
          | _ ->
              None)

      cancelFields
      |> Expect.isSome
          "background invalidation should emit an explicit CancelRebuild effect"

      let cancelFields = cancelFields |> Option.get
      cancelFields.[0]
      |> Expect.equal "CancelRebuild should target only the background session" (box (Some bSid))
      cancelFields.[1]
      |> Expect.equal "CancelRebuild should carry the background generation" (box pendingB.Generation)
    }
  ]

// =====================================================================
// Stream 5 — Session-Aware Downstream Routing
// =====================================================================

[<Tests>]
let stream5Tests =
  testList "Stream 5 — Session-Aware Downstream Routing" [

    test "background session tick emits a session-aware FCS request" {
      let snapA = mkSession "session-a" "/projects/alpha"
      let snapB = mkSession "session-b" "/projects/beta"
      let bSid = WorkerProtocol.SessionId.value snapB.Id
      let model =
        SageFsModel.initial ()
        |> withSession snapA
        |> withSession snapB
        |> withLiveTesting

      let fileB = "/projects/beta/src/Lib.fs"
      let afterChange, _ =
        SageFsUpdate.update
          (SageFsMsg.FileContentChanged (fileB, "module Lib"))
          model

      let _model', effects =
        SageFsUpdate.update
          (SageFsMsg.TestCycleTick (DateTimeOffset.UtcNow.AddSeconds 5.0))
          afterChange

      let request =
        effects
        |> List.tryPick (fun effect ->
          match effect with
          | SageFsEffect.TestCycle testCycleEffect when unionCaseNameOf testCycleEffect = "RequestFcsTypeCheck" ->
              Some testCycleEffect
          | _ ->
              None)

      request
      |> Expect.isSome
          "background session debounce should eventually emit RequestFcsTypeCheck"

      match request with
      | Some (TestCycleEffect.RequestFcsTypeCheck (targetSession, requestedFilePath, content, analysisIdentity, _tsElapsed)) ->
          targetSession
          |> Expect.equal "background FCS request should target the owning session" (Some bSid)
          requestedFilePath
          |> Expect.equal "background FCS request should preserve the changed file path" fileB
          content
          |> Expect.equal
              "background FCS request should analyze the freshest buffered content for that session"
              (Some "module Lib")
          analysisIdentity
          |> Expect.equal
              "background FCS request should carry the content identity that matches the buffered text"
              (Some (AnalysisIdentity.ofContent "module Lib"))
      | Some other ->
          failtestf "expected RequestFcsTypeCheck, got %A" other
      | None ->
          ()
    }

    test "background session run start updates only background live-testing state" {
      let snapA = mkSession "session-a" "/projects/alpha"
      let snapB = mkSession "session-b" "/projects/beta"
      let aSid = WorkerProtocol.SessionId.value snapA.Id
      let bSid = WorkerProtocol.SessionId.value snapB.Id
      let tcA = mkTestCase "Primary.Tests.alpha" TestFramework.Expecto TestCategory.Unit
      let tcB = mkTestCase "Background.Tests.beta" TestFramework.Expecto TestCategory.Unit
      let model =
        SageFsModel.initial ()
        |> withSession snapA
        |> withSession snapB
        |> withLiveTesting
        |> fun m ->
          { m with
              LiveTesting =
                { LiveTestCycleState.empty with
                    TestState =
                      { LiveTestState.empty with
                          Activation = LiveTestingActivation.Active
                          DiscoveredTests = [| tcA |]
                          TestSessionMap = Map.ofList [ tcA.Id, aSid ] } }
              PerSessionLiveTesting =
                Map.ofList [
                  bSid,
                  { LiveTestCycleState.empty with
                      TestState =
                        { LiveTestState.empty with
                            Activation = LiveTestingActivation.Active
                            DiscoveredTests = [| tcB |]
                            TestSessionMap = Map.ofList [ tcB.Id, bSid ] } }
                ] }

      let model', effects =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestRunStarted ([| tcB.Id |], Some bSid)))
          model

      model'.LiveTesting.TestState.AffectedTests
      |> Expect.isEmpty
          "primary live-testing state should stay untouched when a background session starts running tests"

      model'.LiveTesting.TestState.RunPhases
      |> Map.containsKey bSid
      |> Expect.isFalse
          "primary run phases should not claim the background session's running phase"

      let backgroundState =
        model'.PerSessionLiveTesting
        |> Map.find bSid

      backgroundState.TestState.AffectedTests
      |> Expect.containsAll
          "background live-testing state should record the tests that actually started running"
          (set [ tcB.Id ])

      backgroundState.TestState.RunPhases
      |> Map.tryFind bSid
      |> function
        | Some (TestRunPhase.Running _) -> ()
        | other -> failtestf "expected background session to be Running, got %A" other

      effects
      |> Expect.isEmpty "run-start should only mutate the targeted live-testing state"
    }

    test "background session FCS completion updates only background live-testing state" {
      let snapA = mkSession "session-a" "/projects/alpha"
      let snapB = mkSession "session-b" "/projects/beta"
      let bSid = WorkerProtocol.SessionId.value snapB.Id
      let tcA = mkTestCase "Primary.Tests.alpha" TestFramework.Expecto TestCategory.Unit
      let tcB = mkTestCase "Background.Tests.beta" TestFramework.Expecto TestCategory.Unit
      let fileB = "/projects/beta/src/Lib.fs"
      let refs = [
        { SymbolReference.SymbolFullName = "Background.Tests.beta"
          UseKind = SymbolUseKind.Definition
          UsedInTestId = None
          FilePath = fileB
          Line = 1 }
        { SymbolReference.SymbolFullName = "Lib.add"
          UseKind = SymbolUseKind.Reference
          UsedInTestId = None
          FilePath = fileB
          Line = 5 }
      ]
      let model =
        SageFsModel.initial ()
        |> withSession snapA
        |> withSession snapB
        |> withLiveTesting
        |> fun m ->
          { m with
              LiveTesting =
                { LiveTestCycleState.empty with
                    TestState =
                      { LiveTestState.empty with
                          Activation = LiveTestingActivation.Active
                          DiscoveredTests = [| tcA |] } }
              PerSessionLiveTesting =
                Map.ofList [
                  bSid,
                  { LiveTestCycleState.empty with
                      LastTrigger = RunTrigger.FileSave
                      TestState =
                        { LiveTestState.empty with
                            Activation = LiveTestingActivation.Active
                            DiscoveredTests = [| tcB |]
                            TestSessionMap = Map.ofList [ tcB.Id, bSid ] } }
                ] }

      let msg =
        makeSessionAwareMsg
          "FcsTypeCheckCompleted"
          (Some bSid)
          [| box None; box (FcsTypeCheckResult.Success (fileB, refs)) |]

      let model', effects = SageFsUpdate.update msg model

      model'.LiveTesting.DepGraph.SymbolToTests
      |> Expect.isEmpty
          "primary live-testing state should be untouched by a background session FCS completion"

      let backgroundState =
        model'.PerSessionLiveTesting
        |> Map.find bSid

      backgroundState.DepGraph.SymbolToTests
      |> Map.containsKey "Lib.add"
      |> Expect.isTrue
          "background live-testing state should absorb the FCS result"

      match effects with
      | [ SageFsEffect.TestCycle (TestCycleEffect.RequestRebuild (_, tests, _, _, _, sessionId, _)) ] ->
          tests
          |> Expect.equal "background FCS completion should schedule the background tests" [| tcB |]
          sessionId
          |> Expect.equal "background rebuild should stay targeted to the same session" (Some bSid)
      | other ->
          failtestf "expected one background RequestRebuild effect, got %A" other
    }

    test "background session rebuild completion consumes only that session pending rebuild" {
      let snapA = mkSession "session-a" "/projects/alpha"
      let snapB = mkSession "session-b" "/projects/beta"
      let bSid = WorkerProtocol.SessionId.value snapB.Id
      let fileA = "/projects/alpha/src/Lib.fs"
      let fileB = "/projects/beta/src/Lib.fs"
      let tcA = mkTestCase "Primary.Tests.alpha" TestFramework.Expecto TestCategory.Unit
      let tcB = mkTestCase "Background.Tests.beta" TestFramework.Expecto TestCategory.Unit
      let pendingA =
        { Generation = 1L
          Tests = [| tcA |]
          Trigger = RunTrigger.FileSave
          FilePath = fileA
          AnalysisIdentity = None
          TreeSitterElapsed = TimeSpan.FromMilliseconds 5.0
          FcsElapsed = TimeSpan.FromMilliseconds 10.0
          SessionId = None
          InstrumentationMaps = [||] }
      let pendingB =
        { Generation = 2L
          Tests = [| tcB |]
          Trigger = RunTrigger.FileSave
          FilePath = fileB
          AnalysisIdentity = None
          TreeSitterElapsed = TimeSpan.FromMilliseconds 7.0
          FcsElapsed = TimeSpan.FromMilliseconds 12.0
          SessionId = Some bSid
          InstrumentationMaps = [||] }
      let model =
        SageFsModel.initial ()
        |> withSession snapA
        |> withSession snapB
        |> withLiveTesting
        |> fun m ->
          { m with
              LiveTesting =
                { LiveTestCycleState.empty with
                    NextRebuildGeneration = pendingA.Generation
                    PendingRebuild = Some pendingA
                    TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Active } }
              PerSessionLiveTesting =
                Map.ofList [
                  bSid,
                  { LiveTestCycleState.empty with
                      NextRebuildGeneration = pendingB.Generation
                      PendingRebuild = Some pendingB
                      TestState = { LiveTestState.empty with Activation = LiveTestingActivation.Active } }
                ] }

      let msg = SageFsMsg.RebuildCompleted (Some bSid, pendingB.Generation, Ok ())

      let model', effects = SageFsUpdate.update msg model

      model'.LiveTesting.PendingRebuild
      |> Expect.isSome
          "primary pending rebuild should survive a background session completion"

      model'.PerSessionLiveTesting
      |> Map.find bSid
      |> fun cycle -> cycle.PendingRebuild
      |> Expect.isNone
          "background pending rebuild should clear after that session completes"

      match effects with
      | [ SageFsEffect.TestCycle (TestCycleEffect.RunAffectedTests (tests, _, _, _, sessionId, _)) ] ->
          tests
          |> Expect.equal "background rebuild completion should run only the background tests" [| tcB |]
          sessionId
          |> Expect.equal "background test execution should stay targeted to the same session" (Some bSid)
      | other ->
          failtestf "expected one background RunAffectedTests effect, got %A" other
    }
  ]
