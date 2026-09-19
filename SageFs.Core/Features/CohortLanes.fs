namespace SageFs.Features

open System
open System.Text
open SageFs.Cohort

/// Phase 2 item 16 of sagefs-multiagent-vision.md (§6.5 "the lane view from
/// `LaneEvents` with span flames from `CohortFrame.Spans`"), scoped to what
/// the shipped core actually carries. `Cohort.CohortFrame<'m>` (`Cohort.fs:952`)
/// has neither field — `Cohort.fs`'s own module-level scope note
/// (`Cohort.fs:42-47`) explicitly defers `LaneEvents`/`Spans` to a later
/// phase item and calls a speculative placeholder field here "dead weight
/// the roast doctrine explicitly bans." This module does not invent one.
///
/// The only genuinely timestamped, per-member activity stream the shipped
/// core carries is the ledger itself: `LedgerEntry<'m>.Clock` (`Cohort.fs:892`)
/// paired with `Events: CohortEvent<'m> list` (`Cohort.fs:895`) — recorded in
/// arrival order by `CohortOwner`/`CohortLedgerSqlite` and readable in full
/// via `LedgerPort.ReadAll` (`Features/CohortLedger.fs:19`). This module folds
/// that ledger into flame-graph-shaped spans instead.
///
/// Two span kinds map onto two different lane shapes, by the domain's own
/// structure — not an arbitrary UI choice:
///  - **Claim holds** (`ClaimAcquired`/`ClaimReleased`/`ClaimOrphaned`/
///    `ClaimReassigned`, `Cohort.fs:404-407`) are inherently a per-member
///    exclusive resource hold, so each one renders on that member's own
///    lane.
///  - **Landings** (`LandingQueued`/`LandingLanded`/`LandingWithdrawn`/
///    `LandingVetoed`, `Cohort.fs:409,411-413`) serialize through ONE shared
///    FIFO queue (`CohortState.Queue`) onto one `IntegrationHead` — there is
///    one landing pipeline, not one per member — so every landing renders on
///    a single shared "integration" lane (`Lane.Member = None`).
///
/// No IO, no `DateTime.UtcNow`: `project` is a pure fold over the ledger it
/// is given, and an unresolved (still-open) claim or landing renders with
/// `Span.End = None` rather than reading a wall clock to synthesize one —
/// the caller's renderer decides how far "open" draws.
module CohortLanes =

  [<RequireQualifiedAccess>]
  type SpanKind =
    | Claim of ClaimScope
    | Landing

  [<RequireQualifiedAccess>]
  type SpanOutcome =
    | Open
    | Succeeded
    | Failed

  /// One flame bar. `End = None` means the span is still open as of the
  /// ledger's last recorded entry — never a wall-clock "now". `Track` is the
  /// sub-row within the lane a renderer should stack this span onto (0 =
  /// the lane's own row) so two spans that overlap in time — a member can
  /// hold two DIFFERENT claims at once — never draw on top of each other.
  type Span = {
    Kind: SpanKind
    Outcome: SpanOutcome
    Start: DateTime
    End: DateTime option
    Track: int
    Label: string
  }

  /// `Member = None` is the shared integration lane (see module doc).
  /// `Spans` is sorted by `Start`, ascending.
  type Lane<'m> = {
    Member: 'm option
    Spans: Span list
  }

  type LaneModel<'m> = {
    Lanes: Lane<'m> list
    RangeStart: DateTime
    RangeEnd: DateTime
  }

  let private claimLabel (scope: ClaimScope) : string =
    match scope with
    | ClaimScope.File path -> path
    | ClaimScope.Project path -> path

  /// Fold accumulator. `OpenClaims`/`OpenLandings` track the still-open
  /// half of a span (its start, plus whatever the closing event needs to
  /// file the finished span onto the right lane); `MemberSpans`/
  /// `IntegrationSpans` accumulate CLOSED spans as the ledger is walked.
  /// `ClaimScopes` is metadata that outlives any single hold — a claim's
  /// scope never changes across its life, but `decide` only ever reassigns
  /// an already-`Orphaned` claim (`Cohort.fs:627`, `ClaimNotOrphaned`
  /// otherwise), which means by the time a `ClaimReassigned` event arrives
  /// this module has ALREADY removed the claim from `OpenClaims` (the prior
  /// `ClaimOrphaned` closed it) — so the scope has to come from somewhere
  /// that survives that close.
  type private Acc<'m when 'm: comparison> = {
    OpenClaims: Map<ClaimId, DateTime * 'm * ClaimScope>
    ClaimScopes: Map<ClaimId, ClaimScope>
    OpenLandings: Map<LandingId, DateTime * 'm>
    MemberSpans: Map<'m, Span list>
    IntegrationSpans: Span list
  }

  let private addMemberSpan (holder: 'm) (span: Span) (acc: Acc<'m>) : Acc<'m> =
    { acc with
        MemberSpans =
          acc.MemberSpans
          |> Map.change holder (function
            | Some spans -> Some(span :: spans)
            | None -> Some [ span ]) }

  let private closeClaim (claimId: ClaimId) (clock: DateTime) (outcome: SpanOutcome) (acc: Acc<'m>) : Acc<'m> =
    match Map.tryFind claimId acc.OpenClaims with
    | None -> acc
    | Some(start, holder, scope) ->
      let span = { Kind = SpanKind.Claim scope; Outcome = outcome; Start = start; End = Some clock; Track = 0; Label = claimLabel scope }
      { acc with OpenClaims = Map.remove claimId acc.OpenClaims }
      |> addMemberSpan holder span

  let private closeLanding (landingId: LandingId) (clock: DateTime) (outcome: SpanOutcome) (acc: Acc<'m>) : Acc<'m> =
    match Map.tryFind landingId acc.OpenLandings with
    | None -> acc
    | Some(start, _requester) ->
      let (LandingId lid) = landingId
      let span = { Kind = SpanKind.Landing; Outcome = outcome; Start = start; End = Some clock; Track = 0; Label = lid }
      { acc with
          OpenLandings = Map.remove landingId acc.OpenLandings
          IntegrationSpans = span :: acc.IntegrationSpans }

  let private applyEvent (clock: DateTime) (ev: CohortEvent<'m>) (acc: Acc<'m>) : Acc<'m> =
    match ev with
    | CohortEvent.ClaimAcquired(claimId, scope, holder, _fence) ->
      { acc with
          OpenClaims = Map.add claimId (clock, holder, scope) acc.OpenClaims
          ClaimScopes = Map.add claimId scope acc.ClaimScopes }
    | CohortEvent.ClaimReleased(claimId, _by, _fence) ->
      closeClaim claimId clock SpanOutcome.Succeeded acc
    | CohortEvent.ClaimOrphaned(claimId, _previousHolder, _fence) ->
      closeClaim claimId clock SpanOutcome.Failed acc
    | CohortEvent.ClaimReassigned(claimId, toMember, _fence) ->
      // Always follows an Orphaned close (see `Acc.ClaimScopes`'s doc) — this
      // opens a fresh span for `toMember` rather than trying to close one,
      // reading the scope from the side table since `OpenClaims` no longer
      // has it.
      match Map.tryFind claimId acc.ClaimScopes with
      | None -> acc
      | Some scope -> { acc with OpenClaims = Map.add claimId (clock, toMember, scope) acc.OpenClaims }
    | CohortEvent.LandingQueued(landingId, requester) ->
      { acc with OpenLandings = Map.add landingId (clock, requester) acc.OpenLandings }
    | CohortEvent.LandingLanded(landingId, _integrationCommit) ->
      closeLanding landingId clock SpanOutcome.Succeeded acc
    | CohortEvent.LandingWithdrawn landingId ->
      closeLanding landingId clock SpanOutcome.Failed acc
    | CohortEvent.LandingVetoed(landingId, _by, _reason) ->
      closeLanding landingId clock SpanOutcome.Failed acc
    | CohortEvent.MemberJoined _
    | CohortEvent.MemberDeparted _
    | CohortEvent.LeaseRenewed _
    | CohortEvent.ConductorBound _
    | CohortEvent.ConductorDelegated _
    | CohortEvent.ClaimViolationObserved _
    | CohortEvent.LandingStateChanged _
    | CohortEvent.IntegrationConfigured _
    // armfix (cmd-handoff.md item B2): a resolved veto has no lane-span shape
    // of its own — the landing's span was never closed by the veto (unlike
    // `LandingVetoed`, which does, via `closeLanding ... SpanOutcome.Failed`),
    // so resolving it re-opens the SAME landing id's span the next
    // `LandingQueued`-shaped re-entry would otherwise expect; there is no
    // re-open primitive here, and none is needed — the re-Queue emits its own
    // `LandingStateChanged`, which this match already treats as a no-op.
    | CohortEvent.LandingVetoResolved _
    // Retention pruning (`Cohort.Retention.sweep`) removes settled history
    // from `CohortState`; the lane spans were already closed by the events
    // that settled them, so a prune adds nothing to draw.
    | CohortEvent.ClaimPruned _
    | CohortEvent.LandingPruned _
    | CohortEvent.MemberPurged _ -> acc

  /// Greedy interval-scheduling track assignment (classic Gantt/flame-graph
  /// stacking): walk spans in `Start` order, reuse the lowest-numbered track
  /// whose occupant has already ended by this span's `Start`, else open a
  /// new track. An open span (`End = None`) blocks its track forever — a
  /// still-active hold can never be double-booked. Stable for equal `Start`
  /// (input order is already deterministic — see `project`), so the same
  /// span list always gets the same track assignment.
  let private assignTracks (spans: Span list) : Span list =
    match spans with
    | [] -> []
    | _ ->
      let sorted = spans |> List.sortBy (fun s -> s.Start)
      let assigned, _ =
        sorted
        |> List.fold
          (fun (assignedRev: Span list, tracks: (int * DateTime option) list) span ->
            let freeIdx =
              tracks
              |> List.tryFindIndex (fun (_, busyUntil) ->
                match busyUntil with
                | Some until -> until <= span.Start
                | None -> false)
            match freeIdx with
            | Some idx ->
              let trackNum, _ = tracks.[idx]
              let tracks' = tracks |> List.mapi (fun i t -> if i = idx then (trackNum, span.End) else t)
              { span with Track = trackNum } :: assignedRev, tracks'
            | None ->
              let trackNum = tracks.Length
              { span with Track = trackNum } :: assignedRev, tracks @ [ trackNum, span.End ])
          ([], [])
      assigned |> List.rev

  /// Pure: `LedgerEntry<'m> list -> LaneModel<'m>`. The same ledger always
  /// projects the same model (fold order is the ledger's own `Seq` order,
  /// already deterministic; `Map.toArray`/`List.sortBy` below give every
  /// list a stable order too). An empty ledger projects an empty model with
  /// no members and a zero-width range — never a partial/garbage lane list.
  let project (entries: LedgerEntry<'m> list) : LaneModel<'m> =
    match entries with
    | [] -> { Lanes = []; RangeStart = DateTime.MinValue; RangeEnd = DateTime.MinValue }
    | _ ->
      let rangeStart = entries |> List.map (fun e -> e.Clock) |> List.min
      let rangeEnd = entries |> List.map (fun e -> e.Clock) |> List.max

      let timedEvents =
        entries |> List.collect (fun e -> e.Events |> List.map (fun ev -> e.Clock, ev))

      // First-join order (the order members actually entered the cohort),
      // not a sort by identity — matches the arrival order the ledger
      // itself records.
      let joinedMembers =
        timedEvents
        |> List.choose (function
          | _, CohortEvent.MemberJoined(who, _, _) -> Some who
          | _ -> None)
        |> List.distinct

      let emptyAcc: Acc<'m> =
        { OpenClaims = Map.empty; ClaimScopes = Map.empty; OpenLandings = Map.empty; MemberSpans = Map.empty; IntegrationSpans = [] }
      let finalAcc = timedEvents |> List.fold (fun acc (clock, ev) -> applyEvent clock ev acc) emptyAcc

      // Anything still open when the ledger ends stays open (End = None) —
      // still recorded, never dropped or force-closed at a synthetic time.
      let memberSpansWithOpen =
        finalAcc.OpenClaims
        |> Map.toList
        |> List.fold
          (fun spans (_claimId, (start, holder, scope)) ->
            let span = { Kind = SpanKind.Claim scope; Outcome = SpanOutcome.Open; Start = start; End = None; Track = 0; Label = claimLabel scope }
            spans
            |> Map.change holder (function
              | Some xs -> Some(span :: xs)
              | None -> Some [ span ]))
          finalAcc.MemberSpans

      let integrationSpansWithOpen =
        finalAcc.OpenLandings
        |> Map.toList
        |> List.fold
          (fun spans (LandingId lid, (start, _requester)) ->
            let span = { Kind = SpanKind.Landing; Outcome = SpanOutcome.Open; Start = start; End = None; Track = 0; Label = lid }
            span :: spans)
          finalAcc.IntegrationSpans

      let integrationLane = { Member = None; Spans = integrationSpansWithOpen |> assignTracks }
      let memberLanes =
        joinedMembers
        |> List.map (fun m ->
          let spans = memberSpansWithOpen |> Map.tryFind m |> Option.defaultValue [] |> assignTracks
          { Member = Some m; Spans = spans })

      { Lanes = integrationLane :: memberLanes
        RangeStart = rangeStart
        RangeEnd = rangeEnd }

  // ── SVG rendering ───────────────────────────────────────────────────────

  let private escapeXml (s: string) : string =
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;")

  let private outcomeColor (outcome: SpanOutcome) : string =
    match outcome with
    | SpanOutcome.Open -> "#f7d24f"
    | SpanOutcome.Succeeded -> "#4caf50"
    | SpanOutcome.Failed -> "#e15554"

  /// Renders a `LaneModel` as a raw inline SVG flame-graph string — the same
  /// no-served-route, no-new-stream pattern `CohortTerritory.toSvg` uses
  /// (embedded via `Text.raw`, riding the dashboard's existing morph).
  /// `labelMember` renders a lane's `'m option` (`None` is the integration
  /// lane) — kept as an injected function rather than `sprintf "%A"` so this
  /// module never assumes anything about `'m` beyond what `project` already
  /// requires (comparison), matching `Cohort.fs`'s own "member identity is
  /// opaque" discipline. Deterministic: the same model and dimensions always
  /// produce byte-identical markup, so it composes with `SnapshotRenderGuard`
  /// the same way `CohortTerritory.toSvg`/`CohortMatrixRender.toPng` do. An
  /// empty model (no lanes) still renders a valid, empty `<svg>`.
  let toSvg (labelMember: 'm option -> string) (width: float) (rowHeight: float) (model: LaneModel<'m>) : string =
    let rangeMs = max 1.0 ((model.RangeEnd - model.RangeStart).TotalMilliseconds)
    let labelColW = 90.0
    let plotW = max 1.0 (width - labelColW)
    let xOf (t: DateTime) = labelColW + plotW * ((t - model.RangeStart).TotalMilliseconds / rangeMs)

    let rowsWithY, totalHeight =
      model.Lanes
      |> List.fold
        (fun (rowsRev, y) lane ->
          let tracks = if lane.Spans.IsEmpty then 1 else (lane.Spans |> List.map (fun s -> s.Track) |> List.max) + 1
          let laneHeight = rowHeight * float tracks
          (lane, y, laneHeight) :: rowsRev, y + laneHeight)
        ([], 0.0)
    let rowsWithY = rowsWithY |> List.rev
    let totalHeight = max rowHeight totalHeight

    let sb = StringBuilder()
    sb.Append(sprintf "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 %g %g\" width=\"100%%\" height=\"auto\">" width totalHeight)
    |> ignore
    for lane, laneTop, _laneHeight in rowsWithY do
      sb.Append(
        sprintf
          "<text x=\"2\" y=\"%g\" font-size=\"9\" fill=\"#999999\">%s</text>"
          (laneTop + 11.0)
          (escapeXml (labelMember lane.Member)))
      |> ignore
      for span in lane.Spans do
        let x0 = xOf span.Start
        let x1 = match span.End with
                 | Some e -> xOf e
                 | None -> labelColW + plotW
        let w = max 2.0 (x1 - x0)
        let sy = laneTop + float span.Track * rowHeight + 2.0
        let h = max 1.0 (rowHeight - 4.0)
        sb.Append(
          sprintf
            "<rect x=\"%g\" y=\"%g\" width=\"%g\" height=\"%g\" fill=\"%s\"><title>%s</title></rect>"
            x0 sy w h (outcomeColor span.Outcome) (escapeXml span.Label))
        |> ignore
    sb.Append("</svg>") |> ignore
    sb.ToString()
