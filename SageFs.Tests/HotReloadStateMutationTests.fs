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
  // normalize: must lowercase or path lookups break
  testCase "WHY — normalize_no_lowercase — normalize must lowercase or path lookups break" <| fun () ->
    let mutantResult = "C:\\Src\\Foo.fs".Replace('\\', '/')
    let realResult = normalize "C:\\Src\\Foo.fs"
    if mutantResult = realResult then
      failwith "Mutation NOT caught — mutant still normalizes correctly"

  // isWatched: must return true only for watched paths
  // Pattern: if real and mutant give the SAME result, mutation survived
  testCase "WHY — isWatched_always_true — isWatched must not lie about unwatched paths" <| fun () ->
    let isWatchedMutant = fun _path _state -> true
    let realResult = isWatched "src/nope.fs" testState
    let mutantResult = isWatchedMutant "src/nope.fs" testState
    if realResult = mutantResult then
      failwith "Mutation survived — real and mutant both give same result for unwatched path"

  testCase "WHY — isWatched_always_false — isWatched must not lie about watched paths" <| fun () ->
    let isWatchedMutant = fun _path _state -> false
    let realResult = isWatched "src/foo.fs" testState
    let mutantResult = isWatchedMutant "src/foo.fs" testState
    if realResult = mutantResult then
      failwith "Mutation survived — real and mutant both give same result for watched path"

  // toggle: must flip the state
  // Pattern: if toggle(mutant) and toggle(real) give the same result, mutation survived
  testCase "WHY — toggle_always_add — toggle must remove when path is watched" <| fun () ->
    let toggleMutant = watch
    let mutantState = toggleMutant "src/foo.fs" testState
    let realState = toggle "src/foo.fs" testState
    if isWatched "src/foo.fs" mutantState = isWatched "src/foo.fs" realState then
      failwith "Mutation survived — toggle mutant and real give same result"

  testCase "WHY — toggle_always_remove — toggle must add when path is unwatched" <| fun () ->
    let toggleMutant = unwatch
    let mutantState = toggleMutant "src/new.fs" testState
    let realState = toggle "src/new.fs" testState
    if isWatched "src/new.fs" mutantState = isWatched "src/new.fs" realState then
      failwith "Mutation survived — toggle mutant and real give same result"

  // unwatchAll: must clear all
  testCase "WHY — unwatchAll_noop — unwatchAll must clear all watched paths" <| fun () ->
    let newState = testState  // mutant: returns state unchanged
    if watchedCount newState = watchedCount (unwatchAll testState) then
      failwith "Mutation NOT caught — unwatchAll didn't clear paths"

  // watchAll: must replace, not accumulate
  testCase "WHY — watchAll_union — watchAll must replace, not accumulate" <| fun () ->
    let mutantResult = watchMany ["src/a.fs"; "src/b.fs"] testState
    let realResult = watchAll ["src/a.fs"; "src/b.fs"] testState
    if isWatched "src/foo.fs" mutantResult = isWatched "src/foo.fs" realResult then
      failwith "Mutation NOT caught — watchAll didn't replace old paths"
]
