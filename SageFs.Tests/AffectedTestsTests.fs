module SageFs.Tests.AffectedTestsTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.LiveTesting

let private mkTestId (name: string) : TestId = TestId.create name (TestFramework.Unknown "x")

/// Reduce an arbitrary FsCheck string to a non-empty run of letters/digits —
/// no path separators, so tests can compose covered/changed paths from these
/// segments and reason exactly about where `/` boundaries land, without a
/// custom Arbitrary<TestId>/path generator.
let private sanitizeSegment (s: string) : string =
  let cleaned = s |> Seq.filter System.Char.IsLetterOrDigit |> Seq.toArray |> System.String
  if cleaned = "" then "seg" else cleaned

let private sanitizeSegments (xs: string list) : string list =
  xs |> List.map sanitizeSegment

/// `entries` models one test per element: `true` = untrusted (None)
/// coverage, `false` = trustworthy coverage of the given (sanitized) files.
/// Building the TestId->coverage map by index keeps property tests
/// black-box — they never re-derive `affected`'s own filter predicate, so
/// they lock in behavior, not implementation.
let private buildCoveredOf (prefix: string) (entries: (bool * string list) list) =
  let ids = entries |> List.mapi (fun i _ -> mkTestId (sprintf "%s-%d" prefix i))
  let coverageMap =
    List.zip ids entries
    |> List.map (fun (id, (untrusted, covered)) ->
      id, (if untrusted then None else Some (sanitizeSegments covered)))
    |> Map.ofList
  ids, (fun t -> coverageMap.[t])

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

    testProperty "any path matches itself" <| fun (p: string) ->
      AffectedTests.fileMatches p p

    testProperty "a backslash-joined path always matches its forward-slash form" <| fun (p: string) ->
      // norm(p.Replace('/','\\')) = norm(p) for every string, whatever mix of
      // separators p already contains — this generalizes the hand-picked
      // "C:\\repo\\..." example above across arbitrary strings.
      AffectedTests.fileMatches (p.Replace('/', '\\')) p

    testProperty "a covered path with the changed path appended after a '/' boundary always matches" <|
      fun (NonEmptyString dirRaw) (NonEmptyString relRaw) ->
        let dir = sanitizeSegment dirRaw
        let rel = sanitizeSegment relRaw
        let covered = "/" + dir + "/" + rel
        AffectedTests.fileMatches covered rel

    testProperty "concatenating a prefix directly onto a file name, with no separator, never matches" <|
      fun (NonEmptyString relRaw) (NonEmptyString extraRaw) ->
        let rel = sanitizeSegment relRaw
        let extra = sanitizeSegment extraRaw
        // extra and rel are alnum-only (no '/' or '\'), so `extra + rel`
        // contains no separator at all and can never end with "/" + rel —
        // the exact "aFoo.fs must not match Foo.fs" boundary rule, generalized.
        not (AffectedTests.fileMatches (extra + rel) rel)
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

    // --- Properties over `affected` -----------------------------------

    testProperty "affected always returns a subsequence of the input tests, preserving order" <|
      fun (changedRaw: string list) (entries: (bool * string list) list) ->
        let changed = sanitizeSegments changedRaw
        let ids, coveredOf = buildCoveredOf "subseq" entries
        let result = AffectedTests.affected changed coveredOf ids
        let rec isSubsequence xs ys =
          match xs, ys with
          | [], _ -> true
          | x :: xs', y :: ys' when x = y -> isSubsequence xs' ys'
          | _ :: _, _ :: ys' -> isSubsequence xs ys'
          | _ :: _, [] -> false
        isSubsequence result ids

    testProperty "a test with untrusted (None) coverage is always affected, whatever else is in play" <|
      fun (changedRaw: string list) (NonEmptyString name) (otherEntries: (bool * string list) list) ->
        let changed = sanitizeSegments changedRaw
        let untrusted = mkTestId ("untrusted-" + sanitizeSegment name)
        let ids, coveredOfOthers = buildCoveredOf "other" otherEntries
        let coveredOf t = if t = untrusted then None else coveredOfOthers t
        AffectedTests.affected changed coveredOf (untrusted :: ids)
        |> List.contains untrusted

    testProperty "a test whose covered files are all disjoint from every changed file is never affected" <|
      fun (changedRaw: NonEmptyArray<string>) (coveredRaw: NonEmptyArray<string>) ->
        // Disjoint by construction: every changed segment is prefixed "CHG_"
        // and every covered segment "COV_", so no covered path can equal or
        // end with "/" + a changed path.
        let changed = changedRaw.Get |> Array.toList |> List.map (fun s -> "CHG_" + sanitizeSegment s)
        let covered = coveredRaw.Get |> Array.toList |> List.map (fun s -> "COV_" + sanitizeSegment s)
        let t = mkTestId "disjoint-coverage"
        let coveredOf _ = Some covered
        AffectedTests.affected changed coveredOf [ t ] = []

    testProperty "a test covering one of the changed files exactly is affected" <|
      fun (changedRaw: NonEmptyArray<string>) (idx: int) ->
        let changed = changedRaw.Get |> Array.toList |> List.map sanitizeSegment |> List.distinct
        // uint32 wraparound (not abs) sidesteps the Int32.MinValue overflow case.
        let i = int (uint32 idx % uint32 changed.Length)
        let target = changed.[i]
        let t = mkTestId "covers-exactly-one"
        let coveredOf _ = Some [ target ]
        AffectedTests.affected changed coveredOf [ t ] = [ t ]

    testProperty "with no changed files, affected is exactly the untrusted-coverage subset" <|
      fun (entries: (bool * string list) list) ->
        let ids, coveredOf = buildCoveredOf "empty-diff" entries
        let expected = ids |> List.filter (fun t -> coveredOf t |> Option.isNone)
        AffectedTests.affected [] coveredOf ids = expected
  ]
