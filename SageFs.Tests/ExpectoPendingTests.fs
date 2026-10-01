/// A pending Expecto test (`ptest`, `ptestCase`, `ptestList`) is one Expecto's own runner
/// ignores, so the live-testing executor must not run it and must report it as skipped. A
/// skip is not a pass: before this was fixed the executor read only a test's name and body,
/// ran the body of a pending test, and a body that did not throw came back as Passed, so a
/// receipt said "3 passed" for two tests and a `ptest`.
///
/// Focus is the other half of the same rule. When anything is focused (`ftest`), Expecto runs
/// only the focused tests and ignores the rest, so the executor reports those as skipped too.
///
/// Every expectation here is Expecto's own. The oracle is `Expecto.Impl.evalTestsSilent`, the
/// runner behind `runTests`, run on the same trees. Nothing below guesses what Expecto does.
module SageFs.Tests.ExpectoPendingTests

open System.Threading
open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Features.LiveTesting.BuiltInExecutors

/// How a test ended, as one closed set the executor and Expecto's runner are both read into.
type Verdict =
  | RanAndPassed
  | IgnoredPending
  | IgnoredNotFocused
  | Unexpected of string

type FocusKind =
  | Normal
  | Focused
  | Pending

/// A test tree, generated: a leaf, or a list holding more of them.
type Spec =
  | Leaf of FocusKind
  | Group of FocusKind * Spec list

let rec private leavesIn (spec: Spec) : int =
  match spec with
  | Leaf _ -> 1
  | Group (_, kids) -> kids |> List.sumBy leavesIn

/// Build Expecto tests from specs. Leaves are named leaf1, leaf2, ... in build order, so each is unique
/// across every tree and a result can be matched to its leaf by name alone.
let private build (specs: Spec list) : Expecto.Test list =
  let counter = ref 0
  let fresh (prefix: string) =
    counter.Value <- counter.Value + 1
    sprintf "%s%d" prefix counter.Value
  let rec one (spec: Spec) : Expecto.Test =
    match spec with
    | Leaf Normal -> test (fresh "leaf") { () }
    | Leaf Focused -> ftest (fresh "leaf") { () }
    | Leaf Pending -> ptest (fresh "leaf") { () }
    | Group (Normal, kids) -> testList (fresh "group") (kids |> List.map one)
    | Group (Focused, kids) -> ftestList (fresh "group") (kids |> List.map one)
    | Group (Pending, kids) -> ptestList (fresh "group") (kids |> List.map one)
  specs |> List.map one

let private leafOf (fullName: string) : string =
  fullName.Substring(fullName.LastIndexOf '/' + 1)

let private expectoCache () : ExpectoExecutor.ReflectionCache =
  match ExpectoExecutor.tryBuildCacheFromExpecto typeof<Expecto.Test>.Assembly with
  | Some cache -> cache
  | None -> failtest "the Expecto reflection cache could not be built from the Expecto assembly"

/// What Expecto's own runner says about these trees, run together as one tree the way it runs an assembly.
let private oracle (trees: Expecto.Test list) : Async<Map<string, Verdict>> =
  async {
    let! summary = Impl.evalTestsSilent (testList "all" trees)
    return
      summary
      |> List.map (fun (flat, outcome) ->
        let text = (sprintf "%A" outcome.result).Replace("\n", " ")
        let verdict =
          match text with
          | t when t.StartsWith "Passed" -> RanAndPassed
          | t when t.StartsWith "Ignored" && t.Contains "Pending" -> IgnoredPending
          | t when t.StartsWith "Ignored" && t.Contains "Focused" -> IgnoredNotFocused
          | t -> Unexpected t
        List.last flat.name, verdict)
      |> Map.ofList
  }

/// What the SageFs executor says about the same trees, one binding each, by running every test.
let private executor (trees: Expecto.Test list) : Async<Map<string, Verdict>> =
  async {
    let cache = expectoCache ()
    let bindings = trees |> List.mapi (fun i t -> sprintf "Binding%d" i, (fun () -> box t))
    let lookup = ExpectoExecutor.lookupFromBindings cache bindings
    let! results =
      lookup
      |> Map.toList
      |> List.map (fun (fullName, reflected) ->
        async {
          let! result = ExpectoExecutor.executeReflected cache reflected CancellationToken.None
          let verdict =
            match result with
            | TestResult.Passed _ -> RanAndPassed
            | TestResult.Skipped "pending (ptest)" -> IgnoredPending
            | TestResult.Skipped "not focused" -> IgnoredNotFocused
            | other -> Unexpected (sprintf "%A" other)
          return leafOf fullName, verdict
        })
      |> Async.Sequential
    return results |> Map.ofArray
  }

let private countOf (verdict: Verdict) (verdicts: Map<string, Verdict>) : int =
  verdicts |> Map.filter (fun _ v -> v = verdict) |> Map.count

