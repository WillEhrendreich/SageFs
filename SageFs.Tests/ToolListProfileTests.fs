module SageFs.Tests.ToolListProfileTests

/// The small default tool list (docs/plans/small-default-tool-list.md, roast 2026-11-08 priority 9).
///
/// A caller that presents nothing can be shown a starter list instead of the whole catalog. The MCP revision of 2026-07-28
/// says a tool list MUST NOT vary per connection or as a side effect of another request, and MAY vary by the authorization
/// presented, so the profile is a setting of the daemon and the one thing the list reads besides the credential. Every case
/// here guards one half of the promise: the starter list is small, it never moves, and nothing it leaves out becomes
/// unreachable.

open System
open System.Text.Json
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open ModelContextProtocol.Protocol
open SageFs
open SageFs.Capability
open SageFs.Server.McpTools
open SageFs.Tests.ToolListBudgetTests

let private now = DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc)

let private grantFor (preset: RolePreset) : Grant =
  { Preset = preset
    Scope = ScopePrefix.repoRoot
    Route = RouteBinding.BoundToSession "session-1"
    NotAfter = now.AddHours 1.0 }

let private registered : string list = Affordances.declaredGateTools

let private starter (profile: ToolListProfile) : string list =
  ToolList.listed profile IdentityPolicy.ConnectionsAllowed ListedFor.NoCredential registered

/// What happens to a daemon between two `tools/list` requests. None of it is an input to the list.
type private DaemonEvent =
  | SessionCreated
  | MemberJoined
  | SeatTaken
  | SeatVacated
  | LandingQueued

type private DaemonState = { Sessions: int; Members: int; SeatBound: bool; Landings: int }

let private emptyDaemon : DaemonState = { Sessions = 0; Members = 0; SeatBound = false; Landings = 0 }

let private step (state: DaemonState) (event: DaemonEvent) : DaemonState =
  match event with
  | SessionCreated -> { state with Sessions = state.Sessions + 1 }
  | MemberJoined -> { state with Members = state.Members + 1 }
  | SeatTaken -> { state with SeatBound = true }
  | SeatVacated -> { state with SeatBound = false }
  | LandingQueued -> { state with Landings = state.Landings + 1 }

/// Does `listFor` answer the same after every prefix of `events` as it did before any of them?
let private neverMoves (listFor: DaemonState -> string list) (events: DaemonEvent list) : bool =
  let before = listFor emptyDaemon
  events |> List.scan step emptyDaemon |> List.forall (fun state -> listFor state = before)

/// The fault the case exists to catch: a filter that reads the conductor seat, so a join changes the next list.
let private twinThatReadsTheSeat (state: DaemonState) : string list =
  match state.SeatBound with
  | true -> registered
  | false -> starter ToolListProfile.Starter

let private genEvent : Gen<DaemonEvent> =
  Gen.elements [ SessionCreated; MemberJoined; SeatTaken; SeatVacated; LandingQueued ]

let private manualOf (name: string) = RegisteredTools.manual typeof<SageFsTools> name

