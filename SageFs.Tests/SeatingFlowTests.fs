module SageFs.Tests.SeatingFlowTests

open System
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.SeatingFlow

/// P12: a seated member asks for an empty conductor seat to be filled and a PERSON answers. The seat is the authority to mint
/// tokens, configure the integration and reassign claims, so no answer but an explicit yes may seat anyone, and the command is
/// built from a value only the question can produce. These are the pure rules; the tool body through a real owner is
/// `McpSeatingTests`, and the whole thing against real processes is `SeatConductorIntegrationTests`.

let private epoch = DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)
let private machine = CohortScope.Machine
let private entropy : Entropy = [| 1uy |]

let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"
let private carol = MemberId.Minted "carol"
let private cap = MemberId.Capability "0123456789abcdef"

let private next (state: CohortState<MemberId>) (command: CohortCommand<MemberId>) : CohortState<MemberId> =
  match decide epoch entropy state command with
  | Ok(s, _, _) -> s
  | Error e -> failtestf "expected %A to succeed, got %A" command e

let private join who = CohortCommand.Join(who, JoinableRole.Implementer, None, machine)

/// alice joined first and held the seat, bob joined second, then alice left: the seat is vacant and bob is present.
let private vacantWithBob () =
  CohortState.empty ()
  |> fun s -> next s (join alice)
  |> fun s -> next s (join bob)
  |> fun s -> next s (CohortCommand.Depart(alice, machine))

/// A person that answers `answer` and counts how often it was asked and with what.
type private Asked() =
  member val Calls = 0 with get, set
  member val Question = "" with get, set

let private personWho (answer: Answer) (asked: Asked) : Person =
  { Elicitation = Elicitation.Offered
    Ask =
      fun question _ ->
        asked.Calls <- asked.Calls + 1
        asked.Question <- question
        Task.FromResult answer }

let private plan (state: CohortState<MemberId>) (caller: MemberId) (target: MemberId) =
  SeatingFlow.plan epoch state machine caller target

let private planned (state: CohortState<MemberId>) (caller: MemberId) (target: MemberId) : Plan =
  match plan state caller target with
  | Ok p -> p
  | Error refusal -> failtestf "expected a plan, got %A" refusal

let private askWith (person: Person) (p: Plan) : Task<Result<SeatApproval, Refusal>> =
  SeatingFlow.ask person "seat bob?" p

let private answers : (string * Answer * (Refusal -> bool)) list =
  [ "a decline", Answer.Declined, (function Refusal.Declined -> true | _ -> false)
    "a dismissal", Answer.Cancelled, (function Refusal.Cancelled -> true | _ -> false)
    "some other action", Answer.AnsweredOther "reject", (function Refusal.AnsweredOther "reject" -> true | _ -> false)
    "silence", Answer.NoAnswerInTime TestTimeouts.patience, (function Refusal.NoAnswerInTime w -> w = TestTimeouts.patience | _ -> false)
    "a failed question", Answer.CouldNotBeAsked "transport closed", (function Refusal.CouldNotBeAsked "transport closed" -> true | _ -> false) ]