/// The mix as the receipt reports it: "N passed, N skipped".
let private tally (verdicts: Map<string, Verdict>) : string =
  sprintf "%d passed, %d skipped"
    (countOf RanAndPassed verdicts)
    (countOf IgnoredPending verdicts + countOf IgnoredNotFocused verdicts)

let private fixtureModuleName =
  typeof<SageFs.Tests.ExpectoPendingFixtureModule.Marker>.DeclaringType.FullName

[<Tests>]
let pendingExecutorTests =
  testList "Expecto pending and focused tests" [
    testAsync "WHY — a ptest is reported as skipped with its reason, never as passed, and its body does not run" {
      let bodyRan = ref false
      let trees =
        [ testList "mix" [
            test "one passes" { () }
            test "two passes" { () }
            ptest "pending" { bodyRan.Value <- true } ] ]
      let! actual = executor trees
      actual
      |> Expect.equal "two pass, the ptest is ignored as pending"
           (Map.ofList [ "one passes", RanAndPassed; "two passes", RanAndPassed; "pending", IgnoredPending ])
      bodyRan.Value |> Expect.isFalse "a pending test's body must not run"
    }

    testAsync "WHY — two passing tests and one ptest read as '2 passed, 1 skipped', the same as Expecto's own summary" {
      let trees = [ testList "mix" [ test "a" { () }; test "b" { () }; ptest "c" { () } ] ]
      let! expected = oracle trees
      let! actual = executor trees
      tally actual |> Expect.equal "the executor's tally" "2 passed, 1 skipped"
      tally actual |> Expect.equal "matches Expecto's own summary of the same tree" (tally expected)
    }

    testAsync "WHY — every test inside a ptestList is skipped, even a focused one" {
      let trees = [ ptestList "all pending" [ test "n" { () }; ftest "f" { () } ]; test "plain" { () } ]
      let! expected = oracle trees
      let! actual = executor trees
      actual |> Expect.equal "same verdicts as Expecto" expected
      countOf IgnoredPending actual |> Expect.equal "both tests in the ptestList are pending" 2
    }

    testAsync "WHY — with a focused test, the focused one runs and the rest are skipped as not focused" {
      let trees = [ testList "mix" [ test "a" { () }; ftest "f" { () }; ptest "p" { () }; test "b" { () } ] ]
      let! expected = oracle trees
      let! actual = executor trees
      actual |> Expect.equal "same verdicts as Expecto" expected
      actual
      |> Expect.equal "focused runs, pending stays pending, the others are not focused"
           (Map.ofList [ "a", IgnoredNotFocused; "f", RanAndPassed; "p", IgnoredPending; "b", IgnoredNotFocused ])
    }

    testAsync "WHY — focus is one fact for the whole assembly, so a focused test in one binding skips the tests in another" {
      let trees = [ testList "left" [ test "l1" { () } ]; testList "right" [ ftest "r1" { () } ] ]
      let! expected = oracle trees
      let! actual = executor trees
      actual |> Expect.equal "same verdicts as Expecto's single combined tree" expected
      actual |> Map.find "l1" |> Expect.equal "the other binding's test is not focused" IgnoredNotFocused
    }

    testAsync "WHY — discovery lists the ptest and the run reports it skipped through the real Discover and RunTest path" {
      let assembly = typeof<SageFs.Tests.ExpectoPendingFixtureModule.Marker>.Assembly
      match BuiltInExecutors.expecto with
      | TestExecutor.Custom custom ->
        let discovery = custom.Discover assembly
        let fixtureTests =
          discovery.Tests
          |> List.filter (fun tc -> tc.FullName.Contains(sprintf "%s.tests/" fixtureModuleName))
        fixtureTests
        |> List.map (fun tc -> tc.DisplayName)
        |> List.sort
        |> Expect.equal "the ptest is discovered with its two passing neighbours"
             [ "first passes"; "pending is not a pass"; "second passes" ]
        let! results =
          fixtureTests
          |> List.map (fun tc ->
            async {
              let! result = discovery.RunTest tc
              return tc.DisplayName, result
            })
          |> Async.Sequential
        let pending = results |> Array.find (fun (name, _) -> name = "pending is not a pass") |> snd
        pending
        |> Expect.equal "the ptest is skipped with the pending reason" (TestResult.Skipped "pending (ptest)")
        results
        |> Array.filter (fun (_, result) -> match result with TestResult.Passed _ -> true | _ -> false)
        |> Array.length
        |> Expect.equal "exactly the two normal tests passed" 2
      | _ -> failtest "Expected the Expecto executor to be a Custom executor"
    }

    testProperty "WHY — for any mix of normal, focused and pending tests and lists, the executor's verdicts equal Expecto's own runner's" <|
      fun (specs: Spec list) ->
        // A binding with no test in it has nothing to run or skip, and the executor sees tests, not empty
        // lists, so only bindings that hold a test are compared.
        let specs = specs |> List.filter (fun spec -> leavesIn spec > 0)
        async {
          let trees = build specs
          let! expected = oracle trees
          let! actual = executor trees
          return expected = actual
        }
  ]
