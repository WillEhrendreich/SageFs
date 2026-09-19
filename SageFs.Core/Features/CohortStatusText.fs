namespace SageFs.Features

open SageFs

/// The `get_cohort_status` / `cohort://status` text rendering of one cohort
/// frame. Lives in Core (not `Mcp.fs`, whose line budget only ratchets down)
/// because it is a pure `CohortFrame -> string` projection; the lists are
/// bounded by `CohortBoundedView` with an explicit "+N more" line.
module CohortStatusText =

  let render (frame: SageFs.Cohort.CohortFrame<SageFs.MemberTable.MemberId>) : string =
    let sb = System.Text.StringBuilder()
    let conductorText =
      // `frame.Conductor` (Slice 3, item 11) — read straight off the
      // published frame, correct across daemon restarts (replaced the
      // process-lifetime `lastKnownConductor` cache the Slice 2 report
      // flagged as a known limitation).
      match frame.Conductor with
      | Some who -> MemberTable.MemberId.display who
      | None -> "(none yet — no member has joined this cohort)"
    sb.AppendLine(sprintf "Cohort ledger head: v%d" (int64 frame.Version)) |> ignore
    sb.AppendLine(sprintf "Conductor: %s" conductorText) |> ignore
    // Bounded lists (CohortBoundedView): totals stay in the headers, at most
    // `rowCap` rows are listed most-actionable-first, and a "+N more" line
    // names what was hidden — the status never silently truncates.
    let memberView = CohortBoundedView.members CohortBoundedView.rowCap frame
    let claimView = CohortBoundedView.claims CohortBoundedView.rowCap frame
    let landingView = CohortBoundedView.landings CohortBoundedView.rowCap frame
    sb.AppendLine(sprintf "Members (%d):" frame.MemberIds.Length) |> ignore
    for i in memberView.Shown do
      let seat =
        match frame.MemberSeat.[i] with
        | Cohort.SeatState.Present -> "present"
        | Cohort.SeatState.Departed since -> sprintf "departed %s" (since.ToString "u")
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
