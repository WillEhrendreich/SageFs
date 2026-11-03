module SageFs.Tests.AnalyzerBudgetTests

open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Build
open SageFs.Build.AnalyzerBudget
open SageFs.Tests.TestInfrastructure

let private repoRoot = RepoPaths.repoPathFull [||]

let private prop gen body = Prop.forAll (Arb.fromGen gen) body

/// What the CLI prints for a finding, colour codes and all, and the lines around it that are not findings.
let private cliOutput =
  String.concat "\n"
    [ "[21:00:01.000] [FSharp.Analyzers.Cli] info: Registered 1 analyzers from 1 dlls"
      "[21:00:09.000] [FSharp.Analyzers.Cli] info: Running analyzers for /repo/SageFs.Core/A.fs"
      "\u001b[33m/repo/SageFs.Core/A.fs(12,4): Warning SAGE-007 : Mutable binding 'x' in domain function 'f'.\u001b[0m"
      "\u001b[33m/repo/SageFs.Core/A.fs(30,9): Warning SAGE-007 : Mutable binding 'y' in domain function 'g'.\u001b[0m"
      "\u001b[34m/repo/SageFs.Core/B.fs(1,0): Info SAGE-000 : Mixed Error Strategy Analyzer did not finish on this file.\u001b[0m"
      "/repo/SageFs.Core/B.fs(8,2): Warning SAGE-004 : 'failwith' used in domain module 'M'."
      "a message that merely mentions SAGE-005 in passing is not a finding" ]

[<Tests>]
let parseTests =
  testList "Analyzer budget: reading the CLI's output" [

    testCase "WHY — every finding line counts once under its rule, colour codes and all" <| fun _ ->
      countByRule cliOutput
      |> Map.toList
      |> Expect.equal "two SAGE-007, one fault, one SAGE-004" [ "SAGE-000", 1; "SAGE-004", 1; "SAGE-007", 2 ]

    testCase "WHY — a line that only mentions a code, and the CLI's own info lines, are not findings" <| fun _ ->
      countByRule "x SAGE-005 y\n[t] [FSharp.Analyzers.Cli] info: Running analyzers for /a.fs\n"
      |> Expect.isEmpty "nothing counted"

    testCase "WHY — a rule with no findings is absent, not zero, so an empty run is an empty map" <| fun _ ->
      countByRule "" |> Expect.isEmpty "nothing"

    testCase "the key a budget is written under names the project and the rule" <| fun _ ->
      keyed "SageFs.Core" (Map.ofList [ "SAGE-007", 2; "SAGE-004", 1 ])
      |> Expect.equal "project slash rule" [ ("SageFs.Core/SAGE-004", 1); ("SageFs.Core/SAGE-007", 2) ]

    testProperty "WHY — the report a gate records reads back to exactly the counts it was written from" <|
      prop (Gen.listOf (Gen.zip (Gen.elements [ "SageFs.Core/SAGE-007"; "SageFs/SAGE-005"; "SageFs.Host/SAGE-009"; "SageFs/SAGE-000" ]) (Gen.choose (0, 500))) |> Gen.map (List.distinctBy fst)) (fun counts ->
        parseReport (renderReport counts) = List.sortBy fst counts)
  ]

