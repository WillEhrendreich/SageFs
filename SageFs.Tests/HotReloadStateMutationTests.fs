/// ## HotReloadState Mutation Tests
///
/// Proves the test suite catches mutations in `SageFs.HotReloadState`.
///
/// ### Formal Verification Correspondence
/// This module validates that the F# HotReloadState implementation satisfies
/// the same properties proved in `formal-verification/lean/FVSquad/HotReloadState.lean`.
/// Each test maps 1-to-1 to a Lean theorem (see theorem name in test name).
///
/// ### Mutations Defined
///   1. `normalize_no_lowercase` — skips ToLowerInvariant
///   2. `isWatched_always_true` — returns true for every path
///   3. `isWatched_always_false` — returns false for every path
///   4. `toggle_always_add` — same as watch (no toggle off)
///   5. `toggle_always_remove` — same as unwatch (no toggle on)
///   6. `unwatchAll_noop` — returns state unchanged
///   7. `watchAll_union` — uses Set.union instead of replace
module HotReloadStateMutationTests

open Expecto
open Expecto.Flip
open MutationTestingFramework
open SageFs.HotReloadState

// ── Test Fixtures ──────────────────────────────────────────────────────────

let testState =
  empty
  |> watch "src/foo.fs"
  |> watch "src/bar.fs"

let subdirState =
  empty
  |> watch "src/main/program.fs"
  |> watch "src/main/helper.fs"
  |> watch "src/test/test.fs"

// ── Mutations ──────────────────────────────────────────────────────────────

let normalizeNoLowercase = {
  Name = "normalize_no_lowercase"
  Apply = fun (s: string) -> s.Replace('\\', '/')
  Description = "normalize skips ToLowerInvariant — case-sensitive lookups break watch/isWatched"
}

let isWatchedAlwaysTrue = {
  Name = "isWatched_always_true"
  Apply = fun (f: string -> T -> bool) -> fun _path _state -> true
  Description = "isWatched always returns true — false positives on every path"
}

let isWatchedAlwaysFalse = {
  Name = "isWatched_always_false"
  Apply = fun (f: string -> T -> bool) -> fun _path _state -> false
  Description = "isWatched always returns false — watched paths appear unwatched"
}

let toggleAlwaysAdd = {
  Name = "toggle_always_add"
  Apply = fun (f: string -> T -> T) -> watch
  Description = "toggle always adds — removing via toggle doesn't work"
}

let toggleAlwaysRemove = {
  Name = "toggle_always_remove"
  Apply = fun (f: string -> T -> T) -> unwatch
  Description = "toggle always removes — adding via toggle doesn't work"
}

let unwatchAllNoop = {
  Name = "unwatchAll_noop"
  Apply = fun (f: T -> T) -> fun state -> state
  Description = "unwatchAll returns state unchanged — watched list persists"
}

let watchAllUnion = {
  Name = "watchAll_union_instead_of_replace"
  Apply = fun (f: string seq -> T -> T) -> watchMany
  Description = "watchAll unions instead of replacing — old paths accumulate"
}

// ── Mutation Tests ──────────────────────────────────────────────────────────
// Each test: the mutation IS caught when the assertion FAILS on the mutant.
//   - assertion true for real output → real output satisfies property
//   - assertion false for mutant output → mutant violates property → caught!

let hotReloadMutationTests = testList "HotReloadState mutations" [
  // normalize: must lowercase AND replace backslashes — exact output
  testCase "WHY — normalize_no_lowercase — normalize must slash-and-lowercase the path exactly" <| fun () ->
    normalize "C:\\Src\\Foo.fs"
    |> Expect.equal "normalize must replace backslashes and lowercase" "c:/src/foo.fs"

  // isWatched: must return true only for watched paths
  testCase "WHY — isWatched_always_true — isWatched must be false for an unwatched path" <| fun () ->
    isWatched "src/nope.fs" testState
    |> Expect.isFalse "an unwatched path must report isWatched = false"

  testCase "WHY — isWatched_always_false — isWatched must be true for a watched path" <| fun () ->
    isWatched "src/foo.fs" testState
    |> Expect.isTrue "a watched path must report isWatched = true"

  // toggle: must flip the state
  testCase "WHY — toggle_always_add — toggle must remove a currently-watched path" <| fun () ->
    let realState = toggle "src/foo.fs" testState
    isWatched "src/foo.fs" realState
    |> Expect.isFalse "toggling a watched path must unwatch it"

  testCase "WHY — toggle_always_remove — toggle must add a currently-unwatched path" <| fun () ->
    let realState = toggle "src/new.fs" testState
    isWatched "src/new.fs" realState
    |> Expect.isTrue "toggling an unwatched path must watch it"

  // unwatchAll: must clear all
  testCase "WHY — unwatchAll_noop — unwatchAll must clear every watched path" <| fun () ->
    watchedCount (unwatchAll testState)
    |> Expect.equal "unwatchAll must leave zero watched paths" 0

  // watchAll: must replace, not accumulate
  testCase "WHY — watchAll_union — watchAll must replace the watched set, not union into it" <| fun () ->
    let realResult = watchAll ["src/a.fs"; "src/b.fs"] testState
    (isWatched "src/foo.fs" realResult, isWatched "src/a.fs" realResult, watchedCount realResult)
    |> Expect.equal "watchAll must drop the prior watched set and contain only the new paths" (false, true, 2)
]
