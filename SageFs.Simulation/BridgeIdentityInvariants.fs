namespace SageFs.Simulation

open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Simulation.BridgeIdentitySim

/// Named invariants over a `BridgeIdentitySim` trace. Each reads the recorded ids and the cohort the real reducer left,
/// and none of them calls the identity or liveness code to decide what it should have said.
module BridgeIdentityInvariants =

  let private isPresent (cohort: CohortState<MemberId>) (who: MemberId) =
    match Map.tryFind who cohort.Members with
    | Some { Presence = MemberPresence.Present } -> true
    | Some _
    | None -> false

  /// The same client is the same member for as long as the bridge process lives: after a re-initialize, a daemon restart,
  /// a dropped stream. Finding C3: the id was a fingerprint of the session id, so every restart made a stranger.
  let identityIsStable (state: State) : string list =
    [ for KeyValue(index, last) in state.LastId do
        match Map.tryFind index state.FirstId with
        | Some first when first <> last ->
          yield sprintf "bridge %d was %s and is now %s (step %d)" index (MemberId.display first) (MemberId.display last) state.Step
        | Some _
        | None -> () ]

  /// Two clients are two members (F15): a second bridge never speaks as the first.
  let identitiesAreDistinct (state: State) : string list =
    let ids = state.LastId |> Map.toList |> List.map (fun (index, who) -> index, MemberId.display who)
    [ for (a, idA) in ids do
        for (b, idB) in ids do
          if a < b && idA = idB then yield sprintf "bridges %d and %d are both %s (step %d)" a b idA state.Step ]

  /// A seat held by a bridge's member stays recognizable as that bridge: when the conductor is the id a bridge first
  /// used, it is the id that bridge uses now. Otherwise the bridge is locked out of its own seat until the lease lapses.
  let seatHolderIsStillTheBridge (state: State) : string list =
    match state.Cohort.Conductor with
    | ConductorBinding.Bound holder ->
      [ for KeyValue(index, first) in state.FirstId do
          match first = holder, Map.tryFind index state.LastId with
          | true, Some last when last <> holder ->
            yield sprintf "bridge %d held the conductor seat as %s and now calls as %s (step %d)" index (MemberId.display holder) (MemberId.display last) state.Step
          | _ -> () ]
    | ConductorBinding.NeverBound
    | ConductorBinding.Vacant _ -> []

  /// A bridge with an open stream is connected, and a connected member is not departed for being quiet (finding C5: an LLM
  /// conductor that waits on its agents makes no call for longer than the lease).
  let connectedBridgeStaysPresent (state: State) : string list =
    [ for KeyValue(index, bridge) in state.Bridges do
        match bridge.Session, bridge.StreamOpen, Map.tryFind index state.LastId with
        | Some _, true, Some who when not (isPresent state.Cohort who) ->
          yield sprintf "bridge %d is connected with an open stream but %s was departed (step %d)" index (MemberId.display who) state.Step
        | _ -> () ]

  let invariants : (string * (State -> string list)) list =
    [ "IDENTITY-IS-STABLE", identityIsStable
      "IDENTITIES-ARE-DISTINCT", identitiesAreDistinct
      "SEAT-HOLDER-IS-STILL-THE-BRIDGE", seatHolderIsStillTheBridge
      "CONNECTED-BRIDGE-STAYS-PRESENT", connectedBridgeStaysPresent ]

  /// Every violation across the trace, tagged with the invariant that found it.
  let violations (states: State list) : string list =
    [ for state in states do
        for (name, check) in invariants do
          for message in check state do
            yield sprintf "%s: %s" name message ]