[<Tests>]
let judgeTests =
  testList "Analyzer budget: judging" [

    testProperty "WHY — a rule is within, over or under its budget exactly as its count compares to the number" <|
      prop (Gen.zip (Gen.choose (0, 60)) (Gen.choose (0, 60))) (fun (count, budget) ->
        match judge [ ("P/SAGE-007", budget) ] [ ("P/SAGE-007", count) ] with
        | [ ("P/SAGE-007", WithinBudget) ] -> count = budget
        | [ ("P/SAGE-007", OverBudget (c, b)) ] -> count > budget && c = count && b = budget
        | [ ("P/SAGE-007", BudgetSlack (c, b)) ] -> count < budget && c = count && b = budget
        | _ -> false)

    testCase "WHY — a rule with no budget has a budget of zero, so a new kind of finding fails at once" <| fun _ ->
      judge [] [ ("SageFs/SAGE-001", 1) ]
      |> Expect.equal "over a zero budget" [ ("SageFs/SAGE-001", OverBudget (1, 0)) ]

    testCase "WHY — a budget for a rule that no longer fires is slack, so room that was won back cannot be spent again" <| fun _ ->
      judge [ ("SageFs/SAGE-001", 3) ] []
      |> Expect.equal "under its budget" [ ("SageFs/SAGE-001", BudgetSlack (0, 3)) ]

    testCase "WHY — an analyzer that did not finish a file is a finding, so a file cannot go unchecked quietly" <| fun _ ->
      let counts = keyed "SageFs.Core" (countByRule cliOutput)
      judge [] counts
      |> List.exists (fun (key, standing) -> key = "SageFs.Core/SAGE-000" && fails standing)
      |> Expect.isTrue "the fault is over a zero budget"

    testCase "the words for a failing standing say what to do, and never offer to raise a budget" <| fun _ ->
      describe "SageFs/SAGE-007" (OverBudget (12, 10)) |> Expect.stringContains "says a new one landed" "a new one landed"
      describe "SageFs/SAGE-007" (OverBudget (12, 10)) |> Expect.stringContains "refuses a raise" "never raised"
      describe "SageFs/SAGE-007" (BudgetSlack (8, 10)) |> Expect.stringContains "names the command" "--ratchets --tighten"
      describe "SageFs/SAGE-000" (OverBudget (1, 0)) |> Expect.stringContains "says a file went unchecked" "unchecked"
  ]

[<Tests>]
let commandTests =
  testList "Analyzer budget: the command" [

    testCase "WHY — the CLI is told the project, the one folder of the pack, the configuration and the framework" <| fun _ ->
      cliArguments "analyzers" "Release" "net11.0" "SageFs.Core"
      |> Expect.equal "the arguments"
           [ "fsharp-analyzers"; "--project"; "SageFs.Core/SageFs.Core.fsproj"; "--analyzers-path"; "analyzers"
             "--configuration"; "Release"; "--property"; "TargetFramework=net11.0"; "--verbosity"; "normal" ]

    testCase "WHY — a machine whose newest runtime is a release candidate needs both roll-forward settings" <| fun _ ->
      environment |> Expect.contains "major roll-forward" ("DOTNET_ROLL_FORWARD", "LatestMajor")
      environment |> Expect.contains "prerelease roll-forward" ("DOTNET_ROLL_FORWARD_TO_PRERELEASE", "1")
  ]

/// The newest counts there are, for `--ratchets --tighten` to lower the table to: the last passed gate's record, or the
/// file `scripts/analyzer-counts.fsx` wrote in this checkout, whichever is newer. The second is what makes a lowering
/// possible when the pack gets better: the gate fails on a budget that is now too high, so no passed gate ever holds the
/// new numbers, but the script writes them before it judges. A machine with neither has nothing to compare, so it reports
/// the table itself and a tighten changes nothing.
let private lastCounts () : (string * int) list =
  let local = Path.Combine(repoRoot, "test-results", reportFileName)
  [ GatePassedRecord.newestPath reportFileName
    (match File.Exists local with
     | true -> Some local
     | false -> None) ]
  |> List.choose id
  |> List.sortByDescending File.GetLastWriteTimeUtc
  |> List.tryHead
  |> Option.map (File.ReadAllText >> parseReport)
  |> Option.defaultValue RatchetBudgets.analyzerFindings

/// The budget table as data, so `--ratchets --tighten` can lower it.
let private budgetTable =
  Ratchet.table
    { Name = "analyzer findings"
      SourceFile = RatchetBudgets.sourceFile
      Budgets = RatchetBudgets.analyzerFindings
      Actual = lastCounts }

