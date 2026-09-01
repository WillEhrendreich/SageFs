/// ## HotReloadState Correspondence Tests
///
/// Validates that the F# HotReloadState satisfies the same properties
/// proved in `formal-verification/lean/FVSquad/HotReloadState.lean`.
/// Each test maps 1-to-1 to a Lean theorem.
module HotReloadStateCorrespondenceTests

open Expecto
open Expecto.Flip
open SageFs.HotReloadState

// ── Test Fixtures ──────────────────────────────────────────────────────────

let testState =
  empty
  |> watch "src/foo.fs"
  |> watch "src/bar.fs"

let testDir =
  empty
  |> watch "src/main/program.fs"
  |> watch "src/main/helper.fs"
  |> watch "src/test/test.fs"

// ── Group 1: empty — Lean: empty has no watched paths ──────────────────────

let emptyTests =
  testList "empty" [
    test "empty state has count 0" {
      watchedCount empty |> Expect.equal "empty count should be 0" 0
    }
    test "empty state has no watched paths" {
      isWatched "src/foo.fs" empty |> Expect.isFalse "nothing should be watched in empty state"
    }
  ]

// ── Group 2: watch — Lean: watch_makes_watched ─────────────────────────────

let watchTests =
  testList "watch" [
    test "WHY — watch_makes_watched — watching a path makes it watched (Lean: watch_makes_watched)" {
      let s = watch "src/foo.fs" empty
      isWatched "src/foo.fs" s |> Expect.isTrue "src/foo.fs should be watched after watch"
    }

    test "WHY — watch_idempotent — watching twice doesn't change state (Lean: watch_idempotent)" {
      let s1 = watch "src/foo.fs" empty
      let s2 = watch "src/foo.fs" s1
      s1 = s2 |> Expect.isTrue "watching same path twice should be idempotent"
    }

    test "WHY — watch_preserves_other — watching doesn't affect other paths (Lean: watch_preserves_other)" {
      let s1 = watch "src/foo.fs" empty
      let s2 = watch "src/bar.fs" s1
      isWatched "src/foo.fs" s2 |> Expect.isTrue "src/foo.fs should still be watched"
      isWatched "src/bar.fs" s2 |> Expect.isTrue "src/bar.fs should be watched"
    }

    test "watch is case-insensitive via normalize" {
      let s1 = watch "src/Foo.fs" empty
      isWatched "src/foo.fs" s1 |> Expect.isTrue "case-insensitive watch should work"
    }
  ]

// ── Group 3: unwatch — Lean: unwatch_not_watched ───────────────────────────

let unwatchTests =
  testList "unwatch" [
    test "WHY — unwatch_not_watched — unwatching removes path (Lean: unwatch_not_watched)" {
      let s1 = watch "src/foo.fs" empty
      let s2 = unwatch "src/foo.fs" s1
      isWatched "src/foo.fs" s2 |> Expect.isFalse "src/foo.fs should not be watched after unwatch"
    }

    test "WHY — unwatch_preserves_other — unwatching doesn't affect other paths (Lean: unwatch_preserves_other)" {
      let s1 = watch "src/foo.fs" empty |> watch "src/bar.fs"
      let s2 = unwatch "src/foo.fs" s1
      isWatched "src/bar.fs" s2 |> Expect.isTrue "src/bar.fs should still be watched"
      isWatched "src/foo.fs" s2 |> Expect.isFalse "src/foo.fs should not be watched"
    }

    test "WHY — unwatch_watch_new — unwatch after watch returns to original (Lean: unwatch_watch_new)" {
      let s1 = watch "src/foo.fs" empty
      let s2 = unwatch "src/foo.fs" s1
      s2 = empty |> Expect.isTrue "unwatch after watch should return to empty"
    }

    test "unwatch on absent path is no-op" {
      let s = unwatch "src/nope.fs" testState
      s = testState |> Expect.isTrue "unwatching absent path should be no-op"
    }
  ]

// ── Group 4: toggle — Lean: toggle_adds_unwatched, toggle_removes_watched ──

let toggleTests =
  testList "toggle" [
    test "WHY — toggle_adds_unwatched — toggle adds when not present (Lean: toggle_adds_unwatched)" {
      let s = toggle "src/foo.fs" empty
      isWatched "src/foo.fs" s |> Expect.isTrue "toggle should add unwatched path"
    }

    test "WHY — toggle_removes_watched — toggle removes when present (Lean: toggle_removes_watched)" {
      let s1 = watch "src/foo.fs" empty
      let s2 = toggle "src/foo.fs" s1
      isWatched "src/foo.fs" s2 |> Expect.isFalse "toggle should remove watched path"
    }

    test "WHY — toggle_involution — toggle twice returns to original (Lean: toggle_involution)" {
      let s1 = toggle "src/foo.fs" testState
      let s2 = toggle "src/foo.fs" s1
      isWatched "src/foo.fs" s2 = isWatched "src/foo.fs" testState
      |> Expect.isTrue "toggle involution should preserve membership"
    }
  ]

// ── Group 5: watchMany — Lean: watchMany_makes_watched ─────────────────────

