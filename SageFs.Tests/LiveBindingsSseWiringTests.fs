module SageFs.Tests.LiveBindingsSseWiringTests

// Gap 2 (roast-8 §2): `formatLiveBindingsEvent`/`formatDomainModelEvent`
// (SseWriter.fs) had zero production callers. Investigation:
//   - `live_bindings`: REAL, useful, already-computed data. Every successful
//     eval already pulls a `Features.LiveValueTree.LiveValueSnapshot` via
//     `GetLiveValues` (AppState.fs) — off the eval thread, fire-and-forget,
//     bounded (MaxBindings/MaxNodes/MaxDepth) — and feeds it to the
//     dashboard's `LiveBindingsAdaptive` store. WIRED: `wrapLiveSnapshotSinkForSse`
//     makes the SAME already-paid snapshot ALSO reach SSE clients, at zero
//     extra reflection cost.
//   - `domain_model`: its OWN data source (`DomainModelViz.annotateWithHealth`)
//     also had zero production callers, and the one live MCP tool that
//     touches `DomainModelViz` (`visualizeDomainModel`) never populates
//     `Transitions` at all (hardcoded `[]`) — there was no live pipeline to
//     wire, even in principle. DELETED, along with the tests that existed
//     only to exercise it.
//
// These tests prove: (1) `wrapLiveSnapshotSinkForSse` actually pushes a
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
let wrapLiveSnapshotSinkForSseTests = testList "wrapLiveSnapshotSinkForSse" [

  testCase "None in, None out — no behavior change when no sink is wired" <| fun _ ->
    wrapLiveSnapshotSinkForSse (Event<string>()) jsonOpts None
    |> Expect.isNone "wrapping None stays None"

  testCase "calls the inner sink AND pushes a live_bindings SSE frame carrying the real snapshot" <| fun _ ->
    let broadcast = Event<string>()
    let received = ResizeArray<string>()
    broadcast.Publish.Add(received.Add)
    let innerCalls = ResizeArray<string * LiveValueSnapshot>()
    let inner sid (snap: LiveValueSnapshot) = innerCalls.Add(sid, snap)

    let wrapped =
      match wrapLiveSnapshotSinkForSse broadcast jsonOpts (Some inner) with
      | Some w -> w
      | None -> failwith "expected Some — an inner sink was provided"

    let snap = mkSnapshot "sess-1"
    wrapped "sess-1" snap

    // The dashboard's own consumer must still run — this is additive, not a
    // replacement. A broken wrap that forgets to call `inner` would fail this.
    innerCalls.Count |> Expect.equal "inner sink still called exactly once" 1
    innerCalls.[0] |> Expect.equal "inner sink got the real args" ("sess-1", snap)

    // A broken wrap that never triggers the broadcast would fail this.
    received.Count |> Expect.equal "exactly one live_bindings frame pushed" 1
    received.[0] |> Expect.stringStarts "rides the session channel" "event: live_bindings\n"
    received.[0] |> Expect.stringContains "carries the session id" "sess-1"
    received.[0] |> Expect.stringContains "carries the real binding name" "\"x\""

  testCase "one wrapped sink call per snapshot — no extra pushes" <| fun _ ->
    let broadcast = Event<string>()
    let received = ResizeArray<string>()
    broadcast.Publish.Add(received.Add)
    let wrapped =
      match wrapLiveSnapshotSinkForSse broadcast jsonOpts (Some (fun _ _ -> ())) with
      | Some w -> w
      | None -> failwith "expected Some"
    wrapped "s1" (mkSnapshot "s1")
    wrapped "s2" (mkSnapshot "s2")
    received.Count |> Expect.equal "two calls, two pushes, no duplication" 2
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
