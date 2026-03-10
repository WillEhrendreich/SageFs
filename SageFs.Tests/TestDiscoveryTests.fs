module SageFs.Tests.TestDiscoveryTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Features.TestDiscovery

// ── Test helpers ──────────────────────────────────────────────

let private mkLoc testName filePath line =
  { CellId = 0
    TestName = testName
    FilePath = filePath
    StartLine = line
    EndLine = line }

let private sampleLocs = [
  mkLoc "Auth.should login user"        "tests/AuthTests.fs"   10
  mkLoc "Auth.should reject bad pass"   "tests/AuthTests.fs"   25
  mkLoc "Cart.should add item"          "tests/CartTests.fs"   15
  mkLoc "Cart.should remove item"       "tests/CartTests.fs"   30
  mkLoc "Cart.should calculate total"   "tests/CartTests.fs"   45
  mkLoc "Payment.should charge card"    "tests/PaymentTests.fs" 8
]

// ── applyQuery — no filter ────────────────────────────────────

[<Tests>]
let noFilterTests =
  testList "TestDiscovery.applyQuery no filter" [

    testCase "returns all tests when no filter set" <| fun _ ->
      let result = TestDiscovery.applyQuery TestDiscovery.defaultQuery sampleLocs
      result.Tests |> Expect.hasLength "all 6" 6

    testCase "TotalCount equals filtered length" <| fun _ ->
      let result = TestDiscovery.applyQuery TestDiscovery.defaultQuery sampleLocs
      result.TotalCount |> Expect.equal "total = 6" 6

    testCase "FilterApplied is None when no filter" <| fun _ ->
      let result = TestDiscovery.applyQuery TestDiscovery.defaultQuery sampleLocs
      result.FilterApplied |> Expect.isNone "no filter applied"

    testCase "results sorted by file then line" <| fun _ ->
      let result = TestDiscovery.applyQuery TestDiscovery.defaultQuery sampleLocs
      let files = result.Tests |> List.map (fun t -> t.FilePath)
      files
      |> Expect.equal "sorted by file"
           [ "tests/AuthTests.fs"
             "tests/AuthTests.fs"
             "tests/CartTests.fs"
             "tests/CartTests.fs"
             "tests/CartTests.fs"
             "tests/PaymentTests.fs" ]

    testCase "grouped by file produces 3 groups" <| fun _ ->
      let result = TestDiscovery.applyQuery TestDiscovery.defaultQuery sampleLocs
      result.GroupedByFile |> Expect.hasLength "3 files" 3

    testCase "returns empty result for empty input" <| fun _ ->
      let result = TestDiscovery.applyQuery TestDiscovery.defaultQuery []
      result.Tests |> Expect.isEmpty "no tests"
      result.TotalCount |> Expect.equal "total 0" 0
      result.GroupedByFile |> Expect.isEmpty "no groups"
  ]

// ── applyQuery — pattern filter ───────────────────────────────

[<Tests>]
let patternFilterTests =
  testList "TestDiscovery.applyQuery pattern filter" [

    testCase "filters by name substring case-insensitive" <| fun _ ->
      let query = { TestDiscovery.defaultQuery with Pattern = Some "auth" }
      let result = TestDiscovery.applyQuery query sampleLocs
      result.Tests |> Expect.hasLength "2 auth tests" 2

    testCase "filters by file substring case-insensitive" <| fun _ ->
      let query = { TestDiscovery.defaultQuery with Pattern = Some "CART" }
      let result = TestDiscovery.applyQuery query sampleLocs
      result.Tests |> Expect.hasLength "3 cart tests" 3

    testCase "empty pattern returns no results" <| fun _ ->
      let query = { TestDiscovery.defaultQuery with Pattern = Some "nonexistent_xyz_404" }
      let result = TestDiscovery.applyQuery query sampleLocs
      result.Tests |> Expect.isEmpty "nothing matches"
      result.TotalCount |> Expect.equal "total 0" 0

    testCase "FilterApplied contains pattern description" <| fun _ ->
      let query = { TestDiscovery.defaultQuery with Pattern = Some "auth" }
      let result = TestDiscovery.applyQuery query sampleLocs
      match result.FilterApplied with
      | Some f -> f |> Expect.stringContains "contains pattern" "pattern:"
      | None   -> failtest "FilterApplied should be set"

    testCase "partial name match works" <| fun _ ->
      let query = { TestDiscovery.defaultQuery with Pattern = Some "login" }
      let result = TestDiscovery.applyQuery query sampleLocs
      result.Tests |> Expect.hasLength "1 login test" 1
  ]

