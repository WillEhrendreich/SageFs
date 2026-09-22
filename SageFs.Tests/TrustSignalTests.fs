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
    // These two run a real Expecto run inside a test. --no-spinner, always: a
    // nested spinner shares the outer run's console locks, and that deadlocked
    // the whole tier once.
    testCase "a real run whose filter matches nothing exits 3, not 0" <| fun _ ->
      let tree = testList "trust-probe" [ testCase "only" ignore ]
      runReporting ignore "probe" [| "--no-spinner"; "--filter-test-case"; "no-such-case" |] tree
      |> Expect.equal "a filter matching nothing must not read as green" 3

    testCase "a real unfiltered run of everything registered is Trusted (exit 0)" <| fun _ ->
      let tree = testList "trust-probe" [ testCase "a" ignore; ptestCase "b" ignore ]
      runReporting ignore "probe" [| "--no-spinner" |] tree |> Expect.equal "pending cases count as ran-and-ignored" 0

    testCase "a redirected run never starts Expecto's spinner" <| fun _ ->
      spinnerArgs Redirected
      |> List.exists (function Expecto.Tests.CLIArguments.No_Spinner -> true | _ -> false)
      |> Expect.isTrue "the spinner's timer lock deadlocks against console writers"
      spinnerArgs Interactive |> Expect.isEmpty "a terminal still gets its spinner"
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

/// The repo is PUBLIC and both the main build and the release publish run on
/// this developer machine through a self-hosted runner. A `pull_request` job is
/// executed from the PR's OWN copy of the workflow, so a fork PR reaching a
/// self-hosted job would run arbitrary code in this machine's home directory
/// (tokens, keys, the daemon). `workflow_call` is the same hole one step
/// removed: a PR-triggered workflow can call a reusable one.
///
/// The invariant, over EVERY workflow file rather than just main.yml: a job on
/// the self-hosted runner is either guarded by its own
/// `github.event_name != 'pull_request'`, or lives in a workflow a pull
/// request cannot reach at all.
module private SelfHostedSafety =
  /// A job key's whole value: its own line plus any deeper-indented
  /// continuation, so a block scalar (`if: |`) is read in full instead of
  /// being mistaken for an empty guard.
  let keyBlock (prefix: string) (body: string list) =
    match body |> List.tryFindIndex (fun line -> line.StartsWith prefix) with
    | None -> ""
    | Some start ->
      body
      |> List.skip start
      |> List.indexed
      |> List.takeWhile (fun (i, line) -> i = 0 || line.Trim() = "" || line.StartsWith "      ")
      |> List.map snd
      |> String.concat "\n"

  /// (job name, runs-on, if) for one workflow's text. Only the job's own keys
  /// at job-body indentation count: comment text that merely MENTIONS
  /// "self-hosted" must never decide anything.
  let jobsIn (text: string) =
    let lines = text.Replace("\r\n", "\n").Split('\n')
    match lines |> Array.tryFindIndex (fun l -> l = "jobs:") with
    | None -> []
    | Some jobsAt ->
      let jobKey = Regex("^  ([A-Za-z0-9_-]+):\\s*$")
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
        name, keyBlock "    runs-on:" body, keyBlock "    if:" body)

  /// Whether a pull request can reach this workflow at all: a `pull_request`
  /// trigger runs the fork's copy of it, and a `workflow_call` one lets a
  /// PR-triggered workflow call it.
  let reachableFromPullRequest (text: string) =
    let lines = text.Replace("\r\n", "\n").Split('\n')
    let on =
      match lines |> Array.tryFindIndex (fun l -> l = "jobs:") with
      | Some jobsAt -> lines[.. jobsAt - 1] |> String.concat "\n"
      | None -> text
    Regex.IsMatch(on, @"^\s{2,4}(pull_request|workflow_call):", RegexOptions.Multiline)

  let isSelfHosted (_, runsOn: string, _) = runsOn.Contains "self-hosted"

  /// The jobs that break the invariant, named file/job.
  let breaches (workflows: (string * string) list) =
    workflows
    |> List.collect (fun (file, text) ->
      match reachableFromPullRequest text with
      | false -> []
      | true ->
        jobsIn text
        |> List.filter isSelfHosted
        |> List.filter (fun (_, _, guard) -> not (guard.Contains "github.event_name != 'pull_request'"))
        |> List.map (fun (name, _, _) -> sprintf "%s/%s" file name))