[<Tests>]
let tests =
  testList "Seating flow (P12): a person says yes before anyone is seated" [

    testList "the plan applies the cohort's own rules before a person is asked" [

      testCase "WHY - a present member can be put forward for a vacant seat, and the plan says who held it and why it is empty" <| fun _ ->
        let state = vacantWithBob ()
        let p = planned state bob bob
        p.Target |> Expect.equal "the member to seat" bob
        p.TargetRole |> Expect.equal "in the role they joined with" JoinableRole.Implementer
        (match p.Seat with
         | ConductorBinding.Vacant(former, _, why) -> former = alice && why = VacancyReason.ConductorLeft
         | _ -> false)
        |> Expect.isTrue "the plan carries the vacancy: alice held it and left"

      testCase "WHY - a seated member can put forward another present member, not only itself" <| fun _ ->
        let state = next (vacantWithBob ()) (join carol)
        (plan state bob carol |> Result.isOk) |> Expect.isTrue "bob asks for carol"

      testCase "WHY - a seat that is held is refused with its holder named, and nobody is asked" <| fun _ ->
        let held = CohortState.empty () |> fun s -> next s (join alice) |> fun s -> next s (join bob)
        match plan held bob bob with
        | Error(Refusal.CohortRefused(CohortError.ConductorSeatTaken sitting)) -> sitting |> Expect.equal "alice holds it" alice
        | other -> failtestf "expected ConductorSeatTaken, got %A" other

      testCase "WHY - a member who is not present is refused with the roster" <| fun _ ->
        match plan (vacantWithBob ()) bob carol with
        | Error(Refusal.CohortRefused(CohortError.SeatTargetAbsent(target, present))) ->
          target |> Expect.equal "carol is not here" carol
          present |> Expect.equal "bob is" [ bob ]
        | other -> failtestf "expected SeatTargetAbsent, got %A" other

      testCase "WHY - a caller with no seat in the cohort is refused" <| fun _ ->
        match plan (vacantWithBob ()) carol bob with
        | Error(Refusal.CallerNotPresent caller) -> caller |> Expect.equal "carol is no member" carol
        | other -> failtestf "expected CallerNotPresent, got %A" other

      testCase "WHY - a caller that left is no longer present, so it cannot ask" <| fun _ ->
        match plan (vacantWithBob ()) alice bob with
        | Error(Refusal.CallerNotPresent caller) -> caller |> Expect.equal "alice departed" alice
        | other -> failtestf "expected CallerNotPresent, got %A" other

      testCase "WHY - a caller under a member token never asks: no token may seat a conductor" <| fun _ ->
        plan (vacantWithBob ()) cap bob |> Expect.equal "refused before the cohort is read" (Error Refusal.CallerHoldsToken)

      testCase "WHY - a member under a token is never the one seated: the seat would be stranded" <| fun _ ->
        let state = next (vacantWithBob ()) (CohortCommand.Join(cap, JoinableRole.Implementer, None, machine))
        plan state bob cap |> Expect.equal "refused with the reason" (Error(Refusal.TargetHoldsToken cap))

      testCase "WHY - a seat that was never bound has nobody to seat" <| fun _ ->
        match plan (CohortState.empty ()) alice alice with
        | Error _ -> ()
        | Ok _ -> failtest "an empty cohort has no present member to put forward"
    ]

    testList "only a yes makes an approval, and the command is built from it" [

      testTask "WHY - a yes gives an approval, and its command is the seating of the member the plan named, in the plan's cohort" {
        let state = vacantWithBob ()
        let asked = Asked()
        let! outcome = askWith (personWho Answer.Yes asked) (planned state bob bob)
        match outcome with
        | Ok approval ->
          SeatingFlow.commandOf approval |> Expect.equal "the command" (CohortCommand.SeatConductor(bob, machine))
          let seated = next state (SeatingFlow.commandOf approval)
          seated.Conductor |> Expect.equal "and the cohort accepts it: bob is the conductor" (ConductorBinding.Bound bob)
        | Error refusal -> failtestf "a yes must approve, got %A" refusal
        asked.Calls |> Expect.equal "the person was asked exactly once" 1
      }

      testTask "WHY - the person is shown the question the caller wrote" {
        let asked = Asked()
        let! _ = askWith (personWho Answer.Yes asked) (planned (vacantWithBob ()) bob bob)
        asked.Question |> Expect.equal "verbatim" "seat bob?"
      }

      for (what, answer, expected) in answers do
        testTask (sprintf "WHY - %s is a refusal, never an approval" what) {
          let! outcome = askWith (personWho answer (Asked())) (planned (vacantWithBob ()) bob bob)
          match outcome with
          | Error refusal -> expected refusal |> Expect.isTrue (sprintf "the refusal names the case: %A" refusal)
          | Ok _ -> failtestf "%s must not approve" what
        }

      testTask "WHY - a client that cannot be asked is refused, and the question is never put (the opposite of the confirm tier)" {
        let asked = Asked()
        let silent = { personWho Answer.Yes asked with Elicitation = Elicitation.NotOffered }
        let! outcome = askWith silent (planned (vacantWithBob ()) bob bob)
        outcome |> Expect.equal "no elicitation means no seating" (Error Refusal.ClientCannotAsk)
        asked.Calls |> Expect.equal "nobody was asked" 0
      }

      testTask "WHY - a question that throws is a refusal that says so, never a yes" {
        let breaks = { Elicitation = Elicitation.Offered; Ask = fun _ _ -> Task.FromException<Answer>(InvalidOperationException "the transport closed") }
        let! outcome = askWith breaks (planned (vacantWithBob ()) bob bob)
        match outcome with
        | Error(Refusal.CouldNotBeAsked reason) -> reason |> Expect.stringContains "the cause is kept" "the transport closed"
        | other -> failtestf "expected CouldNotBeAsked, got %A" other
      }

      testTask "WHY - over every answer the person can give, with any wording, an approval comes out exactly when the answer is yes" {
        let p = planned (vacantWithBob ()) bob bob
        let wordings = [ ""; "reject"; "accept"; "yes"; "\u0000"; String('x', 500) ]
        let every =
          [ yield Answer.Yes
            yield Answer.Declined
            yield Answer.Cancelled
            for w in wordings do
              yield Answer.AnsweredOther w
              yield Answer.CouldNotBeAsked w
            for ticks in [ 0L; 1L; Int64.MaxValue ] do
              yield Answer.NoAnswerInTime(TimeSpan.FromTicks ticks) ]
        for answer in every do
          let! outcome = askWith (personWho answer (Asked())) p
          Result.isOk outcome |> Expect.equal (sprintf "%A" answer) (answer = Answer.Yes)
      }
    ]

    testList "an approval cannot be made any other way" [

      testCase "WHY - SeatApproval has no public constructor, so no caller outside the module can write one" <| fun _ ->
        let approval = typeof<SeatApproval>
        approval.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance) |> Expect.isEmpty "no public constructor"

      testCase "WHY - exactly one public function turns anything into a command, and it takes an approval" <| fun _ ->
        let flow = typeof<SeatApproval>.DeclaringType
        let commandBuilders =
          flow.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
          |> Array.filter (fun m -> m.ReturnType = typeof<CohortCommand<MemberId>>)
        commandBuilders |> Array.map (fun m -> m.Name) |> Expect.equal "only commandOf" [| "commandOf" |]
        commandBuilders.[0].GetParameters() |> Array.map (fun p -> p.ParameterType) |> Expect.equal "from an approval" [| typeof<SeatApproval> |]

      testCase "WHY - the tool's body commits only a command that came out of commandOf" <| fun _ ->
        let text = IO.File.ReadAllText(IO.Path.Combine(RepoPaths.repoPathFull [||], "SageFs", "McpSeating.fs"))
        text.Contains "CohortCommand.SeatConductor" |> Expect.isFalse "the tool file never writes the command itself"
        text.Contains "commitCohort ctx (SeatingFlow.commandOf approval)" |> Expect.isTrue "it commits the approval's command"
        let commitSites = text.Split("commitCohort").Length - 1
        commitSites |> Expect.equal "and there is one commit in the file" 1
    ]
  ]
