/// The outcome gate for the symbol ladder (docs/plans/walk-the-rest-of-the-symbol-ladder.md): what an agent holding a
/// name actually reads back. The matrix is tool (`list_members`, `list_references`) by the state of the name in the
/// session (it resolves, it resolves and holds nothing, its parent resolves and it does not, no ancestor resolves,
/// the session cannot be asked). Every cell runs the route code the tool runs, over a fake session whose members come
/// from a small tree, and asserts the first line (the scope line for references, the total or the reason for members),
/// that the text says what to do next, and that the reply is under the tool's row in the `ladder reply size` table.
///
/// There is no skipped cell: `expectation` is an exhaustive match over both unions, so a new tool or a new state does
/// not compile until it says what it reads back, and the cells run are the unions' cases read by reflection.
module SageFs.Tests.LadderOutcomeTests

open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.FSharp.Reflection
open SageFs
open SageFs.Features
open SageFs.Features.AutoCompletion
open SageFs.Tests.CompletionFixtures

[<RequireQualifiedAccess>]
type Tool =
  | ListMembers
  | ListReferences

[<RequireQualifiedAccess>]
type NameState =
  | Resolves
  | ResolvesButHoldsNothing
  | ParentResolvesNameDoesNot
  | NoAncestorResolves
  | SessionCannotBeAsked

/// What a cell must read back: how the first line starts, and phrases the rest of the reply must carry.
type Expectation = { FirstLineStarts: string; Says: string list }

let private tree : Map<string, CompletionEntry list> =
  Map.ofList
    [ "Demo", [ entry CompletionKind.Module "Shapes"; entry CompletionKind.Module "Empty"; entry CompletionKind.Constant "Version" ]
      "Demo.Shapes", [ entry CompletionKind.Class "Circle"; entry CompletionKind.Method "area" ]
      "Demo.Empty", [] ]

let private nameOf (state: NameState) : string =
  match state with
  | NameState.Resolves
  | NameState.SessionCannotBeAsked -> "Demo.Shapes"
  | NameState.ResolvesButHoldsNothing -> "Demo.Empty"
  | NameState.ParentResolvesNameDoesNot -> "Demo.Shapez"
  | NameState.NoAncestorResolves -> "Nowhere.Thing"

/// The fake session's member lister. A name outside the tree lists nothing, as a name the compiler does not know does.
let private lister (state: NameState) (name: string) : Task<CompletionAnswer<CompletionEntry>> =
  match state with
  | NameState.SessionCannotBeAsked -> Task.FromResult(CompletionAnswer.Unavailable CompletionUnavailable.SessionNotActive)
  | NameState.Resolves
  | NameState.ResolvesButHoldsNothing
  | NameState.ParentResolvesNameDoesNot
  | NameState.NoAncestorResolves -> Task.FromResult(exactly (Map.tryFind name tree |> Option.defaultValue []))

let private sitesOfDemo : ReferenceSite list =
  [ for i in 1 .. 3 -> { File = "/work/Demo/Use.fs"; Line = i; Column = 4; Kind = ReferenceKind.Use } ]

/// The compiler's answer, for the states where the compiler is asked at all. For the others it must not be asked: the
/// name does not resolve, so a count would only make the agent wait out the budget to be told nothing.
let private compiler (state: NameState) (name: string) : Task<ReferenceReply> =
  match state with
  | NameState.Resolves -> Task.FromResult(ReferenceReply.Counted(CountedScope.ProjectWide [ "Core" ], sitesOfDemo, []))
  | NameState.ResolvesButHoldsNothing -> Task.FromResult(ReferenceReply.Counted(CountedScope.ProjectWide [ "Core" ], [], []))
  | NameState.SessionCannotBeAsked ->
    Task.FromResult(ReferenceReply.NotCounted(ReferenceUnavailable.SessionUnreachable "the session is not running"))
  | NameState.ParentResolvesNameDoesNot
  | NameState.NoAncestorResolves -> failwithf "the compiler was asked about %s, a name that does not resolve" name

let private replyOf (tool: Tool) (state: NameState) : Task<string> =
  let name = nameOf state
  match tool with
  | Tool.ListMembers -> McpMembersReply.membersReply (lister state) name 0
  | Tool.ListReferences ->
    task {
      let! page = McpReferencesRoute.referencesPage (lister state) (compiler state) name SageFsConfig.ListReferencesMax 0
      return McpReferencesPage.render page
    }

