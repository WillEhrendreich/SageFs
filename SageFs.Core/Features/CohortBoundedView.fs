namespace SageFs.Features

open SageFs.Cohort

/// Bounded rows for the cohort's read surfaces (the dashboard panel and
/// `get_cohort_status`). `Cohort.Retention` bounds what `CohortState` KEEPS;
/// this bounds what a surface DRAWS in the meantime, so a burst of stale
/// members/claims/landings can never grow a panel without limit. Truncation is
/// never silent: every view carries how many rows it hid, by class, so the
/// surface can say "+35 more claims (35 orphaned)".
///
/// Shown rows are chosen most-actionable-first — a class's position in its DU
/// declaration IS its priority (F# orders DU cases by declaration), so a live
/// `Held` claim is never hidden behind stale orphans — and rows of one class
/// keep frame (index) order, so a re-render of an unchanged frame is stable.
module CohortBoundedView =

  /// Rows drawn per list before the overflow indicator takes over.
  let rowCap = 10

  /// Declaration order = display priority. Do not reorder casually.
  [<RequireQualifiedAccess>]
  type ClaimClass =
    | HeldClaim
    | OrphanedClaim
    | ReleasedClaim

  [<RequireQualifiedAccess>]
  type MemberClass =
    | PresentMember
    | DepartedMember

  [<RequireQualifiedAccess>]
  type LandingClass =
    | LiveLanding
    | SettledLanding

  /// `Shown` are indices into the frame's index-aligned arrays. `HiddenByClass`
  /// carries only classes with something hidden.
  type BoundedView<'c when 'c: comparison> = {
    Shown: int[]
    HiddenTotal: int
    HiddenByClass: Map<'c, int>
  }

  let bound (cap: int) (classOf: int -> 'c) (count: int) : BoundedView<'c> =
    let ordered = Array.init count id |> Array.sortBy (fun i -> classOf i, i)
    let shown = ordered |> Array.truncate (max 0 cap)
    let hidden = ordered |> Array.skip shown.Length
    { Shown = shown
      HiddenTotal = hidden.Length
      HiddenByClass = hidden |> Array.countBy classOf |> Map.ofArray }

  /// TWIN — the naive shape a list gets when nobody thinks about bounds:
  /// take the first `cap` rows in frame order and say nothing about the rest.
  /// NOT wired into any product path; `CohortBoundedViewTests` proves it hides
  /// live claims behind stale ones with no indicator.
  let truncateTwin (cap: int) (count: int) : int[] =
    Array.init count id |> Array.truncate (max 0 cap)

  let private overflowLabel (noun: string) (nameOf: 'c -> string) (view: BoundedView<'c>) : string =
    match view.HiddenTotal with
    | 0 -> ""
    | hidden ->
      let breakdown =
        view.HiddenByClass
        |> Map.toList
        |> List.map (fun (c, n) -> sprintf "%d %s" n (nameOf c))
        |> String.concat ", "
      sprintf "+%d more %s (%s)" hidden noun breakdown

  let claimOverflowLabel (view: BoundedView<ClaimClass>) : string =
    overflowLabel "claims"
      (function
        | ClaimClass.HeldClaim -> "held"
        | ClaimClass.OrphanedClaim -> "orphaned"
        | ClaimClass.ReleasedClaim -> "released")
      view

  let memberOverflowLabel (view: BoundedView<MemberClass>) : string =
    overflowLabel "members"
      (function
        | MemberClass.PresentMember -> "present"
        | MemberClass.DepartedMember -> "departed")
      view

  let landingOverflowLabel (view: BoundedView<LandingClass>) : string =
    overflowLabel "landings"
      (function
        | LandingClass.LiveLanding -> "live"
        | LandingClass.SettledLanding -> "settled")
      view

  // ── frame projections ──────────────────────────────────────────────────

  let claims (cap: int) (frame: CohortFrame<'m>) : BoundedView<ClaimClass> =
    bound cap
      (fun i ->
        match frame.ClaimState.[i] with
        | ClaimState.Held _ -> ClaimClass.HeldClaim
        | ClaimState.Orphaned _ -> ClaimClass.OrphanedClaim
        | ClaimState.Released _ -> ClaimClass.ReleasedClaim)
      frame.ClaimIds.Length

  let members (cap: int) (frame: CohortFrame<'m>) : BoundedView<MemberClass> =
    bound cap
      (fun i ->
        match frame.MemberSeat.[i] with
        | SeatState.Present -> MemberClass.PresentMember
        | SeatState.Departed _ -> MemberClass.DepartedMember)
      frame.MemberIds.Length

  let landings (cap: int) (frame: CohortFrame<'m>) : BoundedView<LandingClass> =
    bound cap
      (fun i ->
        match frame.LandingState.[i] with
        | LandingState.Queued
        | LandingState.Rebasing _
        | LandingState.Verifying _ -> LandingClass.LiveLanding
        | LandingState.Blocked _
        | LandingState.Landed _
        | LandingState.Withdrawn -> LandingClass.SettledLanding)
      frame.LandingIds.Length
