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
  | ReloadPlan.RestartRequired (first, rest) -> failtestf "expected a patch plan, got restart %A" (first :: rest)

[<Tests>]
let patchedNNResidualTests =
  testList "ReloadPlanning.confirmPatchAsOutcome — the Patched(n,n) residual" [

    // WHY — THE regression, as one executable assertion: a save that ONLY
    // adds a declaration must never read as "patched into the running app".
    testCase "WHY — a save that adds one new declaration and changes nothing else is NoEffect, not Patched" <| fun _ ->
      let before = declsOf baselineSource
      let current = declsOf (baselineSource + "\nlet helper (x: int) = x + 1\n")
      let patched = patchedDecls before current
      patched |> List.map _.Name |> Expect.equal "helper is the only patch candidate" [ "helper" ]
      // Nothing was detoured: a brand-new function has no old method in the
      // running process for Harmony to redirect.
      match confirmAllReached before patched [] with
      | ReloadOutcome.NoEffect(considered, [ RestartReason.NewDeclaration "helper" ]) ->
        considered |> Expect.equal "one definition was put in front of the process" 1
      | other -> failtestf "adding one declaration must be NoEffect with NewDeclaration, got %A" other

    testCase "WHY — the wire must never tell the page to refresh for a declaration it cannot reach" <| fun _ ->
      let before = declsOf baselineSource
      let current = declsOf (baselineSource + "\nlet helper (x: int) = x + 1\n")
      let outcome = confirmAllReached before (patchedDecls before current) []
      Outcome.shouldRefreshBrowser outcome
      |> Expect.isFalse "the running process did not change, so refreshing would serve stale-but-different code"
      Outcome.describe outcome
      |> Expect.stringContains "the count reads as the non-event it is, not a success" "0 of 1"

    // WHY — a save that BOTH changes an existing function and adds a new
    // one must report the partial truth: 1 of 2 landed, not 2 of 2 — the
    // exact shape of the roast's "Patched(n, n)" complaint.
    testCase "WHY — a save that changes one function and adds another reports the partial truth" <| fun _ ->
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
      // detoured — Harmony has nothing to redirect `helper` onto.
      match confirmAllReached before patched [ "Demo.Web.Program.render" ] with
      | ReloadOutcome.Patched(patchedCount, considered) ->
        patchedCount |> Expect.equal "only the existing, detoured function landed" 1
        considered |> Expect.equal "both candidates were considered" 2
      | other -> failtestf "a mixed save must be Patched(1, 2), not %A" other
  ]
