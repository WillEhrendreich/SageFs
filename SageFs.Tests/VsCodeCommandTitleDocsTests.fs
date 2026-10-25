/// A doc that tells a person to run **SageFs: Something** from the Command Palette is only true while
/// the VS Code extension contributes a command with that title. Nothing used to check it: the Readme told
/// people to run "SageFs: Mark All Tests Stale", which the extension never had, and the getting-started
/// walkthrough promised tests that nothing ran. The extension's `package.json` is the one list of what
/// the palette shows, so this reads it and compares every command title the shipped markdown names in
/// bold with the `SageFs:` prefix.
///
/// The check is one-sided on purpose. A command the docs never mention is fine. A title the docs name
/// that the extension does not have is a stale doc, and a person following it finds nothing.
module SageFs.Tests.VsCodeCommandTitleDocsTests

open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

/// Why the titles or the docs could not be read, so a red run says which step broke and not only that
/// something did.
type ReadRefusal =
  | PackageJsonMissing of path: string
  | PackageJsonNotJson of path: string * detail: string
  | NoCommandsContributed of path: string
  | GitCouldNotListFiles of detail: string

module ReadRefusal =
  let describe (refusal: ReadRefusal) : string =
    match refusal with
    | PackageJsonMissing path -> sprintf "the extension manifest is not at %s" path
    | PackageJsonNotJson (path, detail) -> sprintf "%s is not JSON: %s" path detail
    | NoCommandsContributed path -> sprintf "%s contributes no commands, so there is nothing to compare the docs with" path
    | GitCouldNotListFiles detail -> sprintf "git could not list the tracked markdown files: %s" detail

/// What the Command Palette shows for one contributed command: its title, with the category in front
/// when it has one (VS Code writes "Category: Title"). This extension puts the prefix in the title itself,
/// so a command without a category shows just its title.
let shownAs(command: JsonElement) : string option =
  match command.TryGetProperty "title" with
  | true, title ->
    match command.TryGetProperty "category" with
    | true, category -> Some (sprintf "%s: %s" (category.GetString()) (title.GetString()))
    | false, _ -> Some (title.GetString())
  | false, _ -> None

/// Every title the palette can show, from the text of the extension's `package.json`. The titles of
/// commands the palette hides are in the set too; a doc naming one is still naming a real command.
let commandTitles (path: string) (packageJson: string) : Result<Set<string>, ReadRefusal> =
  let parsed =
    try Result.Ok (JsonDocument.Parse packageJson)
    with :? JsonException as e -> Result.Error (PackageJsonNotJson (path, e.Message))
  match parsed with
  | Result.Error refusal -> Result.Error refusal
  | Result.Ok doc ->
    use _doc = doc
    let titles =
      match doc.RootElement.TryGetProperty "contributes" with
      | true, contributes ->
        match contributes.TryGetProperty "commands" with
        | true, commands -> commands.EnumerateArray() |> Seq.choose shownAs |> Set.ofSeq
        | false, _ -> Set.empty
      | false, _ -> Set.empty
    match Set.isEmpty titles with
    | true -> Result.Error (NoCommandsContributed path)
    | false -> Result.Ok titles

let boldTitle= Regex(@"\*\*(SageFs: [^*\r\n]+?)\*\*", RegexOptions.Compiled)

/// The `**SageFs: ...**` titles a markdown text names, with their 1-based lines. Fenced code is skipped:
/// a sample there may show a title a reader is not being told to run.
let namedTitles (markdown: string) : (int * string) list =
  let lines = markdown.Split('\n')
  let isFenceMarker (line: string) = line.TrimStart().StartsWith "```"
  // insideBefore.[i] is whether line i starts inside a fence.
  let insideBefore =
    lines |> Array.scan (fun inside line -> match isFenceMarker line with | true -> not inside | false -> inside) false
  lines
  |> Array.mapi (fun i line -> i, line)
  |> Array.collect (fun (i, line) ->
    match isFenceMarker line || insideBefore.[i] with
    | true -> [||]
    | false -> boldTitle.Matches line |> Seq.map (fun m -> i + 1, m.Groups.[1].Value) |> Array.ofSeq)
  |> List.ofArray

