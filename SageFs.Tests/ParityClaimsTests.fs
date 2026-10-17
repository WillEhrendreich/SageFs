/// The comparison pages say where Microsoft's tools are ahead of SageFs, and two things about those pages must not
/// drift.
///
/// First, every row under a "Where Visual Studio is still ahead" or "Where Microsoft is still ahead" line says what
/// SageFs plans about it, and when that was written. A row passes when it carries ONE of these two markers:
///
///   * a link to the roadmap (the text `roadmap.md` in the row), which holds the plan and keeps it true, or
///   * the dated plan marker `Plan, written YYYY-MM-DD` (a plan that is "none" still says so, with the date).
///
/// The opening line may carry the dated marker for every row below it, which is how a list whose plans were all written
/// on one day says so once. A "still ahead" line with no bullets under it is one row, and the line is the row.
///
/// Second, no page says Neovim cannot debug a test while sagefs.nvim has the file that does it
/// (`lua/sagefs/debug_test.lua`). That check reads the plugin's checkout, which is a separate repository, so it looks
/// where SseParityTests looks (`SAGEFS_NVIM_DIR`, else next to the main checkout). When the checkout is not there the
/// test is skipped and the skip says why, and the pure matcher is still proved against sample sentences in the same
/// list, so a missing checkout can never turn the matcher off without a word.
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

/// `Plan, written 2026-10-01`, the one dated marker.
let datedPlan = Regex(@"Plan, written \d{4}-\d{2}-\d{2}", RegexOptions.Compiled)

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

/// True when a statement is about debugging and says Neovim can't.
let saysNeovimCannotDebug (statement: string) : bool =
  statement.IndexOf("debug", StringComparison.OrdinalIgnoreCase) >= 0
  && neovimLacksPatterns |> List.exists (fun p -> p.IsMatch statement)

/// Where sagefs.nvim is checked out: `SAGEFS_NVIM_DIR`, else next to the main checkout (the same rule as SseParityTests).
let nvimCheckout () : string =
  match Environment.GetEnvironmentVariable "SAGEFS_NVIM_DIR" with
  | null | "" -> RepoPaths.siblingCheckoutDir repoRoot "sagefs.nvim"
  | dir -> Path.GetFullPath dir

let debugTestLua () : string =
  Path.Combine(nvimCheckout (), "lua", "sagefs", "debug_test.lua")

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
      rowIsCovered (row "- Years. Plan, written 2026-10-17: none.") |> Expect.isTrue "a dated plan counts"
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
      match File.Exists lua with
      | false ->
        skiptest (sprintf "sagefs.nvim is not checked out (%s is missing). Clone WillEhrendreich/sagefs.nvim next to this repo, or set SAGEFS_NVIM_DIR to its folder, and this check runs." lua)
      | true ->
        let claims =
          comparisonPages
          |> List.collect (fun page ->
            File.ReadAllLines(pagePath page)
            |> Array.toList
            |> List.mapi (fun i line -> i + 1, line)
            |> List.collect (fun (n, line) ->
              claimsOnLine line
              |> List.filter saysNeovimCannotDebug
              |> List.map (fun s -> sprintf "%s:%d: %s" (pageName page) n (s.Substring(0, min 100 s.Length)))))
        claims
        |> Expect.equal (sprintf "no statement says Neovim has no test debugging, because %s exists; these do" lua) []
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant tests |> ignore
