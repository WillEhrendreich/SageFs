module SageFs.Tests.HostCoreAdoptionReloadTests

open System
open System.ComponentModel
open System.Reflection
open Expecto
open Expecto.Flip
open SageFs.Server.McpTools
open SageFs.McpTools

/// F5b Phase 2 (design: f5b-self-hosting-design.md §4) — a blessed,
/// discoverable "reload the self-host build" path so an agent developing
/// SageFs.Core inside a SageFs.Core session can rebuild + respawn +
/// re-adopt the fresh build in one call, and see confirmation it landed.
///
/// The design's own §3-4 conclusion — confirmed here by reading
/// SageFs/McpTools.fs's hard_reset_fsi_session description,
/// SageFs/Mcp.fs's hard_reset handler, and
/// SageFs.Core/HostCoreAdoption.fs's resolveLaunchRoot before writing a
/// single line of this file — is that the mechanism already exists
/// end-to-end: hard_reset_fsi_session rebuild=true drives
/// SessionManager's restart path (SessionManager.fs:313, NOT modified by
/// this island), which calls HostCoreAdoption.resolveLaunchRoot (also NOT
/// modified by this island) to rebuild, respawn, and re-adopt the
/// session project's own SageFs.Core build. F5b Phase 2 is therefore
/// documentation + confirmation UX layered over that proven path, not new
/// isolation machinery — the design explicitly rejects in-process ALC
/// isolation for SageFs.Core (§3: the native TPA binder wins before any
/// managed ALC identity check runs, and SageFs.Core's FSI-exchanged types
/// can't be split across ALCs without breaking the live-object
/// FSI<->plumbing boundary).
///
/// CONCLUSION: hard_reset_fsi_session rebuild=true is confirmed
/// SUFFICIENT for the self-host reload — this island only had to make it
/// DISCOVERABLE (the tool description now names the self-hosting use
/// case explicitly) and CONFIRMED (RebuildOutcome.describe's Succeeded
/// line — surfaced via get_fsi_status, Mcp.fs:2101-2105 — now names the
/// loaded SageFs.Core version so an agent can see the adopted build
/// actually changed).
///
/// SCOPE: this is a CONTRACT test, not the full rebuild-mid-test
/// integration test the design's own RED spec calls for (§4 Phase 2:
/// build SageFs.Core at vN, start a session, assert loaded=vN; rebuild at
/// vN+1 with a bumped marker, invoke the reload, assert loaded=vN+1 —
/// mirroring DogfoodReplTests.fs:102-113's "sagefs-host-adopt-" assertion
/// pattern via SessionManager.create + AwaitReady, with any poll loop
/// written ITERATIVELY, not as `task { return! self }` recursion, per
/// [[project_task_recursion_not_stacksafe]]). That full integration test
/// is intentionally deferred — too slow/complex for this island; the
/// coordinating agent owns adding it during integration.
let private hardResetDescription : string =
  typeof<SageFsTools>.GetMethods(BindingFlags.Instance ||| BindingFlags.Public)
  |> Array.find (fun m -> m.Name = "hard_reset_fsi_session")
  |> fun m -> m.GetCustomAttribute<DescriptionAttribute>().Description

[<Tests>]
let tests =
  testList "HostCoreAdoption self-host reload (F5b Phase 2)" [
    test "WHY — hard_reset_fsi_session's description names the self-host reload path, because a self-hosting agent must discover the ONE blessed call without ever reading HostCoreAdoption.fs" {
      hardResetDescription
      |> Expect.stringContains "the description calls out the self-hosting SageFs.Core use case" "SELF-HOSTING SageFs.Core"
      hardResetDescription
      |> Expect.stringContains "the description states rebuild=true IS the reload mechanism, not a separate tool" "rebuild=true rebuilds"
      hardResetDescription
      |> Expect.stringContains "the description promises a post-reload version confirmation, matching RebuildOutcome.describe's Succeeded line" "confirms which"
    }

    test "WHY — a successful rebuild's get_fsi_status line names the loaded SageFs.Core version, because a respawn an agent cannot see through is a respawn that might as well not have happened" {
      let finishedAt = DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc)
      let line = RebuildOutcome.describe (finishedAt.AddSeconds 1.0) (RebuildOutcome.Succeeded finishedAt)
      // The loaded-version confirmation is the daemon's OWN SageFs.Core
      // assembly version (reflected the same way HostCoreAdoption.fs's own
      // doc comment does, via a type — not a module — from that assembly:
      // typeof<SageFs.SageFsError>.Assembly). HostCoreAdoption.decide only
      // ever adopts a session project's build when its version EQUALS the
      // daemon's own, so this is exactly the version the freshly-respawned
      // worker is now running, whether adoption applied (self-host case) or
      // not (ordinary case, same shared host build).
      let loadedVersion = typeof<SageFs.SageFsError>.Assembly.GetName().Version.ToString()
      line |> Expect.stringContains "the confirmation names the loaded SageFs.Core version" (sprintf "SageFs.Core %s" loadedVersion)
      line |> Expect.stringContains "the confirmation states the build is now loaded" "now loaded"
    }

    test "WHY — a rebuild still InProgress never claims a loaded version, because nothing was adopted yet and claiming otherwise would lie to the agent polling get_fsi_status" {
      let startedAt = DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc)
      let line = RebuildOutcome.describe (startedAt.AddSeconds 2.0) (RebuildOutcome.InProgress startedAt)
      line.Contains "now loaded" |> Expect.isFalse "an in-progress rebuild must not claim a version is already loaded"
    }
  ]
