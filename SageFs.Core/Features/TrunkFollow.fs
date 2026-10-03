namespace SageFs.Features

open SageFs
open SageFs.Cohort

/// The trunk follows what lands (live-loop-plan-2026-10-02.md, direction 2).
///
/// The cohort lands agents' work into the integration branch. The TRUNK is the checkout the cohort lands into: a daemon-owned
/// git checkout that is moved to each landing's commit once the landing has landed, and nowhere else. A TRUNK SESSION is a
/// session whose working directory is that checkout. When a session there runs an app, the landing is carried into the
/// running process by the same save pipeline a person's save takes, and the daemon records what that pipeline said about it.
///
/// This file is the pure decision. The shell (`TrunkFollowOwner`) moves the checkout and asks the worker; this folds what
/// they answer. No IO, no clock, no ids minted here, so every order a landing and its answers can arrive in is a reproducible
/// test input (`SageFs.Simulation.TrunkFollowSim`).
///
/// Three rules the shape of it enforces:
///  - Only a landing that LANDED is acted on. The input is `LandingLanded`, which the cohort emits after the landing is on
///    its ledger; a landing that was blocked, withdrawn or is still being verified never reaches this machine.
///  - Landings are acted on one at a time, in the order they landed, and none is dropped while another is in flight.
///  - The record says what the save pipeline said and nothing more. A patch is `PatchPending` until the worker has seen the
///    new body run, and only a later report from the worker makes it `Patched`.
module TrunkFollow =

  /// What a landing did to one file of the trunk checkout.
  [<RequireQualifiedAccess>]
  type SaveKind =
    | Changed
    | Created
    | Deleted

  /// A file the trunk checkout moved under, as the save pipeline is told about it.
  type SavedFile = { Path: string; Kind: SaveKind }

  /// Where a worker's save pipeline takes its saves from. A worker takes them from the files it watches, as it always did. A
  /// trunk session takes them from landings only: the daemon moves the trunk checkout's files itself, so a watcher that also
  /// saw those writes would run the same save twice, and report the second as a save that changed nothing over the first.
  [<RequireQualifiedAccess>]
  type SaveSource =
    | WatchedFiles
    | LandedOnly

  /// The extensions of the files a build reads. A landing that changes none of them (a document, a script) leaves every compiled
  /// output as it was.
  let private buildInputExtensions = [ ".fs"; ".fsi"; ".fsproj"; ".props"; ".targets" ]

  /// Does a file the landing changed feed the build of the project a trunk session runs?
  let affectsBuild (file: SavedFile) : bool =
    let extension = System.IO.Path.GetExtension file.Path
    buildInputExtensions |> List.exists (fun e -> System.String.Equals(e, extension, System.StringComparison.OrdinalIgnoreCase))

  /// A landing that has landed: the cohort's own id, and the commit the integration branch moved to.
  type LandedLanding = { Landing: LandingId; Commit: string }

  /// What a session in the trunk checkout is, as far as a landing is concerned.
  [<RequireQualifiedAccess>]
  type TrunkSessionState =
    /// Ready and running an app: the landing is carried into the process.
    | Serving
    /// Ready, with no app running: nothing to update.
    | NoRunningApp
    /// Faulted, stopped, or never became ready, so nothing could be asked of it.
    | Unavailable of reason: string

  type TrunkSession = { Session: string; State: TrunkSessionState }

  /// What moving the trunk checkout to a landing's commit came to.
  [<RequireQualifiedAccess>]
  type TrunkMove =
    /// The checkout is at the commit. `files` are what changed under it, and `sessions` are the sessions that work in it, read
    /// before the files changed.
    | Moved of files: SavedFile list * sessions: TrunkSession list
    | NotMoved of reason: string

  /// Why a restart was asked for, the way the save pipeline named it: the refusal's case and its message.
  type RestartCause = { Case: string; Message: string }

  /// What the save pipeline said about one file.
  [<RequireQualifiedAccess>]
  type FileOutcome =
    /// The pipeline ran and reached a verdict, with the causes it named when the verdict is a restart.
    | Reloaded of ReloadFacts * causes: RestartCause list
    /// The file is not something a running process can take: a project file changed, so the app has to be rebuilt.
    | NeedsRebuild of reason: string
    /// Not source the pipeline watches, so it did nothing with it.
    | NotWatched
    /// The pipeline ran and said nothing, so no verdict can be claimed.
    | NoVerdict of reason: string

  type FileVerdict = { File: string; Outcome: FileOutcome }

  /// What came of delivering a landing to one trunk session.
  [<RequireQualifiedAccess>]
  type SessionOutcome =
    /// The session runs no app, so there was nothing to update.
    | NoApp
    /// The save pipeline answered for every file the landing changed.
    | Delivered of FileVerdict list
    /// The worker has no hot reload pipeline (no project directories, or watching disabled).
    | NoPipeline of reason: string
    /// The worker did not answer.
    | Unreachable of reason: string
    /// The session was not in a state to ask.
    | Unavailable of reason: string

  type SessionDelivery = { Session: string; Outcome: SessionOutcome }

  /// What the trunk did with one landing.
  [<RequireQualifiedAccess>]
  type TrunkVerdict =
    /// No session works in the trunk checkout. The checkout moved all the same, so a session started there later has it.
    | NoTrunkSession
    /// The checkout could not be moved to the landing's commit, so nothing was delivered.
    | NotMoved of reason: string
    /// The checkout moved, and each trunk session was dealt with.
    | Followed of SessionDelivery list

  type TrunkRecord = { Landing: LandingId; Commit: string; Verdict: TrunkVerdict }

  /// How many records the machine keeps. The newest are the ones anyone reads, and a trunk that follows landings for a day
  /// should not grow without bound.
  let recordLimit = 64

  [<RequireQualifiedAccess>]
  type Phase =
    | Idle
    /// The checkout is being moved to this landing's commit.
    | Moving of LandedLanding
    /// Delivered to these sessions, waiting for the ones in `waiting`.
    | Delivering of landing: LandedLanding * files: SavedFile list * waiting: string list * decided: SessionDelivery list

  type TrunkMachine =
    { Phase: Phase
      /// Landings that landed while another was being followed, oldest first.
      Queued: LandedLanding list
      /// Oldest first, at most `recordLimit`.
      Records: TrunkRecord list
      /// Reports of a finished save that arrived for a session whose delivery has not been answered yet, in arrival order.
      Early: Map<string, ReloadFacts list> }

  let initial : TrunkMachine = { Phase = Phase.Idle; Queued = []; Records = []; Early = Map.empty }

  [<RequireQualifiedAccess>]
  type TrunkEvent =
    /// The cohort recorded this landing as landed.
    | Landed of LandedLanding
    | Moved of LandingId * TrunkMove
    | Answered of LandingId * session: string * SessionOutcome
    /// A session's reload row finished a save: a later word on a patch that was pending.
    | ReloadReported of session: string * ReloadFacts

  [<RequireQualifiedAccess>]
  type TrunkEffect =
    | MoveTrunk of LandedLanding
    | Deliver of LandedLanding * session: string * files: SavedFile list

  /// The one place a cohort event becomes a trunk event: only a landing that landed.
  let ofCohortEvent (event: CohortEvent<'m>) : TrunkEvent option =
    match event with
    | CohortEvent.LandingLanded (landing, commit) -> Some (TrunkEvent.Landed { Landing = landing; Commit = commit })
    // A cohort opening says which scope exists. The trunk follows one cohort's landings and
    // nothing else, so this is not its business.
    | CohortEvent.CohortOpened _
    | CohortEvent.MemberJoined _
    | CohortEvent.MemberDeparted _
    | CohortEvent.LeaseRenewed _
    | CohortEvent.ConductorBound _
    | CohortEvent.ConductorDelegated _
    | CohortEvent.ClaimAcquired _
    | CohortEvent.ClaimReleased _
    | CohortEvent.ClaimOrphaned _
    | CohortEvent.ClaimReassigned _
    | CohortEvent.ClaimViolationObserved _
    | CohortEvent.LandingQueued _
    | CohortEvent.LandingStateChanged _
    | CohortEvent.LandingWithdrawn _
    | CohortEvent.LandingVetoed _
    | CohortEvent.IntegrationConfigured _
    | CohortEvent.LandingVetoResolved _
    | CohortEvent.ClaimPruned _
    | CohortEvent.LandingPruned _
    | CohortEvent.MemberPurged _ -> None

  /// Has the machine already seen this landing, in flight, queued or recorded?
  let private knows (machine: TrunkMachine) (landing: LandingId) : bool =
    let inFlight =
      match machine.Phase with
      | Phase.Idle -> false
      | Phase.Moving l
      | Phase.Delivering (l, _, _, _) -> l.Landing = landing
    inFlight
    || machine.Queued |> List.exists (fun l -> l.Landing = landing)
    || machine.Records |> List.exists (fun r -> r.Landing = landing)

  let private isSettled (facts: ReloadFacts) : bool =
    facts.Case = ReloadCase.Patched || facts.Case = ReloadCase.NeverEntered

  /// A pending patch is settled by the report that follows it, when it is the same kind of patch.
  let private settles (pending: ReloadFacts) (reported: ReloadFacts) : bool =
    pending.Case = ReloadCase.PatchPending && isSettled reported && pending.Mechanism = reported.Mechanism

  /// Settle the first pending file verdict a report settles, if any.
  let private settleIn (verdicts: FileVerdict list) (reported: ReloadFacts) : FileVerdict list * bool =
    let rec go (remaining: FileVerdict list) (acc: FileVerdict list) =
      match remaining with
      | [] -> List.rev acc, false
      | ({ Outcome = FileOutcome.Reloaded (pending, causes) } as verdict) :: rest when settles pending reported ->
        List.rev acc @ ({ verdict with Outcome = FileOutcome.Reloaded (reported, causes) } :: rest), true
      | verdict :: rest -> go rest (verdict :: acc)
    go verdicts []

  let private verdictsOf (outcome: SessionOutcome) : FileVerdict list =
    match outcome with
    | SessionOutcome.Delivered verdicts -> verdicts
    | SessionOutcome.NoApp
    | SessionOutcome.NoPipeline _
    | SessionOutcome.Unreachable _
    | SessionOutcome.Unavailable _ -> []

  /// Settle the oldest pending patch of a session that a report settles, across the records.
  let private settleInRecords (records: TrunkRecord list) (session: string) (reported: ReloadFacts) : TrunkRecord list * bool =
    let rec go (remaining: TrunkRecord list) (acc: TrunkRecord list) =
      match remaining with
      | [] -> List.rev acc, false
      | record :: rest ->
        match record.Verdict with
        | TrunkVerdict.Followed deliveries ->
          let rec across (ds: SessionDelivery list) (accD: SessionDelivery list) =
            match ds with
            | [] -> None
            | d :: more when d.Session = session ->
              match d.Outcome with
              | SessionOutcome.Delivered verdicts ->
                let settled, applied = settleIn verdicts reported
                match applied with
                | true -> Some (List.rev accD @ ({ d with Outcome = SessionOutcome.Delivered settled } :: more))
                | false -> across more (d :: accD)
              | SessionOutcome.NoApp
              | SessionOutcome.NoPipeline _
              | SessionOutcome.Unreachable _
              | SessionOutcome.Unavailable _ -> across more (d :: accD)
            | d :: more -> across more (d :: accD)
          match across deliveries [] with
          | Some updated -> List.rev acc @ ({ record with Verdict = TrunkVerdict.Followed updated } :: rest), true
          | None -> go rest (record :: acc)
        | TrunkVerdict.NoTrunkSession
        | TrunkVerdict.NotMoved _ -> go rest (record :: acc)
    go records []

  let private append (record: TrunkRecord) (records: TrunkRecord list) : TrunkRecord list =
    let all = records @ [ record ]
    match List.length all > recordLimit with
    | true -> List.skip (List.length all - recordLimit) all
    | false -> all

  /// Close the landing in flight with its verdict, and start on the next one that landed meanwhile.
  let private finish (machine: TrunkMachine) (landing: LandedLanding) (verdict: TrunkVerdict) : TrunkMachine * TrunkEffect list =
    let record = { Landing = landing.Landing; Commit = landing.Commit; Verdict = verdict }
    let recorded = { machine with Records = append record machine.Records }
    match recorded.Queued with
    | [] -> { recorded with Phase = Phase.Idle }, []
    | next :: rest -> { recorded with Phase = Phase.Moving next; Queued = rest }, [ TrunkEffect.MoveTrunk next ]

  let private delivery (session: TrunkSession) : SessionDelivery option =
    match session.State with
    | TrunkSessionState.Serving -> None
    | TrunkSessionState.NoRunningApp -> Some { Session = session.Session; Outcome = SessionOutcome.NoApp }
    | TrunkSessionState.Unavailable reason -> Some { Session = session.Session; Outcome = SessionOutcome.Unavailable reason }

  /// The fold. Total: an event that does not belong to what is in flight is dropped, never applied to something else.
  let step (machine: TrunkMachine) (event: TrunkEvent) : TrunkMachine * TrunkEffect list =
    match event with
    | TrunkEvent.Landed landing ->
      match knows machine landing.Landing with
      | true -> machine, []
      | false ->
        match machine.Phase with
        | Phase.Idle -> { machine with Phase = Phase.Moving landing }, [ TrunkEffect.MoveTrunk landing ]
        | Phase.Moving _
        | Phase.Delivering _ -> { machine with Queued = machine.Queued @ [ landing ] }, []
    | TrunkEvent.Moved (id, move) ->
      match machine.Phase with
      | Phase.Moving landing when landing.Landing = id ->
        match move with
        | TrunkMove.NotMoved reason -> finish machine landing (TrunkVerdict.NotMoved reason)
        | TrunkMove.Moved (_, []) -> finish machine landing TrunkVerdict.NoTrunkSession
        | TrunkMove.Moved (files, sessions) ->
          let decided = sessions |> List.choose delivery
          let serving = sessions |> List.filter (fun s -> s.State = TrunkSessionState.Serving) |> List.map _.Session
          match serving with
          | [] -> finish machine landing (TrunkVerdict.Followed decided)
          | _ ->
            { machine with Phase = Phase.Delivering (landing, files, serving, decided) },
            serving |> List.map (fun s -> TrunkEffect.Deliver (landing, s, files))
      | Phase.Idle
      | Phase.Moving _
      | Phase.Delivering _ -> machine, []
    | TrunkEvent.Answered (id, session, outcome) ->
      match machine.Phase with
      | Phase.Delivering (landing, files, waiting, decided) when landing.Landing = id && List.contains session waiting ->
        let early = Map.tryFind session machine.Early |> Option.defaultValue []
        let settledOutcome =
          match outcome with
          | SessionOutcome.Delivered verdicts ->
            SessionOutcome.Delivered (early |> List.fold (fun vs reported -> fst (settleIn vs reported)) verdicts)
          | other -> other
        let remaining = waiting |> List.filter (fun s -> s <> session)
        let decided' = decided @ [ { Session = session; Outcome = settledOutcome } ]
        let machine' = { machine with Early = Map.remove session machine.Early }
        match remaining with
        | [] -> finish machine' landing (TrunkVerdict.Followed decided')
        | _ -> { machine' with Phase = Phase.Delivering (landing, files, remaining, decided') }, []
      | Phase.Idle
      | Phase.Moving _
      | Phase.Delivering _ -> machine, []
    | TrunkEvent.ReloadReported (session, facts) ->
      match isSettled facts with
      | false -> machine, []
      | true ->
        let records, applied = settleInRecords machine.Records session facts
        match applied with
        | true -> { machine with Records = records }, []
        | false ->
          match machine.Phase with
          | Phase.Delivering (_, _, waiting, _) when List.contains session waiting ->
            let early = Map.tryFind session machine.Early |> Option.defaultValue []
            { machine with Early = Map.add session (early @ [ facts ]) machine.Early }, []
          | Phase.Idle
          | Phase.Moving _
          | Phase.Delivering _ -> machine, []

  // -- what the save pipeline's payload says -----------------------------------------------------------------------------

  /// The causes a payload names: each refusal's case and message, in the order the worker listed them.
  let private causesOf (payload: string) : RestartCause list =
    try
      use doc = System.Text.Json.JsonDocument.Parse payload
      match doc.RootElement.TryGetProperty "reasons" with
      | true, reasons when reasons.ValueKind = System.Text.Json.JsonValueKind.Array ->
        [ for reason in reasons.EnumerateArray() ->
            let text (name: string) =
              match reason.TryGetProperty name with
              | true, value when value.ValueKind = System.Text.Json.JsonValueKind.String -> value.GetString()
              | _ -> ""
            { Case = text "case"; Message = text "message" } ]
      | _ -> []
    with :? System.Text.Json.JsonException -> []

  /// What the save pipeline's last terminal payload says about a file: its verdict and the causes it named, or why it is not a
  /// verdict.
  let outcomeOfPayload (payload: string) : FileOutcome =
    match SessionReload.ofPayloadJson payload with
    | Result.Ok (SessionReload.Finished facts) -> FileOutcome.Reloaded (facts, causesOf payload)
    | Result.Ok other -> FileOutcome.NoVerdict (SessionReload.describe other)
    | Result.Error why -> FileOutcome.NoVerdict (ReloadPayloadError.describe why)

  // -- what is said about it ---------------------------------------------------------------------------------------------

  let private firstLine (text: string) : string =
    match text.Split('\n') |> Array.tryHead with
    | Some line -> line.Trim()
    | None -> ""

  let private describeCause (cause: RestartCause) : string =
    match firstLine cause.Message with
    | "" -> cause.Case
    | message -> sprintf "%s: %s" cause.Case message

  let private fileName (path: string) : string = System.IO.Path.GetFileName path

  let describeFile (verdict: FileVerdict) : string =
    let name = fileName verdict.File
    match verdict.Outcome with
    | FileOutcome.Reloaded (facts, causes) ->
      let mechanism =
        match SageFs.Features.ReloadOutcome.PatchMechanism.wireName facts.Mechanism with
        | "" -> ""
        | wire -> sprintf " by %s" wire
      let causeText =
        match causes with
        | [] -> ""
        | _ -> sprintf " (%s)" (causes |> List.map describeCause |> String.concat "; ")
      sprintf "%s %s%s%s" name (ReloadCase.token facts.Case) mechanism causeText
    | FileOutcome.NeedsRebuild reason -> sprintf "%s needs a rebuild: %s" name reason
    | FileOutcome.NotWatched -> sprintf "%s is not watched, so nothing ran" name
    | FileOutcome.NoVerdict reason -> sprintf "%s ran the save pipeline and it reached no verdict: %s" name reason

  let describeDelivery (delivery: SessionDelivery) : string =
    match delivery.Outcome with
    | SessionOutcome.NoApp ->
      sprintf "session %s: no running app to update (the trunk checkout holds the landing; rebuild the session before run_app, so the app starts from it)" delivery.Session
    | SessionOutcome.Delivered [] -> sprintf "session %s: the landing changed no file the app runs" delivery.Session
    | SessionOutcome.Delivered verdicts ->
      sprintf "session %s: %s" delivery.Session (verdicts |> List.map describeFile |> String.concat "; ")
    | SessionOutcome.NoPipeline reason -> sprintf "session %s: no hot reload pipeline: %s" delivery.Session reason
    | SessionOutcome.Unreachable reason -> sprintf "session %s: did not answer: %s" delivery.Session reason
    | SessionOutcome.Unavailable reason -> sprintf "session %s: not available: %s" delivery.Session reason

  let describeVerdict (verdict: TrunkVerdict) : string =
    match verdict with
    | TrunkVerdict.NoTrunkSession -> "no session works in the trunk checkout"
    | TrunkVerdict.NotMoved reason -> sprintf "the trunk checkout did not move: %s" reason
    | TrunkVerdict.Followed [] -> "no session works in the trunk checkout"
    | TrunkVerdict.Followed deliveries -> deliveries |> List.map describeDelivery |> String.concat "; "

  /// The `get_cohort_status` lines for the trunk: one per landing the trunk has dealt with, newest last, and one for the
  /// landing in flight.
  let statusLines (machine: TrunkMachine) : string list =
    let inFlight =
      match machine.Phase with
      | Phase.Idle -> []
      | Phase.Moving l -> [ sprintf "  trunk %s: following (moving the trunk checkout)" (let (LandingId id) = l.Landing in id) ]
      | Phase.Delivering (l, _, waiting, _) ->
        [ sprintf "  trunk %s: following (waiting on session %s)" (let (LandingId id) = l.Landing in id) (String.concat ", " waiting) ]
    let queued =
      machine.Queued |> List.map (fun l -> sprintf "  trunk %s: queued behind the landing in flight" (let (LandingId id) = l.Landing in id))
    let recorded =
      machine.Records
      |> List.map (fun r -> sprintf "  trunk %s: %s" (let (LandingId id) = r.Landing in id) (describeVerdict r.Verdict))
    recorded @ inFlight @ queued