// ── applyQuery — file filter ──────────────────────────────────

[<Tests>]
let fileFilterTests =
  testList "TestDiscovery.applyQuery file filter" [

    testCase "restricts to specific file" <| fun _ ->
      let query = { TestDiscovery.defaultQuery with FilePath = Some "PaymentTests" }
      let result = TestDiscovery.applyQuery query sampleLocs
      result.Tests |> Expect.hasLength "1 payment test" 1

    testCase "file filter case-insensitive" <| fun _ ->
      let query = { TestDiscovery.defaultQuery with FilePath = Some "carttests" }
      let result = TestDiscovery.applyQuery query sampleLocs
      result.Tests |> Expect.hasLength "3 cart tests" 3

    testCase "both pattern and file combined" <| fun _ ->
      let query = { Pattern = Some "remove"; FilePath = Some "CartTests"; MaxResults = 100 }
      let result = TestDiscovery.applyQuery query sampleLocs
      result.Tests |> Expect.hasLength "1 combined" 1
      result.Tests.[0].TestName |> Expect.stringContains "remove test" "remove"

    testCase "FilterApplied includes both pattern and file" <| fun _ ->
      let query = { Pattern = Some "item"; FilePath = Some "Cart"; MaxResults = 100 }
      let result = TestDiscovery.applyQuery query sampleLocs
      match result.FilterApplied with
      | Some f ->
        f |> Expect.stringContains "has pattern" "pattern:"
        f |> Expect.stringContains "has file" "file:"
      | None -> failtest "FilterApplied should be set"
  ]

// ── applyQuery — MaxResults truncation ───────────────────────

[<Tests>]
let maxResultsTests =
  testList "TestDiscovery.applyQuery MaxResults" [

    testCase "truncates to MaxResults" <| fun _ ->
      let query = { TestDiscovery.defaultQuery with MaxResults = 2 }
      let result = TestDiscovery.applyQuery query sampleLocs
      result.Tests |> Expect.hasLength "truncated to 2" 2

    testCase "TotalCount reflects untruncated count" <| fun _ ->
      let query = { TestDiscovery.defaultQuery with MaxResults = 2 }
      let result = TestDiscovery.applyQuery query sampleLocs
      result.TotalCount |> Expect.equal "total = 6 not 2" 6

    testCase "MaxResults of 0 returns empty but TotalCount correct" <| fun _ ->
      let query = { TestDiscovery.defaultQuery with MaxResults = 0 }
      let result = TestDiscovery.applyQuery query sampleLocs
      result.Tests |> Expect.isEmpty "truncated to 0"
      result.TotalCount |> Expect.equal "total still 6" 6
  ]

// ── summarize ─────────────────────────────────────────────────

[<Tests>]
let summarizeTests =
  testList "TestDiscovery.summarize" [

    testCase "summarize unfiltered result" <| fun _ ->
      let result = TestDiscovery.applyQuery TestDiscovery.defaultQuery sampleLocs
      let s = TestDiscovery.summarize result
      s |> Expect.stringContains "has total" "6"
      s |> Expect.stringContains "has file count" "3"

    testCase "summarize filtered result mentions filter" <| fun _ ->
      let query = { TestDiscovery.defaultQuery with Pattern = Some "auth" }
      let result = TestDiscovery.applyQuery query sampleLocs
      let s = TestDiscovery.summarize result
      s |> Expect.stringContains "has filter tag" "filter:"

    testCase "summarize empty result is stable" <| fun _ ->
      let result = TestDiscovery.applyQuery TestDiscovery.defaultQuery []
      let s = TestDiscovery.summarize result
      s |> Expect.stringContains "zero tests" "0"
  ]

[<Tests>]
let allTests =
  testList "TestDiscovery" [
    noFilterTests
    patternFilterTests
    fileFilterTests
    maxResultsTests
    summarizeTests
  ]
