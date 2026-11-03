/// What the custom analyzer pack (analyzers/sageAnalyzers.dll, source at WillEhrendreich/SageAnalyzers) finds in the
/// product projects, judged against a budget that only goes down.
///
/// The pack checks the house rules a reviewer used to check by hand: a `Result` or `Async` passed to `ignore`, an
/// exception thrown from domain code, `let mutable` in a domain function, swappable primitive parameters, a ref cell.
/// Each rule is counted per project, and the counts are the budget: a new finding fails the gate, and so does a budget
/// above the real count, because room that was won back must not be spendable again (the same two-sided rule as
/// `IgnoredBudget`). A rule that has no budget has a budget of ZERO, so a new kind of finding fails at once.
///
/// `SAGE-000` is not a rule. It is the pack saying that one of its analyzers threw on a file and was skipped for it,
/// which means that file went unchecked. It is counted like any finding and has no budget, so it fails the gate.
///
/// Pure, so the pipeline script and the tests share it. Loaded by ci-pipeline.fsx (`#load`).
module SageFs.Build.AnalyzerBudget

open System.Text.RegularExpressions

/// The code the pack gives an analyzer's failure on a file.
let faultCode = "SAGE-000"

/// The product projects whose findings are counted.
let projects : string list = [ "SageFs.Core"; "SageFs"; "SageFs.Host" ]

/// A project's file, repo-relative, by the repo's `<name>/<name>.fsproj` convention.
let projectFile (project: string) : string = sprintf "%s/%s.fsproj" project project

/// A machine whose newest runtime is a release candidate (this one: .NET 11 RC) makes the CLI refuse to start
/// without both. They cost nothing on a machine that has a release runtime.
let environment : (string * string) list =
  [ "DOTNET_ROLL_FORWARD", "LatestMajor"
    "DOTNET_ROLL_FORWARD_TO_PRERELEASE", "1" ]

/// The pinned CLI's arguments for one project (run as `dotnet <these>`). `analyzersPath` must be ONE folder holding
/// ONE build of the pack: the CLI loads every DLL under it, so two builds count each finding twice.
let cliArguments (analyzersPath: string) (configuration: string) (targetFramework: string) (project: string) : string list =
  [ "fsharp-analyzers"
    "--project"; projectFile project
    "--analyzers-path"; analyzersPath
    "--configuration"; configuration
    "--property"; sprintf "TargetFramework=%s" targetFramework
    "--verbosity"; "normal" ]

let ansiEscape = Regex(@"\x1b\[[0-9;]*m", RegexOptions.Compiled)

/// `<file>(<line>,<col>): Warning SAGE-007 : <message>` is a finding. A line that merely mentions a code is not.
let findingLine = Regex(@"\(\d+,\d+\): (?:Hint|Info|Warning|Error) (SAGE-\d{3}) : ", RegexOptions.Compiled)

/// Findings per rule code in one project's CLI output. A rule with none is absent.
let countByRule (cliOutput: string) : Map<string, int> =
  ansiEscape.Replace(cliOutput, "").Split('\n')
  |> Array.choose (fun line ->
    let m = findingLine.Match line
    match m.Success with
    | true -> Some m.Groups.[1].Value
    | false -> None)
  |> Array.countBy id
  |> Map.ofArray

/// `SageFs.Core/SAGE-007`: a rule's count in one project, the key a budget is written under.
let keyed (project: string) (counts: Map<string, int>) : (string * int) list =
  counts |> Map.toList |> List.map (fun (code, n) -> sprintf "%s/%s" project code, n)

/// Where a rule stands against its budget in one project.
type Standing =
  | WithinBudget
  | OverBudget of count: int * budget: int
  | BudgetSlack of count: int * budget: int

/// Every key that has a count or a budget, against its budget. A key with neither listed counts zero on that side.
let judge (budgets: (string * int) list) (counts: (string * int) list) : (string * Standing) list =
  let lookup (table: (string * int) list) (key: string) =
    table |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd |> Option.defaultValue 0
  (budgets |> List.map fst) @ (counts |> List.map fst)
  |> List.distinct
  |> List.sort
  |> List.map (fun key ->
    let count, budget = lookup counts key, lookup budgets key
    key,
    (match compare count budget with
     | 0 -> WithinBudget
     | c when c > 0 -> OverBudget (count, budget)
     | _ -> BudgetSlack (count, budget)))

let fails (standing: Standing) : bool =
  match standing with
  | WithinBudget -> false
  | OverBudget _
  | BudgetSlack _ -> true

/// What a failing standing says, including what to do about it. It never offers to raise a budget.
let describe (key: string) (standing: Standing) : string =
  match standing with
  | WithinBudget -> sprintf "%s is within its budget" key
  | OverBudget (count, budget) when key.EndsWith("/" + faultCode) ->
    sprintf "%s: %d analyzer run(s) did not finish a file (budget %d), so those files went unchecked. Read the SAGE-000 lines in the stage's log for the analyzer and the exception." key count budget
  | OverBudget (count, budget) ->
    sprintf "%s has %d findings and its budget is %d: a new one landed. Fix it; a budget is never raised." key count budget
  | BudgetSlack (count, budget) ->
    sprintf "%s has %d findings and its budget is %d: the room was won back. Lower the budget with `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets --tighten`." key count budget

/// The counts as the line-per-key text a gate records beside its ledger, for `--ratchets --tighten` to read.
let renderReport (counts: (string * int) list) : string =
  counts |> List.sortBy fst |> List.map (fun (key, n) -> sprintf "%s %d" key n) |> String.concat "\n"

let parseReport (text: string) : (string * int) list =
  text.Split('\n')
  |> Array.choose (fun line ->
    match line.Trim().Split(' ') with
    | [| key; n |] when fst (System.Int32.TryParse n) -> Some (key, int n)
    | _ -> None)
  |> List.ofArray

/// The file name the gate records the counts under, in `test-results/` and then in the passed record.
let reportFileName = "analyzer-counts.txt"
