/// How many tests a tier may skip, judged against a budget that only goes down.
///
/// The trust ledger counted an ignored test (`ptestCase`, `skiptest`) as one that ran, and the table said "every
/// registered test ran and passed" beside five or six ignored cases per tier. Some of those cases skip themselves
/// exactly when they would fail, so a skip is how a red can hide. A tier now has an ignored budget (the table is
/// `RatchetBudgets.ignoredPerTier`): more ignored cases than the budget fails the gate, so an ignored test can never be
/// added silently, and a budget above the real count fails too, so room that was won back cannot be spent again.
///
/// A tier that is not in the table has a budget of zero. A sharded tier (`--integration-host[2/5]`) is judged as one
/// tier, by the sum of its shards, because the shards between them hold one suite.
///
/// Pure, so the pipeline script and the tests share it. Loaded by ci-pipeline.fsx (`#load`).
module SageFs.Build.IgnoredBudget

open System.Text.RegularExpressions

/// Where a tier stands against its budget.
type Standing =
  | WithinBudget
  | OverBudget of ignored: int * budget: int
  | BudgetSlack of ignored: int * budget: int

/// One ledger row as the judge needs it. `Red` says the tier already failed for another reason.
type TierRow = { Tier: string; Ignored: int; Red: bool }

let shardSuffix = Regex(@"\[\d+/\d+\]$", RegexOptions.Compiled)

/// `--integration-host[2/5]` is the tier `--integration-host`; a tier with no shard suffix is itself.
let baseTier (tier: string) : string = shardSuffix.Replace(tier, "")

/// Every tier that ran, against its budget. A tier any of whose rows is already red is not judged: a shard that never
/// finished has an ignored count that says nothing about the suite, and the tier is red for its own reason.
let judge (budgets: (string * int) list) (rows: TierRow list) : (string * Standing) list =
  let budgetOf (tier: string) =
    budgets |> List.tryFind (fun (key, _) -> key = tier) |> Option.map snd |> Option.defaultValue 0
  rows
  |> List.groupBy (fun r -> baseTier r.Tier)
  |> List.filter (fun (_, group) -> group |> List.forall (fun r -> not r.Red))
  |> List.map (fun (tier, group) ->
    let ignored = group |> List.sumBy (fun r -> r.Ignored)
    let budget = budgetOf tier
    let standing =
      match compare ignored budget with
      | 0 -> WithinBudget
      | c when c > 0 -> OverBudget (ignored, budget)
      | _ -> BudgetSlack (ignored, budget)
    tier, standing)

/// Whether a standing fails the gate.
let fails (standing: Standing) : bool =
  match standing with
  | WithinBudget -> false
  | OverBudget _ | BudgetSlack _ -> true

/// The words for a standing, for the trust table's Detail column.
let describe (standing: Standing) : string =
  match standing with
  | WithinBudget -> "within its ignored budget"
  | OverBudget (ignored, budget) ->
    sprintf "ignored %d test(s) and the budget is %d: a test was skipped that was not before. Make it run, or raise the budget in RatchetBudgets.ignoredPerTier with the reason." ignored budget
  | BudgetSlack (ignored, budget) ->
    sprintf "ignored %d test(s) but the budget is %d: lower it in RatchetBudgets.ignoredPerTier so the room cannot be spent again." ignored budget

/// One tier's ignored cases as its ledger row carries them. `Names` is empty for a row written before the ledger named
/// them (a tier that took a pass record from an older run).
type IgnoredTier = { Tier: string; Count: int; Names: string list }

/// The section under the trust table that names every ignored case, so a skip is never only a number. Empty when no
/// tier ignored anything.
let renderSection (tiers: IgnoredTier list) : string =
  match tiers |> List.filter (fun t -> t.Count > 0) with
  | [] -> ""
  | withIgnored ->
    let lines =
      withIgnored
      |> List.collect (fun t ->
        let heading = sprintf "- %s: %d ignored" t.Tier t.Count
        match t.Names with
        | [] -> [ heading + " (the run that wrote this row did not name them)" ]
        | names -> heading :: (names |> List.map (fun n -> sprintf "  - %s" n)))
    String.concat "\n" ("" :: "### Ignored cases (skipped, so not passed)" :: "" :: lines)

/// The verdict name the trust table shows, one per standing.
let verdictName (standing: Standing) : string =
  match standing with
  | WithinBudget -> "IgnoredWithinBudget"
  | OverBudget _ -> "IgnoredOverBudget"
  | BudgetSlack _ -> "IgnoredBudgetSlack"
