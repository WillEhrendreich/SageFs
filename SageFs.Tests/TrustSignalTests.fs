module SageFs.Tests.TrustSignalTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Tests.TestInfrastructure
open SageFs.Tests.TestInfrastructure.TrustSignal

let private tally passed failed errored ignored =
  { Passed = passed; Failed = failed; Errored = errored; Ignored = ignored }

let verdictTests =
  testList "TrustSignal verdict" [
    testProperty "any failure or error outranks every other verdict" <|
      fun (NonNegativeInt p) (NonNegativeInt f) (NonNegativeInt e) (NonNegativeInt i) (NonNegativeInt r) (narrowed: bool) ->
        let f, e = f + 1, e
        let scope = match narrowed with true -> Narrowed | false -> Acceptance
        judge scope r (tally p f e i) = TestsFailed (f, e)

    testProperty "a run that executed nothing is never green, filtered or not" <|
      fun (NonNegativeInt r) (narrowed: bool) ->
        let scope = match narrowed with true -> Narrowed | false -> Acceptance
        let v = judge scope r (tally 0 0 0 0)
        v = NothingRan && Verdict.exitCode 0 v = 3

    testProperty "an acceptance run is Trusted exactly when it ran what was registered" <|
      fun (PositiveInt p) (NonNegativeInt i) (NonNegativeInt r) ->
        let t = tally p 0 0 i
        match judge Acceptance r t with
        | Trusted -> t.Ran = r
        | CountMismatch (reg, ran) -> reg = r && ran = t.Ran && t.Ran <> r
        | _ -> false

    testProperty "a filtered clean run is only ever NarrowedRun, never Trusted" <|
      fun (PositiveInt p) (NonNegativeInt r) ->
        judge Narrowed r (tally p 0 0 0) = NarrowedRun

    testCase "every narrowing flag makes the run Narrowed" <| fun _ ->
      [ "--filter"; "--filter-test-list"; "--filter-test-case"; "--run"; "--stress" ]
      |> List.iter (fun flag ->
        scopeOf [| "--summary"; flag; "x" |] |> Expect.equal (sprintf "%s narrows the run" flag) Narrowed)
      scopeOf [| "--summary"; "--sequenced" |] |> Expect.equal "summary/sequenced are acceptance-shaped" Acceptance

    testCase "NothingRan and CountMismatch never exit 0, whatever Expecto said" <| fun _ ->
      Verdict.exitCode 0 NothingRan |> Expect.equal "empty run fails closed" 3
      Verdict.exitCode 0 (CountMismatch (5, 4)) |> Expect.equal "under-run fails closed" 3
      Verdict.exitCode 0 (TestsFailed (1, 0)) |> Expect.equal "failure is never 0" 1
      Verdict.exitCode 0 (SurvivorsUnderBar 2) |> Expect.equal "survivors under the bar keep the gate's own verdict" 0

    // The AGENTS.md trap, through the real runner: a case filter that matches
    // nothing made Expecto print "0 failed" and exit 0. Measured before this
    // change; it must now exit non-zero.
    testCase "a real run whose filter matches nothing exits 3, not 0" <| fun _ ->
      let tree = testList "trust-probe" [ testCase "only" ignore ]
      runReporting ignore "probe" [| "--filter-test-case"; "no-such-case" |] tree
      |> Expect.equal "a filter matching nothing must not read as green" 3

    testCase "a real unfiltered run of everything registered is Trusted (exit 0)" <| fun _ ->
      let tree = testList "trust-probe" [ testCase "a" ignore; ptestCase "b" ignore ]
      runReporting ignore "probe" [||] tree |> Expect.equal "pending cases count as ran-and-ignored" 0
  ]