/// Whether the palette shows this title. A doc may leave off a trailing parenthetical the palette adds
/// ("SageFs: Cycle Density" for "SageFs: Cycle Density (Full → Normal → Minimal)"): typing the short form
/// finds the command. Nothing else is forgiven, so an invented or renamed command is still found.
let isShown (known: Set<string>) (title: string) : bool =
  known.Contains title || known |> Set.exists (fun shown -> shown.StartsWith(title + " (", System.StringComparison.Ordinal))

/// The titles a text names that the extension does not contribute.
let unknownTitles (known: Set<string>) (markdown: string) : (int * string) list =
  namedTitles markdown |> List.filter (fun (_, title) -> not (isShown known title))

let repoRoot= RepoPaths.repoPathFull [||]

let manifestPath= Path.Combine(repoRoot, "sagefs-vscode", "package.json")

let realTitles() : Result<Set<string>, ReadRefusal> =
  match File.Exists manifestPath with
  | false -> Result.Error (PackageJsonMissing manifestPath)
  | true -> commandTitles manifestPath (File.ReadAllText manifestPath)

/// The tracked markdown files, repo-relative: what the repo ships, never build output and never another
/// worktree's checkout.
let trackedMarkdown() : Result<string list, ReadRefusal> =
  let psi = ProcessStartInfo("git", "ls-files \"*.md\"")
  psi.WorkingDirectory <- repoRoot
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  psi.UseShellExecute <- false
  match Process.Start psi with
  | null -> Result.Error (GitCouldNotListFiles "the process did not start")
  | p ->
    use _p = p
    let out = p.StandardOutput.ReadToEnd()
    let err = p.StandardError.ReadToEnd()
    p.WaitForExit()
    match p.ExitCode with
    | 0 ->
      out.Split('\n')
      |> Array.map (fun l -> l.Trim().Replace('\\', '/'))
      |> Array.filter (fun rel ->
        rel <> ""
        && not (rel.Contains "/obj/")
        && not (rel.Contains "/bin/")
        && not (rel.Contains "/node_modules/")
        && not (rel.Contains ".claude/worktrees/"))
      |> List.ofArray
      |> Result.Ok
    | code -> Result.Error (GitCouldNotListFiles (sprintf "exit %d: %s" code err))

/// Every bad mention in the shipped docs, as "file:line  title".
let staleMentions(known: Set<string>) (files: string list) : string list =
  files
  |> List.collect (fun rel ->
    unknownTitles known (File.ReadAllText(Path.Combine(repoRoot, rel)))
    |> List.map (fun (line, title) -> sprintf "%s:%d  %s" rel line title))

/// Run a check that needs the extension's titles, turning a refusal into a failed test that says why.
let withTitles(check: Set<string> -> unit) : unit =
  match realTitles () with
  | Result.Ok titles -> check titles
  | Result.Error refusal -> failtest (ReadRefusal.describe refusal)

