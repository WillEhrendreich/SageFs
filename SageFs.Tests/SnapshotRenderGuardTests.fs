module SageFs.Tests.SnapshotRenderGuardTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Server.Dashboard

/// Roast-6 Finding #10: `renderMainContent snap |> renderNode` used to run on
/// every SSE tick, per connection, before the byte-compare against the last
/// sent HTML ever ran — an unchanged tick still paid for the full whole-page
/// render and HTML-string materialization. `SnapshotRenderGuard.decide` is
/// the pure short-circuit: it decides render-vs-skip from the built
/// `DashboardSnapshot` alone, with no server, no HTTP context, and no call
/// into renderMainContent/renderNode — so it is testable in complete
/// isolation from the live SSE stream.
///
/// `SnapshotVersion` construction is private outside SnapshotRenderGuard
/// (mirroring WorkerProtocol.SessionId), so these tests build expected
/// versions via `SnapshotVersion.initial`/`.next` — "one bump", "two bumps"
/// — rather than any int literal.
let private oneBump = SnapshotRenderGuard.SnapshotVersion.next SnapshotRenderGuard.SnapshotVersion.initial
let private twoBumps = SnapshotRenderGuard.SnapshotVersion.next oneBump

[<Tests>]
let tests = testList "SnapshotRenderGuard — render only when the snapshot actually changed" [

  test "WHY — the first tick has nothing to compare against, so it always renders" {
    SnapshotRenderGuard.decide SnapshotRenderGuard.RenderMemory.initial "content-a"
    |> Expect.equal "first tick renders"
      (SnapshotRenderGuard.Decision.Render { LastRendered = Some "content-a"; Version = oneBump })
  }

  test "WHY — an identical snapshot on the next tick is Skip, so renderMainContent/renderNode never run for it" {
    let firstDecision = SnapshotRenderGuard.decide SnapshotRenderGuard.RenderMemory.initial "content-a"
    match firstDecision with
    | SnapshotRenderGuard.Decision.Render memoryAfterFirst ->
      SnapshotRenderGuard.decide memoryAfterFirst "content-a"
      |> Expect.equal "unchanged content is Skip" SnapshotRenderGuard.Decision.Skip
    | SnapshotRenderGuard.Decision.Skip ->
      failtest "the first tick must always render"
  }

  test "WHY — a genuinely different snapshot renders and bumps the version by exactly one" {
    let firstDecision = SnapshotRenderGuard.decide SnapshotRenderGuard.RenderMemory.initial "content-a"
    match firstDecision with
    | SnapshotRenderGuard.Decision.Render memoryAfterFirst ->
      SnapshotRenderGuard.decide memoryAfterFirst "content-b"
      |> Expect.equal "changed content renders with version + 1"
        (SnapshotRenderGuard.Decision.Render { LastRendered = Some "content-b"; Version = twoBumps })
    | SnapshotRenderGuard.Decision.Skip ->
      failtest "the first tick must always render"
  }

  test "WHY — a Skip never advances the version, because nothing was rendered" {
    let firstDecision = SnapshotRenderGuard.decide SnapshotRenderGuard.RenderMemory.initial "content-a"
    match firstDecision with
    | SnapshotRenderGuard.Decision.Render memoryAfterFirst ->
      match SnapshotRenderGuard.decide memoryAfterFirst "content-a" with
      | SnapshotRenderGuard.Decision.Skip -> ()
      | SnapshotRenderGuard.Decision.Render _ -> failtest "unchanged content must not render"
      // The version this connection remembers is still the one from the
      // render that actually happened — a Skip carries no new version.
      memoryAfterFirst.Version
      |> Expect.equal "version unchanged by a Skip" oneBump
    | SnapshotRenderGuard.Decision.Skip ->
      failtest "the first tick must always render"
  }

  testProperty "WHY — a bumped version triggers exactly one render: a repeated candidate right after a render is always Skip" <|
    fun (a: int) (b: int) ->
      (a <> b) ==>
        lazy (
          match SnapshotRenderGuard.decide SnapshotRenderGuard.RenderMemory.initial a with
          | SnapshotRenderGuard.Decision.Skip -> false // the first tick must always render
          | SnapshotRenderGuard.Decision.Render memoryAfterA ->
            let repeatIsSkip =
              match SnapshotRenderGuard.decide memoryAfterA a with
              | SnapshotRenderGuard.Decision.Skip -> true
              | SnapshotRenderGuard.Decision.Render _ -> false
            let changeRendersOnce =
              match SnapshotRenderGuard.decide memoryAfterA b with
              | SnapshotRenderGuard.Decision.Render memoryAfterB ->
                memoryAfterB.Version = SnapshotRenderGuard.SnapshotVersion.next memoryAfterA.Version
              | SnapshotRenderGuard.Decision.Skip -> false
            repeatIsSkip && changeRendersOnce
        )

  testProperty "WHY — replaying any sequence of ticks always ends remembering the last value in the sequence, whether that tick rendered or was skipped" <|
    fun (values: int list) ->
      not (List.isEmpty values) ==>
        lazy (
          let finalMemory =
            values
            |> List.fold
              (fun memory candidate ->
                match SnapshotRenderGuard.decide memory candidate with
                | SnapshotRenderGuard.Decision.Render newMemory -> newMemory
                | SnapshotRenderGuard.Decision.Skip -> memory)
              SnapshotRenderGuard.RenderMemory.initial
          finalMemory.LastRendered = Some (List.last values)
        )

  testProperty "WHY — the version never decreases across any sequence of ticks, rendered or skipped" <|
    fun (values: int list) ->
      let memories =
        values
        |> List.scan
          (fun memory candidate ->
            match SnapshotRenderGuard.decide memory candidate with
            | SnapshotRenderGuard.Decision.Render newMemory -> newMemory
            | SnapshotRenderGuard.Decision.Skip -> memory)
          SnapshotRenderGuard.RenderMemory.initial
      memories
      |> List.pairwise
      |> List.forall (fun (before, after) -> after.Version >= before.Version)
]
