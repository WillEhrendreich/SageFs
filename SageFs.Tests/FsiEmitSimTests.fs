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

    // ── The one that pins the shipped bug ────────────────────────────────
    testCase "a SECOND flattened save re-points only the previous eval's copy, which the running app never calls" <| fun _ ->
      // The save the user actually makes twice. In `--multiemit-` mode FSI
      // accumulates every eval in one assembly (fsi.fs:1818-1830), so by the
      // second save there IS an older FSI copy to pair with — and it pairs,
      // because both copies were re-declared the same way. The compiled copy
      // the app holds still cannot pair.
      //
      // `reloadedMethods` therefore contains "localTypeHandler", because FSI's
      // FSI_NNNN wrapper is stripped and every copy shares that one name. The
      // real decision counts it as landed and reports Patched 1 of 1, while
      // the app goes on serving the old body. That is the exact wire payload
      // observed against a real host.
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

      let violations = observations |> FsiEmitInvariants.honestClaim
      // PROOF OF BROKEN, pinned deliberately — this asserts the CURRENT
      // behaviour is WRONG, so it fails the day the gap closes.
      //
      // `confirmPatchAsOutcome` now TAKES the reached-the-running-process
      // evidence, but the worker cannot yet supply it honestly, so it passes
      // the redirect-set and this trace still over-claims. Two narrower rules
      // were measured against real hosts and both broke a working reload:
      // requiring a COMPILED entry point fails for a `#load`ed file (the app
      // holds an FSI copy), and "...or no compiled copy exists" fails too,
      // because a file can have a compiled copy AND be `#load`ed with the app
      // holding the FSI one. Assembly kind is not a proxy for what the app
      // captured.
      //
      // Closing this needs the captured copy recorded where the handler table
      // is BUILT, not inferred at detour time. When that lands, flip this to
      // `Expect.isEmpty` — do not delete it, and leave the ground-truth
      // assertion above untouched.
      violations
      |> Expect.isNonEmpty
        "PROOF OF BROKEN: the save re-points only a previous eval's copy and still reports the running process changed"

      violations
      |> List.map (fun v -> v.ClaimedChange, v.ActualChange)
      |> Expect.contains
        "the violation is specifically an OVER-claim: reported changed, actually unchanged"
        (true, false)

      // And the designed fix is CORRECT — only the evidence is missing. Run the
      // identical trace through the same real decision, given the reached-set it
      // now accepts, and the over-claim disappears. So the remaining work is
      // plumbing that fact from where the app captures its copy, not rethinking
      // the rule.
      runWithEvidence scenario
      |> FsiEmitInvariants.all
      |> Expect.isEmpty
        "given which copy the app holds, the same decision reports the save honestly"

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
  ]
