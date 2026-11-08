module SageFs.Tests.McpSeatingTests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Protocol
open SageFs
open SageFs.Affordances
open SageFs.Cohort
open SageFs.McpTools
open SageFs.MemberTable
open SageFs.SeatingFlow
open SageFs.Tests.ToolAuthorityGateTests

/// `seat_conductor` through the real tool body and a real cohort owner: alice is the conductor and leaves, bob is a present
/// Implementer, and bob asks. The person is a fake that answers yes, no, nothing, or is absent. Whatever else happens, the
/// seat changes only when the answer is yes.

let private wd = Some molinaRepo

let private ok (what: string) (result: Result<string, SageFsError>) : string =
  match result with
  | Result.Ok text -> text
  | Result.Error err -> failtestf "%s failed: %s" what (SageFsError.describeForAgent err)

let private refused (what: string) (result: Result<string, SageFsError>) : string =
  match result with
  | Result.Ok text -> failtestf "%s should have been refused, got: %s" what text
  | Result.Error err -> SageFsError.describeForAgent err

/// alice (conductor) left; bob and vera (Implementers) are present; olive observes.
let private vacant () = task {
  let ctx = mkContext ()
  do! join ctx "alice" "Implementer" molinaRepo
  do! join ctx "bob" "Implementer" molinaRepo
  do! join ctx "vera" "Implementer" molinaRepo
  do! join ctx "olive" "Observer" molinaRepo
  let! _ = McpCohortTools.leaveCohort ctx "alice" wd
  return ctx
}

