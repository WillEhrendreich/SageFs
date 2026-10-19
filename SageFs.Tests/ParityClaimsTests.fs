/// The comparison pages say where Microsoft's tools are ahead of SageFs, and three things about the pages must not
/// drift.
///
/// First, every row under a "Where Visual Studio is still ahead" or "Where Microsoft is still ahead" line says what
/// SageFs plans about it, and when that was written. A row passes when it carries ONE of these two markers:
///
///   * a link to the roadmap (the text `roadmap.md` in the row), which holds the plan and keeps it true, or
///   * the dated plan marker `Plan, written YYYY-MM-DD`, followed by what is planned.
///
/// The opening line may carry the dated marker for every row below it, which is how a list whose plans were all written
/// on one day says so once. A "still ahead" line with no bullets under it is one row, and the line is the row.
///
/// A dated plan that reads `Plan, written YYYY-MM-DD: none` is a placeholder and does not count. Say what is planned,
/// or link the roadmap.
///
/// Second, no page says Neovim cannot debug a test or cannot switch a workflow while sagefs.nvim has the files that do
/// it (`lua/sagefs/debug_test.lua`, `lua/sagefs/workflow.lua`). A claim is a sentence, or a table cell, on a line that
/// is about the topic (the sentence "Neovim does not have it yet." never says "debug"), or a table row about the topic
/// whose Neovim column is a dash or "none". That check reads the plugin's checkout, which is a separate repository, so it looks
/// where SseParityTests looks (`SAGEFS_NVIM_DIR`, else next to the main checkout). When the checkout is not there the
/// test fails and the failure says how to clone it (CI checks it out in main.yml), and the pure matcher is still proved
/// against sample sentences in the same list.
///
/// Third, no reader page says its work is "not in a release yet" (the sentence is true until the next tag and false
/// after it), and every Status line in the progress diary names the release tag it is in.
module SageFs.Tests.ParityClaimsTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

let private repoRoot = RepoPaths.repoPathFull [||]

/// The pages that carry a "still ahead" list or a debugging claim. Generated pages (roadmap.md, configuration.md) and
/// the dated diary (progress.md) are left out on purpose: the first two are written by scripts, and the diary says what
/// was true on the day it was written.
let comparisonPages : string list list =
  [ [ "Readme.md" ]
    [ "docs"; "how-live-testing-works.md" ]
    [ "docs"; "how-hot-reload-works.md" ] ]

let private pagePath (segments: string list) : string =
  Path.Combine(repoRoot :: segments |> Array.ofList)

let private pageName (segments: string list) : string = String.Join("/", segments)

// ---- the "still ahead" rows ----------------------------------------------------------------------------------------

/// The line that opens a list of things Microsoft does better.
let aheadOpener = Regex(@"^Where (Visual Studio|Microsoft) is still ahead\b", RegexOptions.Compiled)

/// A link to the roadmap page, in any relative form (`roadmap.md`, `docs/roadmap.md`, `../docs/roadmap.md#next-6`).
let roadmapLink = Regex(@"\]\([^)\s]*roadmap\.md[^)]*\)", RegexOptions.Compiled)

/// `Plan, written 2026-10-01`, the one dated marker. A marker followed by `: none` is a placeholder that says
/// nothing was planned, so it is not a plan and does not count; say what is planned, or link the roadmap.
let datedPlan = Regex(@"Plan, written \d{4}-\d{2}-\d{2}(?!\s*:\s*(?i:none)\b)", RegexOptions.Compiled)

type AheadRow = { Opener: string; Text: string }

let hasPlanMarker (text: string) : bool =
  roadmapLink.IsMatch text || datedPlan.IsMatch text

/// The rows under every "still ahead" opener in a page: the bullets that follow it (after any blank lines), or the
/// opener line itself when no bullet follows.
let aheadRows (lines: string[]) : AheadRow list =
  [ for i in 0 .. lines.Length - 1 do
      match aheadOpener.IsMatch lines.[i] with
      | false -> ()
      | true ->
        let mutable j = i + 1
        while j < lines.Length && String.IsNullOrWhiteSpace lines.[j] do j <- j + 1
        let bullets =
          lines
          |> Array.skip (min j lines.Length)
          |> Array.takeWhile (fun l -> l.StartsWith "- ")
          |> Array.toList
        match bullets with
        | [] -> yield { Opener = lines.[i]; Text = lines.[i] }
        | rows -> for row in rows -> { Opener = lines.[i]; Text = row } ]

