module SageFs.Tests.LiveBindingsSseWiringTests

// Gap 2 (roast-8 §2): `formatLiveBindingsEvent`/`formatDomainModelEvent`
// (SseWriter.fs) had zero production callers. Investigation:
//   - `live_bindings`: REAL, useful, already-computed data. Every successful
//     eval already pulls a `Features.LiveValueTree.LiveValueSnapshot` via
//     `GetLiveValues` (AppState.fs) — off the eval thread, fire-and-forget,
//     bounded (MaxBindings/MaxNodes/MaxDepth) — and feeds it to the
//     dashboard's `LiveBindingsAdaptive` store. WIRED: `pushLiveBindingsOverSse`
//     makes the SAME already-paid snapshot ALSO reach SSE clients, at zero
//     extra reflection cost.
//   - `domain_model`: its OWN data source (`DomainModelViz.annotateWithHealth`)
//     also had zero production callers, and the one live MCP tool that
//     touches `DomainModelViz` (`visualizeDomainModel`) never populates
//     `Transitions` at all (hardcoded `[]`) — there was no live pipeline to
//     wire, even in principle. DELETED, along with the tests that existed
//     only to exercise it.
//
// These tests prove: (1) `pushLiveBindingsOverSse` actually pushes a
// `live_bindings` SSE frame carrying the real snapshot content, without
// dropping the original consumer; (2) `domain_model` no longer exists in the
// SSE event registry.

open System
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Server.McpServer
open SageFs.Features.LiveValueTree

let private jsonOpts =
  let o = JsonSerializerOptions()
  o.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
  o

let private mkSnapshot (sessionId: string) : LiveValueSnapshot =
  let root : LiveValueNode =
    { Label = "x"; TypeName = "int"; Preview = "42"; Kind = NodeKind.Leaf
      Children = []; BestEffort = false; Depth = 0 }
  { SessionId = sessionId
    Generation = 1L
    Bindings = [ { Name = "x"; TypeSignature = "int"; Root = root } ]
    Truncated = false
    CapturedAt = DateTimeOffset.UtcNow }

[<Tests>]
let pushLiveBindingsOverSseTests = testList "pushLiveBindingsOverSse" [

  let hubOf () : SageFs.Features.LiveBindingsPane.Hub =
    { Adaptive = SageFs.Features.LiveBindingsAdaptive.create ()
      Notes = SageFs.Features.LiveBindingsPane.PaneStore.create ()
      ConfiguredWalk = fun _ -> WalkSafe }

  testCase "no hub, no subscription — nothing is pushed" <| fun _ ->
    use _subscription = pushLiveBindingsOverSse (Event<string>()) jsonOpts None
    ()

  testCase "every store update pushes a live_bindings SSE frame carrying the real snapshot" <| fun _ ->
    let broadcast = Event<string>()
    let received = ResizeArray<string>()
    broadcast.Publish.Add(received.Add)
    let hub = hubOf ()
    use _subscription = pushLiveBindingsOverSse broadcast jsonOpts (Some hub)
    SageFs.Features.LiveBindingsAdaptive.update hub.Adaptive "sess-1" (mkSnapshot "sess-1")
    received.Count |> Expect.equal "exactly one live_bindings frame pushed" 1
    received.[0] |> Expect.stringStarts "rides the session channel" "event: live_bindings\n"
    received.[0] |> Expect.stringContains "carries the session id" "sess-1"
    received.[0] |> Expect.stringContains "carries the real binding name" "\"x\""

  testCase "one push per update — no extra pushes, and none after the subscription is disposed" <| fun _ ->
    let broadcast = Event<string>()
    let received = ResizeArray<string>()
    broadcast.Publish.Add(received.Add)
    let hub = hubOf ()
    let subscription = pushLiveBindingsOverSse broadcast jsonOpts (Some hub)
    SageFs.Features.LiveBindingsAdaptive.update hub.Adaptive "s1" (mkSnapshot "s1")
    SageFs.Features.LiveBindingsAdaptive.update hub.Adaptive "s2" (mkSnapshot "s2")
    received.Count |> Expect.equal "two updates, two pushes, no duplication" 2
    subscription.Dispose()
    SageFs.Features.LiveBindingsAdaptive.update hub.Adaptive "s3" (mkSnapshot "s3")
    received.Count |> Expect.equal "disposed, so quiet" 2
]

// ── domain_model deletion (roast-8 §2) ────────────────────────────────────

[<Tests>]
let domainModelDeletionTests = testList "domain_model SSE event deleted" [

  testCase "'domain_model' is no longer in the SSE event registry" <| fun _ ->
    SseWriter.allSseEventTypes
    |> List.contains "domain_model"
    |> Expect.isFalse "domain_model was deleted: zero production callers of the emitter or its data source"

  testCase "'live_bindings' is still registered (it was NOT deleted — it was wired instead)" <| fun _ ->
    SseWriter.allSseEventTypes
    |> List.contains "live_bindings"
    |> Expect.isTrue "live_bindings stays in the registry"

  testCase "no formatDomainModelEvent method remains on SseWriter" <| fun _ ->
    let sseWriterType = typeof<SseWriter.FsiBinding>.DeclaringType
    sseWriterType.GetMethods()
    |> Array.exists (fun m -> m.Name = "formatDomainModelEvent")
    |> Expect.isFalse "the dead emitter function itself is gone"
]
