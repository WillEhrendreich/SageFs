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
/// It also asks to REPLACE the holder of a bound seat (`AskToReplace`), and while the person reads that question the holder may
/// hand the seat to someone else. What it proves there: a bound seat changes hands only on a yes and only from the holder the
/// person was shown, so an answer that comes back over a seat that changed hands moves nothing.
///
/// `Real` is the product. `SkipsTheQuestionTwin` seats on the plan alone (the tool as an agent-callable shortcut) and
/// `IgnoresTheAnswerTwin` asks and then seats whatever came back: the invariants must fail against both.
/// `ReplacesWhoeverHoldsNowTwin` takes the yes and then replaces whoever holds the seat when the answer comes: it must break
/// the holder-shown invariant.
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
    /// `caller` asks for `target` to replace `holder` (a member index, `currentHolder` for whoever holds the seat when the
    /// question is put, or -1 for a member under a token). While the person reads the question the seat's holder, when
    /// there is one, hands it to member `intervening` (when given), so the answer comes back over a seat that changed hands.
    | AskToReplace of caller: int * target: int * holder: int * reply: Reply * intervening: int option

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    | SkipsTheQuestionTwin
    | IgnoresTheAnswerTwin
    /// Asks and takes a yes, then replaces whoever holds the seat WHEN THE ANSWER COMES and not the holder the person was
    /// shown. The compare-and-swap on the holder is what the real flow keeps.
    | ReplacesWhoeverHoldsNowTwin

  /// What one `Ask` or `AskToReplace` did, for the invariants.
  type Attempt =
    { Caller: MemberId
      Target: MemberId
      /// The cohort when the question was planned.
      Before: CohortState<MemberId>
      /// The cohort when the answer came and the command was committed: `Before` unless the seat changed hands while the
      /// person read the question.
      AtCommit: CohortState<MemberId>
      /// The holder the person was shown, for a replacement. None for a seating of an empty seat.
      Shown: MemberId option
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

  /// The member index that means "whoever holds the seat when the question is put".
  [<Literal>]
  let currentHolder = -2

  /// What the caller asks for: an empty seat filled, or the holder it names replaced.
  [<RequireQualifiedAccess>]
  type private Request =
    | Fill
    | Replace of holder: MemberId

  let private attempt
    (behavior: Behavior)
    (state: State)
    (callerIndex: int)
    (targetIndex: int)
    (request: Request)
    (reply: Reply)
    (intervening: int option)
    : State =
    let caller = memberOf callerIndex
    let target = memberOf targetIndex
    let asked = ref 0
    // The cohort as it is at any moment: the question is put against `state.Cohort`, and the holder may hand the seat on while
    // the person reads it.
    let current = ref state.Cohort
    let whileReading () =
      match intervening, current.Value.Conductor with
      | Some index, ConductorBinding.Bound holder -> current.Value <- decideOrKeep state.Clock current.Value (CohortCommand.DelegateConductor(holder, memberOf index, machine))
      | _ -> ()
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
            whileReading ()
            Task.FromResult(answerOf reply) }
    let commit (command: CohortCommand<MemberId>) =
      match decide state.Clock entropy current.Value command with
      | Result.Ok(next, _, _) -> next, true
      | Result.Error _ -> current.Value, false
    let planned =
      match request with
      | Request.Fill -> SeatingFlow.plan state.Clock state.Cohort machine caller target
      | Request.Replace holder -> SeatingFlow.planReplace state.Clock state.Cohort machine caller target holder
    let shown =
      match request with
      | Request.Fill -> None
      | Request.Replace holder -> Some holder
    let skipsTheQuestion (p: Plan) : CohortCommand<MemberId> =
      match p.Mode with
      | Mode.Fill -> CohortCommand.SeatConductor(p.Target, p.Scope)
      | Mode.Replace(holder, _) -> CohortCommand.ReplaceConductor(holder, p.Target, p.Scope)
    let asks (p: Plan) = (SeatingFlow.ask person "seat them?" p).GetAwaiter().GetResult()
    let cohort, seated =
      match planned with
      | Result.Error _ -> state.Cohort, false
      | Result.Ok p ->
        match behavior with
        | Behavior.Real ->
          match asks p with
          | Result.Ok approval -> commit (SeatingFlow.commandOf approval)
          | Result.Error _ -> current.Value, false
        | Behavior.SkipsTheQuestionTwin -> commit (skipsTheQuestion p)
        | Behavior.IgnoresTheAnswerTwin ->
          asks p |> ignore
          commit (skipsTheQuestion p)
        | Behavior.ReplacesWhoeverHoldsNowTwin ->
          match asks p with
          | Result.Ok _ ->
            match p.Mode, current.Value.Conductor with
            | Mode.Replace _, ConductorBinding.Bound now -> commit (CohortCommand.ReplaceConductor(now, p.Target, p.Scope))
            | Mode.Replace _, (ConductorBinding.Vacant _ | ConductorBinding.NeverBound)
            | Mode.Fill, _ -> commit (skipsTheQuestion p)
          | Result.Error _ -> current.Value, false
    { state with
        Cohort = cohort
        Attempt =
          Some
            { Caller = caller
              Target = target
              Before = state.Cohort
              AtCommit = current.Value
              Shown = shown
              Asked = asked.Value
              Reply = reply
              Seated = seated } }

  let private apply (behavior: Behavior) (state: State) (event: SimEvent) : State =
    let stepped = { state with Attempt = None; Step = state.Step + 1 }
    match event with
    | SimEvent.Join index ->
      { stepped with Cohort = decideOrKeep stepped.Clock stepped.Cohort (CohortCommand.Join(memberOf index, JoinableRole.Implementer, None, machine)) }
    | SimEvent.Leave index -> { stepped with Cohort = decideOrKeep stepped.Clock stepped.Cohort (CohortCommand.Depart(memberOf index, machine)) }
    | SimEvent.Lapse ->
      let clock = stepped.Clock + Cohort.leaseWindow + Cohort.leaseWindow
      { stepped with Clock = clock; Cohort = decideOrKeep clock stepped.Cohort (CohortCommand.Tick machine) }
    | SimEvent.Ask(caller, target, reply) -> attempt behavior stepped caller target Request.Fill reply None
    | SimEvent.AskToReplace(caller, target, holder, reply, intervening) ->
      let named =
        match holder, stepped.Cohort.Conductor with
        | h, ConductorBinding.Bound sitting when h = currentHolder -> sitting
        | h, (ConductorBinding.Vacant _ | ConductorBinding.NeverBound) when h = currentHolder -> memberOf 0
        | h, _ -> memberOf h
      attempt behavior stepped caller target (Request.Replace named) reply intervening

  /// Fold the events; the state after each one.
  let run (behavior: Behavior) (events: SimEvent list) : State list =
    events |> List.scan (apply behavior) initial |> List.tail

  let members = 4

  let private replies = [| Reply.Yes; Reply.No; Reply.Dismiss; Reply.OtherAction; Reply.Silence; Reply.Broken; Reply.NotThere |]

  /// A seeded lifecycle over four members and one token holder.
  let scenarioOf (seed: int) (steps: int) : SimEvent list =
    let rnd = Random(seed)
    let pick () = match rnd.Next 8 with 0 -> -1 | _ -> rnd.Next members
    // Half the answers are a yes, so a replacement is reached often enough to be a property of the run and not of luck.
    let replyFor () = match rnd.Next 2 with 0 -> Reply.Yes | _ -> replies.[rnd.Next replies.Length]
    [ for _ in 1 .. steps do
        match rnd.Next 16 with
        // One member in eight is the one under a token, whether it joins, leaves, asks or is named.
        | 0 | 1 | 2 -> yield SimEvent.Join(pick ())
        | 3 | 4 -> yield SimEvent.Leave(pick ())
        // A lapse departs everyone who did not renew, so it is one in three of these, and the rest rejoin.
        | 5 ->
          match rnd.Next 3 with
          | 0 -> yield SimEvent.Lapse
          | _ -> yield SimEvent.Join(rnd.Next members)
        | 6 | 7 | 8 -> yield SimEvent.Ask(pick (), pick (), replyFor ())
        | _ ->
          // Most replacements name the holder the seat has; one in three names a member at random, who is the holder only by
          // chance. One in three has the holder hand the seat on while the person reads.
          let holder = match rnd.Next 3 with 0 -> pick () | _ -> currentHolder
          let intervening = match rnd.Next 3 with 0 -> Some(rnd.Next members) | _ -> None
          yield SimEvent.AskToReplace(pick (), pick (), holder, replyFor (), intervening) ]
