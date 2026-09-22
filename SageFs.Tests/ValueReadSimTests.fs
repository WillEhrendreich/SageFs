/// DST for rule 2: a redefined value is Patched only when nothing in the app
/// can still hold the old one. Folds the REAL classifier, ledger and save check
/// (see SageFs.Simulation/ValueReadSim.fs) through thousands of seeded
/// interleavings of startup reads, requests, lazies forced late, the startup
/// window closing, probes coming off, and saves landing anywhere, including
/// while the app is still starting. Every failure prints its seed; the same
/// seed replays the same trace.
module SageFs.Tests.ValueReadSimTests

open Expecto
open Expecto.Flip
open SageFs.Middleware.ValueReads
open SageFs.Simulation
open SageFs.Simulation.ValueReadSim

let private seeds = [ 1 .. 4000 ]

let private battery (wiring: Wiring) = seeds |> List.map (scenarioOf >> runWith wiring)

let private realTraces = lazy (battery real)

let private report (label: string) (violations: ValueReadInvariants.Violation list) =
  match violations with
  | [] -> ()
  | _ ->
    let seedsHit = violations |> List.map _.Seed |> List.distinct
    let shown =
      violations
      |> List.truncate 5
      |> List.map (fun v -> sprintf "  seed=%d save=%d value=%s: %s" v.Seed v.Save v.Value v.Why)
      |> String.concat "\n"
    failtestf "%s: %d violation(s) over %d seed(s). Replay with ValueReadSim.run (scenarioOf <seed>).\n%s" label violations.Length seedsHit.Length shown

/// The first seed a twin breaks, so a test can print it and a human can replay it.
let private firstBroken (wiring: Wiring) (invariant: Trace -> ValueReadInvariants.Violation list) =
  seeds |> List.tryPick (fun seed ->
    match scenarioOf seed |> runWith wiring |> invariant with
    | [] -> None
    | v :: _ -> Some v)

let private count (f: Trace -> int) = realTraces.Value |> List.sumBy f

let private savesWhere (f: SaveObservation -> bool) (t: Trace) = t.Saves |> List.filter f |> List.length

/// Did a save's evidence check run before the window was closed by its own
/// step, i.e. while the app was still starting?
let private savedWhileStarting (t: Trace) =
  let firstSave = t.Steps |> List.tryFindIndex (function Step.SaveQuery _ -> true | _ -> false)
  let windowEnd = t.Steps |> List.tryFindIndex (function Step.EndWindowInLedger -> true | _ -> false)
  match firstSave, windowEnd with
  | Some x, Some y -> x < y
  | Some _, None -> true
  | None, _ -> false