/// The budget table and its place in the gate.
let analyzerBudgetRatchets =
  let pipeline = lazy (File.ReadAllText(Path.Combine(repoRoot, "ci-pipeline.fsx")))
  let localGate = lazy (File.ReadAllText(Path.Combine(repoRoot, "scripts", "local-gate.fsx")))
  let script = lazy (File.ReadAllText(Path.Combine(repoRoot, "scripts", "analyzer-counts.fsx")))
  let keyShape = System.Text.RegularExpressions.Regex(@"^(?<project>[A-Za-z.]+)/SAGE-\d{3}$")

  testList "Analyzer budget table and wiring" [

    testCase "WHY — every key names a project the gate analyzes and a rule, once, and none is the fault code" <| fun _ ->
      let keys = RatchetBudgets.analyzerFindings |> List.map fst
      keys |> List.distinct |> Expect.equal "each key once" keys
      keys
      |> List.filter (fun key ->
        let m = keyShape.Match key
        not m.Success || not (List.contains m.Groups.["project"].Value projects))
      |> Expect.isEmpty "keys that are not <analyzed project>/SAGE-nnn"
      keys
      |> List.filter (fun key -> key.EndsWith("/" + faultCode))
      |> Expect.isEmpty "an analyzer that could not finish a file is a failure, never debt"
      RatchetBudgets.analyzerFindings |> List.forall (fun (_, budget) -> budget > 0) |> Expect.isTrue "a zero budget is written by leaving the key out"
      budgetTable.Budgets |> Expect.equal "the registered table is this table" RatchetBudgets.analyzerFindings

    testCase "WHY — the pack folder holds exactly one build, because the CLI loads every DLL under it and would count each finding twice" <| fun _ ->
      let dlls = Directory.GetFiles(Path.Combine(repoRoot, "analyzers"), "*.dll", SearchOption.AllDirectories)
      dlls |> Array.map Path.GetFileName |> Expect.equal "one DLL" [| "sageAnalyzers.dll" |]

    testCase "WHY — the tool manifest pins the CLI the pack was built for, so a restore cannot bring an older compiler service" <| fun _ ->
      File.ReadAllText(Path.Combine(repoRoot, "dotnet-tools.json"))
      |> Expect.stringContains "fsharp-analyzers is pinned to the SDK version the pack was built against" "\"version\": \"0.41.0\""

    testCase "WHY — the pipeline has an analyzers stage that runs the script, so there is one home for what the stage does" <| fun _ ->
      let text = pipeline.Value
      text |> Expect.stringContains "the stage exists" "stage \"analyzers\""
      text |> Expect.stringContains "and runs the script" "dotnet fsi scripts/analyzer-counts.fsx"
      text |> Expect.stringContains "before the tiers, so a finding fails in minutes" "stage \"test tiers\""
      (text.IndexOf "stage \"analyzers\"" < text.IndexOf "stage \"test tiers\"") |> Expect.isTrue "the stage comes before the tiers"

    testCase "WHY — the script restores the pinned CLI, runs each project with the shared arguments, writes the counts BEFORE it judges, and fails on a non-within standing" <| fun _ ->
      let text = script.Value
      text |> Expect.stringContains "the CLI is restored from the manifest first" "[ \"tool\"; \"restore\" ]"
      text |> Expect.stringContains "each project is run with the shared arguments" "AnalyzerBudget.cliArguments"
      text |> Expect.stringContains "with the shared environment" "AnalyzerBudget.environment"
      text |> Expect.stringContains "the counts are judged over the table" "AnalyzerBudget.judge SageFs.Tests.RatchetBudgets.analyzerFindings"
      text |> Expect.stringContains "a failing standing fails the run" "AnalyzerBudget.fails"
      text |> Expect.stringContains "the counts are written under the shared file name" "AnalyzerBudget.reportFileName"
      (text.IndexOf "File.WriteAllText(reportFile" < text.IndexOf "let failing =") |> Expect.isTrue "the report is written before the verdict"

    testCase "WHY — a passed gate keeps the counts, because `--tighten` reads them from there" <| fun _ ->
      localGate.Value |> Expect.stringContains "the local gate copies the report beside the ledger" reportFileName
  ]

[<Tests>]
let tests =
  let ratchet (test: Test) = Ratchet.register Ratchet.Invariant test
  ratchet analyzerBudgetRatchets
