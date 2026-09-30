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
open Expecto
open Expecto.Flip

let private skillDir =
  let rec up (dir: DirectoryInfo) =
    if File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")) then
      Path.Combine(dir.FullName, "skills", "sagefs")
    elif isNull dir.Parent then
      failtest "could not locate the repository root from the test working directory"
    else
      up dir.Parent
  up (DirectoryInfo(Directory.GetCurrentDirectory()))

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