let watchManyTests =
  testList "watchMany" [
    test "WHY — watchMany_makes_watched — watchMany adds all listed paths (Lean: watchMany_makes_watched)" {
      let s = watchMany ["a.fs"; "b.fs"; "c.fs"] empty
      ["a.fs"; "b.fs"; "c.fs"] |> List.iter (fun p ->
        isWatched p s |> Expect.isTrue $"{p} should be watched after watchMany")
    }

    test "WHY — watchMany_preserves_watched — watchMany preserves existing paths (Lean: watchMany_preserves_watched)" {
      let s1 = watch "existing.fs" empty
      let s2 = watchMany ["new.fs"] s1
      isWatched "existing.fs" s2 |> Expect.isTrue "existing path should be preserved"
    }

    test "WHY — watchMany_nil — watchMany with empty list is no-op (Lean: watchMany_nil)" {
      let s = watchMany [] testState
      s = testState |> Expect.isTrue "watchMany [] should be no-op"
    }
  ]

// ── Group 6: unwatchAll — Lean: unwatchAll_is_empty ────────────────────────

let unwatchAllTests =
  testList "unwatchAll" [
    test "WHY — unwatchAll_is_empty — unwatchAll returns empty (Lean: unwatchAll_is_empty)" {
      let s = unwatchAll testState
      s = empty |> Expect.isTrue "unwatchAll should return empty"
    }

    test "WHY — unwatchAll_clears — nothing watched after unwatchAll (Lean: unwatchAll_clears)" {
      let s = unwatchAll testState
      watchedCount s |> Expect.equal "count should be 0 after unwatchAll" 0
    }
  ]

// ── Group 7: watchAll — Lean: watchAll_ignores_prior ───────────────────────

let watchAllTests =
  testList "watchAll" [
    test "WHY — watchAll_ignores_prior — watchAll ignores prior state (Lean: watchAll_ignores_prior)" {
      let s1 = watchAll ["a.fs"; "b.fs"] empty
      let s2 = watchAll ["a.fs"; "b.fs"] testState
      s1 = s2 |> Expect.isTrue "watchAll should ignore prior state"
    }

    test "WHY — watchAll_nil — watchAll with empty list returns empty (Lean: watchAll_nil)" {
      let s = watchAll [] testState
      s = empty |> Expect.isTrue "watchAll [] should return empty"
    }

    test "WHY — watchAll_makes_watched — watchAll watches listed paths (Lean: watchAll_makes_watched)" {
      let s = watchAll ["a.fs"; "b.fs"] testState
      isWatched "a.fs" s |> Expect.isTrue "a.fs should be watched"
      isWatched "b.fs" s |> Expect.isTrue "b.fs should be watched"
      isWatched "src/foo.fs" s |> Expect.isFalse "old paths should be gone"
    }
  ]

// ── Group 8: watchedCount — Lean: watchedCount_empty, watchedCount_watch_new ──

let watchedCountTests =
  testList "watchedCount" [
    test "WHY — watchedCount_empty — empty state has count 0 (Lean: watchedCount_empty)" {
      watchedCount empty |> Expect.equal "empty count should be 0" 0
    }

    test "WHY — watchedCount_watch_new — watching new path increments count (Lean: watchedCount_watch_new)" {
      let s1 = empty
      let s2 = watch "src/foo.fs" s1
      watchedCount s2 |> Expect.equal "count should be 1 after watch" 1
    }

    test "WHY — watchedCount_watch_existing — re-watching preserves count (Lean: watchedCount_watch_existing)" {
      let s1 = watch "src/foo.fs" empty
      let s2 = watch "src/foo.fs" s1
      watchedCount s2 |> Expect.equal "count should stay 1 after re-watch" 1
    }
  ]

// ── Group 9: directory operations ────────────────────────────────────────────

let directoryTests =
  testList "directory operations" [
    test "WHY — unwatchByDirectory_removes — removes paths in directory (Lean: unwatchByDirectory_removes)" {
      let s = unwatchByDirectory "src/main" testDir
      isWatched "src/main/program.fs" s |> Expect.isFalse "program.fs should be removed"
      isWatched "src/main/helper.fs" s |> Expect.isFalse "helper.fs should be removed"
      isWatched "src/test/test.fs" s |> Expect.isTrue "test.fs should be preserved"
    }

    test "WHY — watchByDirectory_adds — adds paths in directory (Lean: watchByDirectory_adds)" {
      let allPaths = ["src/main/program.fs"; "src/main/helper.fs"; "src/test/test.fs"]
      let s = watchByDirectory "src/main" allPaths empty
      isWatched "src/main/program.fs" s |> Expect.isTrue "program.fs should be watched"
      isWatched "src/main/helper.fs" s |> Expect.isTrue "helper.fs should be watched"
      isWatched "src/test/test.fs" s |> Expect.isFalse "test.fs should not be watched"
    }

    test "WHY — watchedInDirectory_spec — paths in dir are in watched (Lean: watchedInDirectory_spec)" {
      let paths = watchedInDirectory "src/main" testDir
      paths |> List.iter (fun p ->
        isWatched p testDir |> Expect.isTrue $"{p} should be in watched set")
    }
  ]

// ── All tests combined ──────────────────────────────────────────────────────

let hotReloadCorrespondenceTests =
  testList "HotReloadState Correspondence (F# vs Lean)" [
    emptyTests
    watchTests
    unwatchTests
    toggleTests
    watchManyTests
    unwatchAllTests
    watchAllTests
    watchedCountTests
    directoryTests
  ]
