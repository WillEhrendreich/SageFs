namespace SageFs.Simulation

open SageFs.Middleware.ValueReads
open SageFs.Simulation.ValueReadSim

/// Invariants over `ValueReadSim`, and the twins that prove they bite.
module ValueReadInvariants =

  type Violation =
    { Seed: int
      Save: int
      Value: string
      Why: string }

  let private violation (t: Trace) (o: SaveObservation) (why: string) =
    { Seed = t.Scenario.Seed; Save = o.Save; Value = o.Value; Why = why }

  /// THE promise: Patched means nothing the app holds still has the old value.
  /// A copy counts if its read's shape can hold one (ground truth, not the
  /// classifier), whether it was made at startup or later by a lazy or a
  /// cache, and whether or not the read raced the patch.
  let neverPatchedOverAStaleCopy (t: Trace) : Violation list =
    t.Saves
    |> List.choose (fun o ->
      match o.Result, o.StaleHolders with
      | SaveResult.Patched, (_ :: _ as holders) ->
        Some(violation t o (sprintf "reported Patched while %s still hold a copy of the old value" (String.concat ", " holders)))
      | _ -> None)

  /// A `banner`-shaped capture (startup code carrying the value through a
  /// local into a new closure) always comes out as a restart, and the reason
  /// names the reader and what it built around the value.
  let bannerAlwaysRestartsAndSaysWhere (t: Trace) : Violation list =
    t.Saves
    |> List.choose (fun o ->
      match o.Value = Banner && o.BannerCaptured, o.Result with
      | false, _ -> None
      | true, SaveResult.Patched -> Some(violation t o "the banner was captured by a closure at startup and the save still said Patched")
      | true, SaveResult.RefusedBefore verdict
      | true, SaveResult.RefusedAfterPatch verdict ->
        match verdict with
        | ValueVerdict.HeldBy(site, seen) ->
          let text = describeHolder site seen
          match text.Contains "startup.banner.closure" && text.Contains "App.State+handlers@78-9" with
          | true -> None
          | false -> Some(violation t o (sprintf "the restart reason doesn't name the capturing site: %s" text))
        | other -> Some(violation t o (sprintf "a restart for the banner should name who holds it, got %A" other)))

  let all (t: Trace) : Violation list =
    neverPatchedOverAStaleCopy t @ bannerAlwaysRestartsAndSaysWhere t

  // ── twins: the naive rules, each reintroduced in one place ─────────────────

  /// "Reads after the startup window don't matter": the ledger drops anything
  /// it hears once the window has closed. A lazy forced after startup is then
  /// invisible.
  let ignoreReadsAfterWindowTwin : Wiring =
    { real with
        LedgerStep =
          fun ledger event ->
            match ledger.Window, event with
            | StartupWindow.Closed, LedgerEvent.ReaderRan _
            | StartupWindow.Closed, LedgerEvent.GetterRead _ -> ledger
            | _ -> Ledger.step ledger event }

  /// "A stored value and a dead local are the same thing": any `stloc` counts
  /// as thrown away, without following the local to where it goes. The
  /// banner's `stloc; ldloc; newobj` is then invisible.
  let storedLikeDeadLocalTwin : Wiring =
    { real with
        Classify =
          fun instrs ->
            let op = instrs.[1].Op
            match op = System.Reflection.Emit.OpCodes.Stloc_0 with
            | true -> ReadFate.Discarded
            | false -> realClassify instrs }

  /// "Check once, then patch": no second look at the evidence after the patch,
  /// so a read that lands between the check and the patch goes unseen.
  let checkOnceTwin : Wiring = { real with Recheck = false }

  /// "Record the caller after the getter returns" (a Harmony postfix): a read
  /// by code that has no read in its own IL (reflection) gets its value, then
  /// a save checks, patches and rechecks, and only then is the read filed.
  let postfixWatchTwin : Wiring = { real with Watch = WatchTiming.AfterTheRead }
