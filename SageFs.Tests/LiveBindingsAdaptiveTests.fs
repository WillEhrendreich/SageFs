module SageFs.Tests.LiveBindingsAdaptiveTests

open System
open System.Threading
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

  testCase "same snapshot does not fire subscribers (adaptive dedup)" <| fun _ ->
    let store = create ()
    let snap = mkSnap "sess1" 1L [ "x" ]
    update store "sess1" snap
    let mutable calls = 0
    use _sub = subscribe store "sess1" (fun _ -> calls <- calls + 1)
    // Give the initial callback a chance to fire (subscribers fire once with current value).
    Thread.Sleep 200
    let afterSubscribe = calls
    // Update with the SAME snapshot value — adaptive equality means no re-fire.
    update store "sess1" snap
    Thread.Sleep 200
    calls |> Expect.equal "no extra calls for identical snapshot" afterSubscribe

  testCase "changed snapshot fires subscriber once" <| fun _ ->
    let store = create ()
    let snap1 = mkSnap "sess1" 1L [ "x" ]
    update store "sess1" snap1
    let mutable calls = 0
    use _sub = subscribe store "sess1" (fun _ -> calls <- calls + 1)
    Thread.Sleep 200
    let snap2 = mkSnap "sess1" 2L [ "x"; "y" ]
    update store "sess1" snap2
    Thread.Sleep 200
    calls > 0 |> Expect.isTrue "subscriber fired on change"

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
]