[<Tests>]
let profileTests =
  testList "The starter tool list" [

    testCase "WHY - the starter list is the nine tools a first session needs, in registered order, and nothing else" <| fun () ->
      let listed = starter ToolListProfile.Starter
      listed |> Expect.equal "exactly the starter set" (registered |> List.filter (fun tool -> Set.contains tool ToolList.starterNames))
      listed.Length |> Expect.equal "nine tools" 9
      listed |> Expect.contains "eval is there" "send_fsharp_code"
      listed |> Expect.contains "and the doorway to the rest" "discover_features"
      Set.isSubset ToolList.starterNames (Set.ofList registered)
      |> Expect.isTrue "every starter tool is registered, so the list names no tool that is not there"

    testCase "WHY - the Full profile is today's list, every registered tool, so nothing changes until a person sets the profile" <| fun () ->
      starter ToolListProfile.Full |> Expect.equal "every tool" registered
      ToolListProfile.defaultProfile |> Expect.equal "and it is the default" ToolListProfile.Full

    testCase "WHY - the starter list is identical for two anonymous connections, before and after a session is created and after a member joins" <| fun () ->
      let property (events: DaemonEvent list) =
        neverMoves (fun _ -> starter ToolListProfile.Starter) events
      Prop.forAll (Gen.listOf genEvent |> Arb.fromGen) property
      |> Check.QuickThrowOnFailure

    testCase "WHY - the twin that reads the conductor seat fails the same property, so the property can tell" <| fun () ->
      neverMoves twinThatReadsTheSeat [ SeatTaken ]
      |> Expect.isFalse "a join that seats the conductor moved the twin's list"
      neverMoves twinThatReadsTheSeat []
      |> Expect.isTrue "and with no event it holds, so the failure is the seat and not the harness"

    testCase "WHY - every tool outside the starter list is still admitted when a caller that presented nothing names it" <| fun () ->
      let hidden = registered |> List.filter (fun tool -> not (Set.contains tool ToolList.starterNames))
      (hidden.Length > 50) |> Expect.isTrue "the starter list does leave tools out"
      for tool in hidden do
        for seat in [ ConductorSeat.NotBoundYet; ConductorSeat.Bound ] do
          admitTokenless IdentityPolicy.ConnectionsAllowed seat Cohort.Authority.Anonymous tool
          |> Expect.isOk (sprintf "%s is callable by name under the starter profile with the seat %A" tool seat)

    testCase "WHY - a member token's list and the operator's list are not changed by the starter profile" <| fun () ->
      for preset in RolePreset.all do
        let grant = grantFor preset
        ToolList.listed ToolListProfile.Starter IdentityPolicy.ConnectionsAllowed (ListedFor.Member grant) registered
        |> Expect.equal (sprintf "%A keeps its role's tools" preset) (visibleTools grant registered)
      ToolList.listed ToolListProfile.Starter IdentityPolicy.ConnectionsAllowed ListedFor.Operator registered
      |> Expect.equal "the operator credential keeps the full list" registered

    testCase "WHY - the starter profile cannot widen a list that TokenRequired cut" <| fun () ->
      ToolList.listed ToolListProfile.Starter IdentityPolicy.TokenRequired ListedFor.NoCredential registered
      |> Expect.equal "still status and the join tool" (listedTools IdentityPolicy.TokenRequired ListedFor.NoCredential registered)

    testCase "WHY - discover_features names every tool the starter list leaves out, and its manual for each is one call away" <| fun () ->
      let described = RegisteredTools.describe typeof<SageFsTools> |> List.map (fun tool -> tool.Name) |> Set.ofList
      let hidden = registered |> List.filter (fun tool -> not (Set.contains tool ToolList.starterNames))
      for tool in hidden do
        described |> Expect.contains (sprintf "%s is named by discover_features" tool) tool
        match manualOf tool with
        | Ok manual -> manual |> Expect.stringContains (sprintf "the manual for %s opens with the tool's name" tool) tool
        | Error refusal -> failtestf "%s has no manual: %A" tool refusal

    testCase "WHY - the manual carries what the list left out: the rules text and every parameter with its description" <| fun () ->
      match manualOf "send_fsharp_code" with
      | Error refusal -> failtestf "send_fsharp_code has no manual: %A" refusal
      | Ok manual ->
        manual |> Expect.stringContains "the transaction rules the long description holds" "TRANSACTION SEMANTICS"
        manual |> Expect.stringContains "the parameter the list sentence does not name" "block_start_line"

    testCase "WHY - a name that is not a tool is refused with the tools there are, so a typo is not an empty answer" <| fun () ->
      match manualOf "send_fsharp_cod" with
      | Ok manual -> failtestf "a misspelled name must not have a manual: %s" manual
      | Error(ManualRefusal.UnknownTool(requested, known)) ->
        requested |> Expect.equal "the refusal names what was asked for" "send_fsharp_cod"
        known |> Expect.contains "and lists the real names" "send_fsharp_code"

    testList "the profile setting" [
      testCase "WHY - unset is Full, so nothing existing changes" <| fun () ->
        ToolListProfile.fromEnvironment (fun _ -> null) |> Expect.equal "unset" ToolListProfile.Full
        ToolListProfile.fromEnvironment (fun _ -> "  ") |> Expect.equal "blank" ToolListProfile.Full

      testCase "WHY - Starter is read in any case" <| fun () ->
        for raw in [ "Starter"; "starter"; "STARTER"; " Starter " ] do
          ToolListProfile.fromEnvironment (fun name -> match name = ToolListProfile.envVar with | true -> raw | false -> null)
          |> Expect.equal (sprintf "'%s'" raw) ToolListProfile.Starter

      testCase "WHY - a value that is neither is Full, so a typo hides nothing, and the parse names the choices" <| fun () ->
        ToolListProfile.fromEnvironment (fun _ -> "Small") |> Expect.equal "a typo hides no tool" ToolListProfile.Full
        match ToolListProfile.tryParse "Small" with
        | Ok profile -> failtestf "'Small' parsed as %A" profile
        | Error unknown ->
          unknown.Given |> Expect.equal "what was given" "Small"
          unknown.Expected |> Expect.equal "what would have worked" [ "Full"; "Starter" ]
    ]
  ]

