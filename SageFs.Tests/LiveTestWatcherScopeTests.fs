module SageFs.Tests.LiveTestWatcherScopeTests

/// RED tests for per-session file watcher scope (Stream 1) and session
/// watcher lifecycle (Stream 4).  Tests 2, 3, 5, 6, 7 document desired
/// behaviour that does NOT yet exist and should FAIL until the
/// corresponding implementation is merged.  Tests 1 and 4 are baselines
/// that verify existing debounce plumbing works.
///
/// The core bug: `createLiveTestWatcher` (DaemonMode.fs) watches
/// `Environment.CurrentDirectory` (the daemon CWD), not the session's
/// project directories.  File changes in user projects are invisible
/// to live testing.

open System
open Expecto
open Expecto.Flip
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
    // WHY: Without session context on the message, the rebuild step
    // doesn't know which project to `dotnet build`.  After the fix
    // FileContentChanged must carry a sessionId so the pipeline can
    // route the build to the correct session.
    //
    // RED: SageFsMsg.FileContentChanged currently is
    //   `FileContentChanged of filePath: string * content: string`
    // After the fix it should be
    //   `FileContentChanged of filePath: string * content: string * sessionId: string`
    // (or a record with sessionId).
    test "FileContentChanged carries session context so the pipeline knows which session to rebuild" {
      // This test will fail at compile time once we change the DU,
      // but for now it documents the requirement by checking that the
      // model update propagates a session identifier.
      //
      // We simulate two sessions and assert that a FileContentChanged
      // for session A's directory is attributed to session A.
      let snapA = mkSession "session-a" "/projects/alpha"
      let snapB = mkSession "session-b" "/projects/beta"
      let model =
        SageFsModel.initial ()
        |> withSession snapA
        |> withSession snapB
        |> withLiveTesting

      let filePath = "/projects/alpha/src/Lib.fs"
      let _model', effects =
        SageFsUpdate.update
          (SageFsMsg.FileContentChanged (filePath, "module Lib"))
          model

      // RED: Today the update returns zero effects because the Elm
      // model has no session-routing logic for FileContentChanged.
      // After the fix, the effects list should contain a
      // TestCycleEffect whose sessionId matches session A.
      //
      // Until the sessionId field is added to FileContentChanged,
      // this assertion documents the gap.
      let hasSessionScopedEffect =
        effects
        |> List.exists (fun e ->
          match e with
          | SageFsEffect.TestCycle (TestCycleEffect.RequestFcsTypeCheck _) -> true
          | SageFsEffect.TestCycle (TestCycleEffect.ParseTreeSitter _) -> true
          | _ -> false)

      // This is RED: today FileContentChanged only populates the
      // debounce channel — effects are only emitted on tick.
      // The real requirement is that the effect carries session context.
      // For now we check that something downstream acknowledges the session.
      hasSessionScopedEffect
      |> Expect.isTrue
          "FileContentChanged should produce a session-scoped test cycle effect (not yet implemented)"
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
      // RED: Today the global ActiveFile is overwritten to fileB.
      model''.LiveTesting.ActiveFile
      |> Expect.notEqual
          "session A's active file should not be overwritten by session B's change"
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
  testList "Stream 4 — Session Watcher Lifecycle" [

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
          | SageFsEffect.TestCycle (TestCycleEffect.RequestInitialDiscovery) ->
            // This exists today but doesn't cover watcher registration
            false
          | _ ->
            // We're looking for a new effect variant like:
            //   TestCycleEffect.RegisterFileWatcher of sessionId * directory
            // which does not exist yet.
            false)

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
          | SageFsEffect.TestCycle _ ->
            // Looking for a DisposeFileWatcher effect — doesn't exist yet
            false
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
          | SageFsEffect.TestCycle _ ->
            // Looking for DisposeFileWatcher — doesn't exist yet
            false
          | _ -> false)

      let hasRegistration =
        allEffects
        |> List.exists (fun e ->
          match e with
          | SageFsEffect.TestCycle _ ->
            // Looking for RegisterFileWatcher — doesn't exist yet
            false
          | _ -> false)

      hasDisposal
      |> Expect.isTrue
          "session restart should dispose old watcher (not yet implemented)"

      hasRegistration
      |> Expect.isTrue
          "session restart should register new watcher for updated directory (not yet implemented)"
    }
  ]