/// A row is covered by its own marker, or by the dated marker on its opener.
let rowIsCovered (row: AheadRow) : bool =
  hasPlanMarker row.Text || datedPlan.IsMatch row.Opener

// ---- the Neovim debugging claim ------------------------------------------------------------------------------------

/// Phrasings that say Neovim has no way to debug a test, or that debugging a test is VS Code's alone. A claim has to
/// mention debugging and match one of these. Each is written to miss a sentence that says what Neovim does have.
let neovimLacksPatterns : Regex list =
  [ @"(?i)\bnone in neovim"
    @"(?i)neovim has (no|none)\b"
    @"(?i)neovim (does not|doesn't) have (it|that|this)"
    @"(?i)neovim (does not|doesn't) (debug|support)"
    @"(?i)\bno (debug|debugger|debugging) (wiring|support) (in|for) neovim"
    @"(?i)neovim has no (debug|debugger|debugging)"
    @"(?i)\bnot (from|in) neovim"
    @"(?i)\bVS Code only\b"
    @"(?i)\bonly (in |from )?VS Code\b" ]
  |> List.map Regex

/// The statements on a line: each cell of a table row, or each sentence of a paragraph or bullet. A cell is kept whole
/// because the half of a cell that says "none in Neovim yet" often has no word "debug" in it.
let claimsOnLine (line: string) : string list =
  match line.TrimStart().StartsWith "|" with
  | true -> line.Split('|') |> Array.map (fun c -> c.Trim()) |> Array.filter (fun c -> c <> "") |> Array.toList
  | false -> Regex.Split(line, @"(?<=[.!?])\s+") |> Array.toList

/// True when a statement matches one of the lack phrasings, whatever it is about.
let private matchesAny (patterns: Regex list) (statement: string) : bool =
  patterns |> List.exists (fun p -> p.IsMatch statement)

/// True when a statement is about debugging and says Neovim can't.
let saysNeovimCannotDebug (statement: string) : bool =
  statement.IndexOf("debug", StringComparison.OrdinalIgnoreCase) >= 0 && matchesAny neovimLacksPatterns statement

/// Phrasings that say Neovim cannot switch a session's workflow. The plugin has `:SageFsWorkflow [name]` and an
/// older page said the command "only shows the current workflow".
let neovimCannotSwitchPatterns : Regex list =
  [ @"(?i)\bnot from the editor\b"
    @"(?i)takes no argument"
    @"(?i)only shows? the current workflow"
    @"(?i)shows the current workflow and does not switch"
    @"(?i)in neovim,? use the (web )?dashboard"
    @"(?i)neovim (does not|doesn't|cannot|can't) switch"
    @"(?i)neovim is left out on purpose"
    @"(?i)\bnone yet\b"
    @"(?i)neovim has (no|none)\b" ]
  |> List.map Regex

/// True when a statement is about switching workflows and says Neovim can't.
let saysNeovimCannotSwitchWorkflow (statement: string) : bool =
  statement.IndexOf("workflow", StringComparison.OrdinalIgnoreCase) >= 0 && matchesAny neovimCannotSwitchPatterns statement

/// The statements of one line that say Neovim lacks something, when the line is about the topic. The topic is read off
/// the whole line, so a sentence like "Neovim does not have it yet." counts when the cell or paragraph around it is
/// about debugging, though it never says the word.
let lackClaimsOnLine (topic: Regex) (lacks: Regex list) (line: string) : string list =
  match topic.IsMatch line with
  | false -> []
  | true -> claimsOnLine line |> List.filter (matchesAny lacks)

/// Cells that mean "no" in a capability table.
let private emptyCell (cell: string) : bool =
  match cell.Trim().Trim('*', '`').ToLowerInvariant() with
  | "" | "-" | "—" | "–" | "no" | "none" -> true
  | text -> text.StartsWith "none yet" || text.StartsWith "not yet"

