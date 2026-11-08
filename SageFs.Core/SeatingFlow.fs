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

  /// The person at the client: whether the client offered elicitation at all, and the one call that reaches them.
  type Person =
    { CanAsk: bool
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

  /// What would be done, and what the person is told about it. Holds no authority: it is a description.
  type Plan =
    { Caller: MemberId
      Target: MemberId
      TargetRole: JoinableRole
      Scope: CohortScope
      /// The seat as it stands, and why it is empty.
      Seat: ConductorBinding<MemberId> }

  /// The person's yes. Only `ask` makes one, and `commandOf` is the only function that turns it into a command.
  type SeatApproval = private { Target: MemberId; Scope: CohortScope }

  let private isToken (who: MemberId) =
    match who with
    | MemberId.Capability _ -> true
    | MemberId.Browser _
    | MemberId.Mcp _
    | MemberId.Minted _
    | MemberId.Operator -> false

  /// Check the seating against the cohort's rules, before anybody is asked. A caller under a token is refused first, then a
  /// target under one, then a caller with no seat, and the rest is `Cohort.decide`'s own verdict on the seating (run against a
  /// copy of the state and thrown away: nothing is committed here).
  let plan (now: DateTime) (state: CohortState<MemberId>) (scope: CohortScope) (caller: MemberId) (target: MemberId) : Result<Plan, Refusal> =
    match isToken caller, isToken target with
    | true, _ -> Result.Error Refusal.CallerHoldsToken
    | false, true -> Result.Error(Refusal.TargetHoldsToken target)
    | false, false ->
      match Map.tryFind caller state.Members with
      | Some { Presence = MemberPresence.Present } ->
        match decide now [||] state (CohortCommand.SeatConductor(target, scope)) with
        | Result.Error error -> Result.Error(Refusal.CohortRefused error)
        | Result.Ok _ ->
          Result.Ok
            { Caller = caller
              Target = target
              TargetRole = state.Members.[target].Role
              Scope = scope
              Seat = state.Conductor }
      | Some { Presence = MemberPresence.Departed _ }
      | None -> Result.Error(Refusal.CallerNotPresent caller)

  /// Put the question to the person and turn their answer into an approval, or a refusal. A client that never offered
  /// elicitation is refused without a question. A question that throws is a refusal that keeps the cause. The one place a
  /// `SeatApproval` is made, and only for an explicit yes.
  let ask (person: Person) (question: string) (plan: Plan) : Task<Result<SeatApproval, Refusal>> =
    task {
      match person.CanAsk with
      | false -> return Result.Error Refusal.ClientCannotAsk
      | true ->
        let! answer =
          task {
            try
              return! person.Ask question CancellationToken.None
            with ex when not (ex :? OperationCanceledException) ->
              return Answer.CouldNotBeAsked ex.Message
          }
        match answer with
        | Answer.Yes -> return Result.Ok { Target = plan.Target; Scope = plan.Scope }
        | Answer.Declined -> return Result.Error Refusal.Declined
        | Answer.Cancelled -> return Result.Error Refusal.Cancelled
        | Answer.AnsweredOther action -> return Result.Error(Refusal.AnsweredOther action)
        | Answer.NoAnswerInTime waited -> return Result.Error(Refusal.NoAnswerInTime waited)
        | Answer.CouldNotBeAsked reason -> return Result.Error(Refusal.CouldNotBeAsked reason)
    }

  /// The command an approval stands for. The only function that takes one.
  let commandOf (approval: SeatApproval) : CohortCommand<MemberId> = CohortCommand.SeatConductor(approval.Target, approval.Scope)
