namespace SageFs

open System.Threading
open System.Threading.Tasks
open SageFs.McpTools
open SageFs.SeatingFlow

/// `seat_conductor`: a seated member asks the daemon to fill an empty conductor seat, and the daemon asks the PERSON.
///
/// The decision of what may be done is `SeatingFlow` (pure, in Core, with its simulation); this is the edge: which
/// person is at the client of this call, how the question reads, what each refusal tells the caller, and the one call that
/// commits. The elicitation itself is `McpServer.fs`'s (it holds the SDK's server), handed over as a `Person`.
///
/// Fail closed. The confirm tier lets a client that cannot elicit through, because a missing prompt there only costs a
/// click. Here the prompt IS the permission, so a client that cannot be asked, an unanswered question and a decline all
/// refuse, and the refusal names the routes a person can use instead: `sagefs cohort seat` from a shell on the machine
/// that runs the daemon, and the Seat button on the dashboard's cohort panel.
module McpSeating =

  /// The person at the client of the call being served, bound by the request filter beside the identity it binds. None
  /// outside any bound connection (a direct in-process call, a unit test that did not set one), which is a client that
  /// cannot be asked.
  let currentPerson = new AsyncLocal<Person option>()

  /// The routes that need no question put to an agent's client.
  let otherRoutes : string =
    sprintf
      "A person can seat a member without an agent asking: '%s' from a shell on the machine that runs the daemon, or the Seat button on the dashboard's cohort panel."
      Features.CohortStatusText.seatCommandText

  let private nothingDone = "Nothing was done: the conductor seat is as it was."

  let private failed (reason: string) (suggestion: string) : SageFsError = SageFsError.CohortActionFailed(reason, suggestion)

  /// What each refusal says and what to do. Exhaustive: a new refusal cannot be added without writing the caller's sentence.
  let describe (refusal: Refusal) : SageFsError =
    match refusal with
    | Refusal.CallerHoldsToken ->
      failed
        "seat_conductor is not callable under a member token: the seat is the authority to mint tokens, and a token cannot hand itself that."
        otherRoutes
    | Refusal.CallerNotPresent caller ->
      failed
        (sprintf "%s has no seat in this cohort, and only a seated member can ask for the conductor seat to be filled." (MemberTable.MemberId.display caller))
        "Call join_cohort with the SAME working_directory you pass here, then ask again."
    | Refusal.TargetHoldsToken target -> CohortErrorMapping.seatTargetHoldsToken target
    | Refusal.CohortRefused error -> CohortErrorMapping.toSageFsError error
    | Refusal.ClientCannotAsk ->
      failed
        (sprintf "seat_conductor needs a person to say yes, and this client does not offer MCP elicitation, so nobody can be asked. %s" nothingDone)
        otherRoutes
    | Refusal.Declined ->
      failed (sprintf "The person at the client said no. %s" nothingDone) (sprintf "Do not ask again unless they tell you to. %s" otherRoutes)
    | Refusal.Cancelled ->
      failed (sprintf "The question was dismissed before an answer came. %s" nothingDone) otherRoutes
    | Refusal.AnsweredOther action ->
      failed (sprintf "The client answered '%s', which is not a yes. %s" action nothingDone) otherRoutes
    | Refusal.NoAnswerInTime waited ->
      failed (sprintf "No person answered within %O. %s" waited nothingDone) otherRoutes
    | Refusal.CouldNotBeAsked reason ->
      failed (sprintf "The question could not be asked (%s). %s" reason nothingDone) otherRoutes

  /// The question the person reads: the repository, who is asking, who would sit, in what role, who held the seat and why it
  /// is empty (or, for a replacement, who holds it and how long it has been silent), and what the seat is for. `now` is the
  /// clock the holder's silence is measured against.
  let questionText (now: System.DateTime) (plan: Plan) : string =
    let asking =
      match plan.Caller = plan.Target with
      | true -> sprintf "%s asks to be seated" (MemberTable.MemberId.display plan.Caller)
      | false -> sprintf "%s asks to seat %s" (MemberTable.MemberId.display plan.Caller) (MemberTable.MemberId.display plan.Target)
    let seatSentence, closing =
      match plan.Mode with
      | Mode.Fill -> sprintf "The seat is %s." (CohortSeatRoute.bindingText plan.Seat), "Seat them?"
      | Mode.Replace(holder, lastRenewal) ->
        let name = MemberTable.MemberId.display holder
        let standing =
          match Features.CohortStatusText.HolderSilence.ofRenewal lastRenewal now with
          | Features.CohortStatusText.HolderSilence.SilentSince _ as silence ->
            sprintf "%s has been %s" name (Features.CohortStatusText.HolderSilence.text now silence)
          | Features.CohortStatusText.HolderSilence.NotSilent ->
            sprintf "%s renewed its lease %d min ago, so it may still be connected" name (int (now - lastRenewal).TotalMinutes)
        sprintf "The seat is held by %s, who would be replaced. %s. %s stays a member and keeps its claims." name standing name,
        sprintf "Replace %s?" name
    sprintf
      "%s (role %A) as the conductor of the cohort of %s. %s The conductor mints member tokens, configures the integration and reassigns claims. %s"
      asking
      plan.TargetRole
      (Scope.label plan.Scope)
      seatSentence
      closing

  /// Ask for the seat to be filled, or its holder replaced. `memberName` is a member id as `get_cohort_status` prints it;
  /// blank means the caller. `replaces` names the holder to replace in the same form: blank asks to fill an empty seat, a name
  /// asks to replace that holder, and the cohort refuses it when that member is no longer the holder.
  let run
    (ctx: McpContext)
    (agentName: string)
    (memberName: string)
    (replaces: string)
    (workingDirectory: string option)
    : Task<Result<string, SageFsError>> =
    task {
      match currentCapability.Value with
      | Some _ -> return Result.Error(describe Refusal.CallerHoldsToken)
      | None ->
        match cohortOwnerFor ctx workingDirectory with
        | None -> return Result.Error(SageFsError.SessionCreationFailed "no cohort owner is configured for this daemon")
        | Some owner ->
          let caller = memberIdFor agentName
          let frame = owner.ReadFrame()
          let scope =
            match workingDirectory with
            | Some _ -> ScopeOf.ofWorkingDirectory workingDirectory
            | None -> frame.Scope
          let target =
            match System.String.IsNullOrWhiteSpace memberName with
            | true -> caller
            | false -> CohortSeatRoute.resolveTarget frame (memberName.Trim())
          let now = System.DateTime.UtcNow
          let planned =
            match System.String.IsNullOrWhiteSpace replaces with
            | true -> SeatingFlow.plan now (owner.ReadCohortState()) scope caller target
            | false -> SeatingFlow.planReplace now (owner.ReadCohortState()) scope caller target (CohortSeatRoute.resolveTarget frame (replaces.Trim()))
          match planned with
          | Result.Error refusal -> return Result.Error(describe refusal)
          | Result.Ok plan ->
            let person =
              match currentPerson.Value with
              | Some person -> person
              | None -> { Elicitation = Elicitation.NotOffered; Ask = fun _ _ -> Task.FromResult(Answer.CouldNotBeAsked "no client is bound to this call") }
            match! SeatingFlow.ask person (questionText now plan) plan with
            | Result.Error refusal -> return Result.Error(describe refusal)
            | Result.Ok approval ->
              let! committed = commitCohort ctx (SeatingFlow.commandOf approval)
              return
                committed
                |> Result.map (fun (events, _) ->
                  let reply = CohortSeatRoute.seatedReply (owner.ReadFrame()) events
                  // The daemon knows the client accepted. It cannot know that a person did the accepting.
                  sprintf
                    "%s is the conductor now (ended: %s). The client at %s accepted the question."
                    reply.Seated
                    reply.Ended
                    (MemberTable.MemberId.display caller))
    }
