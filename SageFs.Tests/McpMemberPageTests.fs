module SageFs.Tests.McpMemberPageTests

/// Slice 2 of the progressive map (docs/progressive-map-research-2026-10-24.md section 9): the
/// `list_members` reply states a count per REAL kind (the kind FCS gave each member, carried from
/// the worker), groups drillable kinds first, says whether its total is exact, and names a reason
/// whenever a name lists nothing. Default tier, no session.
open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Features.AutoCompletion
open SageFs.McpListMembers
open SageFs.McpMemberKinds
open SageFs.McpMemberPage
open SageFs.McpNamePath
open SageFs.McpNoMembers
open SageFs.Tests.CompletionFixtures

let private repoRoot = RepoPaths.requireRepoRoot ()

let private lines (text: string) : string list =
  text.Split('\n') |> List.ofArray

let private allKinds : CompletionKind list =
  FSharpType.GetUnionCases typeof<CompletionKind>
  |> Array.map (fun case -> FSharpValue.MakeUnion(case, [||]) :?> CompletionKind)
  |> List.ofArray

/// Entries whose kinds are chosen from the whole closed set by an index, so a property covers every kind.
let private entriesFromSeeds (seeds: (string * int) list) : CompletionEntry list =
  seeds
  |> List.map (fun (name, seed) ->
    entry allKinds.[abs (seed % allKinds.Length)] (if isNull name then "" else name))

let private skillText : (string * string) list =
  Directory.GetFiles(Path.Combine(repoRoot, "skills", "sagefs"), "*.md")
  |> Array.sort
  |> Array.map (fun path -> Path.GetFileName path, File.ReadAllText path)
  |> List.ofArray

/// Every page of a member list, walked by following the continuation each page names.
let private walk (cap: int) (total: CompletionTotal) (members: CompletionEntry list) : MemberPage list =
  let rec go offset acc =
    let page = pageMembers "X" cap offset total members
    match page.Continuation with
    | NextPage(next, _) -> go next (page :: acc)
    | LastPage
    | CutShort _ -> List.rev (page :: acc)
  go 0 []

/// A reply whose answer is not empty, so the walk of the name is never consulted. `ResolvedRoot` is the walk's answer
/// when there is nothing to explain.
let private reply (name: string) (cap: int) (offset: int) (answer: CompletionAnswer<CompletionEntry>) : string =
  formatListMembers name cap offset answer NamePathResolution.ResolvedRoot

/// The fixture the roast named: a module with functions, types and nested modules.
let private mixed : CompletionEntry list =
  entriesOf CompletionKind.Module [ for i in 1 .. 12 -> sprintf "Mod%02d" i ]
  @ entriesOf CompletionKind.Method [ for i in 1 .. 40 -> sprintf "fn%02d" i ]
  @ entriesOf CompletionKind.Class [ for i in 1 .. 3 -> sprintf "Type%d" i ]