/// Table rows whose first cell matches `topic` and whose Neovim column is a dash or a "none". The Neovim column is the
/// one headed `Neovim` in the table the row sits in, so a table with no such column is not read.
let neovimEmptyCells (topic: Regex) (lines: string[]) : (int * string) list =
  let cellsOf (line: string) =
    line.Trim().Trim('|').Split('|') |> Array.map (fun c -> c.Trim())
  let isRow (line: string) = line.TrimStart().StartsWith "|"
  let mutable column = -1
  [ for i in 0 .. lines.Length - 1 do
      let line = lines.[i]
      match isRow line with
      | false -> column <- -1
      | true ->
        let cells = cellsOf line
        match Array.tryFindIndex (fun (c: string) -> c.Trim('*') = "Neovim") cells with
        | Some at -> column <- at
        | None ->
          match column >= 0 && column < cells.Length && cells.Length > 0 && topic.IsMatch cells.[0] with
          | true when emptyCell cells.[column] -> yield i + 1, line
          | _ -> () ]

/// Phrasings that say work is merged but not shipped. Each is true on the day it is written and false the day the next
/// release is tagged, which is how a page told readers for sixteen days that work from 2026-10-01 was not installable.
let notInReleaseClaims : Regex list =
  [ @"(?i)\bnot in a release\b"
    @"(?i)\bin a release yet\b"
    @"(?i)\bmerged and unreleased\b"
    @"(?i)\bis unreleased\b"
    @"(?i)(can't|cannot|can not) install it from nuget" ]
  |> List.map Regex

let debugTopic = Regex(@"(?i)debug", RegexOptions.Compiled)
let workflowTopic = Regex(@"(?i)switch(ing)?\s+(a\s+)?workflow", RegexOptions.Compiled)

/// Every page that may state what Neovim can do. Generated pages and the dated diary stay out: the first are written
/// by scripts, and the diary says what was true on the day it was written.
let claimPages : string list list =
  comparisonPages
  @ [ [ "docs"; "workflow-modes.md" ]
      [ "docs"; "FEATURE_MATRIX.md" ]
      [ "docs"; "live-testing-as-you-type.md" ]
      [ "docs"; "LIVE_TESTING_GUIDE.md" ]
      [ "docs"; "architecture-decisions.md" ] ]

/// Every claim on the pages that Neovim lacks a thing, as `page:line: text`: a sentence or cell on a line about the
/// topic, and a table row about the topic whose Neovim column is a dash.
let neovimLackClaims (topic: Regex) (lacks: Regex list) : string list =
  claimPages
  |> List.collect (fun page ->
    let lines = File.ReadAllLines(pagePath page)
    let inText =
      lines
      |> Array.toList
      |> List.mapi (fun i line -> i + 1, line)
      |> List.collect (fun (n, line) ->
        lackClaimsOnLine topic lacks line
        |> List.map (fun s -> sprintf "%s:%d: %s" (pageName page) n (s.Substring(0, min 100 s.Length))))
    let inCells =
      neovimEmptyCells topic lines
      |> List.map (fun (n, line) -> sprintf "%s:%d: %s" (pageName page) n (line.Substring(0, min 100 line.Length)))
    inText @ inCells)

/// Where sagefs.nvim is checked out: `SAGEFS_NVIM_DIR`, else next to the main checkout (the same rule as SseParityTests).
let nvimCheckout () : string =
  match Environment.GetEnvironmentVariable "SAGEFS_NVIM_DIR" with
  | null | "" -> RepoPaths.siblingCheckoutDir repoRoot "sagefs.nvim"
  | dir -> Path.GetFullPath dir

let debugTestLua () : string =
  Path.Combine(nvimCheckout (), "lua", "sagefs", "debug_test.lua")

let workflowLua () : string =
  Path.Combine(nvimCheckout (), "lua", "sagefs", "workflow.lua")

