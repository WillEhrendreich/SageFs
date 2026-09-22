module SageFs.Tests.FsiEmitSimTests

/// DST over FSI's emit topology, modelled from the COMPILER SOURCE
/// (~/Work/fsharp-compiler-services), against the REAL
/// `ReloadPlanning.confirmPatchAsOutcome`.
///
/// This replaces learning one bit per 15-second host spawn. The bug these pin
/// was found by a real-host integration run reporting
/// `{"outcome":"Patched","patched":1,"considered":1}` while the app served the
/// pre-edit value — and narrowing it down from there was guesswork, because the
/// only observable was a served string.

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.FsiEmitSim

let private localTypeDecl = { Name = "localTypeHandler"; MentionsFileLocalType = true }
let private plainDecl = { Name = "plainHandler"; MentionsFileLocalType = false }

/// The exact shape the shape-matrix fixture exercises: an app running from the
/// COMPILED project assembly (no `#load` in front of it), then a save.
let private compiledAppThenSave style decls =
  { Decls = [ localTypeDecl; plainDecl ]
    Ops = [ Op.StartApp; Op.Save(decls, style) ] }

[<Tests>]
let fsiEmitSimTests =
  testList "FSI emit topology (DST)" [

    testCase "a flattened re-emit of a file-local-typed decl cannot pair the compiled copy, and must not claim it did" <| fun _ ->
      // Flattened emit re-declares the file's type in FSI, so the compiled
      // method's parameter type and the re-evaluated one's are different types
      // and `compatibleForDetour` rejects the pair. With no prior eval there is
      // nothing else to pair with either, so nothing moves.
      let observations =
        compiledAppThenSave EmitStyle.Flattened [ localTypeDecl ] |> run
      observations
      |> FsiEmitInvariants.all
      |> Expect.isEmpty "a save that moved nothing must not report that the process changed"

    testCase "a nested re-emit with open global pairs the compiled copy, and the app really does change" <| fun _ ->
      // This is what `CompilationContext.emitStableIdentity` produces after the
      // nested-module fix: the file-local type resolves to the COMPILED type,
      // so the compiled entry point pairs and the running app moves.
      let observations =
        compiledAppThenSave EmitStyle.NestedWithGlobalOpen [ localTypeDecl ] |> run
      observations
      |> List.map (fun o -> o.ActualChange)
      |> Expect.equal "the compiled entry point the app calls should have been re-pointed" [ true ]
      observations
      |> FsiEmitInvariants.all
      |> Expect.isEmpty "and the report should say so"

    // ── The one that pinned the shipped bug — FIXED ─────────────────────
    testCase "a SECOND flattened save re-points only the previous eval's copy — the app holds it, so the fix reaches it" <| fun _ ->
      // The save the user actually makes twice. In `--multiemit-` mode FSI
      // accumulates every eval in one assembly (fsi.fs:1818-1830), so by the
      // second save there IS an older FSI copy to pair with — and it pairs,
      // because both copies were re-declared the same way. The compiled copy
      // the app holds still cannot pair.
      let scenario =
        { Decls = [ localTypeDecl; plainDecl ]
          Ops =
            [ Op.StartApp
              Op.Save([ localTypeDecl ], EmitStyle.Flattened)
              Op.Save([ localTypeDecl ], EmitStyle.Flattened) ] }
      let observations = run scenario

      // Ground truth, tracked without consulting the decision under test.
      observations
      |> List.map (fun o -> o.ActualChange)
      |> Expect.equal "the app holds the compiled copy, which neither save re-pointed" [ false; false ]

      // FIXED: `AppHolds` recorded Compiled as what `StartApp` captured (no
      // save has landed on the compiled entry point since — the second save
      // only ever re-points a previous eval's OWN FSI copy, never Compiled),
      // so `reachedRunningProcess` correctly comes back empty for both saves
      // and the decision never over-claims. This assertion used to be
      // `Expect.isNonEmpty` — it was the deliberately pinned PROOF OF BROKEN,
      // and it flips the day the fix lands: closing the gap needed the app's
      // captured copy tracked at the point the handler table is built, not
      // inferred at detour time. That is `HotReloadCore.State.AppHolds`.
      observations
      |> FsiEmitInvariants.all
      |> Expect.isEmpty
        "given which copy the app holds, the same decision reports both saves honestly"

    // ── The scenario the pinned test above did NOT cover: an app started via
    //    `#load`, which is what the real host repro actually hit ──────────
    testCase "an app started via #load holds the loaded copy even though a compiled copy also exists; a later save reaches the LOADED copy" <| fun _ ->
      // `plainDecl` (no file-local type) mirrors the real bug precisely:
      // `WebAppFixture.Greeting.greeting` has a BCL-only signature, so even a
      // Flattened re-emit pairs the compiled copy fine — which is exactly why
      // the FIRST `#load` (Compiled -> Fragment 1) succeeds and the app, built
      // by a SECOND `#load` (`App.fs`) that resolves `Greeting.greeting` at
      // compile time to that just-loaded fragment, captures Fragment 1 rather
      // than the compiled copy. A save after that must reach FRAGMENT 1
      // specifically — re-pointing the (unreachable) compiled copy again
      // proves nothing.
      let scenario =
        { Decls = [ plainDecl ]
          Ops =
            [ Op.Save([ plainDecl ], EmitStyle.Flattened) // the init script's own #load
              Op.StartApp // App.fs's #load runs the app, capturing what greeting IS right now
              Op.Save([ plainDecl ], EmitStyle.Flattened) ] } // the file-edit save
      let observations = run scenario

      // Ground truth: the app holds Fragment 1 (captured at StartApp, AFTER
      // the load), and the LAST save's redirect reaches it.
      observations
      |> List.map (fun o -> o.ActualChange)
      |> Expect.equal "the load itself claims nothing (no app running yet); the edit reaches the app" [ false; true ]

      observations
      |> FsiEmitInvariants.all
      |> Expect.isEmpty "a #load'ed app's captured copy is reachable by name alone as much as a compiled app's is"

    testCase "a decl with no file-local type pairs the compiled copy even when flattened" <| fun _ ->
      // The control. `plainHandler`'s signature is BCL-only, so a flattened
      // re-emit still produces an identical signature and the compiled copy
      // pairs — which is why some cells of the shape matrix passed all along
      // and made the failure look intermittent rather than structural.
      let observations =
        compiledAppThenSave EmitStyle.Flattened [ plainDecl ] |> run
      observations
      |> List.map (fun o -> o.ActualChange)
      |> Expect.equal "a BCL-only signature pairs regardless of emit style" [ true ]
      observations
      |> FsiEmitInvariants.all
      |> Expect.isEmpty "and the report agrees"

    testCase "a save before the app starts claims nothing, because there is no running process to change" <| fun _ ->
      let scenario =
        { Decls = [ plainDecl ]
          Ops = [ Op.Save([ plainDecl ], EmitStyle.NestedWithGlobalOpen) ] }
      let observations = run scenario
      observations
      |> List.map (fun o -> o.ActualChange)
      |> Expect.equal "nothing is running, so nothing changed" [ false ]

    // ── Item 2: reporting Patched without reached-evidence must be
    //    impossible by construction, not merely avoided by convention ─────
    testCase "no AppHolds entry for a name means it can never be reported Patched, even though it redirected" <| fun _ ->
      // Same trace as the pinned test above, but run through the RETIRED
      // shape (`nameOnlyConfirmTwin`, kept in FsiEmitInvariants precisely so
      // a regression back to "any redirect counts as reaching the process"
      // is still catchable) to prove the invariant still has teeth: this is
      // what shipped, and it over-claims.
      let scenario =
        { Decls = [ localTypeDecl; plainDecl ]
          Ops =
            [ Op.StartApp
              Op.Save([ localTypeDecl ], EmitStyle.Flattened)
              Op.Save([ localTypeDecl ], EmitStyle.Flattened) ] }
      let broken = runWith FsiEmitInvariants.nameOnlyConfirmTwin scenario
      broken
      |> List.map (fun o -> o.ActualChange)
      |> Expect.equal "ground truth is unchanged by which decision function is asked" [ false; false ]
      let violations = broken |> FsiEmitInvariants.honestClaim
      violations
      |> Expect.isNonEmpty
        "the retired name-only shape over-claims Patched with zero evidence the app's copy moved"
      violations
      |> List.map (fun v -> v.ClaimedChange, v.ActualChange)
      |> Expect.contains
        "the violation is specifically an OVER-claim: reported changed, actually unchanged"
        (true, false)

      // And the FIXED decision, given the identical trace, never does this:
      // a name absent from `reachedRunningProcess` (no AppHolds evidence)
      // cannot be counted as landed, so `confirmPatchAsOutcome` routes it to
      // `NoEffect`/`RestartRequired` — `ofPatchCounts` makes `Patched(0, _)`
      // unrepresentable, and `landed` can only be non-empty when
      // `reachedRunningProcess` names the decl (see ReloadPlanning.fs).
      run scenario
      |> List.iter (fun o ->
        match o.Reported with
        | SageFs.Features.ReloadOutcome.ReloadOutcome.Patched(patched, _) ->
          (patched > 0)
          |> Expect.isTrue "Patched(0, _) must be unrepresentable"
        | _ -> ())
  ]