let selfHostedSafetyTests =
  let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

  let workflows =
    lazy
      (Directory.GetFiles(Path.Combine(repoRoot, ".github", "workflows"), "*.yml")
       |> Array.sort
       |> Array.map (fun path -> Path.GetFileName path, File.ReadAllText path)
       |> Array.toList)

  /// A workflow shaped like the real ones, so the twin below breaks the same
  /// parser the repo check uses.
  let workflowText (triggers: string) (guard: string) (runsOn: string) =
    String.concat "\n" [
      "name: made up"
      ""
      "on:"
      triggers
      ""
      "jobs:"
      "  build:"
      guard
      sprintf "    runs-on: %s" runsOn
      "    steps:"
      "    - run: echo hi"
    ]

  let guardLine = "    if: github.event_name != 'pull_request'"
  let blockGuard = "    if: |\n      github.event_name != 'pull_request' &&\n      github.event.workflow_run.conclusion == 'success'"

  testList "Self-hosted runner safety" [
    testCase "no self-hosted job in any workflow is reachable from a pull request" <| fun _ ->
      SelfHostedSafety.breaches workflows.Value
      |> Expect.isEmpty "self-hosted jobs a pull_request can reach (a fork PR would run on this machine)"

    testCase "some job targets the self-hosted runner, so the rule above is not vacuous" <| fun _ ->
      workflows.Value
      |> List.collect (snd >> SelfHostedSafety.jobsIn)
      |> List.exists SelfHostedSafety.isSelfHosted
      |> Expect.isTrue "the main build should run on this machine's self-hosted runner"

    testCase "every workflow file is read, not just main.yml" <| fun _ ->
      workflows.Value
      |> List.map fst
      |> Expect.contains "publish.yml runs on this machine too, so it has to be checked" "publish.yml"

    // The twins: each one is a workflow that MUST be reported, so a parser
    // that quietly stops seeing breaches fails here instead of going green.
    testCase "an unguarded self-hosted job on a pull_request trigger is reported" <| fun _ ->
      [ "bad.yml", workflowText "  pull_request:" "" "[self-hosted, sagefs-local]" ]
      |> SelfHostedSafety.breaches
      |> Expect.equal "a fork PR could run this job on this machine" [ "bad.yml/build" ]

    testCase "an unguarded self-hosted job on a workflow_call trigger is reported" <| fun _ ->
      [ "bad.yml", workflowText "  workflow_call:" "" "[self-hosted, sagefs-local]" ]
      |> SelfHostedSafety.breaches
      |> Expect.equal "a PR-triggered workflow could call this one" [ "bad.yml/build" ]

    testCase "a guard written as a block scalar counts, so the parser reads past `if: |`" <| fun _ ->
      [ "ok.yml", workflowText "  pull_request:" blockGuard "[self-hosted, sagefs-local]" ]
      |> SelfHostedSafety.breaches
      |> Expect.isEmpty "the job excludes pull_request in its multi-line guard"

    testCase "a hosted runner needs no guard" <| fun _ ->
      [ "ok.yml", workflowText "  pull_request:" "" "ubuntu-latest" ]
      |> SelfHostedSafety.breaches
      |> Expect.isEmpty "nothing runs on this machine, so a fork PR is harmless"

    testCase "a comment mentioning self-hosted is not a self-hosted job" <| fun _ ->
      [ "ok.yml", workflowText "  pull_request:" "    # self-hosted was considered here" "ubuntu-latest" ]
      |> SelfHostedSafety.breaches
      |> Expect.isEmpty "only the job's own runs-on decides where it runs"
  ]

[<Tests>]
let tests = testList "TrustSignal" [ verdictTests; ciWiringTests; selfHostedSafetyTests ]
