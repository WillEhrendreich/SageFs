module SageFs.Tests.AffectedTestsTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting

let private mkTestId (name: string) : TestId = TestId.create name (TestFramework.Unknown "x")

[<Tests>]
let fileMatchesTests =
  testList "AffectedTests.fileMatches" [

    test "an absolute covered path matches a repo-relative changed path at a boundary" {
      AffectedTests.fileMatches "/home/will/Work/SageFs/SageFs.Core/Foo.fs" "SageFs.Core/Foo.fs"
      |> Expect.isTrue "absolute coverage path should match the repo-relative diff path"
    }

    test "an exact path equals" {
      AffectedTests.fileMatches "SageFs.Core/Foo.fs" "SageFs.Core/Foo.fs"
      |> Expect.isTrue "identical paths match"
    }

    test "matching is boundary-safe — aFoo.fs must not match Foo.fs" {
      AffectedTests.fileMatches "/x/y/aFoo.fs" "Foo.fs"
      |> Expect.isFalse "a suffix that is not at a path boundary must not match"
    }

    test "backslash and forward-slash separators are normalized" {
      AffectedTests.fileMatches "C:\\repo\\SageFs.Core\\Foo.fs" "SageFs.Core/Foo.fs"
      |> Expect.isTrue "windows-style covered path should match a posix repo-relative path"
    }

    test "a different file does not match" {
      AffectedTests.fileMatches "/x/SageFs.Core/Bar.fs" "SageFs.Core/Foo.fs"
      |> Expect.isFalse "unrelated files do not match"
    }
  ]

[<Tests>]
let affectedTests =
  testList "AffectedTests.affected" [

    test "a test covering a changed file is affected" {
      let t = mkTestId "covers-foo"
      let coveredOf _ = Some [ "/repo/SageFs.Core/Foo.fs" ]
      AffectedTests.affected [ "SageFs.Core/Foo.fs" ] coveredOf [ t ]
      |> Expect.equal "the test that covers the changed file is affected" [ t ]
    }

    test "a test covering only unchanged files is NOT affected" {
      let t = mkTestId "covers-bar"
      let coveredOf _ = Some [ "/repo/SageFs.Core/Bar.fs" ]
      AffectedTests.affected [ "SageFs.Core/Foo.fs" ] coveredOf [ t ]
      |> Expect.isEmpty "a test touching none of the changed files is not affected"
    }

    test "a test with untrusted coverage (None) is ALWAYS affected (conservative)" {
      let t = mkTestId "no-coverage"
      let coveredOf _ = None
      AffectedTests.affected [ "SageFs.Core/Foo.fs" ] coveredOf [ t ]
      |> Expect.equal "a test we can't trust the coverage of must always run" [ t ]
    }

    test "mixes: only the covering + untrusted tests survive narrowing, order preserved" {
      let hits = mkTestId "hits"
      let misses = mkTestId "misses"
      let untrusted = mkTestId "untrusted"
      let coveredOf t =
        if t = hits then Some [ "/r/SageFs.Core/Foo.fs" ]
        elif t = misses then Some [ "/r/SageFs.Core/Bar.fs" ]
        else None
      AffectedTests.affected [ "SageFs.Core/Foo.fs" ] coveredOf [ hits; misses; untrusted ]
      |> Expect.equal "keep the covering test and the untrusted test, drop the miss, in order" [ hits; untrusted ]
    }

    test "with no changed files, only untrusted-coverage tests are affected" {
      let covers = mkTestId "covers"
      let untrusted = mkTestId "untrusted"
      let coveredOf t = if t = covers then Some [ "/r/Foo.fs" ] else None
      AffectedTests.affected [] coveredOf [ covers; untrusted ]
      |> Expect.equal "a Some-coverage test touches nothing in an empty diff; None still runs" [ untrusted ]
    }
  ]
