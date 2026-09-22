/// DST for rule 2's reflection reads, in every mode. Folds the REAL
/// classifier, ledger and save check (SageFs.Simulation/ReflectionReadSim.fs)
/// through thousands of seeded interleavings of reflective reads from many
/// callers, reads inside other callers' reflective calls, async hops between
/// threads, callers turning up mid-run, modes switched mid-run, and saves
/// landing anywhere. Every failure prints its seed; the same seed replays the
/// same trace.
module SageFs.Tests.ReflectionReadSimTests

open Expecto
open Expecto.Flip
open SageFs.Middleware.ValueReads
open SageFs.Simulation
open SageFs.Simulation.ReflectionReadSim

let private seeds = [ 1 .. 3000 ]

let private realTraces = lazy (seeds |> List.map (scenarioOf >> run))

let private report (label: string) (violations: ReflectionReadInvariants.Violation list) =
  match violations with
  | [] -> ()
  | _ ->
    let shown = violations |> List.truncate 5 |> List.map (fun v -> sprintf "  seed=%d: %s" v.Seed v.Why) |> String.concat "\n"
    failtestf "%s: %d violation(s). Replay with ReflectionReadSim.run (scenarioOf <seed>).\n%s" label violations.Length shown

let private firstBroken (wiring: Wiring) (invariant: Trace -> ReflectionReadInvariants.Violation list) =
  seeds |> List.tryPick (fun seed ->
    match scenarioOf seed |> runWith wiring |> invariant with
    | [] -> None
    | v :: _ -> Some v)

let private sum (f: Trace -> int) = realTraces.Value |> List.sumBy f

let private savesWhere (f: SaveObservation -> bool) (t: Trace) = t.Saves |> List.filter f |> List.length

[<Tests>]
let reflectionReadSimTests =
  testList "reflection reads DST" [
    testCase "SAFETY — in every mode, and across mode switches, no save says Patched while a reflective read holds the old value" <| fun _ ->
      realTraces.Value |> List.collect ReflectionReadInvariants.neverPatchedOverAStaleCopy |> report "stale copy behind a Patched"

    testCase "SAFETY — a lapse makes every tracked value can't-tell: no save says Patched while the entry watch is down, in any mode" <| fun _ ->
      realTraces.Value |> List.collect ReflectionReadInvariants.neverPatchedWhileLapsed |> report "Patched while lapsed"

    testCase "ATTRIBUTION — a reflective read is only ever filed under the caller that made it, slot or walk, across threads" <| fun _ ->
      realTraces.Value |> List.collect ReflectionReadInvariants.slotNeverNamesTheWrongCaller |> report "wrong caller"

    testCase "REPLAY — the same seed gives the same trace, step for step" <| fun _ ->
      for seed in seeds |> List.truncate 300 do
        let a = scenarioOf seed |> run
        let b = scenarioOf seed |> run
        (a.Saves, a.Steps) |> Expect.equal (sprintf "seed %d replays exactly" seed) (b.Saves, b.Steps)

    testCase "NON-VACUOUS — the battery patches and refuses in every mode, hits slots, walks, nests reads and switches modes" <| fun _ ->
      let inMode mode f = sum (savesWhere (fun o -> o.Mode = mode && f o))
      let patchedIn mode = inMode mode (fun o -> o.Result = SaveResult.Patched)
      let refusedIn mode =
        inMode mode (fun o ->
          match o.Result with
          | SaveResult.RefusedBefore _
          | SaveResult.RefusedAfterPatch _ -> true
          | SaveResult.Patched -> false)
      let slotHits = sum _.SlotHits
      let walks = sum _.Walks
      let nested = sum _.NestedReads
      let switches = sum _.ModeSwitches
      let lapses = sum _.Lapses
      let refusedWhileLapsed =
        realTraces.Value |> List.sumBy (fun t -> t.Saves |> List.filter (fun o -> o.Lapsed) |> List.length)
      let summary =
        sprintf "patched exact=%d mark=%d probe=%d, refused exact=%d mark=%d probe=%d, slotHits=%d walks=%d nested=%d switches=%d lapses=%d savesWhileLapsed=%d"
          (patchedIn ReflectionReadMode.ExactEveryRead) (patchedIn ReflectionReadMode.MarkOnReflect) (patchedIn ReflectionReadMode.ProbeCallers)
          (refusedIn ReflectionReadMode.ExactEveryRead) (refusedIn ReflectionReadMode.MarkOnReflect) (refusedIn ReflectionReadMode.ProbeCallers)
          slotHits walks nested switches lapses refusedWhileLapsed
      printfn "reflection reads DST coverage: %s" summary
      for mode in ReflectionReadMode.all do
        (patchedIn mode, 20) |> Expect.isGreaterThan (sprintf "%A patches sometimes (%s)" mode summary)
        (refusedIn mode, 20) |> Expect.isGreaterThan (sprintf "%A refuses sometimes (%s)" mode summary)
      (slotHits, 100) |> Expect.isGreaterThan (sprintf "rewired callers are named from the slot (%s)" summary)
      (walks, 100) |> Expect.isGreaterThan (sprintf "unknown callers walk (%s)" summary)
      (nested, 100) |> Expect.isGreaterThan (sprintf "reads happen inside other callers' reflective calls (%s)" summary)
      (switches, 100) |> Expect.isGreaterThan (sprintf "modes switch mid-run (%s)" summary)
      (lapses, 20) |> Expect.isGreaterThan (sprintf "keep-tiering scenarios actually inject a lapse (%s)" summary)
      (refusedWhileLapsed, 20) |> Expect.isGreaterThan (sprintf "a save is actually attempted while lapsed, so the invariant isn't vacuous (%s)" summary)

    testCase "TWIN — a slot held for the caller's whole call pins a read inside the target on the outer caller, and ATTRIBUTION catches it" <| fun _ ->
      match firstBroken ReflectionReadInvariants.staleSlotTwin ReflectionReadInvariants.slotNeverNamesTheWrongCaller with
      | Some v -> printfn "stale-slot twin broken at seed %d: %s" v.Seed v.Why
      | None -> failtest "the twin that trusts a stale slot should name the wrong caller for some seed"

    testCase "TWIN — that same stale slot gets a kept copy reported Patched, and SAFETY catches it" <| fun _ ->
      match firstBroken ReflectionReadInvariants.staleSlotTwin ReflectionReadInvariants.neverPatchedOverAStaleCopy with
      | Some v -> printfn "stale-slot twin patched over a copy at seed %d: %s" v.Seed v.Why
      | None -> failtest "the stale-slot twin should report a Patched over a stale copy for some seed"
  ]