let private expectation (tool: Tool) (state: NameState) : Expectation =
  match tool, state with
  | Tool.ListMembers, NameState.Resolves ->
    { FirstLineStarts = "Members of Demo.Shapes: 2 total"; Says = [ "Drillable: 1 of 2." ] }
  | Tool.ListMembers, NameState.ResolvesButHoldsNothing ->
    { FirstLineStarts = "Demo.Empty is a module listed by Demo"; Says = [ "It may be empty" ] }
  | Tool.ListMembers, NameState.ParentResolvesNameDoesNot ->
    { FirstLineStarts = "Demo.Shapez lists no members, and Demo lists no member named Shapez"
      Says = [ "Nearest names there: Shapes."; "call list_members on Demo" ] }
  | Tool.ListMembers, NameState.NoAncestorResolves ->
    { FirstLineStarts = "Nowhere.Thing lists no members. Nothing named Nowhere is listed in this session"
      Says = [ "check the spelling" ] }
  | Tool.ListMembers, NameState.SessionCannotBeAsked ->
    { FirstLineStarts = "Could not list members of Demo.Shapes:"
      Says = [ CompletionUnavailable.describe CompletionUnavailable.SessionNotActive ] }
  | Tool.ListReferences, NameState.Resolves ->
    { FirstLineStarts = "Scope: project-wide"; Says = [ "References to Demo.Shapes: 3 total" ] }
  | Tool.ListReferences, NameState.ResolvesButHoldsNothing ->
    { FirstLineStarts = "Scope: project-wide"; Says = [ "The name resolves in this session" ] }
  | Tool.ListReferences, NameState.ParentResolvesNameDoesNot ->
    { FirstLineStarts = "Scope: unavailable, the name does not resolve past Demo."
      Says = [ "Nearest names there: Shapes."; "call list_members on Demo" ] }
  | Tool.ListReferences, NameState.NoAncestorResolves ->
    { FirstLineStarts = "Scope: unavailable, nothing named Nowhere is listed in this session."; Says = [ "Nowhere" ] }
  | Tool.ListReferences, NameState.SessionCannotBeAsked ->
    { FirstLineStarts = "Scope: unavailable"; Says = [ "the session is not running" ] }

let private toolName (tool: Tool) : string =
  match tool with
  | Tool.ListMembers -> "list_members"
  | Tool.ListReferences -> "list_references"

let private casesOf<'union> () : 'union list =
  FSharpType.GetUnionCases typeof<'union>
  |> Array.map (fun case -> FSharpValue.MakeUnion(case, [||]) :?> 'union)
  |> List.ofArray

[<Tests>]
let tests =
  testList "Ladder outcomes" [
    testCase "WHY - the matrix is every tool in every state, so no cell of it is quietly absent" <| fun _ ->
      let cells = [ for tool in casesOf<Tool> () do for state in casesOf<NameState> () -> tool, state ]
      cells |> List.length |> Expect.equal "two tools by five states" 10
      cells |> List.distinct |> List.length |> Expect.equal "each cell once" 10

    testTask "WHY - in every cell the reply opens with the answer, says what to do next, and fits the tool's ceiling" {
      for tool in casesOf<Tool> () do
        for state in casesOf<NameState> () do
          let cell = sprintf "%s when %A" (toolName tool) state
          let! (reply: string) = replyOf tool state
          let want = expectation tool state
          (reply.Split('\n').[0]).StartsWith(want.FirstLineStarts, System.StringComparison.Ordinal)
          |> Expect.isTrue (sprintf "%s: the first line starts with %A, got %A" cell want.FirstLineStarts (reply.Split('\n').[0]))
          for phrase in want.Says do
            reply |> Expect.stringContains (sprintf "%s says %A" cell phrase) phrase
          let ceiling = LadderReplySizeTests.budgets |> List.find (fun (name, _) -> name = toolName tool) |> snd
          (reply.Length <= ceiling) |> Expect.isTrue (sprintf "%s: %d characters is over the %d the table allows" cell reply.Length ceiling)
    }
  ]
