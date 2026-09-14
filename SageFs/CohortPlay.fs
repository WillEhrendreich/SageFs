module SageFs.CohortPlay

open System.IO
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Features

/// The read side of `sagefs record`/`play` (sagefs-multiagent-vision.md §6,
/// Phase 1 item 18b): an OFFLINE CLI command that replays a portable cohort
/// ledger file (`*.ledger.jsonl`, written by item 18a's `CohortLedgerExport`)
/// and prints the reconstructed cohort state. No daemon, no live session —
/// `Cohort.replay`/`Cohort.project` are pure, so this is a pure fold over a
/// file's contents.
///
/// `renderPlaySummary` is the pure half (testable directly, no file IO);
/// `runPlay` is the thin IO wrapper (read file -> parse -> replay -> render).
/// Neither ever throws — every failure mode (missing file, malformed JSONL)
/// is a `Result` `Error` with a human-readable message, mirroring
/// `CohortLedgerExport.fromJsonl`'s fail-closed discipline.

/// Renders a concise, human-readable summary of a replayed cohort. Every
/// value is derived from the given ledger head / frame — nothing here is
/// hardcoded. `frame` (from `Cohort.project head [||]`) supplies the
/// member/claim read model; `head.State`'s `Queue`/`Landings` supply the
/// landing-queue detail `CohortFrame` does not yet carry (see the module-level
/// scope note in `Cohort.fs`: the landing read-model fields are a later
/// phase item).
let renderPlaySummary (entryCount: int) (head: LedgerHead<MemberId>) (frame: CohortFrame<MemberId>) : string =
  let sb = System.Text.StringBuilder()
  let line (s: string) = sb.AppendLine s |> ignore
  let state = head.State

  line (sprintf "Ledger: %d entries, head seq=%d" entryCount (int64 frame.Version))
  line ""

  line (sprintf "Members (%d):" frame.MemberIds.Length)
  Array.zip3 frame.MemberIds frame.MemberRole frame.MemberSeat
  |> Array.sortBy (fun (m, _, _) -> MemberId.display m)
  |> Array.iter (fun (m, role, seat) ->
    let conductorTag = if frame.Conductor = Some m then " [Conductor]" else ""
    let presence =
      match seat with
      | SeatState.Present -> "present"
      | SeatState.Departed since -> sprintf "departed since %s" (since.ToString "o")
    line (sprintf "  - %s  role=%A  %s%s" (MemberId.display m) role presence conductorTag))
  line ""

  match frame.Conductor with
  | Some c -> line (sprintf "Conductor: %s" (MemberId.display c))
  | None -> line "Conductor: (none)"
  line ""

  line (sprintf "Integration head: %s" state.IntegrationHead)
  line ""

  let scopeText scope =
    match scope with
    | ClaimScope.File p -> sprintf "file %s" p
    | ClaimScope.Project p -> sprintf "project %s" p

  let heldClaims =
    [ for i in 0 .. frame.ClaimIds.Length - 1 do
        match frame.ClaimState.[i] with
        | ClaimState.Held holder ->
          let (ClaimId cid) = frame.ClaimIds.[i]
          yield cid, scopeText frame.ClaimScope.[i], MemberId.display holder, int64 frame.ClaimFence.[i]
        | _ -> () ]
    |> List.sortBy (fun (cid, _, _, _) -> cid)

  line (sprintf "Held claims (%d):" heldClaims.Length)
  heldClaims
  |> List.iter (fun (cid, scope, holder, fence) ->
    line (sprintf "  - %s  holder=%s  scope=%s  fence=%d" cid holder scope fence))
  line ""

  line (sprintf "Landing queue (%d):" state.Queue.Length)
  state.Queue
  |> List.iter (fun id ->
    let (LandingId lid) = id
    match Map.tryFind id state.Landings with
    | Some req ->
      let stateText =
        match req.State with
        | LandingState.Queued -> "Queued"
        | LandingState.Rebasing onto -> sprintf "Rebasing onto %s" onto
        | LandingState.Verifying(baseHead, rebasedHead, affected, running) ->
          sprintf "Verifying base=%s rebased=%s affected=%d running=%d" baseHead rebasedHead affected running
        | LandingState.Blocked(blocker, next) -> sprintf "Blocked (%A) next=%A" blocker next
        | LandingState.Landed sha -> sprintf "Landed %s" sha
        | LandingState.Withdrawn -> "Withdrawn"
      line (sprintf "  - %s  requester=%s  %s" lid (MemberId.display req.Requester) stateText)
    | None -> line (sprintf "  - %s  (missing landing record)" lid))

  sb.ToString()

/// Read `path`, parse it as a ledger JSONL, replay + project it, and render
/// the summary. Fail-closed: a missing file, an unreadable file, or a
/// malformed line all come back as `Error msg` — this function never throws.
let runPlay (path: string) : Result<string, string> =
  if not (File.Exists path) then
    Error(sprintf "ledger file not found: %s" path)
  else
    try
      let jsonl = File.ReadAllText path
      match CohortLedgerExport.fromJsonl jsonl with
      | Error msg -> Error msg
      | Ok entries ->
        let head = Cohort.replayHead entries
        let frame = Cohort.project head [||]
        Ok(renderPlaySummary entries.Length head frame)
    with ex ->
      Error(sprintf "failed to read %s: %s" path ex.Message)
