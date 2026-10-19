module SageFs.Tests.IgnoredBudgetTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Build
open SageFs.Build.IgnoredBudget
open SageFs.Tests.TestInfrastructure
open SageFs.Tests.TestInfrastructure.TrustSignal

let private repoRoot = RepoPaths.repoPathFull [||]

/// TrustSignal also has a `judge` (a run's verdict); this file means the budget's.
let private judge = IgnoredBudget.judge

let private row (tier: string) (ignored: int) : TierRow = { Tier = tier; Ignored = ignored; Red = false }

let private prop gen body = Prop.forAll (Arb.fromGen gen) body

let private tally passed failed errored ignored : Tally =
  { Passed = passed; Failed = failed; Errored = errored; Ignored = ignored }

/// The last green gate's ignored count per tier, for `--ratchets --tighten` to lower the table to. A machine with no
/// gate pass yet has nothing to compare, so it reports the table itself and a tighten changes nothing.
let private lastGateIgnored () : (string * int) list =
  let home =
    match Environment.GetEnvironmentVariable "SAGEFS_GATE_HOME" with
    | null | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".local", "share", "sagefs-gate")
    | path -> path
  let ledgers =
    match Directory.Exists(Path.Combine(home, "passed")) with
    | false -> []
    | true ->
      Directory.GetDirectories(Path.Combine(home, "passed"))
      |> Array.map (fun d -> Path.Combine(d, "trust-ledger.jsonl"))
      |> Array.filter File.Exists
      |> Array.sortByDescending File.GetLastWriteTimeUtc
      |> List.ofArray
  match ledgers with
  | [] -> RatchetBudgets.ignoredPerTier
  | newest :: _ ->
    File.ReadAllLines newest
    |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
    |> Array.map (fun l ->
      use doc = Text.Json.JsonDocument.Parse l
      baseTier (doc.RootElement.GetProperty("Tier").GetString()), doc.RootElement.GetProperty("Ignored").GetInt32())
    |> Array.groupBy fst
    |> Array.map (fun (tier, counts) -> tier, counts |> Array.sumBy snd)
    |> List.ofArray

/// The budget table as data, so `--ratchets --tighten` can lower it.
let private budgetTable =
  Ratchet.table
    { Name = "ignored tests per tier"
      SourceFile = RatchetBudgets.sourceFile
      Budgets = RatchetBudgets.ignoredPerTier
      Actual = lastGateIgnored }