/// Every tier the test assembly can run must be invoked by CI, through the
/// ledgered `testTier` step — and CI must invoke nothing the assembly cannot
/// dispatch. Before this, "registered", "dispatched" and "invoked by CI" were
/// three separate facts; a suite could satisfy the first two and still run
/// nowhere (the disconnect journeys did, for weeks).
let ciWiringTests =
  let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
  let pipeline = lazy (File.ReadAllText(Path.Combine(repoRoot, "ci-pipeline.fsx")))

  /// Tier names CI invokes: the first token of each `testTier "<args>"`, with
  /// the bare `--summary` default run named "default".
  let invokedTiers () =
    pipelineTierArgs pipeline.Value |> List.map tierOfArgs |> Set.ofList

  let alwaysPresent = set [ "default"; "--integration-host"; "--mutation-score" ]

  testList "TrustSignal CI wiring" [
    testCase "every tier the assembly registers is invoked by a CI stage" <| fun _ ->
      let expected =
        Integration.dedicatedEntryPoints ()
        |> List.filter Integration.isBareFlagEntryPoint
        |> Set.ofList
        |> Set.union alwaysPresent
      Set.difference expected (invokedTiers ())
      |> Set.toList
      |> Expect.isEmpty "registered tiers no CI stage invokes (a dark gate: written, registered, never run)"

    // The reverse dark gate: an entry point that dispatches, and that CI
    // invokes, but that no suite registers against runs NOTHING. That is what
    // `--integration-shapes` became once the shape matrix moved into the host
    // suite — and the first trust-ledger run reported it as NothingRan.
    testCase "every dispatched entry point has registered suites to run" <| fun _ ->
      let registered = Integration.dedicatedEntryPoints () |> Set.ofList
      Set.difference (Set.ofList Integration.dispatchedEntryPoints) registered
      |> Set.toList
      |> Expect.isEmpty "dispatched entry points with no registered suite (they would run zero tests)"

    testCase "CI invokes no tier the test assembly cannot dispatch" <| fun _ ->
      let dispatchable = Set.union alwaysPresent (Set.ofList Integration.dispatchedEntryPoints)
      Set.difference (invokedTiers ()) dispatchable
      |> Set.toList
      |> Expect.isEmpty "CI invokes flags Program.fs has no branch for"

    testCase "every test-assembly invocation in CI goes through the ledgered testTier step" <| fun _ ->
      Regex.Matches(pipeline.Value, "run \\$\"dotnet \\{testDll\\}")
      |> Seq.length
      |> Expect.equal "a raw `run $\"dotnet {testDll} ...\"` bypasses the trust ledger and stops the pipeline on red" 0
  ]

/// The repo is PUBLIC and main build runs on this developer machine through a
/// self-hosted runner. A `pull_request` job is executed from the PR's OWN copy
/// of the workflow, so a fork PR reaching a self-hosted job would run arbitrary
/// code in this machine's home directory (tokens, keys, the daemon). The
/// invariant: every job that targets the self-hosted runner excludes
/// pull_request, and pull requests run only on GitHub-hosted runners.
let selfHostedSafetyTests =
  let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
  let workflow = lazy (File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "main.yml")))

  /// (job name, its `runs-on:` line, its `if:` line) for every job under
  /// `jobs:`. Only the job's own keys at job-body indentation count — comment
  /// text that merely MENTIONS "self-hosted" must never decide anything.
  let jobs () =
    let lines = workflow.Value.Replace("\r\n", "\n").Split('\n')
    let jobsAt = lines |> Array.findIndex (fun l -> l = "jobs:")
    let jobKey = Regex("^  ([A-Za-z0-9_-]+):\\s*$")
    let keyLine (prefix: string) (body: string list) =
      body |> List.tryFind (fun l -> l.StartsWith prefix) |> Option.defaultValue ""
    lines[jobsAt + 1 ..]
    |> Array.fold (fun (acc: (string * string list) list) line ->
      match jobKey.Match line with
      | m when m.Success -> (m.Groups[1].Value, []) :: acc
      | _ ->
        match acc with
        | (name, body) :: rest -> (name, line :: body) :: rest
        | [] -> acc) []
    |> List.rev
    |> List.map (fun (name, body) ->
      let body = List.rev body
      name, keyLine "    runs-on:" body, keyLine "    if:" body)

  let selfHosted (_, runsOn: string, _) = runsOn.Contains "self-hosted"

  testList "Self-hosted runner safety" [
    testCase "every self-hosted job refuses pull_request events" <| fun _ ->
      jobs ()
      |> List.filter selfHosted
      |> List.filter (fun (_, _, guard) -> not (guard.Contains "github.event_name != 'pull_request'"))
      |> List.map (fun (name, _, _) -> name)
      |> Expect.isEmpty "self-hosted jobs reachable from a pull_request (a fork PR would run on this machine)"

    testCase "some job targets the self-hosted runner, so the rule above is not vacuous" <| fun _ ->
      jobs ()
      |> List.exists selfHosted
      |> Expect.isTrue "main build should run on this machine's self-hosted runner"
  ]

[<Tests>]
let tests = testList "TrustSignal" [ verdictTests; ciWiringTests; selfHostedSafetyTests ]