/// Run `body` with this person at the call's client, then take them away again.
let private withPerson (person: Person option) (body: unit -> Task<'a>) : Task<'a> = task {
  let previous = McpSeating.currentPerson.Value
  McpSeating.currentPerson.Value <- person
  try
    return! body ()
  finally
    McpSeating.currentPerson.Value <- previous
}

let private who (answer: Answer) (questions: List<string>) : Person =
  { Elicitation = Elicitation.Offered
    Ask =
      fun question _ ->
        questions.Add question
        Task.FromResult answer }

let private conductorLine (ctx: McpContext) = task {
  match! McpCohortIntegration.getCohortStatus ctx wd with
  | Result.Ok text -> return text.Split('\n') |> Array.find (fun line -> line.StartsWith "Conductor:")
  | Result.Error err -> return failtestf "status: %s" (SageFsError.describeForAgent err)
}

let private routes = [ "sagefs cohort seat"; "Seat button" ]

[<Tests>]
let tests =
  testList "seat_conductor: the tool body, a real owner and a person who answers" [

    testTask "a yes seats the caller, says who was seated and what the vacancy was, and the status agrees" {
      let! ctx = vacant ()
      let questions = List<string>()
      let! result = withPerson (Some(who Answer.Yes questions)) (fun () -> McpSeating.run ctx "bob" "" wd)
      let text = ok "seating bob" result
      text |> Expect.stringContains "names the new conductor" "bob"
      text |> Expect.stringContains "names the vacancy it ended" "alice"
      text |> Expect.stringContains "says which client accepted, and does not claim to know who was at it" "client at bob accepted"
      text.Contains "A person said yes" |> Expect.isFalse "the daemon cannot know that a person answered"
      questions.Count |> Expect.equal "the person was asked once" 1
      let! line = conductorLine ctx
      line |> Expect.stringContains "the seat is bob's" "bob"
    }

    testTask "the member to seat can be another present member than the caller" {
      let! ctx = vacant ()
      let questions = List<string>()
      let! result = withPerson (Some(who Answer.Yes questions)) (fun () -> McpSeating.run ctx "bob" "vera" wd)
      ok "seating vera" result |> Expect.stringContains "vera is the conductor" "vera"
      let! line = conductorLine ctx
      line |> Expect.stringContains "the seat is vera's" "vera"
    }

    testTask "the question names the repository, who is asking, who would sit, their role, who held the seat and why it is empty" {
      let! ctx = vacant ()
      let questions = List<string>()
      let! _ = withPerson (Some(who Answer.Yes questions)) (fun () -> McpSeating.run ctx "bob" "vera" wd)
      let question = Seq.exactlyOne questions
      for part in [ "gate-scope/molina"; "bob"; "vera"; "Implementer"; "alice"; "left"; "conductor" ] do
        question |> Expect.stringContains (sprintf "the question mentions '%s'" part) part
    }

    for (what, answer) in
      [ "a decline", Answer.Declined
        "a dismissal", Answer.Cancelled
        "another action", Answer.AnsweredOther "reject"
        "silence", Answer.NoAnswerInTime TestTimeouts.patience
        "a failed question", Answer.CouldNotBeAsked "the transport closed" ] do
      testTask (sprintf "%s refuses, leaves the seat empty, and names the other routes" what) {
        let! ctx = vacant ()
        let! result = withPerson (Some(who answer (List<string>()))) (fun () -> McpSeating.run ctx "bob" "" wd)
        let text = refused what result
        for route in routes do
          text |> Expect.stringContains (sprintf "names '%s'" route) route
        text |> Expect.stringContains "says nothing was done" "Nothing was done"
        let! line = conductorLine ctx
        line |> Expect.stringContains "the seat is still empty" "VACANT"
      }

    testTask "a client that cannot be asked is refused and nothing is put to anyone" {
      let! ctx = vacant ()
      let questions = List<string>()
      let person = { who Answer.Yes questions with Elicitation = Elicitation.NotOffered }
      let! result = withPerson (Some person) (fun () -> McpSeating.run ctx "bob" "" wd)
      let text = refused "a client without elicitation" result
      text |> Expect.stringContains "says why" "does not offer MCP elicitation"
      for route in routes do
        text |> Expect.stringContains (sprintf "names '%s'" route) route
      questions.Count |> Expect.equal "nobody was asked" 0
      let! line = conductorLine ctx
      line |> Expect.stringContains "still empty" "VACANT"
    }

    testTask "a call with no client bound at all is a client that cannot be asked" {
      let! ctx = vacant ()
      let! result = withPerson None (fun () -> McpSeating.run ctx "bob" "" wd)
      refused "no client" result |> Expect.stringContains "refused for want of a person" "Nothing was done"
      let! line = conductorLine ctx
      line |> Expect.stringContains "still empty" "VACANT"
    }

    testTask "nobody is asked about a seating the cohort would refuse" {
      let! ctx = vacant ()
      let questions = List<string>()
      // A member who is not present.
      let! absent = withPerson (Some(who Answer.Yes questions)) (fun () -> McpSeating.run ctx "bob" "nobody-here" wd)
      refused "an absent member" absent |> Expect.stringContains "names the roster" "bob"
      // A caller with no seat.
      let! stranger = withPerson (Some(who Answer.Yes questions)) (fun () -> McpSeating.run ctx "stranger" "" wd)
      refused "a caller with no seat" stranger |> Expect.stringContains "says to join" "join_cohort"
      questions.Count |> Expect.equal "the person was never asked" 0
    }

    testTask "a held seat is refused with its holder named, and nobody is asked" {
      let ctx = mkContext ()
      do! join ctx "alice" "Implementer" molinaRepo
      do! join ctx "bob" "Implementer" molinaRepo
      let questions = List<string>()
      let! result = withPerson (Some(who Answer.Yes questions)) (fun () -> McpSeating.run ctx "bob" "" wd)
      refused "a held seat" result |> Expect.stringContains "names the holder" "alice"
      questions.Count |> Expect.equal "nobody was asked" 0
    }

    testTask "asking twice cannot seat twice: the second ask finds the seat held" {
      let! ctx = vacant ()
      let questions = List<string>()
      let! first = withPerson (Some(who Answer.Yes questions)) (fun () -> McpSeating.run ctx "bob" "" wd)
      ok "the first ask" first |> ignore
      let! second = withPerson (Some(who Answer.Yes questions)) (fun () -> McpSeating.run ctx "vera" "" wd)
      refused "the second ask" second |> Expect.stringContains "names bob" "bob"
      questions.Count |> Expect.equal "only the first was a question" 1
    }
  ]

let private elicited (action: string) (confirmed: bool option) : ElicitResult =
  match confirmed with
  | None -> ElicitResult(Action = action)
  | Some value ->
    let element = JsonDocument.Parse(if value then "true" else "false").RootElement
    ElicitResult(Action = action, Content = Dictionary<string, JsonElement>(dict [ "confirmed", element ]))

[<Tests>]
let tableTests =
  testList "seat_conductor is in every table a tool must be in, and only a seated Implementer reaches it" [

    testCase "WHY - it is a ToolName, a cohort verb, routed by repository, and a tool class holds it" <| fun _ ->
      ToolName.tryParse "seat_conductor" |> Option.isSome |> Expect.isTrue "a ToolName"
      ToolName.allToolNames |> Expect.contains "in the closed set" "seat_conductor"
      ToolName.tryParse "seat_conductor" |> Option.bind ToolName.tryCohortTool |> Option.isSome |> Expect.isTrue "a CohortTool"
      CohortTool.all |> List.map CohortTool.toToolName |> Expect.contains "named by CohortTool" "seat_conductor"
      Capability.RouteKind.ofTool "seat_conductor" |> Expect.equal "picks a cohort by directory" (Some Capability.RouteKind.InCohort)
      Capability.ToolClass.ofTool "seat_conductor" |> Expect.equal "administration" (Some Capability.ToolClass.CohortAdmin)

    testCase "WHY - no member token role can call it, so a token holder can never seat anyone, and a token's tools/list leaves it out" <| fun _ ->
      let classOf = Capability.ToolClass.ofTool "seat_conductor" |> Option.get
      for preset in Capability.RolePreset.all do
        Capability.RolePreset.toolClasses preset
        |> Set.contains classOf
        |> Expect.isFalse (sprintf "the %A token preset must not hold CohortAdmin" preset)

    testCase "WHY - a seated Implementer and the conductor reach it; an observer, a verifier and someone with no seat do not" <| fun _ ->
      let allowed (authority: Authority<MemberId>) =
        checkAuthorityAllowed authority (ToolName.tryParse "seat_conductor" |> Option.get) |> Result.isOk
      let me = MemberId.Minted "me"
      allowed (Authority.Member(me, JoinableRole.Implementer)) |> Expect.isTrue "an Implementer asks"
      allowed (Authority.Conductor me) |> Expect.isTrue "the conductor is let through (the cohort refuses a held seat)"
      allowed (Authority.Member(me, JoinableRole.Observer)) |> Expect.isFalse "an observer reads"
      allowed (Authority.Member(me, JoinableRole.Verifier)) |> Expect.isFalse "a verifier verifies"
      allowed Authority.Anonymous |> Expect.isFalse "someone who never joined has no seat to ask from"

    testCase "WHY - it is not conductor-only: the seat it exists to fill has no conductor" <| fun _ ->
      ToolRole.conductorOnlyTools |> Set.map ToolName.toString |> Set.contains "seat_conductor" |> Expect.isFalse "not conductor-only"

    testCase "WHY - the description is short" <| fun _ ->
      let tool = typeof<SageFs.Server.McpTools.SageFsTools>.GetMethod "seat_conductor"
      let description = (tool.GetCustomAttributes(typeof<System.ComponentModel.DescriptionAttribute>, false) |> Array.head :?> System.ComponentModel.DescriptionAttribute).Description
      (description.Length < 600) |> Expect.isTrue (sprintf "the description is %d characters" description.Length)
  ]

[<Tests>]
let answerTests =
  testList "what a person's answer to the question means" [

    testCase "WHY - only an accept with the box ticked is the yes: a client that accepts without content, or with the default, does not seat anyone" <| fun _ ->
      let answer = SageFs.Server.McpServer.seatingAnswerOf
      answer (elicited "accept" (Some true)) |> Expect.equal "accept, ticked" Answer.Yes
      answer (elicited "accept" (Some false)) |> Expect.equal "accept with the box unticked" Answer.Declined
      (match answer (elicited "accept" None) with
       | Answer.AnsweredOther _ -> true
       | _ -> false)
      |> Expect.isTrue "an accept that carries no answer is not a yes (a client that accepts everything would otherwise seat)"

    testCase "WHY - the box the person is shown starts unticked, so a client that applies defaults and accepts answers no" <| fun _ ->
      let text = IO.File.ReadAllText(IO.Path.Combine(RepoPaths.repoPathFull [||], "SageFs", "McpServer.fs"))
      let start = text.IndexOf "let seatingPerson"
      let body = text.Substring(start, 1800)
      body |> Expect.stringContains "the seating form's default is false" "flag.Default <- System.Nullable false"

    testCase "WHY - decline, cancel and anything else are not a yes" <| fun _ ->
      SageFs.Server.McpServer.seatingAnswerOf (elicited "decline" None) |> Expect.equal "decline" Answer.Declined
      SageFs.Server.McpServer.seatingAnswerOf (elicited "cancel" None) |> Expect.equal "cancel" Answer.Cancelled
      SageFs.Server.McpServer.seatingAnswerOf (elicited "maybe" None) |> Expect.equal "other" (Answer.AnsweredOther "maybe")
  ]
