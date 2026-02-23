module SageFs.Tests.HotReloadStateTests

open Expecto
open Expecto.Flip
open SageFs.HotReloadState

let basicTests = testList "basic operations" [
  test "empty has zero watched" {
    watchedCount empty
    |> Expect.equal "empty state has 0" 0
  }
  test "watch adds file" {
    empty |> watch "src/Foo.fs" |> isWatched "src/Foo.fs"
    |> Expect.isTrue "file should be watched"
  }
  test "unwatch removes file" {
    empty |> watch "src/Foo.fs" |> unwatch "src/Foo.fs" |> isWatched "src/Foo.fs"
    |> Expect.isFalse "file should not be watched"
  }
  test "toggle flips state" {
    let s1 = empty |> toggle "src/Foo.fs"
    isWatched "src/Foo.fs" s1
    |> Expect.isTrue "toggled on"
    let s2 = toggle "src/Foo.fs" s1
    isWatched "src/Foo.fs" s2
    |> Expect.isFalse "toggled off"
  }
  test "normalize handles Windows paths" {
    empty |> watch "src\\Auth\\Login.fs" |> isWatched "src/auth/login.fs"
    |> Expect.isTrue "normalized path should match"
  }
]

let bulkTests = testList "bulk operations" [
  test "watchAll watches all paths" {
    let s = watchAll ["a.fs"; "b.fs"; "c.fs"] empty
    watchedCount s |> Expect.equal "should watch 3" 3
  }
  test "unwatchAll clears all" {
    let s = watchAll ["a.fs"; "b.fs"] empty |> unwatchAll
    watchedCount s |> Expect.equal "should watch 0" 0
  }
  test "watchMany adds to existing" {
    let s = watch "a.fs" empty |> watchMany ["b.fs"; "c.fs"]
    watchedCount s |> Expect.equal "should watch 3" 3
  }
]

let directoryTests = testList "directory operations" [
  test "watchByDirectory watches only matching files" {
    let files = ["src/Auth/Login.fs"; "src/Auth/Token.fs"; "src/Users/Profile.fs"]
    let s = watchByDirectory "src/Auth" files empty
    watchedCount s |> Expect.equal "should watch 2" 2
    isWatched "src/Auth/Login.fs" s |> Expect.isTrue "Login.fs"
    isWatched "src/Users/Profile.fs" s |> Expect.isFalse "Profile.fs"
  }
  test "watchByDirectory handles Windows paths" {
    let files = ["src/Auth/Login.fs"; "src/Auth/Token.fs"; "src/Users/Profile.fs"]
    let s = watchByDirectory "src\\Auth" files empty
    watchedCount s |> Expect.equal "should watch 2" 2
  }
  test "unwatchByDirectory removes matching" {
    let files = ["src/Auth/Login.fs"; "src/Auth/Token.fs"; "src/Users/Profile.fs"]
    let s = watchAll files empty |> unwatchByDirectory "src/Auth"
    watchedCount s |> Expect.equal "should watch 1" 1
    isWatched "src/Users/Profile.fs" s |> Expect.isTrue "Profile.fs survives"
  }
  test "watchedInDirectory returns subset" {
    let files = ["src/Auth/Login.fs"; "src/Auth/Token.fs"; "src/Users/Profile.fs"]
    let s = watchAll files empty
    watchedInDirectory "src/Auth" s |> List.length |> Expect.equal "2 auth files" 2
    watchedInDirectory "src/Users" s |> List.length |> Expect.equal "1 user file" 1
  }
  test "watchedInDirectory returns empty for unwatched dir" {
    let files = ["src/Auth/Login.fs"; "src/Users/Profile.fs"]
    let s = watchByDirectory "src/Auth" files empty
    watchedInDirectory "src/Users" s |> List.length |> Expect.equal "0 user files" 0
  }
  test "watchByDirectory handles nested dirs" {
    let files = ["src/A/B/C/deep.fs"; "src/A/B/shallow.fs"; "src/X/other.fs"]
    let s = watchByDirectory "src/A" files empty
    watchedCount s |> Expect.equal "should watch 2 nested" 2
  }
]

let projectTests = testList "project operations" [
  test "watchByProject watches exact set" {
    let s = watchByProject ["a.fs"; "b.fs"] empty
    watchedCount s |> Expect.equal "2 project files" 2
  }
  test "unwatchByProject removes exact set" {
    let s = watchAll ["a.fs"; "b.fs"; "c.fs"] empty |> unwatchByProject ["a.fs"; "b.fs"]
    watchedCount s |> Expect.equal "1 remaining" 1
    isWatched "c.fs" s |> Expect.isTrue "c.fs survives"
  }
]

[<Tests>]
let tests = testList "HotReloadState" [
  basicTests
  bulkTests
  directoryTests
  projectTests
]
