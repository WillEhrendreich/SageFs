module SageFs.Tests.SkillLayoutTests

/// WHY — skills/sagefs/SKILL.md loads in full on every F# task, so every line
/// of it is paid for in context tokens before any work starts. The Lemmings
/// audit found small models spending their budget reading the skill. The core
/// is kept short, and everything else lives in sibling files the core names
/// with a "read this when" trigger. These tests keep that shape from eroding:
/// the core cannot quietly grow back, and a reference file nothing points at
/// cannot exist (nobody would ever read it).
open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip

// The skill directory is located from the ASSEMBLY's own location at run time, not by
// walking up from the working directory. Walking up from the CWD means this binding
// THROWS during static initialisation whenever the suite is launched from outside the
// checkout — and a type-initializer failure takes the whole discovery pass down with it,
// which reads as a corrupted test run rather than a path bug.
let private skillDir =
  Path.Combine(RepoPaths.requireRepoRoot (), "skills", "sagefs")

let private core = File.ReadAllText(Path.Combine(skillDir, "SKILL.md"))

let private referenceFiles =
  Directory.GetFiles(skillDir, "*.md")
  |> Array.map Path.GetFileName
  |> Array.filter (fun name -> name <> "SKILL.md")
  |> Array.sort

let private wordCount (text: string) =
  text.Split([| ' '; '\n'; '\r'; '\t' |], StringSplitOptions.RemoveEmptyEntries).Length

[<Tests>]
let skillLayoutTests = testList "skill layout" [

  testCase "WHY — the always-loaded core stays under its line budget, so it cannot grow back into the 345-line file" <| fun _ ->
    let lines = core.Split('\n').Length
    (lines, 120)
    |> Expect.isLessThanOrEqual "SKILL.md line count"

  testCase "WHY — the always-loaded core stays under its word budget, because words are the context tokens a small model spends" <| fun _ ->
    (wordCount core, 950)
    |> Expect.isLessThanOrEqual "SKILL.md word count"

  testCase "WHY — the reference files exist, so the split is real and the core is not just truncated" <| fun _ ->
    (referenceFiles.Length, 0)
    |> Expect.isGreaterThan "sibling reference files next to SKILL.md"

  testCase "WHY — every reference file is named in the core, so a rule moved out of it is still reachable by a trigger" <| fun _ ->
    let unreferenced =
      referenceFiles
      |> Array.filter (fun name -> not (core.Contains name))
      |> List.ofArray
    unreferenced
    |> Expect.isEmpty "reference files the core never mentions"

  testCase "WHY — the front matter keeps a valid name and a description that triggers on F# work" <| fun _ ->
    (core.StartsWith "---\nname: sagefs\n" || core.StartsWith "---\r\nname: sagefs\r\n")
    |> Expect.isTrue "front matter must open with name: sagefs"
    core.Contains "description:"
    |> Expect.isTrue "front matter must carry a description"
    core.Contains "Use at the start of every F# task"
    |> Expect.isTrue "the description must still trigger on F# work"
]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.SizeProxy skillLayoutTests |> ignore

// ---- the install steps copy every file the skill tells an agent to read ---------------------------------------------

/// The fenced block under "**Claude Code:**" in docs/agents.md, the install steps an outside user follows.
let installBlock (agentsDoc: string) : string =
  let afterHeading = agentsDoc.IndexOf "**Claude Code:**"
  match afterHeading with
  | -1 -> ""
  | start ->
    let fence = agentsDoc.IndexOf("```", start)
    match fence with
    | -1 -> ""
    | open' ->
      let close = agentsDoc.IndexOf("```", open' + 3)
      match close with
      | -1 -> ""
      | close' -> agentsDoc.Substring(open', close' - open')

/// The skill files an install block puts on the user's disk. A `for f in a b c; do ... $f.md` loop copies exactly
/// those names. A copy of the folder (a `cp -r` of `skills/sagefs`) copies every file in it. Anything else copies none.
let copiedBy (skillFiles: string list) (block: string) : string list =
  let loop = Regex.Match(block, @"for\s+f\s+in\s+([^;\r\n]+);\s*do")
  match loop.Success with
  | true ->
    loop.Groups.[1].Value.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun name -> name + ".md")
    |> Array.toList
  | false ->
    match Regex.IsMatch(block, @"cp\s+-[a-zA-Z]*r[a-zA-Z]*\s+\S*skills/sagefs\S*\s") with
    | true -> skillFiles
    | false -> []

/// Every `name.md` the skill's core points at that is a file in the skill folder.
let filesNamedBy (skillFiles: string list) (coreText: string) : string list =
  Regex.Matches(coreText, @"[A-Za-z][A-Za-z0-9_-]*\.md")
  |> Seq.map (fun m -> m.Value)
  |> Seq.distinct
  |> Seq.filter (fun name -> List.contains name skillFiles)
  |> Seq.toList

[<Tests>]
let skillInstallTests =
  let skillFiles = Array.append [| "SKILL.md" |] referenceFiles |> Array.toList
  let agentsDoc = File.ReadAllText(Path.Combine(RepoPaths.requireRepoRoot (), "docs", "agents.md"))

  testList "skill install steps" [

    testCase "WHY - the install steps copy every skill file SKILL.md tells an agent to read" <| fun _ ->
      let copied = installBlock agentsDoc |> copiedBy skillFiles
      filesNamedBy skillFiles core
      |> List.filter (fun name -> not (List.contains name copied))
      |> Expect.isEmpty "files SKILL.md names that docs/agents.md does not copy"

    testCase "WHY - the install steps copy the whole skill folder, so a file added later is not left behind" <| fun _ ->
      let copied = installBlock agentsDoc |> copiedBy skillFiles
      skillFiles
      |> List.filter (fun name -> not (List.contains name copied))
      |> Expect.isEmpty "files in skills/sagefs that docs/agents.md does not copy"

    testCase "WHY - the install block is found and the folder has files, so the checks cannot pass by reading nothing" <| fun _ ->
      installBlock agentsDoc |> Expect.isNotEmpty "the Claude Code install block in docs/agents.md"
      (List.length skillFiles >= 12) |> Expect.isTrue "the skill folder has its twelve files"

    testCase "WHY - the matcher reads a loop as exactly its names, and a folder copy as everything" <| fun _ ->
      let files = [ "SKILL.md"; "design.md"; "loop.md" ]
      copiedBy files "for f in SKILL loop; do\n  curl x -o $f.md\ndone" |> Expect.equal "a loop copies its names" [ "SKILL.md"; "loop.md" ]
      copiedBy files "git clone x t\ncp -r t/skills/sagefs ~/.claude/skills/" |> Expect.equal "a cp -r of the folder copies all" files
      copiedBy files "echo nothing" |> Expect.isEmpty "anything else copies none"
      filesNamedBy files "Read `design.md`, then loop.md, never other.md" |> Expect.equal "only files in the folder count" [ "design.md"; "loop.md" ]
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant skillInstallTests |> ignore