[<Tests>]
let judgeTests =
  testList "Ignored budget" [

    testCase "WHY — a shard suffix is stripped, so the host shards are one tier and a plain tier is itself" <| fun _ ->
      baseTier "--integration-host[2/5]" |> Expect.equal "a shard" "--integration-host"
      baseTier "--integration-lt[1/4]" |> Expect.equal "another sharded tier" "--integration-lt"
      baseTier "default-net10" |> Expect.equal "a framework label is not a shard" "default-net10"

    testProperty "WHY — a tier is within, over or under its budget exactly as its ignored count compares to the number" <|
      prop (Gen.zip (Gen.choose (0, 40)) (Gen.choose (0, 40))) (fun (ignored, budget) ->
        match judge [ "default", budget ] [ row "default" ignored ] with
        | [ "default", WithinBudget ] -> ignored = budget
        | [ "default", OverBudget (i, b) ] -> ignored > budget && i = ignored && b = budget
        | [ "default", BudgetSlack (i, b) ] -> ignored < budget && i = ignored && b = budget
        | _ -> false)

    testCase "WHY — the budget for a tier that is not in the table is zero, so a new tier cannot ignore anything silently" <| fun _ ->
      judge [] [ row "--integration-browser" 1 ] |> Expect.equal "one ignored case, no entry" [ "--integration-browser", OverBudget (1, 0) ]
      judge [] [ row "--integration-browser" 0 ] |> Expect.equal "nothing ignored, no entry" [ "--integration-browser", WithinBudget ]

    testCase "WHY — the shards of one tier add up before they are judged" <| fun _ ->
      judge [ "--integration-host", 6 ] [ row "--integration-host[1/5]" 0; row "--integration-host[2/5]" 4; row "--integration-host[5/5]" 2 ]
      |> Expect.equal "4 + 2 against 6" [ "--integration-host", WithinBudget ]

    testProperty "WHY — however the ignored cases are spread over shards, only their sum is judged" <|
      prop (Gen.listOf (Gen.choose (0, 9))) (fun counts ->
        let rows = counts |> List.mapi (fun i c -> row (sprintf "--integration-host[%d/9]" (i + 1)) c)
        let expected = List.sum counts
        match rows with
        | [] -> judge [ "--integration-host", 3 ] rows = []
        | _ -> judge [ "--integration-host", expected ] rows = [ "--integration-host", WithinBudget ])

    testCase "WHY — a tier with a red row is not judged: a shard that never finished says nothing about the suite" <| fun _ ->
      judge [ "--integration-host", 6 ] [ row "--integration-host[1/5]" 0; { row "--integration-host[2/5]" 4 with Red = true } ]
      |> Expect.isEmpty "the tier is red for its own reason, and no budget verdict is made from half of it"

    testCase "WHY — over and under budget both fail, within does not, and the words say what to do" <| fun _ ->
      fails WithinBudget |> Expect.isFalse "within"
      fails (OverBudget (7, 6)) |> Expect.isTrue "over"
      fails (BudgetSlack (5, 6)) |> Expect.isTrue "slack"
      describe (OverBudget (7, 6)) |> Expect.stringContains "over says to make it run or raise with a reason" "raise the budget"
      describe (BudgetSlack (5, 6)) |> Expect.stringContains "slack says to lower it" "lower it"
      [ WithinBudget; OverBudget (7, 6); BudgetSlack (5, 6) ]
      |> List.map verdictName
      |> List.distinct
      |> Expect.hasLength "one verdict name per standing" 3

    testCase "WHY — the section names every ignored case under its tier, and is empty when nothing was ignored" <| fun _ ->
      renderSection [] |> Expect.equal "no tiers" ""
      renderSection [ { Tier = "default"; Count = 0; Names = [] } ] |> Expect.equal "nothing ignored" ""
      let text = renderSection [ { Tier = "default"; Count = 2; Names = [ "A.one"; "B.two" ] } ]
      text |> Expect.stringContains "the tier and its count" "- default: 2 ignored"
      text |> Expect.stringContains "the first case by name" "  - A.one"
      text |> Expect.stringContains "the second case by name" "  - B.two"

    testCase "WHY — a row an older run wrote has a count and no names, and the section says so rather than listing nothing" <| fun _ ->
      renderSection [ { Tier = "default"; Count = 5; Names = [] } ]
      |> Expect.stringContains "honest about the gap" "did not name them"
  ]

