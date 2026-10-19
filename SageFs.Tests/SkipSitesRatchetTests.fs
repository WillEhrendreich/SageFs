/// A test that skips itself checks nothing.
///
/// `skiptest`, `ptestCase`, `ptestList`, `ptestTask`, `ptestProperty` and `ptestSequenced` all end a run with
/// "passed" beside a case that never asserted anything, and a skip that fires only on another OS, or only when a
/// fixture was not built, is the same defect on a machine nobody is looking at. On 2026-10-19 the local gate failed on its own
/// ignored-count check while every tier was green, and the owner's answer was that ignoring tests instead of fixing
/// them or deleting them is not acceptable.
///
/// So the rule is structural: no file under `SageFs.Tests` uses a skip. A test either runs, is registered only
/// where it is meaningful (decided when the list is built, so it does not exist elsewhere), fails loudly when a
/// prerequisite is missing, or is deleted. `RatchetBudgets.skipSites` names the files that may still hold one and how
/// many, it only goes down, and a file that is not in it has a budget of zero.
///
/// What this counts: occurrences of those names in CODE. Comments and string literals are removed first, because
/// the product and this file talk about the pattern. Not seen: a skip reached through an alias, or one built in
/// a file outside `SageFs.Tests`.
module SageFs.Tests.SkipSitesRatchetTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

let private repoRoot = RepoPaths.repoPathFull [||]

/// Every way this suite has of ending a case as not-run.
let private skipPattern =
  Regex(@"\b(skiptest|ptestCase|ptestList|ptestTask|ptestProperty|ptestSequenced|ptestCaseAsync|ptestAsync)\b", RegexOptions.Compiled)

/// Removes string literals (regular, verbatim and triple-quoted) and `//` comments, leaving code.
let private codeOnly (text: string) : string =
  let tripleQuoted = Regex("\"\"\"[\\s\\S]*?\"\"\"", RegexOptions.Compiled)
  let verbatim = Regex("@\"(?:[^\"]|\"\")*\"", RegexOptions.Compiled)
  let regular = Regex("\"(?:[^\"\\\\\\n]|\\\\.)*\"", RegexOptions.Compiled)
  let comment = Regex(@"(^|\s)//[^\n]*", RegexOptions.Compiled ||| RegexOptions.Multiline)
  text
  |> fun t -> tripleQuoted.Replace(t, "\"\"")
  |> fun t -> verbatim.Replace(t, "\"\"")
  |> fun t -> regular.Replace(t, "\"\"")
  |> fun t -> comment.Replace(t, "$1")

/// How many skips `text` holds in code.
let countSkips (text: string) : int = skipPattern.Matches(codeOnly text).Count

let private actual : (string * int) list =
  let dir = Path.Combine(repoRoot, "SageFs.Tests")
  [ for file in Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories) do
      let relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/')
      match relative.Contains "/obj/" || relative.Contains "/bin/" with
      | true -> ()
      | false ->
        match countSkips (File.ReadAllText file) with
        | 0 -> ()
        | n -> yield relative, n ]

let private budgets : (string * int) list = RatchetBudgets.skipSites

let private budgetTable =
  TestInfrastructure.Ratchet.table
    { Name = "skip sites"
      SourceFile = RatchetBudgets.sourceFile
      Budgets = budgets
      Actual = fun () -> actual }

[<Tests>]
let tests =
  let ratchet (test: Test) = TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant test
  testList "Skip sites" [

    testCase "WHY: no test file skips a case beyond its budget, because a skipped case asserts nothing and reads as a pass" <| fun _ ->
      let allowed = Map.ofList budgets
      let over =
        actual
        |> List.choose (fun (file, count) ->
          let budget = Map.tryFind file allowed |> Option.defaultValue 0
          match count > budget with
          | true -> Some (sprintf "%s has %d, budget %d" file count budget)
          | false -> None)
      over
      |> Expect.isEmpty (sprintf "every file is within its budget (make the case run, register it by platform, fail loudly, or delete it). Over: %s" (String.concat "; " over))

    testCase "WHY: a skip budget above the file's real count is stale, so room that was already won back cannot be spent again" <| fun _ ->
      let counts = Map.ofList actual
      let stale =
        budgets
        |> List.choose (fun (file, budget) ->
          let count = Map.tryFind file counts |> Option.defaultValue 0
          match count < budget with
          | true -> Some (sprintf "%s has %d but its budget is %d: lower it with --ratchets --tighten" file count budget)
          | false -> None)
      stale
      |> Expect.isEmpty (sprintf "every budget equals the file's current count. Stale: %s" (String.concat "; " stale))

    testCase "WHY: the scan sees each way the suite ends a case as not-run, so the ratchet cannot go blind" <| fun _ ->
      let shapes =
        [ "skiptest call", "| None -> skiptest \"not here\""
          "skiptest qualified", "Tests.skiptest \"no\""
          "ptestCase", "ptestCase \"x\" ignore"
          "ptestList", "let l = ptestList \"x\" []"
          "ptestTask", "ptestTask \"x\" { () }"
          "ptestProperty", "ptestProperty \"x\" (fun () -> ())"
          "skiptest with a format", "skiptest (sprintf \"%d\" 1)" ]
      let missed = shapes |> List.filter (fun (_, text) -> countSkips text <> 1) |> List.map fst
      missed |> Expect.isEmpty "every shape counts as exactly one skip"

    testCase "WHY: the scan leaves alone what only talks about a skip, so a budget of zero is reachable" <| fun _ ->
      let innocent =
        [ "comment", "// we used to skiptest here"
          "doc comment", "/// ptestCase is how a case ends up ignored\nlet x = 1"
          "string literal", "let message = \"this would skiptest on Windows\""
          "triple-quoted", "let doc = \"\"\"ptestList and skiptest\"\"\""
          "verbatim", "let p = @\"C:\\ptestCase\\x\""
          "a longer name", "let skiptestCount = 3\nlet myptestCaseHelper = 1" ]
      let wrong = innocent |> List.filter (fun (_, text) -> countSkips text <> 0) |> List.map fst
      wrong |> Expect.isEmpty "none of these counts"
  ]
  |> ratchet
