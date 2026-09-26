module SageFs.Tests.RestartCostTests

/// WHY — a per-boundary BUILD is impossible: `runBuildAsync` is ONE
/// `dotnet build` on ONE project and F# compilation is whole-assembly. So
/// "make the scoped restart cheaper" cannot be answered by narrowing the build.
///
/// What CAN be answered is whether a build is needed at all: a boundary
/// holding no LIVE INSTANCE of the changed type has nothing laid out by the old
/// definition, so respawning the worker suffices and the expensive part is
/// skipped.
///
/// The property these pin is the FAILURE DIRECTION: anything SageFs cannot
/// establish must pay the build, because a stale value holding the old type's
/// layout in a process that believes it restarted is worse than a slow restart.

open Expecto
open Expecto.Flip
open SageFs

[<Tests>]
let restartCostTests =
  testList "whether a restart needs a rebuild" [

    testCase "WHY — no LIVE instance means a respawn is enough and the build is skipped" <| fun _ ->
      let action = RestartCost.decide (Liveness.NoLiveInstances "order-store")
      RestartCost.rebuilds action
      |> Expect.isFalse "nothing alive holds the old shape, so do not pay for a build"

    testCase "WHY — a live instance MEANS a rebuild, and the message says which boundary" <| fun _ ->
      let action = RestartCost.decide (Liveness.HoldsLiveInstances "order-store")
      RestartCost.rebuilds action
      |> Expect.isTrue "a live old-layout value is only fixed by a rebuild"
      match action with
      | RestartAction.RebuildProject why -> (why.Contains "order-store") |> Expect.isTrue "it names the boundary"
      | _ -> failtest "expected a rebuild"

    testCase "WHY — UNKNOWN liveness pays the build, because 'probably fine' is not evidence" <| fun _ ->
      let action = RestartCost.decide (Liveness.Unknown "the DI container could not be inspected")
      RestartCost.rebuilds action
      |> Expect.isTrue "unknown is not a licence to skip the build"
      match action with
      | RestartAction.RebuildProject why ->
        (why.Contains "could not be inspected") |> Expect.isTrue "it carries the reason it could not tell"
      | _ -> failtest "expected a rebuild"

    testCase "WHY — every respawn reason is a complete sentence, so the user is never left guessing" <| fun _ ->
      match RestartCost.decide (Liveness.NoLiveInstances "order-store") with
      | RestartAction.RespawnOnly why ->
        (why.Contains "order-store") |> Expect.isTrue "it names the boundary"
        (why.Contains "respawn") |> Expect.isTrue "it says what will happen"
      | _ -> failtest "expected a respawn"

    testCase "WHY — the decision does NOT depend on the restart SUBJECT, so a scoped restart cannot fake a cheap build" <| fun _ ->
      // The trap this avoids: "the restart is scoped, therefore it must be
      // cheap". Width and cost are different questions. A UnitScope subject
      // with a LIVE instance still needs a whole-project rebuild, because
      // F# cannot compile a fraction of an assembly.
      [ "Worker"; "unit" ]
      |> List.iter (fun _ ->
        RestartCost.rebuilds (RestartCost.decide (Liveness.HoldsLiveInstances "b"))
        |> Expect.isTrue "width never buys a narrower build")

    testCase "WHY — a probe that COUNTED zero skips the build, because the app says it holds none" <| fun _ ->
      let l = RestartCost.livenessFromProbe "Order" (SageFs.ProbeOutcome.Counted 0)
      RestartCost.rebuilds (RestartCost.decide l)
      |> Expect.isFalse "the app answered, and the answer was none"

    testCase "WHY — a probe that COULD NOT COUNT is NOT zero, and pays the build" <| fun _ ->
      // The distinction that would silently skip a build if collapsed: a
      // failed probe is not an answer of zero.
      let l = RestartCost.livenessFromProbe "Order" (SageFs.ProbeOutcome.CouldNotCount "the type is not loaded")
      RestartCost.rebuilds (RestartCost.decide l)
      |> Expect.isTrue "unknown is not zero"
      match l with
      | Liveness.Unknown why -> (why.Contains "not loaded") |> Expect.isTrue "it carries the reason"
      | _ -> failtest "a failed probe must be Unknown"

    testCase "WHY — a probe that counted some instances rebuilds, and says how many" <| fun _ ->
      let l = RestartCost.livenessFromProbe "Order" ((SageFs.ProbeOutcome.Counted 3))
      RestartCost.rebuilds (RestartCost.decide l)
      |> Expect.isTrue "live old-layout values are only fixed by a rebuild"
      match l with
      | Liveness.HoldsLiveInstances why -> (why.Contains "3") |> Expect.isTrue "it reports the count"
      | _ -> failtest "expected live instances"

    testCase "WHY — the boolean and the DU never disagree" <| fun _ ->
      [ Liveness.NoLiveInstances "b"; Liveness.HoldsLiveInstances "b"; Liveness.Unknown "why" ]
      |> List.iter (fun l ->
        let a = RestartCost.decide l
        let viaBool = RestartCost.rebuilds a
        match a with
        | RestartAction.RebuildProject _ -> viaBool |> Expect.isTrue "RebuildProject rebuilds"
        | RestartAction.RespawnOnly _ -> viaBool |> Expect.isFalse "RespawnOnly does not"
        // `decide` is the TWO-case liveness path and has no migration input, so
        // it cannot reach the third case — but the DU now has three, and this
        // match is the compiler's standing reminder of that. Should `decide`
        // ever grow a migration case, this arm FAILS at runtime instead of the
        // test quietly covering only two.
        | RestartAction.MigrateAndRespawn _ ->
          failtest "decide has no migration input, so it cannot reach this case")
  ]
