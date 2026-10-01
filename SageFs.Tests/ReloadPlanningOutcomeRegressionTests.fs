/// Pins the "Patched(n, n)" residual named in sagefs-ux-roast.md §6.2 /
/// §11 Island C item 2: a save that ADDS a declaration and changes nothing
/// else must never be reported as patched into a running app that has no
/// old method there to redirect.
///
/// `ReloadPlanning.confirmPatchAsOutcome` is the fix — it classifies every
/// declaration the planner queued for patching by whether the running
/// process actually detoured onto it (`existed && detoured` = landed,
/// `existed && not detoured` = SignatureChanged, `not existed` =
/// NewDeclaration), so a brand-new declaration can never inflate the
/// numerator the way a raw `changed = List.length functions` tally would.
/// At the time this file was written, WorkerMain.fs's PatchInPlace branch
/// already calls `confirmPatchAsOutcome` rather than the raw tally the
/// roast quoted — this is a regression PIN on that pure boundary (owned by
/// another island), not a fix to it, using the same realistic
/// extractDecls/planReload pipeline ReloadPlanningTests.fs uses rather than
/// hand-built `SourceDecl` records.
module SageFs.Tests.ReloadPlanningOutcomeRegressionTests

open Expecto
open Expecto.Flip
open SageFs.Features.ReloadOutcome
open SageFs.Features.ReloadPlanning

module Outcome = SageFs.Features.ReloadOutcome.ReloadOutcome

/// Predates `confirmPatchAsOutcome`'s reached-the-running-process argument.
/// Every case below describes redirects that DID reach the entry point the
/// running app calls, so the reached-set is the redirect set. Named rather than
/// inlined so that assumption is visible instead of looking like a typo'd
/// duplicate argument.
let private confirmAllReached before patched reloaded =
  confirmPatchAsOutcome before patched reloaded reloaded

let private baselineSource = """module Demo.Web.Program

let render (items: int list) =
  sprintf "%d remaining" items.Length
"""

let private declsOf (source: string) =
  match extractDecls source with
  | Ok decls -> decls
  | Error reason -> failtestf "extractDecls failed: %s" reason

let private patchedDecls (before: FileDecls) (current: FileDecls) : SourceDecl list =
  match planReload before current with
  | ReloadPlan.PatchFunctions fs -> fs
  | ReloadPlan.PatchKeepingState (fs, first, rest) -> failtestf "expected a plain patch plan, got %A keeping %A" (fs |> List.map _.Name) (first :: rest)
  | ReloadPlan.RestartRequired (first, rest) -> failtestf "expected a patch plan, got restart %A" (first :: rest)

[<Tests>]
let patchedNNResidualTests =
  testList "ReloadPlanning.confirmPatchAsOutcome — the Patched(n,n) residual" [

    // WHY — THE regression, as one executable assertion: a save that ONLY
    // adds a declaration must never read as "patched into the running app".
    //
    // A new declaration used to be a restart (NoEffect, NewDeclaration). It no longer is: it is defined in FSI,
    // and the saved code that uses it is patched to call it. What has not changed is the pin: such a save is never
    // `Patched`, because nothing in the running app has run it. It is applied, and it ends never-entered.
    testCase "WHY - a save that adds one new declaration and changes nothing else is applied, never Patched, and ends never-entered when nothing runs it" <| fun _ ->
      let before = declsOf baselineSource
      let current = declsOf (baselineSource + "\nlet helper (x: int) = x + 1\n")
      let patched = patchedDecls before current
      patched |> List.map _.Name |> Expect.equal "helper is the only patch candidate" [ "helper" ]
      // Nothing was detoured: a brand-new function has no old method in the
      // running process for Harmony to redirect.
      let watched, outcome = confirmPatchLanding before patched [] []
      outcome |> Expect.equal "one definition applied, none seen running" (ReloadOutcome.PatchPending(1, 1, []))
      match SageFs.Features.PatchConfirmation.start (SageFs.Features.PatchConfirmation.watchedOfLanded watched []) outcome with
      | SageFs.Features.PatchConfirmation.Begun.Watching(_, watch) ->
        match SageFs.Features.PatchConfirmation.settle { Sightings = [] } watch with
        | SageFs.Features.PatchConfirmation.WatchStep.Settled(ReloadOutcome.NeverEntered("helper", [], 0, 1, [])) -> ()
        | other -> failtestf "nothing ran helper, so the save must end never-entered, got %A" other
      | other -> failtestf "the added function has to be watched, got %A" other

    testCase "WHY - the wire is never told Patched for a declaration nothing has run" <| fun _ ->
      let before = declsOf baselineSource
      let current = declsOf (baselineSource + "\nlet helper (x: int) = x + 1\n")
      let outcome = confirmAllReached before (patchedDecls before current) []
      match outcome with
      | ReloadOutcome.Patched _ -> failtest "a declaration nothing has run must not be reported as patched"
      | _ -> ()
      Outcome.describe outcome
      |> Expect.stringContains "the count says it is applied and unconfirmed" "not confirmed yet"

    // WHY — a save that BOTH changes an existing function and adds a new
    // one is applied as 2 of 2, and only the changed function is WATCHED. The added one has no probe and no
    // running code enters it until a caller does, so the caller's probe is what can make the save Patched.
    testCase "WHY - a save that changes one function and adds another watches the changed one, because the added one has nothing to watch" <| fun _ ->
      let before = declsOf baselineSource
      let edited =
        baselineSource.Replace(
          "sprintf \"%d remaining\" items.Length",
          "sprintf \"%d left\" items.Length")
        + "\nlet helper (x: int) = x + 1\n"
      let current = declsOf edited
      let patched = patchedDecls before current
      patched |> List.map _.Name |> List.sort |> Expect.equal "both candidates are queued" [ "helper"; "render" ]
      // Only `render` existed before, and only `render` was actually
      // detoured - Harmony has nothing to redirect `helper` onto. `helper` is applied by being defined; `render`
      // is what is watched, and it is what makes the save Patched once it has run.
      let watched, outcome = confirmPatchLanding before patched [ "Demo.Web.Program.render" ] [ "Demo.Web.Program.render" ]
      watched |> List.map _.Name |> Expect.equal "only the re-pointed function is watched" [ "render" ]
      match outcome with
      | ReloadOutcome.PatchPending(applied, considered, _) ->
        applied |> Expect.equal "the re-pointed function and the added one are both applied" 2
        considered |> Expect.equal "both candidates were considered" 2
      | other -> failtestf "a mixed save must be PatchPending(2, 2), not %A" other
  ]
