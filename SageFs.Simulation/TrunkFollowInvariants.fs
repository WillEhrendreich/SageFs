namespace SageFs.Simulation

open SageFs
open SageFs.Cohort
open SageFs.Features.TrunkFollow
open SageFs.Simulation.TrunkFollowSim

/// Named invariants over a drained `TrunkFollowSim.Trace`. Same stable-id / `Holds`-vs-`Violated` shape as the other simulations.
module TrunkFollowInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private replay (t: Trace) = sprintf "seed=%d, ops=%A" t.Scenario.Seed t.Scenario.Ops

  let private landingOf (effect: TrunkEffect) : LandingId =
    match effect with
    | TrunkEffect.MoveTrunk landing -> landing.Landing
    | TrunkEffect.Deliver (landing, _, _) -> landing.Landing

  let private firstViolation (t: Trace) (check: Step -> string option) : Outcome =
    t.Steps
    |> List.tryPick check
    |> function
       | Some message -> Outcome.Violated (sprintf "%s (%s)" message (replay t))
       | None -> Outcome.Holds

  let private isRestart (facts: ReloadFacts) : bool =
    facts.Case = ReloadCase.Restarted || facts.Case = ReloadCase.RestartRequired

  /// Every file verdict the machine recorded, with the landing it belongs to.
  let private recordedVerdicts (t: Trace) : (LandingId * ReloadFacts * RestartCause list) list =
    t.Final.Records
    |> List.collect (fun record ->
      match record.Verdict with
      | TrunkVerdict.Followed deliveries ->
        deliveries
        |> List.collect (fun delivery ->
          match delivery.Outcome with
          | SessionOutcome.Delivered verdicts ->
            verdicts
            |> List.choose (fun v ->
              match v.Outcome with
              | FileOutcome.Reloaded (facts, causes) -> Some (record.Landing, facts, causes)
              | FileOutcome.NeedsRebuild _
              | FileOutcome.NotWatched
              | FileOutcome.NoVerdict _ -> None)
          | SessionOutcome.NoApp
          | SessionOutcome.NoPipeline _
          | SessionOutcome.Unreachable _
          | SessionOutcome.Unavailable _ -> [])
      | TrunkVerdict.NoTrunkSession
      | TrunkVerdict.NotMoved _ -> [])

  /// never-apply-a-landing-that-did-not-land: the trunk checkout is only ever moved to, and a session only ever told about, a
  /// landing the cohort had already recorded as landed. A landing that is being verified, blocked or withdrawn never reaches the
  /// trunk, because a trunk app that served it would be serving something the integration branch does not hold.
  let neverApplyALandingThatDidNotLand : Invariant =
    { Id = "never-apply-a-landing-that-did-not-land"
      Description = "Every effect the trunk performs names a landing the cohort had recorded as landed by then."
      Check = fun t ->
        firstViolation t (fun s ->
          s.Effects
          |> List.tryPick (fun effect ->
            match List.contains (landingOf effect) s.LandedSoFar with
            | true -> None
            | false -> Some (sprintf "step %A performed %A for %A, which had not landed (landed so far: %A)" s.Op effect (landingOf effect) s.LandedSoFar))) }

  /// landings-applied-in-order: the checkout moves to landings in the order they landed, and one landing's deliveries all come
  /// before the next landing's move, so the app never sees a later landing's files first.
  let landingsAppliedInOrder : Invariant =
    { Id = "landings-applied-in-order"
      Description = "Moves follow the order the landings landed, and no delivery is interleaved with another landing's move."
      Check = fun t ->
        let effects = t.Steps |> List.collect (fun s -> s.Effects)
        let moves = effects |> List.choose (function TrunkEffect.MoveTrunk l -> Some l.Landing | TrunkEffect.Deliver _ -> None)
        let expected = t.Landed |> List.truncate (List.length moves)
        match moves = expected with
        | false -> Outcome.Violated (sprintf "the trunk moved for %A but the landings landed in the order %A (%s)" moves t.Landed (replay t))
        | true ->
          let rec interleaved (current: LandingId option) (remaining: TrunkEffect list) =
            match remaining with
            | [] -> None
            | TrunkEffect.MoveTrunk l :: rest -> interleaved (Some l.Landing) rest
            | TrunkEffect.Deliver (l, _, _) :: rest ->
              match current with
              | Some c when c = l.Landing -> interleaved current rest
              | _ -> Some (sprintf "a delivery for %A came while the trunk was following %A" l.Landing current)
          match interleaved None effects with
          | Some message -> Outcome.Violated (sprintf "%s (%s)" message (replay t))
          | None -> Outcome.Holds }

  /// no-lost-landing: once everything in flight has ended, every landing that landed has exactly one record, and nothing is left
  /// queued or half done. A landing that lands while another is being followed waits its turn; it is never dropped.
  let noLostLanding : Invariant =
    { Id = "no-lost-landing"
      Description = "After everything ends, every landed landing has exactly one record and the machine is idle with nothing queued."
      Check = fun t ->
        let recorded = t.Final.Records |> List.map (fun r -> r.Landing)
        let missing = t.Landed |> List.filter (fun id -> not (List.contains id recorded))
        let doubled = recorded |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst
        match missing, doubled, t.Final.Phase, t.Final.Queued with
        | [], [], Phase.Idle, [] -> Outcome.Holds
        | _ ->
          Outcome.Violated (sprintf "landed %A, recorded %A (missing %A, recorded twice %A), phase %A, queued %A (%s)" t.Landed recorded missing doubled t.Final.Phase t.Final.Queued (replay t)) }

  /// state-kept-or-restart-named: what the record says about a delivery matches what the world did to the app. A delivery that
  /// restarted the app says it restarted it and names why; one that kept the process never says it restarted; and a restart is
  /// never recorded without a cause.
  let stateKeptOrRestartNamed : Invariant =
    { Id = "state-kept-or-restart-named"
      Description = "A recorded restart names its cause, matches what really happened to the app, and a kept process is never recorded as restarted."
      Check = fun t ->
        let problem =
          recordedVerdicts t
          |> List.tryPick (fun (landing, facts, causes) ->
            let truth = Map.tryFind landing t.Truths
            match isRestart facts, causes, truth with
            | true, [], _ -> Some (sprintf "%A is recorded as %A with no cause named" landing facts.Case)
            | false, _, Some Truth.Restarted -> Some (sprintf "%A restarted the app and is recorded as %A" landing facts.Case)
            | true, _, Some Truth.StateKept -> Some (sprintf "%A kept the process and is recorded as %A" landing facts.Case)
            | _ -> None)
        match problem with
        | Some message -> Outcome.Violated (sprintf "%s (%s)" message (replay t))
        | None -> Outcome.Holds }

  /// How many verdicts a set of records holds that say Patched.
  let private patchedIn (records: TrunkRecord list) : int =
    records
    |> List.sumBy (fun record ->
      match record.Verdict with
      | TrunkVerdict.Followed deliveries ->
        deliveries
        |> List.sumBy (fun delivery ->
          match delivery.Outcome with
          | SessionOutcome.Delivered verdicts ->
            verdicts
            |> List.filter (fun v ->
              match v.Outcome with
              | FileOutcome.Reloaded (facts, _) -> facts.Case = ReloadCase.Patched
              | FileOutcome.NeedsRebuild _
              | FileOutcome.NotWatched
              | FileOutcome.NoVerdict _ -> false)
            |> List.length
          | SessionOutcome.NoApp
          | SessionOutcome.NoPipeline _
          | SessionOutcome.Unreachable _
          | SessionOutcome.Unavailable _ -> 0)
      | TrunkVerdict.NoTrunkSession
      | TrunkVerdict.NotMoved _ -> 0)

  /// patched-only-after-ran: a verdict says Patched only after the worker reported a patch settled, which it does when it has seen
  /// the new body run. At no step do the records hold more Patched verdicts than reports of a settled patch the machine had been
  /// given by then.
  let patchedOnlyAfterRan : Invariant =
    { Id = "patched-only-after-ran"
      Description = "At every step, no more verdicts are recorded as Patched than settled reports had reached the machine."
      Check = fun t ->
        let settledReports (s: Step) =
          match s.Event with
          | Some (TrunkEvent.ReloadReported (_, facts)) when facts.Case = ReloadCase.Patched || facts.Case = ReloadCase.NeverEntered -> 1
          | _ -> 0
        let _, problem =
          t.Steps
          |> List.fold
            (fun (reports, found) s ->
              let reports' = reports + settledReports s
              match found with
              | Some _ -> reports', found
              | None ->
                let patched = patchedIn s.After.Records
                match patched <= reports' with
                | true -> reports', None
                | false -> reports', Some (sprintf "step %A left %d verdicts recorded as Patched with only %d settled reports given" s.Op patched reports'))
            (0, None)
        match problem with
        | Some message -> Outcome.Violated (sprintf "%s (%s)" message (replay t))
        | None -> Outcome.Holds }

  /// pending-resolves: once every patched body has run and been reported, no patch is still recorded as pending. A report that
  /// arrives before the answer it belongs to is held, and is still applied when the answer lands.
  let pendingResolves : Invariant =
    { Id = "pending-resolves"
      Description = "After every patched body has run, no recorded verdict is still PatchPending."
      Check = fun t ->
        let pending = recordedVerdicts t |> List.filter (fun (_, facts, _) -> facts.Case = ReloadCase.PatchPending)
        match t.StillPending, pending with
        | 0, [] -> Outcome.Holds
        | 0, stuck -> Outcome.Violated (sprintf "every patched body ran, and %A are still recorded as pending (%s)" (stuck |> List.map (fun (l, _, _) -> l)) (replay t))
        | n, _ -> Outcome.Violated (sprintf "%d patched bodies were left unrun by the drain (%s)" n (replay t)) }

  let all : Invariant list =
    [ neverApplyALandingThatDidNotLand
      landingsAppliedInOrder
      noLostLanding
      stateKeptOrRestartNamed
      patchedOnlyAfterRan
      pendingResolves ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some (inv.Id, msg))