[<Tests>]
let ledgerTests =
  testList "Ignored accounting in the trust ledger" [

    testCase "WHY — a clean tier with ignored cases no longer says every registered test ran and passed" <| fun _ ->
      let detail = detailOf Trusted (tally 13476 0 0 6)
      detail |> Expect.stringContains "it counts the passed" "13476 passed"
      detail |> Expect.stringContains "it counts the ignored" "6 ignored"
      detail.Contains "ran and passed" |> Expect.isFalse "the false sentence is gone"
      detailOf Trusted (tally 10 0 0 0) |> Expect.equal "no ignored keeps the old sentence" "every registered test ran and passed"

    testCase "WHY — only a Trusted verdict changes its sentence: a failure keeps saying what failed" <| fun _ ->
      detailOf (TestsFailed (1, 0)) (tally 5 1 0 2) |> Expect.equal "failed" (Verdict.describe (TestsFailed (1, 0)))
      detailOf NarrowedRun (tally 5 0 0 2) |> Expect.equal "narrowed" (Verdict.describe NarrowedRun)

    testCase "WHY — a real run carries the ignored cases' names into the ledger row" <| fun _ ->
      let tree = testList "probe" [ testCase "a" ignore; ptestCase "skipped one" ignore; ptestCase "skipped two" ignore ]
      let rows = ResizeArray<Row>()
      runReporting rows.Add "probe" [| "--no-spinner" |] tree |> Expect.equal "pending cases do not fail the run" 0
      match List.ofSeq rows with
      | [ r ] ->
        r.Ignored |> Expect.equal "two ignored" 2
        r.IgnoredNames |> Array.sort |> Expect.equal "both named, by full name" [| "probe.skipped one"; "probe.skipped two" |]
        r.Detail |> Expect.stringContains "the detail counts them" "2 ignored"
        r.Verdict |> Expect.equal "still Trusted: the budget decides, not the verdict" "Trusted"
      | other -> failtestf "expected exactly one row, got %d" other.Length

    testCase "WHY — a run with nothing ignored has an empty list of names" <| fun _ ->
      let rows = ResizeArray<Row>()
      runReporting rows.Add "probe" [| "--no-spinner" |] (testList "probe" [ testCase "a" ignore ]) |> ignore
      rows.[0].IgnoredNames |> Expect.isEmpty "nothing to name"
  ]

/// The budget table and its place in the gate.
let ignoredBudgetRatchets =
  let pipeline = lazy (File.ReadAllText(Path.Combine(repoRoot, "ci-pipeline.fsx")))

  /// The tier names the pipeline invokes, framework-qualified the way the ledger names them.
  let invokedTierNames () =
    TrustSignal.pipelineFrameworkTiers pipeline.Value
    |> List.map (fun (framework, args) -> TierPlan.qualify framework (TrustSignal.tierOfArgs args))
    |> Set.ofList

  testList "Ignored budget table and wiring" [
    testCase "WHY — every tier the budget table names is a tier the pipeline runs, so a typo cannot hide an entry that budgets nothing" <| fun _ ->
      let known = invokedTierNames ()
      RatchetBudgets.ignoredPerTier
      |> List.map fst
      |> List.filter (fun key -> not (known.Contains key))
      |> Expect.isEmpty "table keys that no pipeline tier carries"

    testCase "WHY — the table has no tier twice and no negative budget" <| fun _ ->
      let keys = RatchetBudgets.ignoredPerTier |> List.map fst
      keys |> List.distinct |> Expect.equal "each tier once" keys
      RatchetBudgets.ignoredPerTier |> List.forall (fun (_, budget) -> budget >= 0) |> Expect.isTrue "budgets are counts"
      budgetTable.Budgets |> Expect.equal "the registered table is this table" RatchetBudgets.ignoredPerTier

    testCase "WHY — the pipeline judges the budget over the ledger and a failing standing is a red row" <| fun _ ->
      let text = pipeline.Value
      text |> Expect.stringContains "the budget module is loaded" "#load \"build/IgnoredBudget.fs\""
      text |> Expect.stringContains "the budget table is loaded" "#load \"SageFs.Tests/RatchetBudgets.fs\""
      text |> Expect.stringContains "the rows come from the same lines the table prints" "IgnoredBudget.judge SageFs.Tests.RatchetBudgets.ignoredPerTier"
      text |> Expect.stringContains "a failing standing is red" "Red = IgnoredBudget.fails standing"
      text |> Expect.stringContains "the budget rows join the trust lines" "@ ignoredBudgetLines lines"

    testCase "WHY — the ignored cases are named in the report the trust stage writes" <| fun _ ->
      pipeline.Value |> Expect.stringContains "the section is rendered by the pure function" "IgnoredBudget.renderSection"
      pipeline.Value |> Expect.stringContains "and appended to the table" "renderTrustTable lines + ignoredCasesSection ()"
  ]

[<Tests>]
let tests =
  let ratchet (test: Test) = Ratchet.register Ratchet.Invariant test
  ratchet ignoredBudgetRatchets
