/// The daemon degrading honestly before the OS OOM-kills it: `MemorySupervisor`
/// is the pure policy that decides what to shed and when. These tests pin the
/// three named shapes from the brief this module was written to close
/// (a starving daemon with five sessions, a healthy daemon, three faulted
/// sessions with plenty of memory), the hysteresis that keeps the level from
/// flapping at a boundary, and that a user-active session is never a shedding
/// target.
module SageFs.Tests.MemorySupervisorTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.MemorySupervisor

let private mkSession id status isUserActive : SessionSnapshot =
  { Id = id; Status = status; IsUserActive = isUserActive }

let private machine daemonRss available total : MachineStats =
  { DaemonRssBytes = daemonRss; MachineAvailableBytes = available; MachineTotalBytes = total }

[<Tests>]
let namedShapeTests =
  testList "MemorySupervisor.step — named shapes from the brief" [

    testCase "a 55GB daemon with 1GB free and five sessions: reap, shed idle, refuse" <| fun () ->
      let critical = machine 55_000_000_000L 1_000_000_000L 62_000_000_000L
      let sessions =
        [ mkSession "active-viewed" SessionMemoryStatus.Active true
          mkSession "idle-2h" (SessionMemoryStatus.Idle(TimeSpan.FromHours 2.0)) false
          mkSession "idle-5m" (SessionMemoryStatus.Idle(TimeSpan.FromMinutes 5.0)) false
          mkSession "dead" SessionMemoryStatus.Dead false
          mkSession "active-unviewed" SessionMemoryStatus.Active false ]
      let decision = step defaultThresholds MemoryPressure.Normal critical sessions
      decision.Level |> Expect.equal "1.6% available is under the 8% refuse floor" MemoryPressure.Critical
      decision.Actions |> Expect.contains "the dead session is reaped" (ShedAction.ReapDeadSessions [ "dead" ])
      decision.Actions
      |> Expect.contains "only the idle session past the 30-minute floor is shed, not the 5-minute one" (ShedAction.StopIdleSessions [ "idle-2h" ])
      let refused =
        decision.Actions
        |> List.exists (function ShedAction.RefuseNewSessions _ -> true | _ -> false)
      refused |> Expect.isTrue "new sessions are refused at this pressure"
      decision.Reason |> Expect.isSome "a refusal this severe must carry a reason"

    testCase "a healthy daemon still reaps a dead session, and does nothing else" <| fun () ->
      let healthy = machine 500_000_000L 40_000_000_000L 64_000_000_000L
      let sessions =
        [ mkSession "active" SessionMemoryStatus.Active true
          mkSession "dead" SessionMemoryStatus.Dead false ]
      let decision = step defaultThresholds MemoryPressure.Normal healthy sessions
      decision.Level |> Expect.equal "62.5% available is nowhere near either floor" MemoryPressure.Normal
      decision.Actions |> Expect.equal "reap only — nothing else to do at Normal" [ ShedAction.ReapDeadSessions [ "dead" ] ]

    testCase "three faulted sessions with plenty of memory: still reaped, unconditionally" <| fun () ->
      let healthy = machine 500_000_000L 40_000_000_000L 64_000_000_000L
      let sessions =
        [ mkSession "f1" SessionMemoryStatus.Dead false
          mkSession "f2" SessionMemoryStatus.Dead false
          mkSession "f3" SessionMemoryStatus.Dead false ]
      let decision = step defaultThresholds MemoryPressure.Normal healthy sessions
      decision.Level |> Expect.equal "plenty of memory" MemoryPressure.Normal
      decision.Actions |> Expect.equal "reaping the dead has no downside at any pressure level" [ ShedAction.ReapDeadSessions [ "f1"; "f2"; "f3" ] ]
  ]

[<Tests>]
let hysteresisTests =
  testList "MemorySupervisor.nextLevel — hysteresis, not a single threshold" [

    testCase "hovering right at the shed-enter boundary does not flap the level every sample" <| fun () ->
      let readings = [ 0.25; 0.19; 0.21; 0.19; 0.205; 0.195; 0.19 ]
      let levels = readings |> List.scan (nextLevel defaultThresholds) MemoryPressure.Normal
      // Once it enters SheddingIdle it must STAY there while readings hover
      // below ShedExitFrac (0.30) — never bounce back to Normal just because
      // one sample ticked back above ShedEnterFrac (0.20).
      let enteredAt = levels |> List.findIndex (fun l -> l = MemoryPressure.Tight)
      levels
      |> List.skip enteredAt
      |> List.forall (fun l -> l = MemoryPressure.Tight)
      |> Expect.isTrue "no oscillation once inside the shed band, given readings that never clear the exit threshold"

    testCase "a genuine recovery past the exit threshold clears the level" <| fun () ->
      let levels =
        [ 0.15; 0.35 ] |> List.scan (nextLevel defaultThresholds) MemoryPressure.Tight
      levels |> List.last |> Expect.equal "0.35 clears the 0.30 exit threshold" MemoryPressure.Normal

    testCase "a cliff straight from Normal to refuse-range reaches RefusingAdmission in one step, no delay" <| fun () ->
      nextLevel defaultThresholds MemoryPressure.Normal 0.01
      |> Expect.equal "a genuine emergency is never held back by hysteresis" MemoryPressure.Critical

    testProperty "the level is always one of the three defined cases, for any fraction" <|
      fun (NormalFloat f) ->
        let level = nextLevel defaultThresholds MemoryPressure.Normal f
        match level with
        | MemoryPressure.Normal
        | MemoryPressure.Tight
        | MemoryPressure.Critical -> true
  ]

[<Tests>]
let neverTouchesActiveTests =
  testList "MemorySupervisor.step — never sheds a user-active session" [

    testCase "every session active: refuses and reaps, but proposes stopping nobody" <| fun () ->
      let critical = machine 55_000_000_000L 1_000_000_000L 62_000_000_000L
      let sessions =
        [ mkSession "a" (SessionMemoryStatus.Idle(TimeSpan.FromHours 5.0)) true
          mkSession "b" (SessionMemoryStatus.Idle(TimeSpan.FromHours 5.0)) true ]
      let decision = step defaultThresholds MemoryPressure.Normal critical sessions
      decision.Actions
      |> List.exists (function ShedAction.StopIdleSessions _ -> true | _ -> false)
      |> Expect.isFalse "both idle sessions are user-active — never a shedding target"

    testCase "idle-eligible but freshly touched (under the idle floor) is never shed" <| fun () ->
      let pressured = machine 20_000_000_000L 3_000_000_000L 62_000_000_000L
      let sessions = [ mkSession "fresh" (SessionMemoryStatus.Idle(TimeSpan.FromMinutes 2.0)) false ]
      let decision = step defaultThresholds MemoryPressure.Normal pressured sessions
      decision.Actions
      |> List.exists (function ShedAction.StopIdleSessions _ -> true | _ -> false)
      |> Expect.isFalse "2 minutes idle is well under the 30-minute floor"
  ]

[<Tests>]
let refusalReasonTests =
  testList "MemorySupervisor.step — a refusal always carries a reason" [
    testCase "RefuseNewSessions always carries a non-empty, human-readable reason" <| fun () ->
      let critical = machine 55_000_000_000L 1_000_000_000L 62_000_000_000L
      let decision = step defaultThresholds MemoryPressure.Normal critical []
      match decision.Actions |> List.tryPick (function ShedAction.RefuseNewSessions r -> Some r | _ -> None) with
      | None -> failtest "expected a refusal at this pressure"
      | Some reason -> (reason.Length > 0) |> Expect.isTrue "the reason must actually say something"
  ]
