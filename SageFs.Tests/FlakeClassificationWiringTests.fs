module SageFs.Tests.FlakeClassificationWiringTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open SageFs.Tests.TestInfrastructure

/// The pure parts (FlakeClassification, CaseHistory) are proven in FlakeClassificationTests. These cases keep the glue
/// in ci-pipeline.fsx from quietly losing its teeth: a classifier nobody calls is a stub with a test, and a classifier
/// that could turn a red into a green would be worse than none. The owner's rule is that rerunning to escape a flake
/// proves nothing, so the verdict is information and never an outcome.
let flakeClassificationWiringTests =
  let repoRoot = RepoPaths.repoPathFull [||]
  let pipeline = lazy (File.ReadAllText(Path.Combine(repoRoot, "ci-pipeline.fsx")))

  /// The text of one stage, from its declaration to the next stage (or the end of the file).
  let stageText (name: string) =
    let text = pipeline.Value
    match text.IndexOf(sprintf "stage \"%s\"" name) with
    | -1 -> ""
    | start ->
      let tail = text.Substring start
      match tail.IndexOf("\n  stage \"", 1) with
      | -1 -> tail
      | next -> tail.Substring(0, next)

  /// The text of one top-level `let`, from its declaration to the next top-level declaration.
  let letText (name: string) =
    let text = pipeline.Value
    match text.IndexOf(sprintf "\nlet %s " name) with
    | -1 -> ""
    | start ->
      let tail = text.Substring(start + 1)
      match Regex.Match(tail.Substring 1, "\n(let|type|//) ").Index with
      | 0 -> tail
      | next -> tail.Substring(0, next + 1)

  let stages () =
    Regex.Matches(pipeline.Value, "^  stage \"([^\"]+)\"", RegexOptions.Multiline)
    |> Seq.map (fun m -> m.Groups[1].Value)
    |> List.ofSeq

  testList "Flake classification wiring" [
    testCase "the pipeline loads both pure modules, so the script and the tests read one definition" <| fun _ ->
      pipeline.Value |> Expect.stringContains "the classifier is loaded" "#load \"build/FlakeClassification.fs\""
      pipeline.Value |> Expect.stringContains "the history is loaded" "#load \"build/CaseHistory.fs\""
      File.Exists(Path.Combine(repoRoot, "build", "FlakeClassification.fs")) |> Expect.isTrue "the classifier is on disk"
      File.Exists(Path.Combine(repoRoot, "build", "CaseHistory.fs")) |> Expect.isTrue "the history is on disk"

    testCase "a classify stage runs after the tiers and before the trust report that judges them" <| fun _ ->
      let all = stages ()
      let at name = all |> List.tryFindIndex (fun s -> s = name)
      match at "test tiers", at "classify failures", at "trust report" with
      | Some tiers, Some classify, Some report ->
        (tiers, classify) |> Expect.isLessThan "the tiers have ended before a case is rerun"
        (classify, report) |> Expect.isLessThan "the verdicts exist before the report prints them"
      | other -> failtestf "need test tiers, classify failures and trust report in that order, found %A" other

    testCase "the stage reruns the failing cases and cannot fail the pipeline itself" <| fun _ ->
      let stage = stageText "classify failures"
      stage |> Expect.stringContains "it calls the classifier" "classifyFailures ()"
      stage |> Expect.stringContains "it is skipped with the tiers it classifies" "when' (not bypassTests)"
      stage.Contains "Error" |> Expect.isFalse "the stage returns Ok whatever the reruns came to: the trust report decides red"

    testCase "each failing case is rerun alone, N times, with the command the failure block prints" <| fun _ ->
      let body = letText "classifyFailures"
      body |> Expect.stringContains "N is the named value" "FlakeClassification.defaultReruns"
      body |> Expect.stringContains "the number of cases is capped by a named value" "FlakeClassification.maxCasesClassified"
      body |> Expect.stringContains "the reruns become a verdict through the pure classifier" "FlakeClassification.classify"
      body |> Expect.stringContains "the failing cases come from what the tiers reported" "failedCases"
      letText "reportFailure" |> Expect.stringContains "the rerun command is the printed `rerun:` command" "FailureReport.reproduceCommand"
      letText "reportFailure" |> Expect.stringContains "it is kept for the classifier with the case" "failedCases.Add((tier, failure, reproduce))"

    testCase "a rerun is judged by its TRUST line, so a filter that matched nothing is an error and not a pass" <| fun _ ->
      let body = letText "rerunCaseOnce"
      body |> Expect.stringContains "the outcome is read by the pure reader" "FlakeClassification.outcomeOf"
      body |> Expect.stringContains "the command is the reproduce command" "reproduce"
      body.Contains "SAGEFS_TRUST_LEDGER"
      |> Expect.isFalse "a rerun writes no ledger row: the table would read it as the tier's own"

    testCase "the verdict never decides red or green: the trust table's Red is still the tier's own verdict and exit" <| fun _ ->
      pipeline.Value
      |> Expect.stringContains "red is the tier's verdict and exit, nothing else" "Red = not (greenVerdict && stepOk)"
      letText "withVerdicts"
      |> fun text -> text.Contains "Red"
      |> Expect.isFalse "the verdict helper only words the Detail column"

    testCase "the verdict and the history are in the failure block, the trust table and the history file" <| fun _ ->
      let report = stageText "trust report"
      report |> Expect.stringContains "the final failure block carries the verdict lines" "FlakeClassification.reportLines"
      report |> Expect.stringContains "the final failure block carries the history sentence" "FailureReport.Ledgered"
      letText "tierTrustLines" |> Expect.stringContains "the trust table's detail carries the rate" "withVerdicts tier"
      letText "classifyFailures" |> Expect.stringContains "every classification is appended to the history file" "CaseHistory.append"
      letText "classifyFailures" |> Expect.stringContains "the history is read before this pass adds to it" "CaseHistory.read"
      letText "caseHistoryFile" |> Expect.stringContains "the file sits in the pass-record directory" "CaseHistory.fileName"

    testCase "the stub's wording is not what the failure block prints at stream time" <| fun _ ->
      letText "reportFailure"
      |> fun text -> text.Contains "FailureReport.NotRecorded"
      |> Expect.isFalse "at stream time the history is pending, not 'not recorded'"
  ]

[<Tests>]
let tests =
  let ratchet (test: Test) = TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant test
  ratchet flakeClassificationWiringTests
