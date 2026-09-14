namespace SageFs.Features

open SageFs
open SageFs.Cohort
open SageFs.Measures

/// The time-scrubber's pure projection (sagefs-multiagent-vision.md §6.5,
/// Phase 2 item 16 — "the **scrubber** over a `CohortFrame` `SnapshotRing`
/// with per-tab `Viewing` and `f` = `fork_cohort` at the viewed seq").
///
/// `Cohort.fs` deliberately builds neither a `SnapshotRing` nor `fork` — its
/// own module doc says so explicitly: "`fork` (§5.2's `atSeq` forking) is
/// not built here. Item 7's brief is `decide` + `replay` + `project` + the
/// seventeen properties; `fork` is a thin wrapper over `replay (prefix
/// atSeq)` plus a `Forked` event this module does not yet define, and is
/// left for whichever item actually consumes it." This item is that
/// consumer for the SCRUB half only — a `SnapshotRing` needs no new storage
/// at all: `Cohort.LedgerHead` already carries the `Seq` a frame was
/// projected from, so "the ring" IS the existing ledger
/// (`DashboardInfra.ReadCohortLedger`) plus this module's replay-to-a-prefix
/// helper. "Scrub to seq N" is therefore just a SHORTER prefix of the same
/// ledger the live view already reads, folded through the exact same
/// `Cohort.replay`/`Cohort.project` every live push already calls — no new
/// read model, and every existing renderer (matrix/territory/lanes/
/// inspector) draws a scrubbed frame with zero changes because a scrubbed
/// `CohortFrame` and a live one are the same type.
///
/// `fork_cohort` is NOT built here. Minting a new cohort whose own history
/// starts at the viewed seq would need a `CohortCommand`/`CohortEvent` case
/// to append through `decide` (never mutate ledger state outside the pure
/// core) — `Cohort.fs` has neither, and this item's brief marks `Cohort.fs`
/// read-only. Inventing a fork command in a dashboard-only module would
/// either bypass `decide` entirely (exactly the illegal-state-by-
/// construction failure mode the vision's domain doctrine bans) or require
/// extending the read-only core, which is out of scope here. The fork
/// affordance is rendered disabled with an honest "forking not yet
/// supported" note (`DashboardFragments.renderCohortScrubControl`) rather
/// than faked.
module CohortScrubber =

  /// Every recorded entry whose `Seq` is at or before `targetSeq` — the
  /// prefix `Cohort.replay` folds to reconstruct the state as of that seq.
  /// A `targetSeq` before the ledger's first entry yields an empty prefix
  /// (the pristine, no-members `CohortState.empty` frame); a `targetSeq` at
  /// or beyond the ledger's last entry yields the WHOLE ledger — scrubbing
  /// past the end degrades to "the live view" rather than an error or a
  /// silently truncated one. Public: the lane view (`CohortLanes.project`)
  /// takes a ledger, not a frame, so the dashboard truncates the SAME way
  /// for the lanes panel under scrub — one clamp rule, not two.
  let ledgerThroughSeq (entries: LedgerEntry<'m> list) (targetSeq: int64<ledgerSeq>) : LedgerEntry<'m> list =
    entries |> List.filter (fun e -> e.Seq <= targetSeq)

  /// The highest `Seq` recorded in the ledger, if any — the scrub control's
  /// natural upper bound. `None` for an empty ledger (nothing to scrub).
  let latestSeq (entries: LedgerEntry<'m> list) : int64<ledgerSeq> option =
    entries |> List.tryLast |> Option.map (fun e -> e.Seq)

  /// Pure: `entries + targetSeq -> CohortFrame` (the module doc's brief).
  /// Reconstructs the state as of `targetSeq` by replaying only the prefix
  /// at or before it (`ledgerThroughSeq`), then projects it through the
  /// SAME `Cohort.project` every live push calls. Deterministic — same
  /// `entries`/`targetSeq`/`snapshots` always yield a structurally-equal
  /// frame (inherited from `Cohort.project`'s own determinism, Cohort.fs
  /// property 10).
  let frameAtSeq (entries: LedgerEntry<'m> list) (targetSeq: int64<ledgerSeq>) (snapshots: SessionSnapshot<'m>[]) : CohortFrame<'m> =
    let prefix = ledgerThroughSeq entries targetSeq
    let head = replayHead prefix
    project head snapshots

  /// Parse a scrub-control signal value ("" or a non-numeric string means
  /// "not scrubbing") into a target seq. Pure — no clamping here; clamping
  /// against the ledger's actual bounds is `ledgerThroughSeq`/`frameAtSeq`'s
  /// job, not the parser's, so an out-of-range value the user typed still
  /// parses (and then degrades sanely at replay time) instead of being
  /// silently swallowed as "not scrubbing".
  let tryParseSeq (raw: string) : int64<ledgerSeq> option =
    match System.Int64.TryParse raw with
    | true, v -> Some (LanguagePrimitives.Int64WithMeasure<ledgerSeq> v)
    | false, _ -> None

  /// The frame one connection should render this tick — the pure form of
  /// the push-gate contract (roast-6 §3's `Viewing` pattern, applied to the
  /// cohort): `None` (this tab is not scrubbing) shows whatever frame the
  /// daemon just published; `Some seq` (this tab IS scrubbing) ALWAYS
  /// returns `Scrubbed`, never `Live` — a tab mid-scrub must never have its
  /// view silently replaced by whatever just landed on the live ledger, no
  /// matter how the caller got `liveFrame`. The `Scrubbed` frame is
  /// recomputed fresh every call rather than cached, but because it replays
  /// a FIXED prefix of an append-only ledger, its bytes never change from
  /// tick to tick until the viewed seq itself changes — so a scrubbing tab's
  /// cohort view is pinned, not merely "usually the same".
  [<RequireQualifiedAccess>]
  type ViewedFrame<'m> =
    | Live of CohortFrame<'m>
    | Scrubbed of CohortFrame<'m> * seq: int64<ledgerSeq>

  let resolveViewedFrame
    (entries: LedgerEntry<'m> list)
    (snapshots: SessionSnapshot<'m>[])
    (liveFrame: CohortFrame<'m>)
    (viewingSeq: int64<ledgerSeq> option)
    : ViewedFrame<'m> =
    match viewingSeq with
    | None -> ViewedFrame.Live liveFrame
    | Some seq -> ViewedFrame.Scrubbed(frameAtSeq entries seq snapshots, seq)
