namespace SageFs.Simulation

open System.Collections.Generic
open SageFs
open SageFs.Cohort

/// The exhaustive proof for the four cohort commands that no tool used to issue and that now have tools:
/// `DelegateConductor`, `WithdrawLanding`, `VetoLanding`, `ResolveVeto`.
///
/// `CohortSpec.fs` proves the landing pipeline over two Implementers; it models `ResolveVeto` and a veto by one
/// fixed member, and it says in its own coverage note that it does not model `WithdrawLanding` or
/// `DelegateConductor` at all. Adding five actors and every target to its alphabet would multiply its 350k-node
/// exploration out of reach, so these four commands get their own bounded space, sized to close in seconds:
///
///   * FIVE actors: `a` (the first joiner, so the conductor), `b` (an implementer), `v` (a verifier), `o` (an
///     observer) and `z`, who never joins. Every one of them is offered every one of the four commands against
///     every landing and every target, so "who may" is explored as well as "what happens".
///   * Two landings at most (one per requester, ids minted from the requester's entropy), driven through the
///     pipeline by the completions of the single pending effect, so a landing can be live, vetoed, withdrawn or
///     landed when a command meets it.
///   * `Depart` and `Join` for the four seated members, so the conductor seat can empty and a delegate's target
///     can have left.
///
/// A rule here is of two kinds. A STATE rule holds at every reachable state. A STEP rule holds of every
/// (state, command, result) the explorer takes, including the REFUSED ones, because "a veto after the landing
/// landed is a refusal and not a silent no-op" is a statement about the result of a step and cannot be read off
/// any state. The explorer therefore keeps refused steps for the step rules, and only the accepted ones expand
/// the frontier.
///
/// Every rule has a TWIN: the real `decide` with that one rule removed. A rule no broken world can violate proves
/// nothing, so each twin is explored through the same space and must be caught by the rule it was built for.
/// `at-most-one-conductor` is the one rule without a twin of its own, for a reason worth stating: the seat is ONE
/// `ConductorBinding` value, so a state with two conductors cannot be written. The rule that gives it teeth is
/// `HANDOFF-LEAVES-ONE-CONDUCTOR`, which reads the `ConductorDelegated` event against the state after it and is
/// violated by the twin that announces a handoff and leaves the old holder seated.
module CohortVetoSpec =

  type Member = string

  type Decide = CohortSpec.Decide

  type Result' = Result<CohortState<Member> * CohortEvent<Member> list * CohortEffect<Member> list, CohortError<Member>>

  /// One step the explorer took: where it started, what it issued, and what the reducer said.
  type Step =
    { Before: CohortState<Member>
      Command: CohortCommand<Member>
      Outcome: Result' }

  // ── The alphabet ──────────────────────────────────────────────────────────

  let cohortScope = CohortScope.Machine
  let clock : Clock = System.DateTime(2020, 1, 1)

  let conductorSeat : Member = "a"
  let implementer : Member = "b"
  let verifier : Member = "v"
  let observer : Member = "o"
  /// Never joins: the member the cohort has never heard of.
  let stranger : Member = "z"

  let seated : Member list = [ conductorSeat; implementer; verifier; observer ]
  let actors : Member list = seated @ [ stranger ]

  let roleOf (m: Member) : JoinableRole =
    if m = verifier then JoinableRole.Verifier
    elif m = observer then JoinableRole.Observer
    else JoinableRole.Implementer

  let requesters : Member list = [ conductorSeat; implementer ]

  let requestEntropy (m: Member) : Entropy = if m = conductorSeat then [| 1uy |] else [| 2uy |]

  let ghost = LandingId "l-ghost"

  let isPresent (s: CohortState<Member>) (m: Member) =
    match Map.tryFind m s.Members with
    | Some { Presence = MemberPresence.Present } -> true
    | _ -> false

  let presentRoster (s: CohortState<Member>) : Member list =
    s.Members
    |> Map.toList
    |> List.choose (fun (m, r) -> match r.Presence with MemberPresence.Present -> Some m | MemberPresence.Departed _ -> None)
    |> List.sort

  /// The cohort every exploration starts from: all four seated, `a` conductor by being first.
  let startState () : CohortState<Member> =
    seated
    |> List.fold
      (fun s m ->
        match Cohort.decide clock [||] s (CohortCommand.Join(m, roleOf m, None, cohortScope)) with
        | Ok(s', _, _) -> s'
        | Error e -> failwithf "the start scene could not seat %s: %A" m e)
      (CohortState.empty ())

  let landingEffectOf =
    function
    | CohortEffect.Rebase _
    | CohortEffect.ComputeAffected _
    | CohortEffect.RunTests _
    | CohortEffect.FastForward _ as e -> Some e
    | CohortEffect.Notify _ -> None

  let isCompletionCmd =
    function
    | CohortCommand.RebaseCompleted _
    | CohortCommand.AffectedComputed _
    | CohortCommand.TestsCompleted _
    | CohortCommand.VerificationInconclusive _
    | CohortCommand.FastForwardCompleted _
    | CohortCommand.FastForwardFailed _ -> true
    | _ -> false

  let enabled (s: CohortState<Member>, pend: CohortEffect<Member> option) : (CohortCommand<Member> * Entropy) list =
    let landingIds = ghost :: (s.Landings |> Map.toList |> List.map fst)
    [ for m in seated do
        if isPresent s m then yield CohortCommand.Depart(m, cohortScope), [||]
        else yield CohortCommand.Join(m, roleOf m, None, cohortScope), [||]
      for m in requesters do
        if isPresent s m && not (s.Landings |> Map.exists (fun _ l -> l.Requester = m)) then
          yield CohortCommand.RequestLanding(m, [], [ "commit-" + m ], "land", cohortScope), requestEntropy m
      for who in actors do
        for lid in landingIds do
          yield CohortCommand.VetoLanding(who, lid, "reason", cohortScope), [||]
          yield CohortCommand.ResolveVeto(who, lid, cohortScope), [||]
          yield CohortCommand.WithdrawLanding(who, lid, cohortScope), [||]
        for target in actors do
          yield CohortCommand.DelegateConductor(who, target, cohortScope), [||]
      // A blank reason, once per landing, from a member who would otherwise be allowed to veto.
      for lid in landingIds do
        yield CohortCommand.VetoLanding(implementer, lid, "   ", cohortScope), [||]
      match pend with
      | Some(CohortEffect.Rebase(id, _, _)) ->
        yield CohortCommand.RebaseCompleted(id, Result.Ok("R-" + (let (LandingId x) = id in x)), cohortScope), [||]
        yield CohortCommand.RebaseCompleted(id, Result.Error [ "cf" ], cohortScope), [||]
      | Some(CohortEffect.ComputeAffected(id, _, _)) -> yield CohortCommand.AffectedComputed(id, [ TestId "t" ], cohortScope), [||]
      | Some(CohortEffect.RunTests(id, _)) -> yield CohortCommand.TestsCompleted(id, [], cohortScope), [||]
      | Some(CohortEffect.FastForward(id, sha)) -> yield CohortCommand.FastForwardCompleted(id, sha, cohortScope), [||]
      | _ -> () ]

  // ── State rules ───────────────────────────────────────────────────────────

  /// At most one member answers `Conductor` at any state. The seat is one binding, so this is structural; it is
  /// here so a future second field for the seat cannot make two writable without this going red.
  let atMostOneConductor (s: CohortState<Member>) : bool =
    let holders = s.Members |> Map.toList |> List.filter (fun (m, _) -> Authority.present m s = Authority.Conductor m)
    List.length holders <= 1

  /// A vetoed landing is waiting for the conductor, and is out of the queue so it jams nobody.
  let vetoedAwaitsConductor (s: CohortState<Member>) : bool =
    s.Landings
    |> Map.forall (fun id l ->
      match l.State with
      | LandingState.Blocked(LandingBlocker.VetoedBy _, next) -> next = NextAction.AwaitConductor && not (List.contains id s.Queue)
      | _ -> true)

  let stateRules : (string * (CohortState<Member> -> bool)) list =
    [ "AT-MOST-ONE-CONDUCTOR", atMostOneConductor
      "CONDUCTOR-BOUND", CohortSpec.conductorBound
      "NO-TERMINAL-IN-QUEUE", CohortSpec.noTerminalInQueue
      "QUEUE-SERIAL", CohortSpec.queueSerial
      "VETOED-AWAITS-CONDUCTOR", vetoedAwaitsConductor ]

  // ── Step rules ────────────────────────────────────────────────────────────

  let accepted (step: Step) : CohortState<Member> option =
    match step.Outcome with
    | Ok(s', _, _) -> Some s'
    | Error _ -> None

  let eventsOf (step: Step) : CohortEvent<Member> list =
    match step.Outcome with
    | Ok(_, events, _) -> events
    | Error _ -> []

  let landingBefore (step: Step) (id: LandingId) : LandingRequest<Member> option = Map.tryFind id step.Before.Landings

  let isLive (state: LandingState<Member>) : bool =
    match state with
    | LandingState.Queued
    | LandingState.Rebasing _
    | LandingState.Verifying _ -> true
    | LandingState.Blocked _
    | LandingState.Landed _
    | LandingState.Withdrawn -> false

  let isSettled (state: LandingState<Member>) : bool =
    match state with
    | LandingState.Landed _
    | LandingState.Withdrawn -> true
    | LandingState.Queued
    | LandingState.Rebasing _
    | LandingState.Verifying _
    | LandingState.Blocked _ -> false

  /// Whether `by` may object to a landing at all: a seated member who is not an observer.
  let hasStanding (state: CohortState<Member>) (by: Member) : bool =
    match Authority.present by state with
    | Authority.Conductor _ -> true
    | Authority.Member(_, JoinableRole.Implementer)
    | Authority.Member(_, JoinableRole.Verifier) -> true
    | Authority.Member(_, JoinableRole.Observer)
    | Authority.Anonymous -> false

  /// A veto is accepted only from a seated member who is not an observer.
  let vetoNeedsStanding (step: Step) : bool =
    match step.Command, accepted step with
    | CohortCommand.VetoLanding(by, _, _, _), Some _ -> hasStanding step.Before by
    | _ -> true

  /// A veto lands on a landing that is still live and nothing else: after the landing is over, or when it is
  /// already blocked, it is REFUSED. It is not accepted as a no-op and it does not relabel another blocker.
  let vetoOnlyOnLiveLanding (step: Step) : bool =
    match step.Command, accepted step with
    | CohortCommand.VetoLanding(_, id, _, _), Some _ ->
      match landingBefore step id with
      | Some l -> isLive l.State
      | None -> false
    | _ -> true

  /// The refusal is the right one: a veto of a settled landing says the landing is not in the expected state.
  let vetoOfSettledIsRefused (step: Step) : bool =
    match step.Command with
    | CohortCommand.VetoLanding(by, id, reason, _) when reason.Trim() <> "" && hasStanding step.Before by ->
      match landingBefore step id, step.Outcome with
      | Some l, Ok _ when isSettled l.State -> false
      | Some l, Error(CohortError.LandingNotInExpectedState(refused, _)) when isSettled l.State -> refused = id
      | _ -> true
    | _ -> true

  /// A veto names a reason.
  let vetoNeedsReason (step: Step) : bool =
    match step.Command, accepted step with
    | CohortCommand.VetoLanding(_, _, reason, _), Some _ -> reason.Trim() <> ""
    | _ -> true

  /// A withdrawal is by the landing's own requester.
  let withdrawOnlyByRequester (step: Step) : bool =
    match step.Command, accepted step with
    | CohortCommand.WithdrawLanding(who, id, _), Some _ ->
      match landingBefore step id with
      | Some l -> l.Requester = who
      | None -> false
    | _ -> true

  /// A settled landing is not withdrawn again.
  let withdrawNeverOnSettled (step: Step) : bool =
    match step.Command, accepted step with
    | CohortCommand.WithdrawLanding(_, id, _), Some _ ->
      match landingBefore step id with
      | Some l -> not (isSettled l.State)
      | None -> false
    | _ -> true

  /// Only the conductor clears a veto, and only one that is there.
  let resolveOnlyByConductorOnVetoed (step: Step) : bool =
    match step.Command, accepted step with
    | CohortCommand.ResolveVeto(by, id, _), Some _ ->
      let byConductor = Authority.present by step.Before = Authority.Conductor by
      let onVetoed =
        match landingBefore step id with
        | Some { State = LandingState.Blocked(LandingBlocker.VetoedBy _, _) } -> true
        | _ -> false
      byConductor && onVetoed
    | _ -> true

  /// Only the sitting conductor delegates.
  let delegateOnlyByConductor (step: Step) : bool =
    match step.Command, accepted step with
    | CohortCommand.DelegateConductor(by, _, _), Some _ -> Authority.present by step.Before = Authority.Conductor by
    | _ -> true

  /// The seat only ever goes to a member who is present.
  let delegateTargetPresent (step: Step) : bool =
    match step.Command, accepted step with
    | CohortCommand.DelegateConductor(_, target, _), Some after -> isPresent after target
    | _ -> true

  /// A handoff to oneself is refused: it would record a handoff that handed nothing.
  let delegateNeverToSelf (step: Step) : bool =
    match step.Command, accepted step with
    | CohortCommand.DelegateConductor(by, target, _), Some _ -> by <> target
    | _ -> true

  /// The seat moves from one holder to another by a delegation and by nothing else.
  let seatMovesOnlyByDelegation (step: Step) : bool =
    match accepted step with
    | Some after ->
      match step.Before.Conductor, after.Conductor with
      | ConductorBinding.Bound before, ConductorBinding.Bound now when before <> now ->
        match step.Command with
        | CohortCommand.DelegateConductor(by, target, _) -> by = before && target = now
        | _ -> false
      | _ -> true
    | None -> true

  /// A recorded handoff leaves exactly one conductor, and it is the member the event names. The old holder is an
  /// ordinary member afterwards. This is the rule that gives "never two conductors" its teeth.
  let handoffLeavesOneConductor (step: Step) : bool =
    match accepted step with
    | Some after ->
      eventsOf step
      |> List.forall (fun e ->
        match e with
        | CohortEvent.ConductorDelegated(from, target) ->
          let holders = after.Members |> Map.toList |> List.filter (fun (m, _) -> Authority.present m after = Authority.Conductor m)
          Authority.present from after <> Authority.Conductor from
          && Authority.present target after = Authority.Conductor target
          && List.length holders = 1
        | _ -> true)
    | None -> true

  /// A refused delegation to someone who is not here carries the roster, so the caller can pick a member who is.
  let delegateRefusalNamesRoster (step: Step) : bool =
    match step.Command with
    | CohortCommand.DelegateConductor(by, target, _)
      when Authority.present by step.Before = Authority.Conductor by && by <> target && not (isPresent step.Before target) ->
      match step.Outcome with
      | Error(CohortError.DelegateTargetAbsent(refused, roster)) -> refused = target && List.sort roster = presentRoster step.Before
      | _ -> false
    | _ -> true

  let stepRules : (string * (Step -> bool)) list =
    [ "VETO-NEEDS-STANDING", vetoNeedsStanding
      "VETO-ONLY-ON-LIVE-LANDING", vetoOnlyOnLiveLanding
      "VETO-OF-SETTLED-IS-REFUSED", vetoOfSettledIsRefused
      "VETO-NEEDS-REASON", vetoNeedsReason
      "WITHDRAW-ONLY-BY-REQUESTER", withdrawOnlyByRequester
      "WITHDRAW-NEVER-ON-SETTLED", withdrawNeverOnSettled
      "RESOLVE-ONLY-BY-CONDUCTOR-ON-VETOED", resolveOnlyByConductorOnVetoed
      "DELEGATE-ONLY-BY-CONDUCTOR", delegateOnlyByConductor
      "DELEGATE-TARGET-PRESENT", delegateTargetPresent
      "DELEGATE-NEVER-TO-SELF", delegateNeverToSelf
      "SEAT-MOVES-ONLY-BY-DELEGATION", seatMovesOnlyByDelegation
      "HANDOFF-LEAVES-ONE-CONDUCTOR", handoffLeavesOneConductor
      "DELEGATE-REFUSAL-NAMES-ROSTER", delegateRefusalNamesRoster ]

  // ── Coverage: facts the exploration must actually reach ───────────────────
  //
  // An empty violation list over a space that never reached the interesting steps is a vacuous proof. These are
  // the steps the rules above are about; the test asserts every one was taken at least once.

  let refusedWith (step: Step) (matches: CohortError<Member> -> bool) =
    match step.Outcome with
    | Error e -> matches e
    | Ok _ -> false

  let facts : (string * (Step -> bool)) list =
    [ "veto accepted from the conductor",
      (fun s -> accepted s |> Option.isSome && (match s.Command with CohortCommand.VetoLanding(by, _, r, _) -> by = conductorSeat && r.Trim() <> "" | _ -> false))
      "veto accepted from an implementer",
      (fun s -> accepted s |> Option.isSome && (match s.Command with CohortCommand.VetoLanding(by, _, _, _) -> by = implementer | _ -> false))
      "veto accepted from a verifier",
      (fun s -> accepted s |> Option.isSome && (match s.Command with CohortCommand.VetoLanding(by, _, _, _) -> by = verifier | _ -> false))
      "veto refused for an observer", (fun s -> refusedWith s (function CohortError.VetoRefused(_, VetoRefusal.ReadOnlyRole) -> true | _ -> false))
      "veto refused for a stranger", (fun s -> refusedWith s (function CohortError.VetoRefused(_, VetoRefusal.NotAMember) -> true | _ -> false))
      "veto refused for a blank reason", (fun s -> refusedWith s (function CohortError.InvalidVetoReason _ -> true | _ -> false))
      "veto refused after the landing landed",
      (fun s ->
        match s.Command with
        | CohortCommand.VetoLanding(_, id, _, _) ->
          (match landingBefore s id with Some { State = LandingState.Landed _ } -> true | _ -> false)
          && refusedWith s (function CohortError.LandingNotInExpectedState _ -> true | _ -> false)
        | _ -> false)
      "veto refused after the landing was withdrawn",
      (fun s ->
        match s.Command with
        | CohortCommand.VetoLanding(_, id, _, _) ->
          (match landingBefore s id with Some { State = LandingState.Withdrawn } -> true | _ -> false)
          && refusedWith s (function CohortError.LandingNotInExpectedState _ -> true | _ -> false)
        | _ -> false)
      "second veto refused", (fun s -> refusedWith s (function CohortError.LandingAlreadyVetoed _ -> true | _ -> false))
      "veto resolved by the conductor", (fun s -> eventsOf s |> List.exists (function CohortEvent.LandingVetoResolved _ -> true | _ -> false))
      "resolve refused for a member", (fun s -> refusedWith s (function CohortError.NotConductor _ -> true | _ -> false) && (match s.Command with CohortCommand.ResolveVeto _ -> true | _ -> false))
      "withdrawn by the requester", (fun s -> eventsOf s |> List.exists (function CohortEvent.LandingWithdrawn _ -> true | _ -> false))
      "withdraw refused for a non-requester", (fun s -> refusedWith s (function CohortError.NotLandingRequester _ -> true | _ -> false))
      "delegated", (fun s -> eventsOf s |> List.exists (function CohortEvent.ConductorDelegated _ -> true | _ -> false))
      "delegate refused for a member", (fun s -> refusedWith s (function CohortError.NotConductor _ -> true | _ -> false) && (match s.Command with CohortCommand.DelegateConductor _ -> true | _ -> false))
      "delegate refused to someone absent, with the roster", (fun s -> refusedWith s (function CohortError.DelegateTargetAbsent _ -> true | _ -> false))
      "delegate refused to oneself", (fun s -> refusedWith s (function CohortError.DelegateToSelf _ -> true | _ -> false))
      "delegate refused on a vacant seat",
      (fun s -> refusedWith s (function CohortError.ConductorVacant _ -> true | _ -> false) && (match s.Command with CohortCommand.DelegateConductor _ -> true | _ -> false)) ]

  // ── The exploration ───────────────────────────────────────────────────────

  type ExploreResult =
    { Nodes: int
      /// True iff the frontier emptied within the cap, so an empty `Violations` is a proof of the bound and not a sample.
      Complete: bool
      Capped: bool
      /// The rules violated at some state or step.
      Violations: string list
      /// The facts that were reached at some step.
      Reached: string list }

  let pendAfter (cmd: CohortCommand<Member>) (pend: CohortEffect<Member> option) (effects: CohortEffect<Member> list) =
    match effects |> List.tryPick landingEffectOf with
    | Some e -> Some e
    | None -> if isCompletionCmd cmd then None else pend

  let explore (decide: Decide) (cap: int) : ExploreResult =
    let visited = HashSet<CohortState<Member> * CohortEffect<Member> option>(HashIdentity.Structural)
    let frontier = Queue<CohortState<Member> * CohortEffect<Member> option>()
    let start = (startState (), None)
    visited.Add start |> ignore
    frontier.Enqueue start
    let violations = HashSet<string>()
    let reached = HashSet<string>()
    let mutable capped = false
    while frontier.Count > 0 && not capped do
      let (s, pend) = frontier.Dequeue()
      for (name, rule) in stateRules do
        if not (rule s) then violations.Add name |> ignore
      for (cmd, ent) in enabled (s, pend) do
        let outcome = decide clock ent s cmd
        let step = { Before = s; Command = cmd; Outcome = outcome }
        for (name, rule) in stepRules do
          if not (rule step) then violations.Add name |> ignore
        for (name, fact) in facts do
          if fact step then reached.Add name |> ignore
        match outcome with
        | Ok(s', _, effs) ->
          let node = (s', pendAfter cmd pend effs)
          if visited.Add node then
            if visited.Count > cap then capped <- true else frontier.Enqueue node
        | Error _ -> ()
    { Nodes = visited.Count
      Complete = (frontier.Count = 0 && not capped)
      Capped = capped
      Violations = violations |> Seq.sort |> List.ofSeq
      Reached = reached |> Seq.sort |> List.ofSeq }

  let defaultCap = 500_000

  /// PROVE the rules over the real `Cohort.decide`.
  let proof () : ExploreResult = explore (fun c e s cmd -> Cohort.decide c e s cmd) defaultCap

  // ── Twins: the real reducer with ONE rule removed ─────────────────────────

  let conductorOf (state: CohortState<Member>) : Member option =
    match state.Conductor with
    | ConductorBinding.Bound c -> Some c
    | ConductorBinding.NeverBound
    | ConductorBinding.Vacant _ -> None

  /// Run the real reducer, but as `actor`.
  let asActor (clk: Clock) (ent: Entropy) (state: CohortState<Member>) (command: CohortCommand<Member>) (actor: Member) : Result' =
    match command with
    | CohortCommand.VetoLanding(_, id, reason, scope) -> Cohort.decide clk ent state (CohortCommand.VetoLanding(actor, id, reason, scope))
    | CohortCommand.ResolveVeto(_, id, scope) -> Cohort.decide clk ent state (CohortCommand.ResolveVeto(actor, id, scope))
    | CohortCommand.WithdrawLanding(_, id, scope) -> Cohort.decide clk ent state (CohortCommand.WithdrawLanding(actor, id, scope))
    | CohortCommand.DelegateConductor(_, target, scope) -> Cohort.decide clk ent state (CohortCommand.DelegateConductor(actor, target, scope))
    | other -> Cohort.decide clk ent state other

  /// No standing check on a veto: anyone, seated or not, observer or not, vetoes. The pre-change core.
  let faultVetoWithoutStanding : Decide =
    fun clk ent state command ->
      match command with
      | CohortCommand.VetoLanding(by, _, _, _) when (match Authority.present by state with Authority.Anonymous | Authority.Member(_, JoinableRole.Observer) -> true | _ -> false) ->
        match conductorOf state with
        | Some c -> asActor clk ent state command c
        | None -> Cohort.decide clk ent state command
      | _ -> Cohort.decide clk ent state command

  /// A veto of a settled landing is accepted and does nothing: the silent no-op.
  let faultVetoSilentNoOp : Decide =
    fun clk ent state command ->
      match command with
      | CohortCommand.VetoLanding(_, id, _, _) when (match Map.tryFind id state.Landings with Some l -> isSettled l.State | None -> false) -> Ok(state, [], [])
      | _ -> Cohort.decide clk ent state command

  /// A veto of a landing already blocked for another reason relabels it as a veto.
  let faultVetoRelabelsBlocked : Decide =
    fun clk ent state command ->
      match command with
      | CohortCommand.VetoLanding(by, id, reason, _) when (match Map.tryFind id state.Landings with Some { State = LandingState.Blocked _ } -> true | _ -> false) ->
        let l = state.Landings.[id]
        let blocked = { l with State = LandingState.Blocked(LandingBlocker.VetoedBy(by, reason), NextAction.AwaitConductor) }
        Ok({ state with Landings = Map.add id blocked state.Landings }, [ CohortEvent.LandingStateChanged(id, blocked.State) ], [])
      | _ -> Cohort.decide clk ent state command

  /// A veto blocks the landing and leaves it in the queue, jamming everyone behind it.
  let faultVetoDoesNotPop : Decide =
    fun clk ent state command ->
      match Cohort.decide clk ent state command, command with
      | Ok(after, events, effects), CohortCommand.VetoLanding(_, id, _, _) when List.contains id state.Queue ->
        Ok({ after with Queue = id :: List.filter (fun x -> x <> id) after.Queue }, events, effects)
      | outcome, _ -> outcome

  /// A blank reason is accepted.
  let faultVetoWithoutReason : Decide =
    fun clk ent state command ->
      match command with
      | CohortCommand.VetoLanding(by, id, reason, scope) when reason.Trim() = "" -> Cohort.decide clk ent state (CohortCommand.VetoLanding(by, id, "(no reason given)", scope))
      | _ -> Cohort.decide clk ent state command

  /// Anyone withdraws anyone's landing: the command is run as the landing's requester.
  let faultWithdrawByAnyone : Decide =
    fun clk ent state command ->
      match command with
      | CohortCommand.WithdrawLanding(_, id, _) ->
        match Map.tryFind id state.Landings with
        | Some l -> asActor clk ent state command l.Requester
        | None -> Cohort.decide clk ent state command
      | _ -> Cohort.decide clk ent state command

  /// A settled landing is withdrawn again.
  let faultWithdrawSettled : Decide =
    fun clk ent state command ->
      match command with
      | CohortCommand.WithdrawLanding(who, id, _) ->
        match Map.tryFind id state.Landings with
        | Some l when l.Requester = who && isSettled l.State ->
          let withdrawn = { l with State = LandingState.Withdrawn }
          Ok({ state with Landings = Map.add id withdrawn state.Landings }, [ CohortEvent.LandingWithdrawn id ], [])
        | _ -> Cohort.decide clk ent state command
      | _ -> Cohort.decide clk ent state command

  /// Anyone clears a veto: the command is run as the conductor.
  let faultResolveByAnyone : Decide =
    fun clk ent state command ->
      match command, conductorOf state with
      | CohortCommand.ResolveVeto _, Some c -> asActor clk ent state command c
      | _ -> Cohort.decide clk ent state command

  /// Anyone delegates the seat: the command is run as the conductor.
  let faultDelegateByAnyone : Decide =
    fun clk ent state command ->
      match command, conductorOf state with
      | CohortCommand.DelegateConductor _, Some c -> asActor clk ent state command c
      | _ -> Cohort.decide clk ent state command

  /// The seat goes to a member who is not present.
  let faultDelegateToAbsent : Decide =
    fun clk ent state command ->
      match command with
      | CohortCommand.DelegateConductor(by, target, _) when Authority.present by state = Authority.Conductor by && not (isPresent state target) && by <> target ->
        Ok({ state with Conductor = ConductorBinding.Bound target }, [ CohortEvent.ConductorDelegated(by, target) ], [])
      | _ -> Cohort.decide clk ent state command

  /// A handoff is announced and the old holder keeps the seat.
  let faultDelegateKeepsOldSeat : Decide =
    fun clk ent state command ->
      match Cohort.decide clk ent state command, command with
      | Ok(_, events, effects), CohortCommand.DelegateConductor _ -> Ok(state, events, effects)
      | outcome, _ -> outcome

  /// A handoff to oneself is recorded.
  let faultDelegateToSelf : Decide =
    fun clk ent state command ->
      match command with
      | CohortCommand.DelegateConductor(by, target, _) when by = target && Authority.present by state = Authority.Conductor by ->
        Ok(state, [ CohortEvent.ConductorDelegated(by, target) ], [])
      | _ -> Cohort.decide clk ent state command

  /// The next member in line is promoted when the conductor leaves: the auto-promotion the seat was built to forbid.
  let faultDepartAutoPromotes : Decide =
    fun clk ent state command ->
      match Cohort.decide clk ent state command, command with
      | Ok(after, events, effects), CohortCommand.Depart(who, _) when conductorOf state = Some who ->
        match presentRoster after with
        | next :: _ -> Ok({ after with Conductor = ConductorBinding.Bound next }, events, effects)
        | [] -> Ok(after, events, effects)
      | outcome, _ -> outcome

  /// The refusal forgets the roster and says only that the member is not present.
  let faultRosterOmitted : Decide =
    fun clk ent state command ->
      match Cohort.decide clk ent state command with
      | Error(CohortError.DelegateTargetAbsent(target, _)) -> Error(CohortError.MemberNotPresent target)
      | outcome -> outcome

  /// Each twin with the rule it exists to be caught by.
  let twins : (string * Decide * string) list =
    [ "veto without standing", faultVetoWithoutStanding, "VETO-NEEDS-STANDING"
      "veto of a settled landing as a silent no-op", faultVetoSilentNoOp, "VETO-OF-SETTLED-IS-REFUSED"
      "veto of a settled landing as a silent no-op", faultVetoSilentNoOp, "VETO-ONLY-ON-LIVE-LANDING"
      "veto relabelling a blocked landing", faultVetoRelabelsBlocked, "VETO-ONLY-ON-LIVE-LANDING"
      "veto without a reason", faultVetoWithoutReason, "VETO-NEEDS-REASON"
      "a veto that leaves the landing in the queue", faultVetoDoesNotPop, "NO-TERMINAL-IN-QUEUE"
      "a veto that leaves the landing in the queue", faultVetoDoesNotPop, "VETOED-AWAITS-CONDUCTOR"
      "withdrawal by anyone", faultWithdrawByAnyone, "WITHDRAW-ONLY-BY-REQUESTER"
      "withdrawal of a settled landing", faultWithdrawSettled, "WITHDRAW-NEVER-ON-SETTLED"
      "veto cleared by anyone", faultResolveByAnyone, "RESOLVE-ONLY-BY-CONDUCTOR-ON-VETOED"
      "delegation by anyone", faultDelegateByAnyone, "DELEGATE-ONLY-BY-CONDUCTOR"
      "delegation to an absent member", faultDelegateToAbsent, "DELEGATE-TARGET-PRESENT"
      "delegation to an absent member", faultDelegateToAbsent, "CONDUCTOR-BOUND"
      "a handoff to oneself", faultDelegateToSelf, "DELEGATE-NEVER-TO-SELF"
      "the next member promoted when the conductor leaves", faultDepartAutoPromotes, "SEAT-MOVES-ONLY-BY-DELEGATION"
      "a handoff that keeps the old seat", faultDelegateKeepsOldSeat, "HANDOFF-LEAVES-ONE-CONDUCTOR"
      "a delegation refusal with no roster", faultRosterOmitted, "DELEGATE-REFUSAL-NAMES-ROSTER" ]
