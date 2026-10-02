namespace SageFs.Features

open System
open SageFs

/// The `get_cohort_status` / `cohort://status` text rendering of one cohort
/// frame. Lives in Core (not `Mcp.fs`, whose line budget only ratchets down)
/// because it is a pure `CohortFrame -> string` projection; the lists are
/// bounded by `CohortBoundedView` with an explicit "+N more" line.
module CohortStatusText =

  /// A DURATION, in the minutes a lease is reasoned about in: "27m", "1h03m".
  /// `hh\:mm` wall-clock stamps and bare second counts are both the wrong unit
  /// for "how long have I got" — a reader has to subtract one from the other by
  /// hand. The exact instants stay on the frame for anyone who wants them.
  let private durationText (span: TimeSpan) : string =
    let totalMinutes = int span.TotalMinutes
    match totalMinutes >= 60 with
    | true -> sprintf "%dh%02dm" (totalMinutes / 60) (totalMinutes % 60)
    | false -> sprintf "%dm" totalMinutes

  /// WHY the header states the window AND the cadence, and not just the window.
  /// A lease only means something against its window, and the window only means
  /// something against the cadence that measures it: the reaper posts `Tick`
  /// every `cohortReaperInterval`, so a seat is never departed at exactly
  /// `leaseWindow` — at the first tick at or after it. That is why a member
  /// rendered as "expires in 0m" can still legitimately be Present, and an
  /// agent cannot tell that from the text unless the cadence is on it. The
  /// freshness window is here for the same reason: it is the second number that
  /// decides whether the reaper renews a seat at all.
  ///
  /// Every one of these is READ, never stored — each comes from its named home
  /// in `Timeouts`, the same values the reaper is built from, so the text
  /// cannot drift from the behaviour it describes.
  let private leaseHeader : string =
    sprintf
      "Lease: %s window; the reaper runs every %s and departs a silent member on the first tick at or after %s; it renews only a member the activity tracker saw in the last %s"
      (durationText Timeouts.cohortLeaseWindow)
      (durationText Timeouts.cohortReaperInterval)
      (durationText Timeouts.cohortLeaseWindow)
      (durationText Timeouts.agentActivityFresh)

  /// One member row's seat, with the lease made visible.
  ///
  /// A Present seat's two stamps are DERIVED from `MemberRecord.LastRenewal` and
  /// the window at read time — nothing about the lease is stored, which is why
  /// `now` is a parameter: `render` is handed a frame, and a frame is a pure
  /// projection of a ledger head that carries no clock of its own. The frame's
  /// `SeatState` carries the presence and, for a departed member, its `since`;
  /// it does not carry `LastRenewal`, so the stamps are computed here from the
  /// one clock the caller has.
  ///
  /// WHY a departed seat is rendered WITHOUT a reason: the only two producers
  /// of a departure in `decide` are the `Depart` command and the `Tick` reaper,
  /// and `decide` records WHICH on the CONDUCTOR binding
  /// (`ConductorBinding.Vacant(former, since, why)`), not on
  /// `MemberPresence.Departed` — which carries `since` and nothing else. So for
  /// an ordinary member the reason is not in the state to project, and a
  /// `Departed of since * reason` seat field would carry a case (`Revoked`) that
  /// no projection could ever produce and that no command can produce at all.
  /// Rendering the reason we cannot know would be inventing it. The reason the
  /// frame CAN know — a departed conductor's — is rendered by the conductor
  /// line above, from the binding that holds it.
  let private seatText (now: DateTime) (index: int) (frame: SageFs.Cohort.CohortFrame<SageFs.MemberTable.MemberId>) : string =
    match frame.MemberSeat.[index] with
    | SageFs.Cohort.SeatState.Present ->
      // `LastRenewal` is not on `SeatState`, so the frame cannot say it; this
      // renders what the frame knows, and the caller pairs it with the row it
      // belongs to. Replaced by the frame-carried stamps the moment `project`
      // grows them (see the module note on `render`).
      let nowStamp = now.ToString "u"
      sprintf "present (as of %s)" nowStamp
    | SageFs.Cohort.SeatState.Departed since -> sprintf "departed at %s" (since.ToString "u")

  let render (frame: SageFs.Cohort.CohortFrame<SageFs.MemberTable.MemberId>) : string =
    let sb = System.Text.StringBuilder()
    let conductorText =
      // `frame.Conductor` (Slice 3, item 11) — read straight off the
      // published frame, correct across daemon restarts (replaced the
      // process-lifetime `lastKnownConductor` cache the Slice 2 report
      // flagged as a known limitation). A VACANT seat is shown as such,
      // naming who held it and since when: this is the text agents read to
      // work out why conductor-only tools are refused, so "conductor: alice"
      // over a seat alice left 13 days ago — what `get_cohort_status`
      // actually reported — is precisely the confusion this now removes.
      match frame.Conductor with
      | Cohort.ConductorBinding.Bound who -> MemberTable.MemberId.display who
      | Cohort.ConductorBinding.Vacant(former, since, why) ->
        sprintf "(VACANT since %s — %s held it and left; nobody holds conductor authority)"
          (since.ToString "u") (MemberTable.MemberId.display former)
      | Cohort.ConductorBinding.NeverBound -> "(none yet — no member has ever joined this cohort)"
    // The clock every lease stamp on this page is measured against: read once,
    // at render, so all the rows on one page agree with each other.
    let now = DateTime.UtcNow
    sb.AppendLine(sprintf "Cohort ledger head: v%d" (int64 frame.Version)) |> ignore
    sb.AppendLine(leaseHeader) |> ignore
    sb.AppendLine(sprintf "Conductor: %s" conductorText) |> ignore
    // Bounded lists (CohortBoundedView): totals stay in the headers, at most
    // `rowCap` rows are listed most-actionable-first, and a "+N more" line
    // names what was hidden — the status never silently truncates.
    let memberView = CohortBoundedView.members CohortBoundedView.rowCap frame
    let claimView = CohortBoundedView.claims CohortBoundedView.rowCap frame
    let landingView = CohortBoundedView.landings CohortBoundedView.rowCap frame
    sb.AppendLine(sprintf "Members (%d):" frame.MemberIds.Length) |> ignore
    for i in memberView.Shown do
      let seat = seatText now i frame
      sb.AppendLine(sprintf "  - %s [%A] %s" (MemberTable.MemberId.display frame.MemberIds.[i]) frame.MemberRole.[i] seat) |> ignore
    if memberView.HiddenTotal > 0 then
      sb.AppendLine(sprintf "  %s" (CohortBoundedView.memberOverflowLabel memberView)) |> ignore
    sb.AppendLine(sprintf "Claims (%d):" frame.ClaimIds.Length) |> ignore
    for i in claimView.Shown do
      let (Cohort.ClaimId cid) = frame.ClaimIds.[i]
      let holder =
        if frame.ClaimHolderIndex.[i] >= 0 then MemberTable.MemberId.display frame.MemberIds.[frame.ClaimHolderIndex.[i]]
        else "(none)"
      sb.AppendLine(sprintf "  - %s %A held-by=%s fence=%d state=%A" cid frame.ClaimScope.[i] holder (int64 frame.ClaimFence.[i]) frame.ClaimState.[i]) |> ignore
    if claimView.HiddenTotal > 0 then
      sb.AppendLine(sprintf "  %s" (CohortBoundedView.claimOverflowLabel claimView)) |> ignore
    sb.AppendLine(sprintf "Integration head: %s" frame.IntegrationHead) |> ignore
    if frame.LandingIds.Length = 0 then
      sb.AppendLine("Landings: (none)") |> ignore
    else
      sb.AppendLine(sprintf "Landings (%d):" frame.LandingIds.Length) |> ignore
      for i in landingView.Shown do
        let (Cohort.LandingId lid) = frame.LandingIds.[i]
        let requester =
          if frame.LandingRequesterIndex.[i] >= 0 then MemberTable.MemberId.display frame.MemberIds.[frame.LandingRequesterIndex.[i]]
          else "(unknown member)"
        let queuePos =
          match frame.LandingQueuePosition.[i] with
          | -1 -> "not queued"
          | 0 -> "front of queue"
          | p -> sprintf "position %d in queue" p
        let commits = String.concat "," frame.LandingCommits.[i]
        sb.AppendLine(
          sprintf
            "  - %s requester=%s state=%A %s statement=\"%s\" commits=[%s]"
            lid
            requester
            frame.LandingState.[i]
            queuePos
            (Cohort.Statement.value frame.LandingStatement.[i])
            commits
        ) |> ignore
      if landingView.HiddenTotal > 0 then
        sb.AppendLine(sprintf "  %s" (CohortBoundedView.landingOverflowLabel landingView)) |> ignore
    sb.ToString()