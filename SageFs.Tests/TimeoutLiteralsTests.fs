/// A timeout is a decision with a name and one home, never a bare number at the call site.
/// `Timeouts` (SageFs.Core/Timeouts.fs) holds the product's, `TestTimeouts`
/// (SageFs.Tests/TestTimeouts.fs) holds the ones a test picks on purpose, and each says
/// what the wait is for and why that long. This pins where an inline literal is still left:
/// `TimeSpan.From...` with a number, `Task.Delay n`, `Thread.Sleep n`, `Async.Sleep n`, `.AddSeconds n` and
/// its kin, `WaitForExit n`, `CancelAfter n`, a `...Ms = n` or `timeout = n` binding, and a bare digit-group
/// number written with underscore groups (a millisecond count wearing no unit). Each file has a budget,
/// the budgets only go DOWN, a file that is not listed has a budget of zero, and a file under its
/// budget is stale. When the table is empty there is nowhere a magic duration can hide.
module SageFs.Tests.TimeoutLiteralsTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

let private repoRoot = RepoPaths.repoPathFull [||]

let private literalPattern =
  Regex(@"TimeSpan\.From(Seconds|Milliseconds|Minutes|Hours|Days)\s*\(?\s*[0-9]|Task\.Delay\s*\(?\s*[0-9]|Thread\.Sleep\s*\(?\s*[0-9]|Async\.Sleep\s*\(?\s*[0-9]|\.Add(Milliseconds|Seconds|Minutes|Hours|Days)\s*\(\s*[0-9]|WaitForExit\s*\(\s*[0-9]|CancelAfter\s*\(\s*[0-9]|[A-Za-z](Ms|Millis|Seconds|Secs)\s*=\s*[0-9]|[Tt]imeout\s*=\s*[0-9]|\b[0-9]{1,3}(_000)+L?\b", RegexOptions.Compiled)

/// The files allowed to hold durations: the product's, and the ones a test picks on purpose.
let private central = Set.ofList [ "SageFs.Core/Timeouts.fs"; "SageFs.Tests/TestTimeouts.fs"; "SageFs.Tests/LiveTestingBudgets.fs" ]

/// What each file may still spell out itself. Ratchet down, never up.
let private budgets : (string * int) list =
  [ "SageFs.Core/WorkflowTypes.fs", 1 ]

/// Every source file with its count of inline timeout literals, except the central module.
let private actual : (string * int) list =
  [ for project in [ "SageFs"; "SageFs.Core"; "SageFs.Host"; "SageFs.Tests" ] do
      let dir = Path.Combine(repoRoot, project)
      match Directory.Exists dir with
      | false -> ()
      | true ->
        for file in Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories) do
          let relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/')
          let skipped =
            relative.Contains "/obj/" || relative.Contains "/bin/" || relative.Contains "/fixtures/" || central.Contains relative
          match skipped with
          | true -> ()
          | false ->
            match literalPattern.Matches(File.ReadAllText file).Count with
            | 0 -> ()
            | n -> yield relative, n ]

/// The budget table as data, so `--ratchets --tighten` can lower it.
let private budgetTable =
  TestInfrastructure.Ratchet.table
    { Name = "timeout literals"
      SourceFile = "SageFs.Tests/TimeoutLiteralsTests.fs"
      Budgets = budgets
      Actual = fun () -> actual }

[<Tests>]
let tests =
  testList "Timeout literals" [

    testCase "WHY — no file spells out more durations than its budget, so the places a magic timeout can hide only shrink" <| fun _ ->
      let allowed = Map.ofList budgets
      let over =
        actual
        |> List.choose (fun (file, count) ->
          let budget = Map.tryFind file allowed |> Option.defaultValue 0
          match count > budget with
          | true -> Some (sprintf "%s has %d, budget %d" file count budget)
          | false -> None)
      over |> Expect.isEmpty "every file is within its budget (a new duration goes in Timeouts, or TestTimeouts for a test, with a name and a reason)"

    testCase "WHY — a budget above the file's real count is stale, so room that was already won back cannot be spent again" <| fun _ ->
      let counts = Map.ofList actual
      let stale =
        budgets
        |> List.choose (fun (file, budget) ->
          let count = Map.tryFind file counts |> Option.defaultValue 0
          match count < budget with
          | true -> Some (sprintf "%s has %d but its budget is %d: lower it" file count budget)
          | false -> None)
      stale |> Expect.isEmpty "every budget equals the file's current count"
  ]
  |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
