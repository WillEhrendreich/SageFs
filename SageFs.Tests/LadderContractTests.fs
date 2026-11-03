/// `SageFs.Core/LadderContract.fs` is compiled by the daemon and, by link, by the VS Code extension (Fable), so
/// the kind words and the drillable split exist once. These cases pin what the move must not change (the words
/// on the wire, the set the Type Explorer has always expanded) and that the daemon's kind union cannot grow a
/// case the contract does not name.
module SageFs.Tests.LadderContractTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open FsCheck
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Features.AutoCompletion

let private repoRoot = RepoPaths.requireRepoRoot ()

let private caseNames (unionType: Type) : Set<string> =
  FSharpType.GetUnionCases unionType |> Array.map (fun case -> case.Name) |> Set.ofArray

let private allCompletionKinds : CompletionKind list =
  FSharpType.GetUnionCases typeof<CompletionKind>
  |> Array.map (fun case -> FSharpValue.MakeUnion(case, [||]) :?> CompletionKind)
  |> List.ofArray

/// The `Compile Include` entries of a project file, in order, as the file names they point at.
let private compiledFiles (projectFile: string) : string list =
  Regex.Matches(File.ReadAllText(Path.Combine(repoRoot, projectFile)), "<Compile Include=\"([^\"]+)\"")
  |> Seq.map (fun m -> m.Groups.[1].Value.Replace('\\', '/') |> fun path -> path.Substring(path.LastIndexOf '/' + 1))
  |> List.ofSeq

[<Tests>]
let tests =
  testList "LadderContract" [
    testCase "WHY - the contract names exactly the kinds the daemon's CompletionKind has, so no kind exists on one side only" <| fun _ ->
      let daemon = caseNames typeof<CompletionKind>
      let contract = caseNames typeof<LadderKind>
      Set.difference daemon contract |> Expect.isEmpty "a CompletionKind the contract does not name"
      Set.difference contract daemon |> Expect.isEmpty "a LadderKind the daemon never sends"

    testCase "WHY - LadderKind.all lists every case, because tryParse finds a word through it and a missing case would refuse a real kind" <| fun _ ->
      LadderKind.all
      |> List.map (fun kind -> (FSharpValue.GetUnionFields(kind, typeof<LadderKind>) |> fst).Name)
      |> List.sort
      |> Expect.equal "every case, once" (caseNames typeof<LadderKind> |> Set.toList |> List.sort)

    testCase "WHY - every kind the daemon can send is the word the contract reads back, and drills the way list_members says it does" <| fun _ ->
      for kind in allCompletionKinds do
        let word = CompletionKind.label kind
        word |> Expect.equal (sprintf "%A keeps its wire word" kind) (LadderKind.label (CompletionKind.toLadder kind))
        match LadderKind.tryParse word with
        | Error (UnknownKindWord unknown) -> failtestf "the contract does not read back %s, which the daemon sends for %A" unknown kind
        | Ok parsed ->
          LadderKind.rung parsed |> Expect.equal (sprintf "%s drills the way McpMemberKinds.rungOf says" word) (McpMemberKinds.rungOf kind)

    testCase "WHY - the daemon's CompletionKind maps onto distinct contract kinds, so two kinds never share a wire word" <| fun _ ->
      allCompletionKinds
      |> List.map CompletionKind.toLadder
      |> List.distinct
      |> List.length
      |> Expect.equal "one contract kind per daemon kind" allCompletionKinds.Length

    testCase "WHY - the kinds the contract calls drillable are the ones the Type Explorer expanded before it shared this file, so no tree changed" <| fun _ ->
      LadderKind.all
      |> List.filter (fun kind -> LadderKind.rung kind = LadderRung.Drillable)
      |> List.map LadderKind.label
      |> List.sort
      |> Expect.equal "the editor's old drillableKinds set" [ "Class"; "Enum"; "Interface"; "Module"; "Namespace"; "Struct"; "Type"; "Union" ]

    testProperty "WHY - tryParse gives back a kind only for the word that kind is spelled with, and hands any other word back unchanged" <| fun (word: string) ->
      match LadderKind.tryParse word with
      | Ok kind -> LadderKind.label kind = word
      | Error (UnknownKindWord unknown) -> unknown = word && (LadderKind.all |> List.forall (fun kind -> LadderKind.label kind <> word))

    testCase "WHY - the size words the extension reads are the words the daemon writes, so a partial listing is never mistaken for a complete one" <| fun _ ->
      for total, size in [ CompletionTotal.Exact 0, LadderSizeKind.Exact; CompletionTotal.AtLeast 0, LadderSizeKind.AtLeast ] do
        LadderSizeKind.tryParse (CompletionTotal.label total)
        |> Expect.equal (sprintf "the daemon's word for %A reads back as %A" total size) (Some size)

    testProperty "WHY - a size word the contract does not name states no size, instead of being read as exact or as a floor" <| fun (word: string) ->
      match LadderSizeKind.tryParse word with
      | Some size -> LadderSizeKind.label size = word
      | None -> word <> "exact" && word <> "atLeast"
  ]

[<Tests>]
let ladderWiringTests =
  testList "LadderContract wiring" [
    testCase "WHY - the Readme's MCP column says the Type explorer is a tool exactly when list_members is registered, so the table never claims or denies it by stale prose" <| fun _ ->
      let rows =
        File.ReadAllLines(Path.Combine(repoRoot, "Readme.md"))
        |> Array.filter (fun line -> line.TrimStart().StartsWith "|")
        |> Array.map (fun line -> line.Trim().Trim('|').Split('|') |> Array.map (fun cell -> cell.Trim().Trim('*').Trim()))
      let header = rows |> Array.find (fun cells -> cells.Length > 0 && cells.[0] = "Capability" && Array.contains "MCP" cells)
      let mcpColumn = Array.findIndex ((=) "MCP") header
      let cell =
        rows
        |> Array.find (fun cells -> cells.Length > mcpColumn && cells.[0] = "Type explorer")
        |> fun cells -> cells.[mcpColumn]
      let registered =
        SageFs.Server.McpTools.RegisteredTools.describe typeof<SageFs.Server.McpTools.SageFsTools>
        |> List.exists (fun tool -> tool.Name = "list_members")
      registered |> Expect.isTrue "list_members is a registered tool"
      cell |> Expect.equal "the Type explorer's MCP cell is a tick when the tool exists" "✅"

    testCase "WHY - both projects compile the contract before the files that use it, because the extension cannot reference the daemon's assembly" <| fun _ ->
      let core = compiledFiles "SageFs.Core/SageFs.Core.fsproj"
      let extension = compiledFiles "sagefs-vscode/src/SageFs.Vscode.fsproj"
      core |> List.contains "LadderContract.fs" |> Expect.isTrue "SageFs.Core compiles the contract"
      extension |> List.contains "LadderContract.fs" |> Expect.isTrue "the extension compiles the same file"
      (List.findIndex ((=) "LadderContract.fs") core, List.findIndex ((=) "AutoCompletion.fs") core)
      |> Expect.isLessThan "Core: the contract precedes the kind that maps onto it"
      (List.findIndex ((=) "LadderContract.fs") extension, List.findIndex ((=) "TypeExplorerPure.fs") extension)
      |> Expect.isLessThan "the extension: the contract precedes the explorer's pure decisions"
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant ladderWiringTests |> ignore
