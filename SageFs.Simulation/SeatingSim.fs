namespace SageFs.Simulation

open System
open System.Threading.Tasks
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.SeatingFlow

/// Deterministic simulation of members joining, leaving and asking for an empty conductor seat to be filled, with a person
/// who answers yes, no, nothing, or is not there, folded through the REAL `SeatingFlow` (`plan`, `ask`, `commandOf`) and the
/// real `Cohort.decide`.
///
/// What it proves: the seat changes hands only when the person said an explicit yes to a question that was put to them, nobody
/// is asked about a seating the cohort would refuse, and a member under a token neither asks nor is seated.
///
/// `Real` is the product. `SkipsTheQuestionTwin` seats on the plan alone (the tool as an agent-callable shortcut) and
/// `IgnoresTheAnswerTwin` asks and then seats whatever came back: the invariants must fail against both.
module SeatingSim =

  /// What the person at the client does when asked.
  [<RequireQualifiedAccess>]
  type Reply =
    | Yes
    | No
    | Dismiss
    | OtherAction
    | Silence
    | Broken
    /// The client never offered elicitation.
    | NotThere

  [<RequireQualifiedAccess>]
  type SimEvent =
    | Join of member': int
    | Leave of member': int
    /// The cohort's lease clock passes and the reaper ticks.
    | Lapse
    /// `caller` (a member index, or -1 for a member under a token) asks for `target` to be seated, and the person replies.
    | Ask of caller: int * target: int * reply: Reply

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    | SkipsTheQuestionTwin
    | IgnoresTheAnswerTwin

  /// What one `Ask` did, for the invariants.
  type Attempt =
    { Caller: MemberId
      Target: MemberId
      Before: CohortState<MemberId>
      /// How many times the person was asked during this event.
      Asked: int
      Reply: Reply
      Seated: bool }

  type State =
    { Cohort: CohortState<MemberId>
      Clock: DateTime
      /// Set by an `Ask` event, empty for the others.
      Attempt: Attempt option
      Step: int }

  let private machine = CohortScope.Machine

  let private entropy : Entropy = [| 1uy |]

  let private t0 = DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)

  /// The one token holder the sim knows: a member that joined under a token.
  let tokenMember : MemberId = MemberId.Capability "0123456789abcdef"

  let memberOf (index: int) : MemberId =
    match index with
    | -1 -> tokenMember
    | i -> MemberId.Minted(sprintf "m%d" i)

  let private initial : State = { Cohort = CohortState.empty (); Clock = t0; Attempt = None; Step = 0 }

  let private decideOrKeep (clock: DateTime) (cohort: CohortState<MemberId>) (command: CohortCommand<MemberId>) =
    match decide clock entropy cohort command with
    | Result.Ok(next, _, _) -> next
    | Result.Error _ -> cohort

  let private answerOf (reply: Reply) : Answer =
    match reply with
    | Reply.Yes -> Answer.Yes
    | Reply.No -> Answer.Declined
    | Reply.Dismiss -> Answer.Cancelled
    | Reply.OtherAction -> Answer.AnsweredOther "reject"
    | Reply.Silence -> Answer.NoAnswerInTime Timeouts.confirmationAnswer
    | Reply.Broken -> Answer.CouldNotBeAsked "the transport closed"
    | Reply.NotThere -> Answer.CouldNotBeAsked "not there"

  let private attempt (behavior: Behavior) (state: State) (callerIndex: int) (targetIndex: int) (reply: Reply) : State =
    let caller = memberOf callerIndex
    let target = memberOf targetIndex
    let asked = ref 0
    let person : Person =
      { Elicitation =
          (match reply with
           | Reply.NotThere -> Elicitation.NotOffered
           | Reply.Yes
           | Reply.No
           | Reply.Dismiss
           | Reply.OtherAction
           | Reply.Silence
           | Reply.Broken -> Elicitation.Offered)
        Ask =
          fun _ _ ->
            asked.Value <- asked.Value + 1
            Task.FromResult(answerOf reply) }
    let commit (command: CohortCommand<MemberId>) =
      match decide state.Clock entropy state.Cohort command with
      | Result.Ok(next, _, _) -> next, true
      | Result.Error _ -> state.Cohort, false
    let cohort, seated =
      match SeatingFlow.plan state.Clock state.Cohort machine caller target with
      | Result.Error _ -> state.Cohort, false
      | Result.Ok planned ->
        match behavior with
        | Behavior.Real ->
          match (SeatingFlow.ask person "seat them?" planned).GetAwaiter().GetResult() with
          | Result.Ok approval -> commit (SeatingFlow.commandOf approval)
          | Result.Error _ -> state.Cohort, false
        | Behavior.SkipsTheQuestionTwin -> commit (CohortCommand.SeatConductor(planned.Target, planned.Scope))
        | Behavior.IgnoresTheAnswerTwin ->
          (SeatingFlow.ask person "seat them?" planned).GetAwaiter().GetResult() |> ignore
          commit (CohortCommand.SeatConductor(planned.Target, planned.Scope))
    { state with
        Cohort = cohort
        Attempt = Some { Caller = caller; Target = target; Before = state.Cohort; Asked = asked.Value; Reply = reply; Seated = seated } }

  let private apply (behavior: Behavior) (state: State) (event: SimEvent) : State =
    let stepped = { state with Attempt = None; Step = state.Step + 1 }
    match event with
    | SimEvent.Join index ->
      { stepped with Cohort = decideOrKeep stepped.Clock stepped.Cohort (CohortCommand.Join(memberOf index, JoinableRole.Implementer, None, machine)) }
    | SimEvent.Leave index -> { stepped with Cohort = decideOrKeep stepped.Clock stepped.Cohort (CohortCommand.Depart(memberOf index, machine)) }
    | SimEvent.Lapse ->
      let clock = stepped.Clock + Cohort.leaseWindow + Cohort.leaseWindow
      { stepped with Clock = clock; Cohort = decideOrKeep clock stepped.Cohort (CohortCommand.Tick machine) }
    | SimEvent.Ask(caller, target, reply) -> attempt behavior stepped caller target reply

  /// Fold the events; the state after each one.
  let run (behavior: Behavior) (events: SimEvent list) : State list =
    events |> List.scan (apply behavior) initial |> List.tail

  let members = 4

  let private replies = [| Reply.Yes; Reply.No; Reply.Dismiss; Reply.OtherAction; Reply.Silence; Reply.Broken; Reply.NotThere |]

  /// A seeded lifecycle over four members and one token holder.
  let scenarioOf (seed: int) (steps: int) : SimEvent list =
    let rnd = Random(seed)
    let pick () = match rnd.Next 8 with 0 -> -1 | _ -> rnd.Next members
    [ for _ in 1 .. steps do
        match rnd.Next 10 with
        // One member in eight is the one under a token, whether it joins, leaves, asks or is named.
        | 0 | 1 -> yield SimEvent.Join(pick ())
        | 2 | 3 -> yield SimEvent.Leave(pick ())
        | 4 -> yield SimEvent.Lapse
        | _ -> yield SimEvent.Ask(pick (), pick (), replies.[rnd.Next replies.Length]) ]
