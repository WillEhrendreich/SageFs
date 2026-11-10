module SageFs.Tests.SilentHostSimTests

open Expecto
open Expecto.Flip
open SageFs.HostAgent.TestDebug
open SageFs.SilentHost
open SageFs.Simulation
open SageFs.Simulation.SilentHostSim
open SageFs.Simulation.SilentHostInvariants

/// DST for a host that is alive and answers nothing: the host freezes and thaws at any tick (a debugger at a breakpoint, an
/// inspector, a killed debugger that never lets go), and the real wait (`SilentHost.Wait`) and pulse (`SilentHost.Pulse`) are
/// folded over every order a seed produces. Each twin must FAIL the invariant built to catch it, which is what shows the
/// invariants can fail at all. The twin with no ceiling is the bug found by hand on 2026-11-09: the release never answers.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (behavior: Behavior) (scenario: Scenario) =
  match violations (trace behavior scenario) with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION: replay this scenario:\n  Seed=%d\n  Events=%A\n  Violations=%A" scenario.Seed scenario.Events vs

let private violatedIds (behavior: Behavior) (scenario: Scenario) : string list =
  violations (trace behavior scenario) |> List.map fst

let private callsOf (behavior: Behavior) (scenario: Scenario) = (final behavior scenario).Calls

[<Tests>]
let tests =
  testList "DST a host that is alive and answers nothing" [

    testList "the real wait and pulse hold every invariant" [

      testPropertyWithConfig simConfig "seeded scenarios: every release ends within its ceiling, silence is never false, the pulse never lies, and a held ticket stays valid"
      <| fun (seed: int) -> assertHolds Behavior.Real (scenarioOf seed)

      testPropertyWithConfig simConfig "a host that freezes and thaws after any number of ticks: every release answers, and the one after the thaw gets the host's own answer"
      <| fun (seed: int) ->
        assertHolds Behavior.Real (freezeThenThaw seed)
        match List.last (callsOf Behavior.Real (freezeThenThaw seed)) with
        | { Wait = Wait.Settled(WaitEnd.Replied _) } -> true
        | _ -> false

      testCase "a debugger killed and the runtime left stopped: every release still ends at its ceiling with the silent answer, never another hang" <| fun _ ->
        assertHolds Behavior.Real frozenForGood
        let calls = callsOf Behavior.Real frozenForGood
        calls |> List.length |> Expect.equal "three releases were sent" 3
        calls
        |> List.forall (fun c -> c.Wait = Wait.Settled WaitEnd.NoAnswer)
        |> Expect.isTrue "each was answered 'no answer' by its ceiling"
        (final Behavior.Real frozenForGood).Pulse |> (function Pulse.Silent _ -> true | Pulse.Responsive -> false) |> Expect.isTrue "the host reads silent while it is frozen"

      testCase "a host that never froze answers its release from inside its park, and is never called silent" <| fun _ ->
        assertHolds Behavior.Real neverFrozen
        match callsOf Behavior.Real neverFrozen with
        | [ { Wait = Wait.Settled(WaitEnd.Replied _) } ] -> ()
        | other -> failtestf "expected one reply, got %A" other

      testCase "the ceiling is what the host owes plus the grace" <| fun _ ->
        ceilingTicks |> Expect.equal "park plus grace, in ticks" (parkTicks + graceTicks)
    ]

    testList "the twins reproduce the failures the invariants exist to catch (teeth)" [

      testCase "REPRODUCED: a wait with no ceiling never ends on a frozen host (the release that never answers)" <| fun _ ->
        violatedIds Behavior.UnboundedWaitTwin frozenForGood
        |> Expect.contains "every-call-ends-within-the-ceiling must fire against the unbounded twin" "every-call-ends-within-the-ceiling"

      testCase "REPRODUCED: a pulse that is never cleared still calls the host silent after it is back" <| fun _ ->
        let stoppedPastTheCeilingThenBack =
          { Seed = 0
            Events =
              [ SimEvent.DebuggerAttaches; SimEvent.ClientContinue; SimEvent.Freeze ]
              @ List.replicate (ceilingTicks + 1) SimEvent.Tick
              @ [ SimEvent.Thaw ]
              @ List.replicate (ceilingTicks + parkTicks) SimEvent.Tick }
        violatedIds Behavior.NeverClearsPulseTwin stoppedPastTheCeilingThenBack
        |> Expect.contains "the-pulse-never-lies must fire against the never-clears twin" "the-pulse-never-lies"

      testCase "REPRODUCED: a ceiling that forgot the park calls a working host silent" <| fun _ ->
        violatedIds Behavior.CeilingForgetsParkTwin neverFrozen
        |> Expect.contains "silence-is-never-false must fire against the forgets-the-park twin" "silence-is-never-false"

      testCase "the seeded scenarios catch every twin too, so the teeth are in the random space and not only in the hand-built shapes" <| fun _ ->
        let seeds = [ 1..400 ]
        let caught (behavior: Behavior) (scenarioOf: int -> Scenario) =
          seeds |> List.exists (fun seed -> not (List.isEmpty (violations (trace behavior (scenarioOf seed)))))
        caught Behavior.UnboundedWaitTwin scenarioOf |> Expect.isTrue "some seed catches the unbounded twin"
        caught Behavior.NeverClearsPulseTwin scenarioOf |> Expect.isTrue "some seed catches the never-clears twin"
        caught Behavior.CeilingForgetsParkTwin scenarioOf |> Expect.isTrue "some seed catches the forgets-the-park twin"
    ]

    testList "determinism and replay" [
      testProperty "the same seed gives the same final state" <| fun (seed: int) ->
        final Behavior.Real (scenarioOf seed) = final Behavior.Real (scenarioOf seed)

      testProperty "the same seed gives the same scenario" <| fun (seed: int) -> scenarioOf seed = scenarioOf seed
    ]
  ]
