namespace SageFs.Simulation

open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Simulation.SeatingSim

/// Named invariants over a `SeatingSim` trace. They read what was asked and answered and the cohort the real reducer left,
/// and none of them calls `SeatingFlow` to decide what it should have said.
module SeatingInvariants =

  let private isPresent (cohort: CohortState<MemberId>) (who: MemberId) =
    match Map.tryFind who cohort.Members with
    | Some { Presence = MemberPresence.Present } -> true
    | Some _
    | None -> false

  let private isEmpty (cohort: CohortState<MemberId>) =
    match cohort.Conductor with
    | ConductorBinding.Vacant _
    | ConductorBinding.NeverBound -> true
    | ConductorBinding.Bound _ -> false

  let private isToken (who: MemberId) =
    match who with
    | MemberId.Capability _ -> true
    | _ -> false

  /// The point of the tool: a seat changes hands only when a person was asked and said an explicit yes.
  let seatsOnlyOnAYes (state: State) : string list =
    match state.Attempt with
    | Some a when a.Seated && not (a.Asked = 1 && a.Reply = Reply.Yes) ->
      [ sprintf "step %d seated %s after %d question(s) and the reply %A" state.Step (MemberId.display a.Target) a.Asked a.Reply ]
    | _ -> []

  /// Nobody is asked a question the cohort would refuse: whenever a person was asked, the seat was empty, the member to seat was
  /// present and not under a token, and the caller was present and not under a token.
  let askedOnlyAboutAPossibleSeating (state: State) : string list =
    match state.Attempt with
    | Some a when a.Asked > 0 ->
      let seatProblem =
        match a.Shown, a.Before.Conductor with
        | None, ConductorBinding.Bound _ -> [ "the seat was held" ]
        | None, (ConductorBinding.Vacant _ | ConductorBinding.NeverBound) -> []
        | Some shown, ConductorBinding.Bound sitting when sitting = shown ->
          match a.Target = shown with
          | true -> [ "the member to seat was the holder it would replace" ]
          | false -> []
        | Some shown, binding -> [ sprintf "the holder named (%s) did not hold the seat: %A" (MemberId.display shown) binding ]
      let problems =
        [ yield! seatProblem
          if not (isPresent a.Before a.Target) then yield "the member to seat was not present"
          if not (isPresent a.Before a.Caller) then yield "the caller was not present"
          if isToken a.Target then yield "the member to seat is under a token"
          if isToken a.Caller then yield "the caller is under a token" ]
      problems |> List.map (fun problem -> sprintf "step %d asked the person although %s" state.Step problem)
    | _ -> []

  /// A member under a token neither asks nor is seated.
  let tokensAreNeverInvolved (state: State) : string list =
    match state.Attempt with
    | Some a when a.Seated && (isToken a.Caller || isToken a.Target) ->
      [ sprintf "step %d seated %s on the word of %s, and a token is involved" state.Step (MemberId.display a.Target) (MemberId.display a.Caller) ]
    | _ -> []

  /// A seating is what the cohort allows: it ended an empty seat, and it bound the seat to a member who was present.
  let aSeatingKeepsTheCohortsRules (state: State) : string list =
    match state.Attempt with
    | Some a when a.Seated ->
      let bound = (state.Cohort.Conductor = ConductorBinding.Bound a.Target)
      [ match a.Shown with
        | None -> if not (isEmpty a.AtCommit) then yield sprintf "step %d seated over a held seat" state.Step
        | Some _ -> if isEmpty a.AtCommit then yield sprintf "step %d replaced a holder of a seat that was empty" state.Step
        if not (isPresent a.AtCommit a.Target) then yield sprintf "step %d seated a member who was not present" state.Step
        if not bound then yield sprintf "step %d reported a seating but the seat is %A" state.Step state.Cohort.Conductor ]
    | _ -> []

  /// An attempt that did not seat anyone leaves the seat as it was when the answer came (a seat that changed hands while the
  /// person read the question stays changed: that was somebody else's act).
  let aRefusalChangesNothing (state: State) : string list =
    match state.Attempt with
    | Some a when not a.Seated && state.Cohort.Conductor <> a.AtCommit.Conductor ->
      [ sprintf "step %d refused and still changed the seat from %A to %A" state.Step a.AtCommit.Conductor state.Cohort.Conductor ]
    | _ -> []

  /// A BOUND seat changes hands only on a yes, and only from the holder the person was shown. The seat may have changed
  /// between the question and the answer; the answer then moves nothing, because the holder the person saw is not the
  /// holder any more.
  let aBoundSeatChangesHandsOnlyFromTheHolderShown (state: State) : string list =
    match state.Attempt with
    | Some a ->
      match a.AtCommit.Conductor with
      | ConductorBinding.Bound sitting when state.Cohort.Conductor <> a.AtCommit.Conductor ->
        match a.Shown with
        | Some shown when shown = sitting && a.Seated && a.Asked = 1 && a.Reply = Reply.Yes -> []
        | Some shown ->
          [ sprintf "step %d took the seat from %s although the person was shown %s (asked %d, reply %A)" state.Step (MemberId.display sitting) (MemberId.display shown) a.Asked a.Reply ]
        | None -> [ sprintf "step %d moved a held seat (%s) without a replacement being asked" state.Step (MemberId.display sitting) ]
      | ConductorBinding.Bound _
      | ConductorBinding.Vacant _
      | ConductorBinding.NeverBound -> []
    | None -> []

  let invariants : (string * (State -> string list)) list =
    [ "SEATS-ONLY-ON-A-YES", seatsOnlyOnAYes
      "ASKED-ONLY-ABOUT-A-POSSIBLE-SEATING", askedOnlyAboutAPossibleSeating
      "TOKENS-ARE-NEVER-INVOLVED", tokensAreNeverInvolved
      "A-SEATING-KEEPS-THE-COHORTS-RULES", aSeatingKeepsTheCohortsRules
      "A-REFUSAL-CHANGES-NOTHING", aRefusalChangesNothing
      "A-BOUND-SEAT-CHANGES-HANDS-ONLY-FROM-THE-HOLDER-SHOWN", aBoundSeatChangesHandsOnlyFromTheHolderShown ]

  /// Every violation across the trace, tagged with the invariant that found it.
  let violations (states: State list) : string list =
    [ for state in states do
        for (name, check) in invariants do
          for message in check state do
            yield sprintf "%s: %s" name message ]
