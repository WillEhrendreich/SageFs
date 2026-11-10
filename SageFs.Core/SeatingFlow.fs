namespace SageFs

open System
open System.Threading
open System.Threading.Tasks
open SageFs.Cohort
open SageFs.MemberTable

/// A seated member asks the daemon to fill an empty conductor seat, and a PERSON answers.
///
/// The seat is the authority to mint tokens, configure the integration and reassign claims, and an MCP tool is callable by
/// any agent. So the tool never seats anybody on its own say-so: the daemon asks the person through MCP elicitation, and
/// acts only on an explicit yes. The command is built from a `SeatApproval`, and the one place that makes an approval is
/// `ask`, after the person said yes. A decline, a dismissal, silence, a failed question and a client that cannot be asked
/// are all refusals: for this tool a skipped prompt would defeat its purpose, which is the opposite of the confirm tier
/// (a client that cannot elicit passes it).
///
/// The rules of who may be seated are `Cohort.decide`'s (a Vacant or NeverBound seat, a present member). `plan` runs them
/// against the cohort's state BEFORE a person is asked, so nobody is asked a question the cohort would refuse.
///
/// A seat whose holder's connection is gone stays bound until its lease lapses. `planReplace` is the same question for that
/// case: the caller names the holder, the person is shown it, and the approval carries it, so the command is
/// `ReplaceConductor` of exactly the holder the person saw. The cohort compares that holder with the seat when the command
/// commits, so a seat that changed hands while the person read the question refuses, naming the new holder.
module SeatingFlow =

  /// What the person at the client said, as the cohort needs to tell the cases apart.
  [<RequireQualifiedAccess>]
  type Answer =
    | Yes
    | Declined
    | Cancelled
    | AnsweredOther of action: string
    | NoAnswerInTime of waited: TimeSpan
    | CouldNotBeAsked of reason: string

  /// Whether the client offered MCP elicitation at all.
  [<RequireQualifiedAccess>]
  type Elicitation =
    | Offered
    | NotOffered

  /// The person at the client: whether the client offered elicitation at all, and the one call that reaches them.
  type Person =
    { Elicitation: Elicitation
      Ask: string -> CancellationToken -> Task<Answer> }

  /// Why a seating did not happen. A closed set with one `describe` in the adapter, so a test matches a case.
  [<RequireQualifiedAccess>]
  type Refusal =
    /// The caller presents a member token. No token may seat a conductor.
    | CallerHoldsToken
    /// The caller has no seat in this cohort.
    | CallerNotPresent of caller: MemberId
    /// The member to seat acts under a member token: no token may call the conductor's tools, so the seat would be stranded.
    | TargetHoldsToken of target: MemberId
    /// The cohort's own rules refused it (the seat is held, the member is not present, wrong cohort).
    | CohortRefused of CohortError<MemberId>
    /// The client never offered elicitation, so nobody can be asked.
    | ClientCannotAsk
    | Declined
    | Cancelled
    | AnsweredOther of action: string
    | NoAnswerInTime of waited: TimeSpan
    | CouldNotBeAsked of reason: string

  /// What the seating does to the seat: fills an empty one, or takes a bound one from the holder the person is shown.
  /// `lastRenewal` is when that holder's lease was last renewed, which is how long it has been silent.
  [<RequireQualifiedAccess>]
  type Mode =
    | Fill
    | Replace of holder: MemberId * lastRenewal: DateTime

  /// What would be done, and what the person is told about it. Holds no authority: it is a description.
  type Plan =
    { Caller: MemberId
      Target: MemberId
      TargetRole: JoinableRole
      Scope: CohortScope
      /// The seat as it stands, and why it is empty.
      Seat: ConductorBinding<MemberId>
      Mode: Mode }

  /// The person's yes. Only `ask` makes one, and `commandOf` is the only function that turns it into a command. `Replacing` is
  /// the holder the person was shown, when the question was about replacing one.
  type SeatApproval = private { Target: MemberId; Scope: CohortScope; Replacing: MemberId option }

  let private isToken (who: MemberId) =
    match who with
    | MemberId.Capability _ -> true
    | MemberId.Browser _
    | MemberId.Mcp _
    | MemberId.Minted _
    | MemberId.Operator -> false

  /// The checks both seatings share. A caller under a token is refused first, then a target under one, then a caller with no
  /// seat, and the rest is `Cohort.decide`'s own verdict on `command` (run against a copy of the state and thrown away:
  /// nothing is committed here). `modeOf` reads the plan's mode off the state the verdict was reached on.
  let planFor
    (now: DateTime)
    (state: CohortState<MemberId>)
    (scope: CohortScope)
    (caller: MemberId)
    (target: MemberId)
    (command: CohortCommand<MemberId>)
    (modeOf: CohortState<MemberId> -> Mode)
    : Result<Plan, Refusal> =
    match isToken caller, isToken target with
    | true, _ -> Result.Error Refusal.CallerHoldsToken
    | false, true -> Result.Error(Refusal.TargetHoldsToken target)
    | false, false ->
      match Map.tryFind caller state.Members with
      | Some { Presence = MemberPresence.Present } ->
        match decide now [||] state command with
        | Result.Error error -> Result.Error(Refusal.CohortRefused error)
        | Result.Ok _ ->
          Result.Ok
            { Caller = caller
              Target = target
              TargetRole = state.Members.[target].Role
              Scope = scope
              Seat = state.Conductor
              Mode = modeOf state }
      | Some { Presence = MemberPresence.Departed _ }
      | None -> Result.Error(Refusal.CallerNotPresent caller)

  /// Check the filling of an EMPTY seat against the cohort's rules, before anybody is asked.
  let plan (now: DateTime) (state: CohortState<MemberId>) (scope: CohortScope) (caller: MemberId) (target: MemberId) : Result<Plan, Refusal> =
    planFor now state scope caller target (CohortCommand.SeatConductor(target, scope)) (fun _ -> Mode.Fill)

  /// Check the REPLACEMENT of `holder` against the cohort's rules, before anybody is asked: the seat must still be bound to
  /// `holder`, and `target` must be someone else who is present. A holder that is not the holder is refused with the seat as
  /// it stands, so the caller learns who holds it. The plan carries when the holder last renewed its lease, for the question.
  let planReplace
    (now: DateTime)
    (state: CohortState<MemberId>)
    (scope: CohortScope)
    (caller: MemberId)
    (target: MemberId)
    (holder: MemberId)
    : Result<Plan, Refusal> =
    planFor
      now
      state
      scope
      caller
      target
      (CohortCommand.ReplaceConductor(holder, target, scope))
      (fun reached -> Mode.Replace(holder, reached.Members.[holder].LastRenewal))

  /// Put the question to the person and turn their answer into an approval, or a refusal. A client that never offered
  /// elicitation is refused without a question. A question that throws is a refusal that keeps the cause. The one place a
  /// `SeatApproval` is made, and only for an explicit yes.
  let ask (person: Person) (question: string) (plan: Plan) : Task<Result<SeatApproval, Refusal>> =
    task {
      match person.Elicitation with
      | Elicitation.NotOffered -> return Result.Error Refusal.ClientCannotAsk
      | Elicitation.Offered ->
        let! answer =
          task {
            try
              return! person.Ask question CancellationToken.None
            with ex when not (ex :? OperationCanceledException) ->
              return Answer.CouldNotBeAsked ex.Message
          }
        match answer with
        | Answer.Yes ->
          let replacing =
            match plan.Mode with
            | Mode.Fill -> None
            | Mode.Replace(holder, _) -> Some holder
          return Result.Ok { Target = plan.Target; Scope = plan.Scope; Replacing = replacing }
        | Answer.Declined -> return Result.Error Refusal.Declined
        | Answer.Cancelled -> return Result.Error Refusal.Cancelled
        | Answer.AnsweredOther action -> return Result.Error(Refusal.AnsweredOther action)
        | Answer.NoAnswerInTime waited -> return Result.Error(Refusal.NoAnswerInTime waited)
        | Answer.CouldNotBeAsked reason -> return Result.Error(Refusal.CouldNotBeAsked reason)
    }

  /// The command an approval stands for. The only function that takes one.
  let commandOf (approval: SeatApproval) : CohortCommand<MemberId> =
    match approval.Replacing with
    | None -> CohortCommand.SeatConductor(approval.Target, approval.Scope)
    | Some holder -> CohortCommand.ReplaceConductor(holder, approval.Target, approval.Scope)
