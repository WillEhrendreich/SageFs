module SageFs.Tests.LadderReplySizeTests

/// Slice 5 of the progressive map (docs/plans/walk-the-rest-of-the-symbol-ladder.md): a ceiling per ladder tool on the
/// length of its reply, measured by running the real formatter over a worst-case fixture at the configured cap. The
/// ladder exists so an agent never receives the whole map in one reply; a change that lets a reply grow (a longer line,
/// a dropped cap, a new section) fails here and has to lower something else or say why. A tool with no row has a
/// budget of zero, and a budget above the measured length is itself a failure, so room that was won back is not spent.

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.AutoCompletion
open SageFs.Tests.CompletionFixtures

/// 500 members of long names across eight kinds, so the page has many kind groups and the cap is what holds it down.
let membersWorstCase : CompletionEntry list =
  let kinds =
    [| CompletionKind.Method; CompletionKind.Property; CompletionKind.Class; CompletionKind.Module
       CompletionKind.Field; CompletionKind.Union; CompletionKind.Interface; CompletionKind.Event |]
  [ for i in 1 .. 500 ->
      entry kinds.[i % kinds.Length] (sprintf "a_long_member_name_that_pads_the_page_out_%03d_%s" i (String('x', 12))) ]

/// 10,000 sites across 200 files with deep paths: the heaviest reply a busy name could ask for.
let sitesWorstCase : ReferenceSite list =
  [ for i in 0 .. 9999 ->
      { File = sprintf "/work/repo/src/a/deeply/nested/folder/structure/module%03d/FileName%03d.fs" (i % 200) (i % 200)
        Line = i + 1
        Column = i % 120
        Kind = (if i % 7 = 0 then ReferenceKind.Definition else ReferenceKind.Use) } ]

let private worstName = "SageFs.Features.SomeVeryLongModuleName.AnotherVeryLongModuleName.theFunctionBeingAsked"

let membersReply (cap: int) : string =
  McpListMembers.formatListMembers worstName cap 0 (exactly membersWorstCase) McpNamePath.NamePathResolution.ResolvedRoot

let referencesReply (cap: int) : string =
  ReferenceReply.Counted(CountedScope.ProjectWide [ "Core" ], sitesWorstCase, [])
  |> McpReferencesPage.build worstName cap 0
  |> McpReferencesPage.render

/// The measured length of each ladder tool's reply, at the cap the product runs with.
let replyChars () : (string * int) list =
  [ "list_members", (membersReply SageFsConfig.ListMembersMax).Length
    "list_references", (referencesReply SageFsConfig.ListReferencesMax).Length ]

/// The table as data, so `--ratchets --tighten` can lower it to the measured length.
let budgets : (string * int) list =
  [ "list_members", 6439
    "list_references", 4225 ]

let _table =
  TestInfrastructure.Ratchet.table
    { Name = "ladder reply size"
      SourceFile = "SageFs.Tests/LadderReplySizeTests.fs"
      Budgets = budgets
      Actual = replyChars }

[<Tests>]
let tests =
  testList "Ladder reply size" [
    testList "budgets" [
      test "WHY - every ladder reply stays within its character budget, so no rung quietly becomes the map again" {
        let measured = Map.ofList (replyChars ())
        for (tool, budget) in budgets do
          (measured.[tool], budget)
          |> Expect.isLessThanOrEqual (sprintf "%s reply may not exceed %d characters: shorten a line or lower the cap" tool budget)
      }

      test "WHY - a budget above the measured length is stale, so room that was won back cannot be spent again" {
        let measured = Map.ofList (replyChars ())
        for (tool, budget) in budgets do
          measured.[tool] |> Expect.equal (sprintf "%s: lower the budget with --ratchets --tighten" tool) budget
      }

      test "WHY - the table has exactly one row per ladder tool, because a tool with no row has no ceiling" {
        budgets |> List.map fst |> List.sort |> Expect.equal "the ladder tools" [ "list_members"; "list_references" ]
      }

      // The twin: a formatter that drops the cap prints every member and every site. If the ratchet could not see
      // that, it would be measuring nothing.
      test "WHY - the ratchet has teeth: a reply with the page cap dropped is over its budget" {
        let measured =
          Map.ofList
            [ "list_members", (membersReply Int32.MaxValue).Length
              "list_references", (referencesReply Int32.MaxValue).Length ]
        for (tool, budget) in budgets do
          (measured.[tool], budget) |> Expect.isGreaterThan (sprintf "%s without its cap must exceed the budget" tool)
      }
    ]
    |> TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant
  ]
