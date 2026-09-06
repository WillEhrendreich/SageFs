module SageFs.Tests.LiveBindingsAdaptiveTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Features.LiveBindingsAdaptive
open SageFs.Features.LiveValueTree

let private mkSnap (sessionId: string) (generation: int64) (names: string list) =
  let bindings =
    names
    |> List.mapi (fun i name ->
      { Name = name
        TypeSignature = "int"
        Root = { Label = name; TypeName = "int"; Preview = string i; Kind = NodeKind.Leaf
                 Children = []; BestEffort = false; Depth = 0 } })
  { SessionId = sessionId; Generation = generation; Bindings = bindings
    Truncated = false; CapturedAt = DateTimeOffset.UtcNow }

[<Tests>]
let liveBindingsAdaptiveTests = testList "LiveBindingsAdaptive" [

  testCase "tryGet returns None before any update" <| fun _ ->
    let store = create ()
    tryGet store "sess1" |> Expect.isNone "no snapshot yet"

  testCase "update then tryGet returns the snapshot" <| fun _ ->
    let store = create ()
    let snap = mkSnap "sess1" 1L [ "x" ]
    update store "sess1" snap
    match tryGet store "sess1" with
    | Some s -> s.Generation |> Expect.equal "generation" 1L
    | None -> failtest "expected snapshot"

  testTask "same snapshot does not fire subscribers (adaptive dedup)" {
    let store = create ()
    let snap = mkSnap "sess1" 1L [ "x" ]
    update store "sess1" snap
    let mutable calls = 0
    let sub = subscribe store "sess1" (fun _ -> calls <- calls + 1)
    try
      // Give the initial callback a chance to fire (subscribers fire once with current value).
      do! Task.Delay 200
      let afterSubscribe = calls
      // Update with the SAME snapshot value — adaptive equality means no re-fire.
      update store "sess1" snap
      do! Task.Delay 200
      calls |> Expect.equal "no extra calls for identical snapshot" afterSubscribe
    finally
      sub.Dispose()
  }

  testTask "changed snapshot fires subscriber once" {
    let store = create ()
    let snap1 = mkSnap "sess1" 1L [ "x" ]
    update store "sess1" snap1
    let mutable calls = 0
    let sub = subscribe store "sess1" (fun _ -> calls <- calls + 1)
    try
      do! Task.Delay 200
      let snap2 = mkSnap "sess1" 2L [ "x"; "y" ]
      update store "sess1" snap2
      do! Task.Delay 200
      calls > 0 |> Expect.isTrue "subscriber fired on change"
    finally
      sub.Dispose()
  }

  testCase "per-session isolation" <| fun _ ->
    let store = create ()
    update store "sessA" (mkSnap "sessA" 1L [ "a" ])
    update store "sessB" (mkSnap "sessB" 1L [ "b" ])
    tryGet store "sessA" |> Option.map (fun s -> s.Bindings.Length) |> Expect.equal "A has 1" (Some 1)
    tryGet store "sessB" |> Option.map (fun s -> s.Bindings.Length) |> Expect.equal "B has 1" (Some 1)

  testCase "generation tracking per session" <| fun _ ->
    let store = create ()
    update store "sess1" (mkSnap "sess1" 5L [ "x" ])
    tryGetGeneration store "sess1" |> Expect.equal "generation" 5L
    tryGetGeneration store "sess-other" |> Expect.equal "no generation" 0L

  testCase "remove drops the session" <| fun _ ->
    let store = create ()
    update store "sess1" (mkSnap "sess1" 1L [ "x" ])
    remove store "sess1"
    tryGet store "sess1" |> Expect.isNone "removed"

  testList "sweepStaleSessionState (roast queue item 2)" [
    // Session lifecycle cleanup: adaptive snapshots for sessions that no
    // longer exist must be released, and the shared feature push state reset
    // when the last session is gone — otherwise memory grows for daemon life.
    testCase "removes dead sessions, keeps live ones, leaves feature state intact" <| fun _ ->
      let store = create ()
      update store "s1" (mkSnap "s1" 1L [ "x" ])
      update store "s2" (mkSnap "s2" 1L [ "y" ])
      let feature = ref SageFs.Features.FeatureHooks.FeaturePushState.empty
      let swept = SageFs.Server.DaemonMode.sweepStaleSessionState (Set.ofList [ "s1" ]) store feature
      swept |> Expect.equal "only s2 swept" [ "s2" ]
      tryGet store "s1" |> Expect.isSome "s1 still live"
      tryGet store "s2" |> Expect.isNone "s2 removed"
      // Sessions remain — the shared feature state is NOT reset yet.
      feature.Value |> Expect.equal "feature state intact" SageFs.Features.FeatureHooks.FeaturePushState.empty

    testCase "when the last session is gone, the shared feature state is reset" <| fun _ ->
      let store = create ()
      update store "s1" (mkSnap "s1" 1L [ "x" ])
      let feature =
        ref (SageFs.Features.FeatureHooks.FeaturePushState.empty
             |> SageFs.Features.FeatureHooks.recordEval "let x = 1" "val x: int = 1" 5L)
      feature.Value.EvalHistory.Length |> Expect.equal "history populated before reset" 1
      let swept = SageFs.Server.DaemonMode.sweepStaleSessionState Set.empty store feature
      swept |> Expect.equal "s1 swept" [ "s1" ]
      tryGet store "s1" |> Expect.isNone "s1 removed"
      feature.Value.EvalHistory |> Expect.isEmpty "shared feature state reset"
  ]
]
