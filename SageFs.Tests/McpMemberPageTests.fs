module SageFs.Tests.McpMemberPageTests

/// Slice 2 of the progressive map (docs/progressive-map-research-2026-10-24.md section 9): the
/// `list_members` reply groups its members by the kinds a client can drill into and states a
/// count per kind before the names, and the skill teaches the walk. Default tier, no session.
open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open FsCheck
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Features.AutoCompletion
open SageFs.McpListMembers
open SageFs.McpMemberPage

let private repoRoot = RepoPaths.requireRepoRoot ()

/// Capitalised names read as drillable, lowercase ones as leaves (`completionKindForLabel`).
let private mixedFixture (drillable: int) (leaf: int) : string list =
  [ for i in 1 .. drillable -> sprintf "Type%03d" i ]
  @ [ for i in 1 .. leaf -> sprintf "member_%03d" i ]

let private lines (text: string) : string list =
  text.Split('\n') |> List.ofArray

let private allKinds : CompletionKind list =
  FSharpType.GetUnionCases typeof<CompletionKind>
  |> Array.map (fun case -> FSharpValue.MakeUnion(case, [||]) :?> CompletionKind)
  |> List.ofArray

/// The kinds the VS Code Type Explorer treats as expandable, read from its source.
let private editorDrillableLabels : Set<string> =
  let source = File.ReadAllText(Path.Combine(repoRoot, "sagefs-vscode", "src", "TypeExplorerProvider.fs"))
  let setLiteral = Regex.Match(source, @"let private drillableKinds =\s*Set \[([^\]]*)\]")
  Regex.Matches(setLiteral.Groups.[1].Value, "\"([A-Za-z]+)\"")
  |> Seq.map (fun m -> m.Groups.[1].Value)
  |> Set.ofSeq

let private skillText : (string * string) list =
  Directory.GetFiles(Path.Combine(repoRoot, "skills", "sagefs"), "*.md")
  |> Array.sort
  |> Array.map (fun path -> Path.GetFileName path, File.ReadAllText path)
  |> List.ofArray

/// Every page of a member list, walked by following the continuation each page names.
let private walk (cap: int) (members: string list) : MemberPage list =
  let rec go offset acc =
    let page = pageMembers "X" cap offset members
    match page.Continuation with
    | NextPage(next, _) -> go next (page :: acc)
    | LastPage -> List.rev (page :: acc)
  go 0 []

[<Tests>]
let tests =
  testList "list_members kinds" [

    testCase "WHY — the reply states a count per kind before the first name, so the caller sizes the level before reading it" <| fun _ ->
      let reply = formatListMembers "System.IO" 100 0 (mixedFixture 12 40)
      let all = lines reply
      let kindsAt = all |> List.findIndex (fun l -> l.StartsWith "By kind:")
      let firstNameAt = all |> List.findIndex (fun l -> l.StartsWith "  ")
      (kindsAt, firstNameAt) |> Expect.isLessThan "the count line comes before any name"
      all.[kindsAt] |> Expect.equal "both groups are counted" "By kind: 12 drillable, 40 leaf."

    testCase "WHY — drillable names are listed before leaves, because the next rung of the walk is one of them" <| fun _ ->
      let page = pageMembers "System.IO" 100 0 (mixedFixture 3 2 |> List.rev)
      page.Groups
      |> List.map fst
      |> Expect.equal "drillable group first" [ Rung.Drillable; Rung.Leaf ]
      page.Groups
      |> List.collect snd
      |> List.take 3
      |> List.forall (fun n -> n.StartsWith "Type")
      |> Expect.isTrue "the first three names are the three drillable ones"

    testCase "WHY — a set with no leaves counts only the group it has, because an empty group is noise" <| fun _ ->
      (pageMembers "X" 100 0 (mixedFixture 4 0)).Counts
      |> Expect.equal "one group" [ { Rung = Rung.Drillable; Count = 4 } ]
      (pageMembers "X" 100 0 []).Counts
      |> Expect.isEmpty "an empty name has no groups"
      formatListMembers "X" 100 0 []
      |> Expect.stringContains "and the reply says so" "By kind: none."

    testCase "WHY — a 500-member name is still capped at the page size and still states the true total and the true counts" <| fun _ ->
      let max = SageFsConfig.ListMembersMax
      let reply = formatListMembers "X" max 0 (mixedFixture 150 350)
      reply |> Expect.stringContains "the total is the real size" "500 total"
      reply |> Expect.stringContains "the counts cover the whole set, not the page" "By kind: 150 drillable, 350 leaf."
      lines reply
      |> List.filter (fun l -> l.StartsWith "  ")
      |> List.length
      |> Expect.equal "names shown are the cap" max
      (reply.Length < 4000) |> Expect.isTrue "one page stays under the house reply ceiling"

    testCase "WHY — the kinds list_members calls drillable are the kinds the editor's Type Explorer expands, so a name that opens in the tree opens here" <| fun _ ->
      allKinds
      |> List.filter (fun k -> rungOf k = Rung.Drillable)
      |> List.map CompletionKind.label
      |> Set.ofList
      |> Expect.equal "TypeExplorerProvider.drillableKinds equals the Drillable kinds" editorDrillableLabels
      (editorDrillableLabels.IsEmpty)
      |> Expect.isFalse "the set literal was found in the editor source"

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
      "WHY — walking the continuations visits every member exactly once, whatever the names and the cap" <|
      fun (names: string list) (cap: PositiveInt) ->
        let members = names |> List.map (fun n -> if isNull n then "" else n)
        let pages = walk cap.Get members
        let seen = pages |> List.collect (fun p -> p.Groups |> List.collect snd) |> List.sort
        seen = List.sort members

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
      "WHY — every page states the same total and the same counts, and the counts add up to the total" <|
      fun (names: string list) (cap: PositiveInt) ->
        let members = names |> List.map (fun n -> if isNull n then "" else n)
        let pages = walk cap.Get members
        let countsAgree = pages |> List.map (fun p -> p.Counts) |> List.distinct |> List.length = 1
        let totalsAgree = pages |> List.forall (fun p -> p.Total = members.Length)
        let sumsToTotal = pages |> List.forall (fun p -> (p.Counts |> List.sumBy (fun c -> c.Count)) = p.Total)
        countsAgree && totalsAgree && sumsToTotal

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
      "WHY — a page never shows more names than the cap, even for an offset outside the set" <|
      fun (names: string list) (cap: PositiveInt) (offset: int) ->
        let members = names |> List.map (fun n -> if isNull n then "" else n)
        let page = pageMembers "X" cap.Get offset members
        (page.Groups |> List.sumBy (fun (_, ns) -> ns.Length)) <= cap.Get

    testCase "WHY — the skill teaches the ladder: some skill file names list_members, so an agent that reads it knows the first rung exists" <| fun _ ->
      skillText
      |> List.filter (fun (_, text) -> text.Contains "list_members")
      |> List.map fst
      |> Expect.isNonEmpty "no file under skills/sagefs mentions list_members"

    testCase "WHY — the skill's ladder says how to continue, because a page that ends mid-set is the common case" <| fun _ ->
      skillText
      |> List.filter (fun (_, text) -> text.Contains "list_members" && text.Contains "offset")
      |> List.map fst
      |> Expect.isNonEmpty "the file that teaches list_members must also name offset"
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant tests |> ignore