[<Tests>]
let tests =
  testList "list_members kinds" [

    testCase "WHY — the reply counts members per real kind before the first name, so the caller sizes the level before reading it" <| fun _ ->
      let text = reply "System.IO" 100 0 (exactly mixed)
      let all = lines text
      let kindsAt = all |> List.findIndex (fun l -> l.StartsWith "By kind:")
      let firstNameAt = all |> List.findIndex (fun l -> l.StartsWith "  ")
      (kindsAt, firstNameAt) |> Expect.isLessThan "the count line comes before any name"
      all.[kindsAt] |> Expect.stringContains "modules are counted" "12 modules"
      all.[kindsAt] |> Expect.stringContains "functions are counted" "40 methods"
      all.[kindsAt] |> Expect.stringContains "types are counted" "3 classes"
      all.[kindsAt] |> Expect.stringContains "and the drillable share is stated" "Drillable: 15 of 55."

    testCase "WHY — a union case under List is a leaf, because the kind is the one FCS gave and not a guess from a capital letter" <| fun _ ->
      let list =
        entriesOf CompletionKind.EnumMember [ "Cons"; "Empty" ]
        @ entriesOf CompletionKind.Method [ "map"; "filter" ]
      let page = pageMembers "List" 100 0 (CompletionTotal.Exact 4) list
      page.Counts
      |> List.exists (fun c -> rungOf c.Kind = LadderRung.Drillable)
      |> Expect.isFalse "nothing under List is drillable"
      render page |> Expect.stringContains "and the reply says so" "Drillable: 0 of 4."

    testCase "WHY — drillable kinds are listed before leaves, because the next rung of the walk is one of them" <| fun _ ->
      let page = pageMembers "System.IO" 100 0 (CompletionTotal.Exact 5) (List.rev mixed |> List.truncate 5)
      page.Groups
      |> List.map (fun (kind, _) -> rungOf kind)
      |> List.distinct
      |> Expect.equal "drillable group first, then leaf" [ LadderRung.Drillable; LadderRung.Leaf ]

    testCase "WHY — a set with one kind counts only that kind, because an empty group is noise" <| fun _ ->
      (pageMembers "X" 100 0 (CompletionTotal.Exact 4) (entriesOf CompletionKind.Module [ "A"; "B"; "C"; "D" ])).Counts
      |> Expect.equal "one group" [ { Kind = CompletionKind.Module; Count = 4 } ]

    testCase "WHY — an exact total says how many there are and a floor says it is a floor, so the reply never claims a size it does not know" <| fun _ ->
      let members = entriesOf CompletionKind.Method [ "a"; "b"; "c" ]
      reply "X" 100 0 (CompletionAnswer.Answered(members, CompletionTotal.Exact 3))
      |> Expect.stringContains "an exact total reads as a count" "3 total"
      let floor = reply "X" 100 0 (CompletionAnswer.Answered(members, CompletionTotal.AtLeast 3))
      floor |> Expect.stringContains "a floor says at least" "at least 3"
      floor.Contains " total" |> Expect.isFalse "and never calls the floor a total"
      floor |> Expect.stringContains "and says the real size is not known" "not known"

    testCase "WHY — a list that ends at the worker's bound says the set is larger instead of ending quietly" <| fun _ ->
      let members = entriesOf CompletionKind.Method [ for i in 1 .. 7 -> sprintf "m%d" i ]
      let text = reply "X" 100 0 (CompletionAnswer.Answered(members, CompletionTotal.AtLeast 7))
      text |> Expect.stringContains "the footer names the cut" "the set is larger"
      text.Contains "call again with offset" |> Expect.isFalse "and offers no offset that would return nothing"

    testCase "WHY — a 500-member name is still capped at the page size and still states the true total and the true counts" <| fun _ ->
      let max = SageFsConfig.ListMembersMax
      let members = entriesOf CompletionKind.Module [ for i in 1 .. 150 -> sprintf "M%03d" i ] @ entriesOf CompletionKind.Method [ for i in 1 .. 350 -> sprintf "f%03d" i ]
      let text = reply "X" max 0 (exactly members)
      text |> Expect.stringContains "the total is the real size" "500 total"
      text |> Expect.stringContains "the counts cover the whole set, not the page" "150 modules, 350 methods"
      lines text
      |> List.filter (fun l -> l.StartsWith "  ")
      |> List.length
      |> Expect.equal "names shown are the cap" max
      (text.Length < 4000) |> Expect.isTrue "one page stays under the house reply ceiling"

    testCase "WHY — every kind has its own plural noun, so a count line never reads '3 values' for properties" <| fun _ ->
      allKinds
      |> List.map (fun k -> snd (kindNoun k))
      |> List.distinct
      |> List.length
      |> Expect.equal "one plural per kind" allKinds.Length

    testCase "WHY — no daemon source guesses a kind from the shape of a name, because the worker sends the real one" <| fun _ ->
      Directory.GetFiles(Path.Combine(repoRoot, "SageFs"), "*.fs")
      |> Array.filter (fun path -> File.ReadAllText(path).Contains "completionKindForLabel" || File.ReadAllText(path).Contains "rungOfName")
      |> Array.map Path.GetFileName
      |> Expect.isEmpty "no file under SageFs/ classifies a member by its spelling"

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
      "WHY — walking the continuations visits every member exactly once, whatever the names, kinds and cap" <|
      fun (seeds: (string * int) list) (cap: PositiveInt) ->
        let members = entriesFromSeeds seeds
        let pages = walk cap.Get (CompletionTotal.Exact members.Length) members
        let seen = pages |> List.collect (fun p -> p.Groups |> List.collect (fun (_, names) -> names)) |> List.sort
        seen = (members |> List.map (fun e -> e.DisplayText) |> List.sort)

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
      "WHY — every page states the same counts, and the counts add up to the members fetched" <|
      fun (seeds: (string * int) list) (cap: PositiveInt) ->
        let members = entriesFromSeeds seeds
        let pages = walk cap.Get (CompletionTotal.Exact members.Length) members
        let countsAgree = pages |> List.map (fun p -> p.Counts) |> List.distinct |> List.length = 1
        let sumsToFetched = pages |> List.forall (fun p -> (p.Counts |> List.sumBy (fun c -> c.Count)) = members.Length && p.Fetched = members.Length)
        countsAgree && sumsToFetched

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
      "WHY — a page never shows more names than the cap, even for an offset outside the set" <|
      fun (seeds: (string * int) list) (cap: PositiveInt) (offset: int) ->
        let members = entriesFromSeeds seeds
        let page = pageMembers "X" cap.Get offset (CompletionTotal.Exact members.Length) members
        (page.Groups |> List.sumBy (fun (_, ns) -> ns.Length)) <= cap.Get

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 200 }
      "WHY — the same members page the same way whatever order the worker listed them in, so offset=100 means the same thing on every call" <|
      fun (seeds: (string * int) list) ->
        let members = entriesFromSeeds seeds
        let first = pageMembers "X" 10 0 (CompletionTotal.Exact members.Length) members
        let reversed = pageMembers "X" 10 0 (CompletionTotal.Exact members.Length) (List.rev members)
        first.Groups = reversed.Groups

    testList "a name that lists nothing says why" [
      testCase "WHY — a method has no members, and the reply says it is a leaf its parent lists, not '0 total'" <| fun _ ->
        let text = formatListMembers "System.IO.Path.Combine" 100 0 (exactly []) (NamePathResolution.Resolved CompletionKind.Method)
        text |> Expect.stringContains "names the kind" "method"
        text |> Expect.stringContains "says it is a leaf" "leaf"
        text |> Expect.stringContains "points at the parent that lists its siblings" "System.IO.Path"
        text.Contains "total" |> Expect.isFalse "and never reports a total for a thing with no members"

      testCase "WHY — a name its parent does not list is reported as not resolving, with the parent to look at" <| fun _ ->
        let resolution = NamePathResolution.StopsAt("System.IO", "Pathh", [ "Path" ])
        let text = formatListMembers "System.IO.Pathh" 100 0 (exactly []) resolution
        text |> Expect.stringContains "says it does not resolve" "does not resolve"
        text |> Expect.stringContains "names the parent" "System.IO"
        text |> Expect.stringContains "offers the near name" "Path"

      testCase "WHY — a deep path that stops several segments up says where it stops, not just that the last name is missing" <| fun _ ->
        let resolution = NamePathResolution.StopsAt("MyApp.Core", "Sesion", [ "Session" ])
        let text = formatListMembers "MyApp.Core.Sesion.Create" 100 0 (exactly []) resolution
        text |> Expect.stringContains "names the prefix that lists" "MyApp.Core"
        text |> Expect.stringContains "names the segment it cannot find" "Sesion"
        text |> Expect.stringContains "offers the near name" "Session"

      testCase "WHY — a list cut at the wire ceiling does not tell the reader the name is absent" <| fun _ ->
        let resolution = NamePathResolution.StopsAtCutList("System", "Zed", [])
        let text = formatListMembers "System.Zed" 100 0 (exactly []) resolution
        text |> Expect.stringContains "says it may be past the cut" "may be past"
        text.Contains "does not resolve" |> Expect.isFalse "and never says the name does not resolve"

      testCase "WHY — a drillable name with no members is an empty container, not a leaf" <| fun _ ->
        classifyEmpty "MyApp.Empty" (NamePathResolution.Resolved CompletionKind.Module)
        |> Expect.equal "an empty module" (NoMembersBecause.EmptyContainer(CompletionKind.Module, "MyApp"))

      testCase "WHY — a one-segment name cannot be checked against a parent, and the reply says that" <| fun _ ->
        classifyEmpty "Nope" (NamePathResolution.NothingResolves "Nope")
        |> Expect.equal "no parent to ask" NoMembersBecause.NoParentToAsk

      testCase "WHY — a first segment that lists nothing in a longer name is named, because the whole path hangs on it" <| fun _ ->
        let text = formatListMembers "Sytem.IO.Path" 100 0 (exactly []) (NamePathResolution.NothingResolves "Sytem")
        text |> Expect.stringContains "names the first segment" "Sytem"
        text |> Expect.stringContains "says nothing is listed under it" "Nothing named Sytem"

      testCase "WHY — when the walk could not ask, the reply says leaf-ness is not known and why" <| fun _ ->
        let resolution = NamePathResolution.Unknown("System.IO", CompletionUnavailable.SessionNotActive)
        let text = formatListMembers "System.IO.Path" 100 0 (exactly []) resolution
        text |> Expect.stringContains "says it is not known" "not known"
        text |> Expect.stringContains "gives the reason's remedy" (CompletionUnavailable.describe CompletionUnavailable.SessionNotActive)

      testCase "WHY — a listing that could not be made at all is an error with the reason, never '0 total'" <| fun _ ->
        let text =
          reply "System.IO.Path" 100 0 (CompletionAnswer.Unavailable(CompletionUnavailable.HostLost "the host exited"))
        text |> Expect.stringContains "says it could not list" "Could not list members of System.IO.Path"
        text |> Expect.stringContains "carries the reason" "the host exited"
        text.Contains "0 total" |> Expect.isFalse "and is not the empty-list reply"

      testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
        "WHY — splitParent and classifyEmpty agree: a parent that lists the leaf name by a leaf kind always yields IsLeaf" <|
        fun (parent: NonEmptyString) (leaf: NonEmptyString) ->
          let clean (s: string) = s.Replace(".", "")
          let parentName, leafName = clean parent.Get, clean leaf.Get
          match parentName = "" || leafName = "" with
          | true -> true
          | false ->
            let qualified = parentName + "." + leafName
            classifyEmpty qualified (NamePathResolution.Resolved CompletionKind.Property)
            = NoMembersBecause.IsLeaf(CompletionKind.Property, parentName)
    ]

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

    testCase "WHY — the skill does not tell an agent that kinds are read from the shape of a name, because they no longer are" <| fun _ ->
      skillText
      |> List.filter (fun (_, text) -> text.Contains "shape of each name")
      |> List.map fst
      |> Expect.isEmpty "no skill file still describes the old name-shape reading"
  ]

do TestInfrastructure.Ratchet.register TestInfrastructure.Ratchet.Invariant tests |> ignore
