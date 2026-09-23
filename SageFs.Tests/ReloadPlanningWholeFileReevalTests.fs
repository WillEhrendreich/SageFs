/// `ReloadPlanning.confirmWholeFileReeval` is the fail-closed patch
/// confirmation for `ReloadRoute.ReevaluateWholeFile` — the save path with NO
/// known-good build baseline (the file was edited after the last build, was
/// never built, or is the exact shape a `.SageFs/init.fsx`-started app takes
/// during warmup). Before this function existed, that path counted a Harmony
/// redirect as a landed patch on name alone, with no check that the redirect
/// reached the SPECIFIC copy the running app calls — the same
/// `reachedRunningProcess` proof `confirmPatchAsOutcome` already requires on
/// the baselined path. Two real bugs shared this one gap:
///
///   1. A save that ADDS a declaration to a never-baselined file reported
///      "Hot reloaded N of M" (Patched) whenever any OTHER, pre-existing
///      function in the same file got redirected — the new declaration was
///      folded into the denominator and never named as the reason M wasn't
///      every declaration in the file.
///   2. On net11, a body edit to a `.SageFs/init.fsx`-started app (which
///      takes this exact no-baseline route during warmup) could redirect
///      SOME same-named copy of the function while the running app kept
///      calling a different one — and still be reported Patched, because
///      nothing on this path ever asked `reachedRunningProcess`.
///
/// These tests pin the fix: nothing lands without `reachedRunningProcess`
/// evidence, and a genuinely reached redirect still reports Patched (the fix
/// must not just stop patching anything, ever).
module SageFs.Tests.ReloadPlanningWholeFileReevalTests

open Expecto
open Expecto.Flip
open SageFs.Features.ReloadOutcome
open SageFs.Features.ReloadPlanning

let private fn (name: string) : SourceDecl =
  { Name = name
    Kind = DeclKind.FunctionDecl
    Access = DeclAccess.Public
    Container = [ "Demo"; "Program" ]
    Header = sprintf "let %s x =" name
    Text = sprintf "let %s x = x" name
    StartLine = 1
    EndLine = 1 }

let private typeDecl (name: string) : SourceDecl =
  { Name = name
    Kind = DeclKind.TypeDecl
    Access = DeclAccess.Public
    Container = [ "Demo"; "Program" ]
    Header = sprintf "type %s =" name
    Text = sprintf "type %s = { X: int }" name
    StartLine = 1
    EndLine = 1 }

[<Tests>]
let confirmWholeFileReevalTests =
  testList "ReloadPlanning.confirmWholeFileReeval" [

    testCase "WHY — a declaration invisible to Harmony (nothing to pair it with) is NewDeclaration, not Patched" <| fun _ ->
      // Nothing in `reloadedMethods` names this function at all: Harmony's
      // name-matcher found no old copy to pair it with, which is exactly
      // what a brand-new declaration looks like from the outside.
      match confirmWholeFileReeval [ fn "helper" ] [] [] with
      | ReloadOutcome.NoEffect(considered, [ RestartReason.NewDeclaration "helper" ]) ->
        considered |> Expect.equal "one candidate was in front of the process" 1
      | other -> failtestf "an unreachable-by-name declaration must be NoEffect/NewDeclaration, got %A" other

    // WHY — job #3's exact false-positive: Harmony redirected SOME copy of
    // the function (it's in `reloadedMethods`), but nothing confirms that
    // copy is the one the running app calls (`reachedRunningProcess` is
    // empty). Reporting Patched here is the net11 lie.
    testCase "WHY — a redirected function with no reached-running-process evidence is UnverifiedCopy, not Patched" <| fun _ ->
      match confirmWholeFileReeval [ fn "greeting" ] [ "Demo.Program.greeting" ] [] with
      | ReloadOutcome.NoEffect(considered, [ RestartReason.UnverifiedCopy "greeting" ]) ->
        considered |> Expect.equal "one candidate was in front of the process" 1
      | other -> failtestf "an unverified redirect must be NoEffect/UnverifiedCopy, got %A" other

    // WHY — the fix must not just refuse to ever patch again: a redirect
    // PROVEN (by reachedRunningProcess, the AppHolds-verified evidence) to
    // reach the copy the app calls still reports Patched.
    testCase "WHY — a redirect proven to reach the running process still reports Patched" <| fun _ ->
      match confirmWholeFileReeval [ fn "greeting" ] [ "Demo.Program.greeting" ] [ "Demo.Program.greeting" ] with
      | ReloadOutcome.Patched(patched, considered) ->
        patched |> Expect.equal "the one genuinely-reached function landed" 1
        considered |> Expect.equal "one candidate was in front of the process" 1
      | other -> failtestf "a proven redirect must be Patched(1, 1), got %A" other

    testCase "WHY — a mixed save reports the partial truth: proven functions land, unproven ones don't inflate the count" <| fun _ ->
      let candidates = [ fn "greeting"; fn "farewell" ]
      // `greeting` is proven reached; `farewell` was redirected but never
      // confirmed — the same partial-visibility limit `confirmPatchAsOutcome`
      // already accepts for `Patched` (see ReloadOutcome.withExtraMisses's own
      // doc comment): a landed patch is reported, and the specific reason a
      // sibling candidate missed is not attached to it.
      match confirmWholeFileReeval candidates [ "Demo.Program.greeting"; "Demo.Program.farewell" ] [ "Demo.Program.greeting" ] with
      | ReloadOutcome.Patched(patched, considered) ->
        patched |> Expect.equal "only the proven redirect counts as landed" 1
        considered |> Expect.equal "both candidates were considered" 2
      | other -> failtestf "a mixed save must be Patched(1, 2), got %A" other

    testCase "WHY — types, mutable bindings, the entry point and nested modules are never counted as patch candidates" <| fun _ ->
      // These kinds have their own dedicated handling elsewhere (types force
      // a restart outright; mutable bindings go through the accessor-pair
      // escalation path). Counting them here would double-count against a
      // denominator this function does not own.
      let candidates = [ typeDecl "TodoItem"; fn "helper" ]
      match confirmWholeFileReeval candidates [] [] with
      | ReloadOutcome.NoEffect(considered, [ RestartReason.NewDeclaration "helper" ]) ->
        considered |> Expect.equal "the type is excluded from the candidate count" 1
      | other -> failtestf "a type in the file must not become a patch candidate, got %A" other

    testCase "WHY — no candidates at all is a non-event, not a claimed success" <| fun _ ->
      match confirmWholeFileReeval [] [] [] with
      | ReloadOutcome.NoEffect(0, []) -> ()
      | other -> failtestf "an empty candidate set must be NoEffect(0, []), got %A" other

    testCase "WHY — the wire must never tell the page to refresh for an unverified copy" <| fun _ ->
      let outcome = confirmWholeFileReeval [ fn "greeting" ] [ "Demo.Program.greeting" ] []
      ReloadOutcome.shouldRefreshBrowser outcome
      |> Expect.isFalse "nothing proves the running process changed, so refreshing would serve who-knows-what"
  ]