[<Tests>]
let tests =
  testList "VS Code command titles named in the docs" [

    testCase "WHY — the manifest yields titles, so a comparison below cannot pass by reading an empty list" <| fun _ ->
      withTitles (fun titles ->
        titles |> Expect.contains "a command every VS Code user has seen" "SageFs: Enable Live Testing"
        titles |> Expect.contains "the rebuild command a stale-source message points at" "SageFs: Hard Reset (Rebuild)")

    testCase "WHY — a category in front of a title is part of what the palette shows" <| fun _ ->
      let manifest = """{"contributes":{"commands":[{"command":"a","title":"Run","category":"SageFs"},{"command":"b","title":"SageFs: Stop"}]}}"""
      match commandTitles "demo/package.json" manifest with
      | Result.Ok titles -> titles |> Expect.equal "both spellings, as the palette writes them" (Set.ofList [ "SageFs: Run"; "SageFs: Stop" ])
      | Result.Error refusal -> failtest (ReadRefusal.describe refusal)

    testCase "WHY — a manifest with no commands is a refusal, not an empty set every doc would then fail against" <| fun _ ->
      commandTitles "demo/package.json" """{"contributes":{}}"""
      |> Expect.equal "named" (Result.Error (NoCommandsContributed "demo/package.json"))

    testCase "WHY — a title that is not a command is found, with its line, so the check has teeth" <| fun _ ->
      let known = Set.ofList [ "SageFs: Run All Tests" ]
      let text = "Run **SageFs: Run All Tests** first.\nThen use the **SageFs: Mark All Tests Stale** command.\n"
      unknownTitles known text
      |> Expect.equal "only the invented title, on its own line" [ 2, "SageFs: Mark All Tests Stale" ]

    testCase "WHY — a doc may drop the palette's trailing parenthetical, and a different command is still found" <| fun _ ->
      let known = Set.ofList [ "SageFs: Cycle Density (Full → Normal → Minimal)"; "SageFs: Hard Reset (Rebuild)" ]
      unknownTitles known "**SageFs: Cycle Density** and **SageFs: Hard Reset (Rebuild)** and **SageFs: Cycle**"
      |> Expect.equal "only the title that is neither whole nor a parenthetical-less form of one" [ 1, "SageFs: Cycle" ]

    testCase "WHY — a title inside a code fence is a sample and is not held to the manifest" <| fun _ ->
      let known = Set.ofList [ "SageFs: Run All Tests" ]
      let text = "```\n**SageFs: Not A Command**\n```\nplain **SageFs: Run All Tests**\n"
      unknownTitles known text |> Expect.isEmpty "nothing outside the fence is wrong"

    testCase "WHY — bold text without the SageFs: prefix is not a command title" <| fun _ ->
      unknownTitles (Set.ofList [ "SageFs: Run All Tests" ]) "Click **Enable** or open the **Testing** view."
      |> Expect.isEmpty "only the prefixed form is a claim about the palette"

    testCase "WHY — every command title the shipped docs name is one the extension contributes, so a renamed or invented command cannot stay documented" <| fun _ ->
      withTitles (fun titles ->
        match trackedMarkdown () with
        | Result.Error refusal -> failtest (ReadRefusal.describe refusal)
        | Result.Ok files ->
          let mentioned =
            files |> List.sumBy (fun rel -> namedTitles (File.ReadAllText(Path.Combine(repoRoot, rel))) |> List.length)
          (mentioned, 0) |> Expect.isGreaterThan "the docs name some titles, so this is not comparing nothing"
          staleMentions titles files
          |> Expect.isEmpty "each **SageFs: ...** in the markdown is a title in sagefs-vscode/package.json (contributes.commands)")

    testCase "WHY — the VS Code commands the product's own messages tell a person to run exist, so a message and the manifest cannot drift" <| fun _ ->
      withTitles (fun titles ->
        let stale =
          SageFs.SourceState.describe
            (SageFs.SourceState.Stale
               [ { Path = "A.fs"
                   Because =
                     SageFs.StaleBecause.EditedAfterBuild
                       (System.DateTime(2026, 10, 1, 12, 0, 5, System.DateTimeKind.Utc), System.DateTime(2026, 10, 1, 12, 0, 0, System.DateTimeKind.Utc)) } ])
        let off = SageFs.Features.LiveTesting.LiveTestDiscoveryState.hint SageFs.Features.LiveTesting.LiveTestDiscoveryState.Disabled
        [ "SageFs: Hard Reset (Rebuild)", stale
          "SageFs: Enable Live Testing", off ]
        |> List.iter (fun (title, message) ->
          titles |> Expect.contains "the extension contributes it" title
          message |> Expect.stringContains "and the message names it" title))
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant tests |> ignore
