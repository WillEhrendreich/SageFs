namespace SageFs.Features

open System
open SageFs

/// The `get_cohort_status` / `cohort://status` text rendering of one cohort
/// frame. Lives in Core (not `Mcp.fs`, whose line budget only ratchets down)
/// because it is a pure `CohortFrame -> string` projection; the lists are
/// bounded by `CohortBoundedView` with an explicit "+N more" line.
module CohortStatusText =

  /// The command a person types to fill an empty conductor seat. One home for the words, because the status text, the
  /// dashboard panel, the refusals and the docs all send a reader to it.
  [<Literal>]
  let seatCommandText = "sagefs cohort seat"

  /// What to do about an empty conductor seat, in one sentence. Every way to fill it ends with a person's say: the question
  /// `seat_conductor` puts to them, the dashboard's Seat button, or the command line with the operator credential.
  let vacantSeatAdvice : string =
    sprintf
      "A person fills it: say yes when seat_conductor asks, click Seat on the dashboard's cohort panel, or run '%s <member id>' on the machine that runs the daemon (with no member id it lists who is present)."
      seatCommandText

  /// The flag that names the holder a person replaces. One home, like `seatCommandText`.
  [<Literal>]
  let replaceFlagText = "--replace"

  /// The command that replaces `holder` with `target`, exactly as a person types it. Either may be a placeholder
  /// such as `<member id>` when the text is advice and not yet a command for a named member.
  let replaceCommandText (target: string) (holder: string) : string =
    sprintf "%s %s %s %s" seatCommandText target replaceFlagText holder

  /// Whether the holder of a BOUND conductor seat is gone, read from the cohort's own ledger: the reaper renews a holder
  /// that has an open stream or made a call on every pass, so a lease that has not been renewed for
  /// `Timeouts.cohortHolderSilentAfter` belongs to a connection that is not there. `lapsesAt` is the instant the lease
  /// window ends (the reaper departs the member on its first tick at or after it); until then the seat stays bound.
  [<RequireQualifiedAccess>]
  type HolderSilence =
    | NotSilent
    | SilentSince of since: DateTime * lapsesAt: DateTime

  module HolderSilence =
    /// The standing of a holder whose lease was last renewed at `lastRenewal`, at `now`.
    let ofRenewal (lastRenewal: DateTime) (now: DateTime) : HolderSilence =
      match now - lastRenewal >= Timeouts.cohortHolderSilentAfter with
      | true -> HolderSilence.SilentSince(lastRenewal, lastRenewal + Timeouts.cohortLeaseWindow)
      | false -> HolderSilence.NotSilent

    /// The standing of the seat's holder at `now`. A seat that is not bound has no holder to be silent.
    let ofState (state: SageFs.Cohort.CohortState<'m>) (now: DateTime) : HolderSilence =
      match state.Conductor with
      | SageFs.Cohort.ConductorBinding.Bound holder ->
        match Map.tryFind holder state.Members with
        | Some record -> ofRenewal record.LastRenewal now
        | None -> HolderSilence.NotSilent
      | SageFs.Cohort.ConductorBinding.NeverBound
      | SageFs.Cohort.ConductorBinding.Vacant _ -> HolderSilence.NotSilent

    /// "silent for 42 min, lapses at 2026-11-07 19:30:00Z": how long, and when the seat empties by itself.
    let text (now: DateTime) (silence: HolderSilence) : string =
      match silence with
      | HolderSilence.NotSilent -> ""
      | HolderSilence.SilentSince(since, lapsesAt) ->
        sprintf "silent for %d min, lapses at %s" (int (now - since).TotalMinutes) (lapsesAt.ToString "u")

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
      "Lease: %s window; the reaper runs every %s and departs a silent member on the first tick at or after %s; it renews a member that holds an open push stream, or that evaluated or made a call in the last %s"
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

  /// One landing's state, in words an agent can act on. A veto names WHO vetoed and WHY, and the two ways out;
  /// `%A` of the blocker printed the member as a union case and the reason in quotes with no next step.
  let landingStateText (state: SageFs.Cohort.LandingState<SageFs.MemberTable.MemberId>) : string =
    match state with
    | SageFs.Cohort.LandingState.Blocked(SageFs.Cohort.LandingBlocker.VetoedBy(by, reason), _) ->
      sprintf
        "Blocked(vetoed by %s: \"%s\") awaiting the conductor: resolve_veto clears it, withdraw_landing takes it back"
        (SageFs.MemberTable.MemberId.display by)
        reason
    | SageFs.Cohort.LandingState.Blocked(SageFs.Cohort.LandingBlocker.CompileErrorInLanding text, _) ->
      sprintf "Blocked(the landing does not compile: %s) fix it in your commit and request the landing again" text
    | SageFs.Cohort.LandingState.Blocked(SageFs.Cohort.LandingBlocker.Inconclusive reason, _) ->
      sprintf "Blocked(verification inconclusive: %s) nothing is known to be wrong with your commit: request the landing again" reason
    // default policy: every other state keeps its structural rendering; only a veto carries a member and a
    // free-text reason that the structural form prints badly.
    | other -> sprintf "%A" other

  /// `landingStateText` for a landing whose failing tests the daemon recorded the names of (finding C-names). A landing Blocked
  /// on failing tests says how many, the first ten by name and where the whole list is, instead of printing every test id;
  /// `reportFor` answers with what was recorded for that landing, or nothing. Every other state reads as `landingStateText`.
  let landingStateTextWith
    (reportFor: string -> FailingTestsText.Report option)
    (landingId: string)
    (state: SageFs.Cohort.LandingState<SageFs.MemberTable.MemberId>)
    : string =
    match state with
    | SageFs.Cohort.LandingState.Blocked(SageFs.Cohort.LandingBlocker.FailingTests tests, _) ->
      let ids = tests |> List.map (fun (SageFs.Cohort.TestId id) -> id)
      sprintf
        "Blocked(%s) Fix those tests in your commits and request the landing again; withdraw_landing takes this one back"
        (FailingTestsText.render FailingTestsText.namesShownInStatus (reportFor landingId) ids)
    // A landing that landed says which tests the gate left to the release gate, so a green status hides nothing.
    | SageFs.Cohort.LandingState.Landed sha ->
      match FailingTestsText.notRunSentence (reportFor landingId) with
      | "" -> landingStateText state
      | sentence -> sprintf "Landed at %s. %s" sha sentence
    // default policy: only a landing Blocked on failing tests carries ids that need names, and only a landed one a left-out list.
    | other -> landingStateText other

  /// Where landing `i` stands in the queue and what is in front of it (P14, C9): a member that waits for a long landing can
  /// tell it is second behind a verification and sleep, instead of polling a position. The landing directly ahead is named with
  /// its state; how many are ahead is the position itself.
  let queuePositionTextWith
    (reportFor: string -> FailingTestsText.Report option)
    (frame: SageFs.Cohort.CohortFrame<SageFs.MemberTable.MemberId>)
    (i: int)
    : string =
    match frame.LandingQueuePosition.[i] with
    | -1 -> "not queued"
    | 0 -> "front of queue"
    | p ->
      let ahead = if p = 1 then "1 landing ahead" else sprintf "%d landings ahead" p
      match frame.LandingQueuePosition |> Array.tryFindIndex (fun position -> position = p - 1) with
      | Some j ->
        let (SageFs.Cohort.LandingId inFront) = frame.LandingIds.[j]
        sprintf "position %d in queue (%s, next in front: %s, %s)" p ahead inFront (landingStateTextWith reportFor inFront frame.LandingState.[j])
      | None -> sprintf "position %d in queue (%s)" p ahead

  /// `queuePositionTextWith` for a frame with no failure reports.
  let queuePositionText (frame: SageFs.Cohort.CohortFrame<SageFs.MemberTable.MemberId>) (i: int) : string =
    queuePositionTextWith (fun _ -> None) frame i

  /// The conductor line for a seat that is held by `who`. A holder that has gone silent is said to be, with how long
  /// and when its lease lapses, and the line names the command a person runs to replace it: the seat otherwise stays
  /// bound to a connection that is not there until the lease lapses.
  let heldSeatText (now: DateTime) (silence: HolderSilence) (who: MemberTable.MemberId) : string =
    let holder = MemberTable.MemberId.display who
    match silence with
    | HolderSilence.NotSilent -> holder
    | HolderSilence.SilentSince _ ->
      sprintf "held by %s, %s; a person can replace it: %s" holder (HolderSilence.text now silence) (replaceCommandText "<member id>" holder)

  /// The cohort status text. `silence` is the standing of the seat's holder, which a frame cannot say (it carries no
  /// lease): the caller reads it off the cohort's state with `HolderSilence.ofState`. `reportFor` gives what the daemon
  /// recorded about a landing's failing tests, by landing id.
  let renderWithSilenceAndReports
    (silence: HolderSilence)
    (reportFor: string -> FailingTestsText.Report option)
    (frame: SageFs.Cohort.CohortFrame<SageFs.MemberTable.MemberId>)
    : string =
    let sb = System.Text.StringBuilder()
    // The clock every lease stamp on this page is measured against: read once,
    // at render, so all the rows on one page agree with each other.
    let now = DateTime.UtcNow
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
      | Cohort.ConductorBinding.Bound who -> heldSeatText now silence who
      | Cohort.ConductorBinding.Vacant(former, since, why) ->
        sprintf "(VACANT since %s — %s held it and left; nobody holds conductor authority. %s)"
          (since.ToString "u") (MemberTable.MemberId.display former) vacantSeatAdvice
      | Cohort.ConductorBinding.NeverBound -> "(none yet — no member has ever joined this cohort)"
    sb.AppendLine(sprintf "Cohort ledger head: v%d" (int64 frame.Version)) |> ignore
    // WHICH cohort this is, and why it is on the page at all.
    //
    // A cohort is about ONE scope, so a member working in another repository is reading a
    // DIFFERENT cohort with its own conductor. An agent that hit a conductor-only refusal and
    // could not tell a scope collision from a permissions problem had no way to recover: the
    // status named members but never said which cohort they belonged to. One line answers it,
    // and it answers it before the conductor line so a reader meets the scope first.
    sb.AppendLine(sprintf "Cohort scope: %s" (Scope.label frame.Scope)) |> ignore
    sb.AppendLine(leaseHeader) |> ignore
    sb.AppendLine(sprintf "Conductor: %s" conductorText) |> ignore
    // Bounded lists (CohortBoundedView): totals stay in the headers, at most
    // `rowCap` rows are listed most-actionable-first, and a "+N more" line
    // names what was hidden — the status never silently truncates.
    let memberView = CohortBoundedView.members CohortBoundedView.rowCap frame
    let claimView = CohortBoundedView.claims CohortBoundedView.rowCap frame
    let landingView = CohortBoundedView.landings CohortBoundedView.rowCap frame
    // MEMBERS PRESENT, not members ever known. `MemberIds` is every id the cohort has ever
    // seen; a departed member stays in it with `SeatState.Departed` so `ClaimHolderIndex`
    // can still resolve a claim it held. Reporting that array's length said "Members (1)"
    // after the last member had LEFT, which contradicts the VACANT conductor line printed
    // two lines above it — the same panel counted the same way, so this was one bug in two
    // renderers rather than two bugs.
    let presentCount =
      frame.MemberSeat
      |> Array.filter (fun seat ->
        match seat with
        | SageFs.Cohort.SeatState.Present -> true
        | SageFs.Cohort.SeatState.Departed _ -> false)
      |> Array.length
    sb.AppendLine(sprintf "Members (%d):" presentCount) |> ignore
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
        let queuePos = queuePositionTextWith reportFor frame i
        let commits = String.concat "," frame.LandingCommits.[i]
        sb.AppendLine(
          sprintf
            "  - %s requester=%s state=%s %s statement=\"%s\" commits=[%s]"
            lid
            requester
            (landingStateTextWith reportFor lid frame.LandingState.[i])
            queuePos
            (Cohort.Statement.value frame.LandingStatement.[i])
            commits
        ) |> ignore
      if landingView.HiddenTotal > 0 then
        sb.AppendLine(sprintf "  %s" (CohortBoundedView.landingOverflowLabel landingView)) |> ignore
    sb.ToString()

  /// The cohort status text for a holder nobody calls silent. `reportFor` gives what the daemon recorded about a landing's
  /// failing tests, by landing id.
  let renderWith
    (reportFor: string -> FailingTestsText.Report option)
    (frame: SageFs.Cohort.CohortFrame<SageFs.MemberTable.MemberId>)
    : string =
    renderWithSilenceAndReports HolderSilence.NotSilent reportFor frame

  /// The cohort status text with no failure reports: every landing Blocked on failing tests reads by count and id.
  let renderWithSilence (silence: HolderSilence) (frame: SageFs.Cohort.CohortFrame<SageFs.MemberTable.MemberId>) : string =
    renderWithSilenceAndReports silence (fun _ -> None) frame

  /// The cohort status text with no failure reports and a holder nobody calls silent.
  let render (frame: SageFs.Cohort.CohortFrame<SageFs.MemberTable.MemberId>) : string =
    renderWith (fun _ -> None) frame