/// A missing plugin file fails the case, saying how to fix it: a check that cannot read the plugin has not checked
/// anything, and a pass there would let a false "Neovim cannot" claim through. CI checks the plugin out and sets
/// `SAGEFS_NVIM_DIR` (main.yml), the same variable and rule SseParityTests uses.
let private requirePluginFile (path: string) : unit =
  match File.Exists path with
  | true -> ()
  | false ->
    failtestf
      "sagefs.nvim is not checked out (%s is missing). Clone WillEhrendreich/sagefs.nvim next to this repo, or set SAGEFS_NVIM_DIR to its folder."
      path

[<Tests>]
let tests =
  testList "Parity claims in the comparison pages" [
    testCase "every row under a still-ahead line carries a roadmap link or a dated plan" <| fun _ ->
      let missing =
        comparisonPages
        |> List.collect (fun page ->
          File.ReadAllLines(pagePath page)
          |> aheadRows
          |> List.filter (rowIsCovered >> not)
          |> List.map (fun row -> sprintf "%s: %s" (pageName page) (row.Text.Substring(0, min 90 row.Text.Length))))
      missing
      |> Expect.equal "every Microsoft-is-ahead row has a link to the roadmap or the marker `Plan, written YYYY-MM-DD`; these have neither" []

    testCase "the pages have still-ahead rows to check, so the scan cannot pass by finding none" <| fun _ ->
      let rows =
        comparisonPages |> List.collect (fun page -> File.ReadAllLines(pagePath page) |> aheadRows)
      (List.length rows >= 3) |> Expect.isTrue "at least three still-ahead rows were found across the pages"
      let pagesWithRows =
        comparisonPages |> List.filter (fun page -> File.ReadAllLines(pagePath page) |> aheadRows |> List.isEmpty |> not)
      (List.length pagesWithRows = List.length comparisonPages)
      |> Expect.isTrue "every page in the list has a still-ahead line, or it should leave the list"

    testCase "the row matcher accepts a link or a dated plan and refuses a row with neither" <| fun _ ->
      let row text = { Opener = "Where Microsoft is still ahead."; Text = text }
      rowIsCovered (row "- Years. See [the roadmap](roadmap.md).") |> Expect.isTrue "a roadmap link counts"
      rowIsCovered (row "- Years. See [the roadmap](../docs/roadmap.md#next-6).") |> Expect.isTrue "a relative roadmap link counts"
      rowIsCovered (row "- Years. Plan, written 2026-10-17: pause and a pattern scope are in, nothing else here.") |> Expect.isTrue "a dated plan that says what is planned counts"
      rowIsCovered (row "- Years. Plan, written 2026-10-17 and not scheduled: a real attach.") |> Expect.isTrue "a dated plan with no colon counts"
      rowIsCovered (row "- Years. Plan, written 2026-10-17: none, only time closes this one.") |> Expect.isFalse "a placeholder that says none is not a plan"
      rowIsCovered (row "- Years. Plan, written 2026-10-17 : None.") |> Expect.isFalse "spacing and case do not turn the placeholder into a plan"
      rowIsCovered (row "- Years. It has been shipping since 2017.") |> Expect.isFalse "a bare row does not count"
      rowIsCovered (row "- Years. Mentions roadmap.md in passing, with no link.") |> Expect.isFalse "the name without a link does not count"
      rowIsCovered { Opener = "Where Microsoft is still ahead. Plan, written 2026-10-01 for each."; Text = "- Years." }
      |> Expect.isTrue "the opener's dated plan covers the rows under it"

    testCase "the row finder takes the bullets after blank lines, and the line itself when there are none" <| fun _ ->
      let page = [| "Intro."; "Where Visual Studio is still ahead."; ""; "- one"; "- two"; ""; "after" |]
      aheadRows page |> List.map (fun r -> r.Text) |> Expect.equal "both bullets and nothing after them" [ "- one"; "- two" ]
      let paragraph = [| "Where Visual Studio is still ahead: it does a thing."; "Next." |]
      aheadRows paragraph |> List.map (fun r -> r.Text) |> Expect.equal "the paragraph is the row" [ "Where Visual Studio is still ahead: it does a thing." ]

    testCase "the Neovim matcher flags the old claims and leaves the true ones alone" <| fun _ ->
      let flagged =
        [ "| Debugging a failure | VS Code only, one test at a time. A Debug lens attaches a .NET debugger. Neovim has none |"
          "| Debugging a failure | The glyph debugs | **VS Code: a Debug lens attaches a debugger.** One test at a time, none in Neovim yet |"
          "Neovim has no debug wiring, and that client lives in another repo."
          "Debugging a failing test: Neovim doesn't have it yet."
          "Debugging is VS Code only and one test at a time." ]
      for s in flagged do
        claimsOnLine s |> List.exists saysNeovimCannotDebug |> Expect.isTrue (sprintf "flagged: %s" s)
      let fine =
        [ "Neovim can debug a test from the editor through nvim-dap and netcoredbg."
          "In Neovim, `:SageFsDebugTest` debugs the failing test on the line."
          "VS Code and Neovim debug one test at a time."
          "Neovim has a gutter, and VS Code only shows it in the Test Explorer."
          "Neovim has none of the dashboard panels." ]
      for s in fine do
        claimsOnLine s |> List.exists saysNeovimCannotDebug |> Expect.isFalse (sprintf "left alone: %s" s)

    testCase "no page says Neovim cannot debug a test while sagefs.nvim has debug_test.lua" <| fun _ ->
      let lua = debugTestLua ()
      requirePluginFile lua
      neovimLackClaims debugTopic neovimLacksPatterns
      |> Expect.equal (sprintf "no statement or table cell says Neovim has no test debugging, because %s exists; these do" lua) []

    testCase "the matcher reads a line's topic, so a sentence without the word debug still counts, and a dash cell does too" <| fun _ ->
      let debugLine = "| **Debugging a failing test** | Hover the glyph, Debug | VS Code only. Neovim does not have it yet. See the guide |"
      lackClaimsOnLine debugTopic neovimLacksPatterns debugLine
      |> List.isEmpty
      |> Expect.isFalse "\"Neovim does not have it yet.\" in a debugging row is a lack claim"
      lackClaimsOnLine debugTopic neovimLacksPatterns "Neovim does not have it yet."
      |> Expect.isEmpty "the same sentence on a line that is not about debugging is not about debugging"
      let table =
        [| "| Capability | VS Code | Neovim | Web Dashboard |"
           "|:---|:---:|:---:|:---:|"
           "| Eval code | ✅ | ✅ | ✅ |"
           "| **Debug a failing test** | ✅ | — | — |"
           "| Switch workflow | Command palette | None yet. Use the dashboard | Dropdown |"
           "| Hover | ✅ | ✅ | — |"
           ""
           "| Other | Thing |"
           "| Debug | - |" |]
      neovimEmptyCells debugTopic table |> List.map fst |> Expect.equal "the dash in the Neovim column of the debug row, and only that one" [ 4 ]
      neovimEmptyCells workflowTopic table |> List.map fst |> Expect.equal "\"None yet\" in the Neovim column of the workflow row" [ 5 ]
      let ok = [| "| Capability | VS Code | Neovim |"; "|:---|:---|:---|"; "| Debug a test | ✅ | ✅ `:SageFsDebugTest` |" |]
      neovimEmptyCells debugTopic ok |> Expect.isEmpty "a cell that names the command is not empty"

    testCase "the workflow matcher flags the old claims and leaves the true ones alone" <| fun _ ->
      let flagged =
        [ "- **Neovim**: not from the editor yet. `:SageFsWorkflow` takes no argument and shows the current workflow only. Use the dashboard or MCP."
          "In Neovim use the dashboard or MCP, since `:SageFsWorkflow` only shows the current workflow."
          "| Switch workflow | Command palette | None yet. `:SageFsWorkflow` shows the current workflow and does not switch it |"
          "Neovim is left out on purpose, because `:SageFsWorkflow` only shows the current workflow." ]
      for s in flagged do
        claimsOnLine s |> List.exists saysNeovimCannotSwitchWorkflow |> Expect.isTrue (sprintf "flagged: %s" s)
      let fine =
        [ "- **Neovim**: `:SageFsWorkflow [name]` with `interactive`, `livetesting` or `hotreload`."
          "In Neovim run `:SageFsWorkflow hotreload`."
          "Neovim is not in that set yet, although `:SageFsWorkflow [name]` switches a session there now."
          "| Switch workflow | Command palette | `:SageFsWorkflow [name]` |" ]
      for s in fine do
        claimsOnLine s |> List.exists saysNeovimCannotSwitchWorkflow |> Expect.isFalse (sprintf "left alone: %s" s)

    testCase "no page says Neovim cannot switch a workflow while sagefs.nvim has workflow.lua and :SageFsWorkflow" <| fun _ ->
      let lua = workflowLua ()
      let commands = Path.Combine(nvimCheckout (), "lua", "sagefs", "commands.lua")
      requirePluginFile lua
      requirePluginFile commands
      File.ReadAllText commands
      |> Expect.stringContains "the plugin registers :SageFsWorkflow" "\"SageFsWorkflow\""
      neovimLackClaims workflowTopic neovimCannotSwitchPatterns
      |> Expect.equal (sprintf "no statement or table cell says Neovim cannot switch workflows, because %s exists; these do" lua) []

    testCase "the claim pages exist, so the scan cannot pass by reading nothing" <| fun _ ->
      claimPages
      |> List.filter (fun page -> not (File.Exists(pagePath page)))
      |> List.map pageName
      |> Expect.equal "every page in the claim list is a real file" []

    testCase "no reader page says its work is not in a release, because the sentence goes stale the day a release ships" <| fun _ ->
      let docsDir = Path.Combine(repoRoot, "docs")
      let pages =
        [ yield [ "Readme.md" ]
          for file in Directory.EnumerateFiles(docsDir, "*.md") do
            let name = Path.GetFileName file
            // roadmap.md and configuration.md are written by scripts, and progress.md is the diary: its Status lines
            // are held by the next test instead.
            match name with
            | "roadmap.md" | "configuration.md" | "progress.md" | "FEATURES_SURVEY.md" -> ()
            | _ -> yield [ "docs"; name ] ]
      let claims =
        pages
        |> List.collect (fun page ->
          File.ReadAllLines(pagePath page)
          |> Array.toList
          |> List.mapi (fun i line -> i + 1, line)
          |> List.collect (fun (n, line) ->
            notInReleaseClaims
            |> List.filter (fun p -> p.IsMatch line)
            |> List.map (fun _ -> sprintf "%s:%d: %s" (pageName page) n (line.Substring(0, min 100 line.Length)))))
      claims
      |> Expect.equal "name the release that has the work (`git tag --contains <commit>`), or say nothing about releases; these say it is not in one" []

    testCase "every Status line in the progress diary says which release it is in, or that it is not in one on the day written" <| fun _ ->
      let statusLines =
        File.ReadAllLines(pagePath [ "docs"; "progress.md" ])
        |> Array.toList
        |> List.mapi (fun i line -> i + 1, line)
        |> List.filter (fun (_, line) -> line.StartsWith "Status:")
      (List.length statusLines >= 10) |> Expect.isTrue "the diary has Status lines to check"
      statusLines
      |> List.filter (fun (_, line) -> not (Regex.IsMatch(line, @"v0\.6\.\d+")))
      |> List.map (fun (n, line) -> sprintf "docs/progress.md:%d: %s" n line)
      |> Expect.equal "each Status line names a release tag" []

    testCase "the release-claim matcher flags the stale sentences and leaves the dated ones alone" <| fun _ ->
      let flagged =
        [ "Status: merged on 2026-10-01 after v0.6.875, not in a release."
          "That fix merged on 2026-10-01, so it is on master and not in a release yet."
          "none of it is in a release yet."
          "You can't install it from NuGet yet." ]
      for s in flagged do
        notInReleaseClaims |> List.exists (fun p -> p.IsMatch s) |> Expect.isTrue (sprintf "flagged: %s" s)
      let fine =
        [ "Status: merged on 2026-10-01 after v0.6.875, released in v0.6.882 (tagged 2026-10-02)."
          "It is in v0.6.882 and every release since."
          "The release notes list the commits behind each version." ]
      for s in fine do
        notInReleaseClaims |> List.exists (fun p -> p.IsMatch s) |> Expect.isFalse (sprintf "left alone: %s" s)
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant tests |> ignore