[<Tests>]
let valueReadSimTests =
  testList "value reads DST" [
    testCase "SAFETY — no interleaving gets a Patched while an escaped read (at startup, or a lazy or cache after it) still holds the old value" <| fun _ ->
      realTraces.Value |> List.collect ValueReadInvariants.neverPatchedOverAStaleCopy |> report "stale copy behind a Patched"

    testCase "BANNER — a value startup captured in a closure always restarts, and the reason names the reader and the closure it built" <| fun _ ->
      realTraces.Value |> List.collect ValueReadInvariants.bannerAlwaysRestartsAndSaysWhere |> report "banner"

    testCase "REPLAY — the same seed gives the same trace, step for step" <| fun _ ->
      for seed in seeds |> List.truncate 500 do
        let a = scenarioOf seed |> run
        let b = scenarioOf seed |> run
        (a.Saves, a.Steps) |> Expect.equal (sprintf "seed %d replays exactly" seed) (b.Saves, b.Steps)

    testCase "NON-VACUOUS — the battery really does patch, refuse, race, and land saves during startup" <| fun _ ->
      let patched = count (savesWhere (fun o -> o.Result = SaveResult.Patched))
      let refusedBefore = count (savesWhere (fun o -> match o.Result with SaveResult.RefusedBefore _ -> true | _ -> false))
      let racedAfterPatch = count (savesWhere (fun o -> match o.Result with SaveResult.RefusedAfterPatch _ -> true | _ -> false))
      let heldAfterStartup =
        count (savesWhere (fun o ->
          match o.Result with
          | SaveResult.RefusedBefore(ValueVerdict.HeldBy(_, ReadSeen.AfterStartup))
          | SaveResult.RefusedAfterPatch(ValueVerdict.HeldBy(_, ReadSeen.AfterStartup)) -> true
          | _ -> false))
      let bannerRefused = count (savesWhere (fun o -> o.Value = Banner && o.BannerCaptured))
      let savedDuringStartup = count (fun t -> match savedWhileStarting t with true -> 1 | false -> 0)
      let recordAfterWindow = count _.LateWatchReports
      let outOfScope = count (savesWhere (fun o -> not (List.isEmpty o.OutOfScopeHolders)))
      let summary =
        sprintf "patched=%d refusedBefore=%d racedAfterPatch=%d heldAfterStartup=%d bannerRefused=%d savedDuringStartup=%d recordAfterWindow=%d outOfScope=%d"
          patched refusedBefore racedAfterPatch heldAfterStartup bannerRefused savedDuringStartup recordAfterWindow outOfScope
      printfn "value reads DST coverage: %s" summary
      (patched, 100) |> Expect.isGreaterThan (sprintf "some saves patch (%s)" summary)
      (refusedBefore, 100) |> Expect.isGreaterThan (sprintf "some are refused up front (%s)" summary)
      (racedAfterPatch, 0) |> Expect.isGreaterThan (sprintf "a read races the patch sometimes, and the recheck catches it (%s)" summary)
      (heldAfterStartup, 0) |> Expect.isGreaterThan (sprintf "a lazy or request read after startup holds the value sometimes (%s)" summary)
      (bannerRefused, 100) |> Expect.isGreaterThan (sprintf "the banner gets saved after it was captured (%s)" summary)
      (savedDuringStartup, 100) |> Expect.isGreaterThan (sprintf "saves land while the app is still starting (%s)" summary)
      (recordAfterWindow, 0) |> Expect.isGreaterThan (sprintf "a getter watch report lands after the window closed (%s)" summary)

    testCase "TWIN — ignoring reads after the startup window gets a lazy forced later patched over, and SAFETY catches it" <| fun _ ->
      match firstBroken ValueReadInvariants.ignoreReadsAfterWindowTwin ValueReadInvariants.neverPatchedOverAStaleCopy with
      | Some v -> printfn "ignore-reads-after-window twin broken at seed %d: %s" v.Seed v.Why
      | None -> failtest "the twin that ignores reads after startup should report a Patched over a stale copy for some seed"

    testCase "TWIN — treating a stored value like a dead local lets the banner through, and BANNER catches it" <| fun _ ->
      match firstBroken ValueReadInvariants.storedLikeDeadLocalTwin ValueReadInvariants.bannerAlwaysRestartsAndSaysWhere with
      | Some v -> printfn "stored-like-dead-local twin broken at seed %d: %s" v.Seed v.Why
      | None -> failtest "the twin that treats stloc as thrown away should patch the banner for some seed"

    testCase "TWIN — checking the evidence once and patching misses a read that races the patch, and SAFETY catches it" <| fun _ ->
      match firstBroken ValueReadInvariants.checkOnceTwin ValueReadInvariants.neverPatchedOverAStaleCopy with
      | Some v -> printfn "check-once twin broken at seed %d: %s" v.Seed v.Why
      | None -> failtest "the twin with no recheck should report a Patched over a copy made between the check and the patch for some seed"

    testCase "TWIN — a getter watch that files the caller after the read (a postfix) lets a reflection read slip past a save, and SAFETY catches it" <| fun _ ->
      match firstBroken ValueReadInvariants.postfixWatchTwin ValueReadInvariants.neverPatchedOverAStaleCopy with
      | Some v -> printfn "postfix-watch twin broken at seed %d: %s" v.Seed v.Why
      | None -> failtest "the twin whose watch records after the read should report a Patched over a stale copy for some seed"
  ]