[<Tests>]
let sentenceTests =
  testList "One sentence per tool in the list" [

    testCase "WHY - every tool's listed description is its one sentence, and no sentence is longer than the cap" <| fun () ->
      let listed = ListedDefinitions.full ()
      listed.Length |> Expect.equal "every registered tool is listed under Full" (List.length registered)
      for tool in listed do
        match Affordances.ToolName.tryParse tool.Name with
        | None -> failtestf "%s is listed and is not a ToolName" tool.Name
        | Some name ->
          tool.Description |> Expect.equal (sprintf "%s lists its sentence" tool.Name) (ToolSummaries.sentenceOf name)
          (tool.Description.Length <= ToolSummaries.maxCharacters)
          |> Expect.isTrue (sprintf "%s is %d characters, over the %d cap: the rest belongs in the manual" tool.Name tool.Description.Length ToolSummaries.maxCharacters)

    testCase "WHY - a sentence is one line that ends with a full stop, and says something no other tool's sentence says" <| fun () ->
      let sentences = Affordances.ToolName.all |> List.map ToolSummaries.sentenceOf
      for sentence in sentences do
        sentence.Contains '\n' |> Expect.isFalse (sprintf "'%s' has a line break" sentence)
        sentence.EndsWith "." |> Expect.isTrue (sprintf "'%s' ends with a full stop" sentence)
      sentences |> List.distinct |> List.length |> Expect.equal "no two tools share a sentence" sentences.Length

    testCase "WHY - a retired tool name does not come back through a sentence or the list" <| fun () ->
      let retired = Affordances.RetiredTool.toolNames
      let listedNames = ListedDefinitions.full () |> List.map (fun tool -> tool.Name)
      for name in retired do
        List.contains name listedNames |> Expect.isFalse (sprintf "%s is retired and must not be listed" name)
        for sentence in Affordances.ToolName.all |> List.map ToolSummaries.sentenceOf do
          sentence.Contains name |> Expect.isFalse (sprintf "'%s' names the retired tool %s" sentence name)

    testCase "WHY - every existing tool stays listed under Full and stays destructive where it was" <| fun () ->
      let listed = ListedDefinitions.full ()
      listed |> List.map (fun tool -> tool.Name) |> List.sort |> Expect.equal "the whole registered set, nothing dropped" (List.sort registered)
      for tool in listed do
        match Affordances.ToolName.tryParse tool.Name with
        | Some name when Set.contains name Affordances.destructiveTools ->
          (not (isNull tool.Annotations) && tool.Annotations.DestructiveHint = Nullable true)
          |> Expect.isTrue (sprintf "%s keeps its destructive hint" tool.Name)
        | _ -> ()
  ]

/// The tools whose parameters the SDK reflects into the list, with each property's description.
let private propertyDescriptions (tool: Tool) : (string * string) list =
  [ for property in tool.InputSchema.GetProperty("properties").EnumerateObject() do
      match property.Value.TryGetProperty "description" with
      | true, description -> yield property.Name, description.GetString()
      | false, _ -> yield property.Name, "" ]

[<Tests>]
let sharedParameterTests =
  testList "Shared parameters are described once" [

    testCase "WHY - a parameter that many tools take carries one of its few named sentences and no private variant" <| fun () ->
      for tool in ListedDefinitions.full () do
        for (parameter, description) in propertyDescriptions tool do
          match SharedParameters.sentencesFor parameter with
          | None -> ()
          | Some allowed ->
            allowed
            |> Expect.contains (sprintf "%s.%s says '%s', which is not one of its named sentences" tool.Name parameter description) description

    testCase "WHY - the named sentences are short, so a shared parameter costs a few dozen characters per tool and not a paragraph" <| fun () ->
      for parameter in SharedParameters.names do
        for sentence in SharedParameters.sentencesFor parameter |> Option.defaultValue [] do
          (sentence.Length <= 140)
          |> Expect.isTrue (sprintf "%s: '%s' is %d characters" parameter sentence sentence.Length)
  ]
