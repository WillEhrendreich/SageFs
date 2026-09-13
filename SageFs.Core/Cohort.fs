namespace SageFs

open System
open SageFs.Measures

/// The pure cohort core (sagefs-multiagent-vision.md §5.1, §5.2, §5.4, §5.7, §7.1, §7.3;
/// Phase 1 item 7, §10). One `decide` turns a command into a new state plus recorded
/// events plus effects-as-data; `replay` folds a recorded ledger back into that same
/// state; `project` turns a ledger head plus session snapshots into the flat,
/// index-aligned `CohortFrame` read model. No IO, no `DateTime.UtcNow`, no
/// `Guid.NewGuid()` anywhere in this file — time and randomness are parameters, so
/// every race a caller can construct is a reproducible, shrinkable test input.
///
/// Scope notes — read before extending this module:
///
/// - **Member identity is opaque.** Every type here is generic over `'m` (with
///   `comparison` where it is used as a Map key). The canonical `MemberId` DU
///   (browser tab / MCP transport / minted, §4.1) is Phase 1 item 8's concern and
///   plugs in at integration by instantiating `'m = MemberId`; this module must
///   never assume anything about `'m` beyond equality and ordering. Because the
///   acting member is always the caller-supplied `'m` value (never a self-declared
///   display string), claim/landing mutations are bound to identity by
///   construction — there is no channel through which one member's command can
///   mutate another member's claim by merely naming itself the same thing.
/// - **`Authority`/`JoinableRole` are included** because the vision asks for their
///   shape here (§4.2). Phase 1 item 8 added `Authority.present` (a total, pure
///   lookup — `CohortState.Conductor` is the only door) and wired `decide` to
///   check it on the two conductor-only commands (`ReassignClaim`,
///   `DelegateConductor`). The total `cohortTools : SessionState * Authority *
///   CohortPhase -> Set<ToolName>` affordance function that *consumes* `Authority`
///   for MCP tool-listing filtering, and its solo-user-invariant property (§7.3
///   #8), is still Phase 1 item 11 (§8.1) — it would require depending on
///   `Affordances.fs`/`SessionState`, which sits outside this island. Likewise the
///   MCP-boundary `Authority.tryPresent : MemberId -> CohortState -> Result<Authority,
///   SageFsError>` the vision text names (token presentation at the transport
///   edge) is a later item's concern — this module's `present` is pure state, so
///   it is total and returns `Authority<'m>` directly, never `SageFsError`.
/// - **`Decision`/`record_decision`** (the ledger's human-facing decision log, the
///   second half of §5.2) is Phase 1 item 10, not built here. The ledger
///   primitive this file *does* build (`LedgerEntry`/`replay`) is what item 10
///   will read and what a `Decision` will be appended alongside.
/// - **`CohortFrame` here is trimmed to what `decide`'s own state and test
///   outcomes can produce**: members, claims, and the three test bitplanes. The
///   remaining §5.7 fields (`LedgerTail`, `Queue`/`LandingSummary`, `Waits`,
///   `LaneEvents`, `Spans`, `Budget`, the territory map, the symbol coupling
///   masks) belong to later phase items (§5.4's landing queue as its own read
///   projection, §6.5's cockpit, §3.4's budgets) whose types do not exist yet.
///   Speculative placeholder fields for them here would be dead weight the
///   roast doctrine explicitly bans; they are left for those items to add.
/// - **`Measures.seq` from the vision text is `Measures.ledgerSeq` here.** A
///   throwaway `dotnet fsi` compile check confirmed that a measure type literally
///   named `seq` shadows both `seq<'T>` type annotations and the `seq { }`
///   computation-expression builder for every file that `open`s the module it is
///   defined in — and `SageFs.Core/Measures.fs` is already `open`ed by files that
///   use `seq { }` (e.g. `Features/LiveTestingTypes.fs`). That is a real,
///   repo-wide collision, not a hypothetical one, so the ledger/frame version
///   measure is named `ledgerSeq` instead; it is the same integer the vision
///   describes (dense, cohort-monotonic, also the `CohortFrame.Version`).
/// - **`fork` (§5.2's `atSeq` forking) is not built here.** Item 7's brief is
///   `decide` + `replay` + `project` + the seventeen properties; `fork` is a thin
///   wrapper over `replay (prefix atSeq)` plus a `Forked` event this module does
///   not yet define, and is left for whichever item actually consumes it.
module Cohort =

  // ── Time and randomness as parameters (§7.1) ──────────────────────────────

  /// When a command arrived. Never read from `DateTime.UtcNow` inside `decide`.
  type Clock = DateTime

  /// The bytes a token/id is minted from. Never read from `Guid.NewGuid()` inside
  /// `decide`. Empty is valid (mints a fixed placeholder id) for callers that do
  /// not need entropy — no command in this module *requires* non-empty entropy.
  type Entropy = byte[]

  // ── Test identity (local, minimal — §5.3's TestRunKey/InputHash overhaul is a
  //    separate item; the cohort core only needs an opaque, orderable test id to
  //    shape the matrix bitplanes in `project`) ──────────────────────────────

  type TestId = TestId of string

  // ── Claim/landing identifiers, minted from entropy (never Guid.NewGuid) ────

  type ClaimId = ClaimId of string
  type LandingId = LandingId of string

  // ── Claim scope (§5.1) ──────────────────────────────────────────────────

  /// v1 is deliberately small: `File`/`Project`, exclusive only. The `Module`/
  /// `Symbol`/`Contract` cases the vision puts in this DU from day one (so
  /// `overlaps` is total from day one) are Phase 3 (§6.1) — they are not added
  /// here because nothing in this module constructs them yet, and an unused case
  /// with no producer is exactly the speculative surface §1's roast complains
  /// about. Extending the DU when Phase 3 lands is a compile error everywhere the
  /// match was not extended — that is the intended forcing function.
  [<RequireQualifiedAccess>]
  type ClaimScope =
    | File of repoRelativePath: string
    | Project of fsprojRelativePath: string

  module ClaimScope =
    let private normalize (path: string) = path.Replace('\\', '/')

    /// `Project` claims a directory (the fsproj's own directory), not the fsproj
    /// file itself — a `File` overlaps a `Project` iff it is under that directory.
    let private projectDir (fsprojRelativePath: string) =
      let n = normalize fsprojRelativePath
      match n.LastIndexOf '/' with
      | -1 -> ""
      | i -> n.Substring(0, i)

    let private underDir (dir: string) (path: string) =
      let d = normalize dir
      let p = normalize path
      if d = "" then true
      else p = d || p.StartsWith(d + "/", StringComparison.Ordinal)

    /// Path arithmetic, never a regex per claim (§5.1). `File` overlaps `File`
    /// iff they name the same path; `Project` overlaps `Project` iff either
    /// directory contains the other; `File`/`Project` overlap iff the file is
    /// under the project's directory.
    let overlaps (a: ClaimScope) (b: ClaimScope) : bool =
      match a, b with
      | ClaimScope.File fa, ClaimScope.File fb -> normalize fa = normalize fb
      | ClaimScope.Project pa, ClaimScope.Project pb ->
        let da, db = projectDir pa, projectDir pb
        da = db || underDir da db || underDir db da
      | ClaimScope.File f, ClaimScope.Project p
      | ClaimScope.Project p, ClaimScope.File f -> underDir (projectDir p) f

  // ── Bounded free-text (§4.3, §5.2) ─────────────────────────────────────

  /// "I am acquiring this claim because ...": one line, bounded. Constructed only
  /// via `Purpose.tryCreate`.
  type Purpose = private Purpose of string

  module Purpose =
    let maxLength = 200

    let tryCreate (raw: string) : Result<Purpose, string> =
      if isNull raw then Error "purpose is null"
      else
        let trimmed = raw.Trim()
        if trimmed.Length = 0 then Error "purpose is empty"
        elif trimmed.Length > maxLength then Error (sprintf "purpose exceeds %d characters" maxLength)
        elif trimmed.Contains "\n" then Error "purpose must be one line"
        else Ok (Purpose trimmed)

    let value (Purpose p) = p

  /// A landing's "why", becomes the squash/merge message body: bounded, may be a
  /// paragraph (unlike `Purpose`, newlines are allowed). Constructed only via
  /// `Statement.tryCreate`. (Also the primitive `Decision.Statement`, §5.2, will
  /// reuse once decisions are built — Phase 1 item 10.)
  type Statement = private Statement of string

  module Statement =
    let maxLength = 1000

    let tryCreate (raw: string) : Result<Statement, string> =
      if isNull raw then Error "statement is null"
      else
        let trimmed = raw.Trim()
        if trimmed.Length = 0 then Error "statement is empty"
        elif trimmed.Length > maxLength then Error (sprintf "statement exceeds %d characters" maxLength)
        else Ok (Statement trimmed)

    let value (Statement s) = s

  // ── Authority (shape only — §4.2; enforcement wiring is Phase 1 item 8) ────

  [<RequireQualifiedAccess>]
  type JoinableRole =
    | Implementer
    | Verifier
    | Observer

  /// A capability, not a role name (§4.2): `Conductor` is a binding this module
  /// will move via a future `delegate_conductor` command (item 8), never a value
  /// a caller can assert about itself. Not yet consulted by `decide` — see the
  /// module-level scope note.
  [<RequireQualifiedAccess>]
  type Authority<'m> =
    | Member of 'm * JoinableRole
    | Conductor of 'm
    | Anonymous

  // ── Membership (§4.1, §4.3) ────────────────────────────────────────────

  [<RequireQualifiedAccess>]
  type MemberPresence =
    | Present
    | Departed of since: DateTime

  type MemberRecord = {
    Role: JoinableRole
    Presence: MemberPresence
    LastRenewal: DateTime
    /// Which SESSION (checkout) this member works in, if any (item 13c,
    /// sagefs-multiagent-vision.md). `None` means the member joined without a
    /// resolvable session — still a full member, just absent from the test
    /// matrix (`CohortOwner.frameOf` only attributes a row to members with
    /// `Some sid`). Set once at `Join` time; there is no v1 command to rebind
    /// it after joining (a member who switches checkouts departs and rejoins).
    Session: string option
  }

  // ── Claims (§5.1, §4.3) ─────────────────────────────────────────────────

  /// The holder lives IN the state, not beside it — a record with a top-level
  /// `Holder` field plus a `Released` state could express "released, still held";
  /// folding the holder into the state makes that unwritable (§4.3).
  [<RequireQualifiedAccess>]
  type ClaimState<'m> =
    | Held of holder: 'm
    | Orphaned of previousHolder: 'm * since: DateTime
    | Released of by: 'm * at: DateTime

  type Claim<'m> = {
    Id: ClaimId
    Fence: int64<fence>
    Scope: ClaimScope
    Purpose: Purpose
    Since: DateTime
    State: ClaimState<'m>
  }

  // ── Landing (§5.4) ──────────────────────────────────────────────────────

  [<RequireQualifiedAccess>]
  type NextAction =
    | RebaseAndResubmit
    | AwaitConductor
    | FixTests of TestId list
    | Withdraw

  /// Trimmed to the blockers `decide` (this item) can actually produce.
  /// `OutsideClaims`/`ClaimOverlap`/`ContractChangedWithoutDecision` need a git
  /// diff's file list, which only exists once the shell's `ComputeAffected`
  /// effect reports it back with that detail (§5.4's `LandingEffect.ComputeAffected`
  /// today only carries shas) — adding those blockers speculatively, before any
  /// command can construct them, is exactly the dead-weight the roast bans.
  [<RequireQualifiedAccess>]
  type LandingBlocker<'m> =
    | RebaseConflict of files: string list
    | FailingTests of TestId list
    | StaleClaimFence of ClaimId
    | HeadMoved of from: string * to': string
    | VetoedBy of 'm * reason: string

  [<RequireQualifiedAccess>]
  type LandingState<'m> =
    | Queued
    | Rebasing of onto: string
    | Verifying of onto: string * affectedTests: int * running: int
    | Blocked of LandingBlocker<'m> * NextAction
    | Landed of integrationCommit: string
    | Withdrawn

  type LandingRequest<'m> = {
    Id: LandingId
    Requester: 'm
    /// The claims the requester is presenting as backing this landing, each with
    /// the fence it was holding them at when the landing was requested.
    Claims: (ClaimId * int64<fence>) list
    Commits: string list
    BaseAtQueue: string
    Statement: Statement
    State: LandingState<'m>
  }

  // ── Cohort state (§7.1) — never persisted; `replay` is the only way to get one ─

  type CohortState<'m when 'm: comparison> = {
    NextFence: int64<fence>
    IntegrationHead: string
    Members: Map<'m, MemberRecord>
    Claims: Map<ClaimId, Claim<'m>>
    Landings: Map<LandingId, LandingRequest<'m>>
    /// Strict FIFO. Only `Queue.Head` may be `Rebasing`/`Verifying` — this is
    /// what makes v1 landing "strictly serial" (§5.4) structural rather than a
    /// convention `decide`'s callers have to honor.
    Queue: LandingId list
    /// The conductor binding (§4.2). `None` until the first member joins an
    /// empty-membership cohort — v1 has no separate `create_cohort` command, so
    /// the first `Join` IS create_cohort's conductor binding. Moved only by
    /// `DelegateConductor`; never a value a member can assert about itself.
    Conductor: 'm option
  }

  /// The well-known git "no parent" sha — a real, meaningful sentinel (`git
  /// hash-object` never produces it), not a fabricated placeholder.
  let nullSha = String.replicate 40 "0"

  /// Silence, not busyness, costs a seat (§4.3): a member's lease is renewed by
  /// any tool call and by `Evaluating`; the reaper is what makes silence cost
  /// something, so this is generous. `Clock` being a parameter means tests exert
  /// this via generated `DateTime` deltas, never a shortened constant.
  let leaseWindow = TimeSpan.FromMinutes 30.0

  module CohortState =
    let empty () : CohortState<'m> = {
      NextFence = 0L<fence>
      IntegrationHead = nullSha
      Members = Map.empty
      Claims = Map.empty
      Landings = Map.empty
      Queue = []
      Conductor = None
    }

  module Authority =
    /// The ONLY door (§4.2): a member's id becomes an `Authority` by looking it up
    /// in `CohortState`. Total — a non-member is `Anonymous`, never an error.
    /// `Conductor` is read straight off the `Conductor` binding in state; it is
    /// never a value a caller can assert about itself by naming a role in `Join`.
    /// The vision's MCP-boundary `tryPresent : MemberId -> CohortState ->
    /// Result<Authority, SageFsError>` (token presentation at the transport edge)
    /// is a later item's concern — this lookup is pure state, so it stays total.
    let present (who: 'm) (state: CohortState<'m>) : Authority<'m> =
      match state.Conductor with
      | Some c when c = who -> Authority.Conductor who
      | _ ->
        match Map.tryFind who state.Members with
        | Some { Presence = MemberPresence.Present; Role = role } -> Authority.Member(who, role)
        | _ -> Authority.Anonymous

  // ── Commands, events, effects (§7.1: effects are data) ────────────────────

  [<RequireQualifiedAccess>]
  type CohortCommand<'m> =
    /// `session` (item 13c) is the caller's resolved SESSION (checkout) id, if
    /// any — the shell resolves this (Mcp.fs's `join_cohort`), `decide` only
    /// stores it verbatim in the new `MemberRecord`.
    | Join of who: 'm * role: JoinableRole * session: string option
    | Depart of who: 'm
    | RenewLease of who: 'm
    /// The shell posts this periodically; `decide` derives who has gone silent
    /// from `Clock - LastRenewal >= leaseWindow`. No timer lives in this module.
    | Tick
    | AcquireClaim of who: 'm * scope: ClaimScope * purpose: string
    | ReleaseClaim of who: 'm * claimId: ClaimId * fence: int64<fence>
    /// Conductor action: reassign an `Orphaned` claim. Gated by `Authority.present
    /// by state = Authority.Conductor _` (Phase 1 item 8) — refused with
    /// `CohortError.NotConductor` otherwise.
    | ReassignClaim of by: 'm * claimId: ClaimId * toMember: 'm
    /// Conductor action: rebind `Conductor` to another Present member (§4.2's
    /// "delegate conductor to member X rebinds it"). Gated the same way as
    /// `ReassignClaim`.
    | DelegateConductor of by: 'm * toMember: 'm
    /// The member's own watcher observed a save; warn (never block — a claim
    /// cannot stop an edit, §5.1) if it landed inside someone else's claim.
    | ObserveSave of who: 'm * path: string
    | RequestLanding of requester: 'm * claims: (ClaimId * int64<fence>) list * commits: string list * statement: string
    /// Effect completions are commands (§5.2) — this is `RebuildCompleted`'s
    /// pattern (`SessionManager.fs:79-82`) applied to landing.
    /// `Ok newHead` or `Error conflictFiles`.
    | RebaseCompleted of LandingId * Result<string, string list>
    | AffectedComputed of LandingId * TestId list
    | TestsCompleted of LandingId * failing: TestId list
    | FastForwardCompleted of LandingId * committedSha: string
    | WithdrawLanding of who: 'm * LandingId
    | VetoLanding of by: 'm * LandingId * reason: string
    /// Conductor action (item 14c): bind `IntegrationHead` to a git sha the
    /// shell has already resolved and checked out into the daemon's
    /// integration worktree (`SageFs/Mcp.fs`'s `set_integration_ref`, which
    /// resolves the ref to a sha with `CohortGit.revParse` BEFORE dispatching
    /// this command — `decide` never resolves a ref itself, it only records
    /// the sha it is given). Gated the same way as `ReassignClaim`/
    /// `DelegateConductor`. This is additive-only: no existing landing arm
    /// (`RequestLanding`'s `advanceQueue` reads `IntegrationHead` as
    /// `onto`, `FastForwardCompleted`'s `HeadMoved` check compares against
    /// it) changes — both already treat `IntegrationHead` as ordinary
    /// mutable state, whatever last set it.
    | SetIntegrationHead of by: 'm * head: string

  [<RequireQualifiedAccess>]
  type CohortEvent<'m> =
    /// Carries the same `session` `Join` was given (item 13c) so `replay`
    /// reconstructs an identical `MemberRecord.Session` from the ledger alone.
    | MemberJoined of 'm * JoinableRole * session: string option
    | MemberDeparted of 'm * since: DateTime
    | LeaseRenewed of 'm
    /// The `Conductor` binding was made for the first time — v1's `create_cohort`
    /// (§4.2), fired alongside `MemberJoined` for the cohort's first joiner only.
    | ConductorBound of 'm
    /// The `Conductor` binding moved from one Present member to another.
    | ConductorDelegated of from: 'm * to': 'm
    | ClaimAcquired of ClaimId * ClaimScope * holder: 'm * fence: int64<fence>
    | ClaimReleased of ClaimId * by: 'm * fence: int64<fence>
    | ClaimOrphaned of ClaimId * previousHolder: 'm * fence: int64<fence>
    | ClaimReassigned of ClaimId * toMember: 'm * fence: int64<fence>
    | ClaimViolationObserved of ClaimId * observer: 'm * holder: 'm * path: string
    | LandingQueued of LandingId * requester: 'm
    | LandingStateChanged of LandingId * LandingState<'m>
    | LandingLanded of LandingId * integrationCommit: string
    | LandingWithdrawn of LandingId
    | LandingVetoed of LandingId * by: 'm * reason: string
    /// The `IntegrationHead` binding was (re)configured (item 14c).
    | IntegrationConfigured of head: string

  /// What the pure machine asks the shell to DO. `decide` never rebases, never
  /// runs a test, never writes SQLite — it returns these, the shell performs
  /// them, and posts a typed completion back as a command (§7.1).
  [<RequireQualifiedAccess>]
  type CohortEffect<'m> =
    | Rebase of LandingId * onto: string
    | ComputeAffected of LandingId * baseSha: string * headSha: string
    | RunTests of LandingId * TestId list
    | FastForward of LandingId * toSha: string
    | Notify of 'm * CohortEvent<'m>

  [<RequireQualifiedAccess>]
  type CohortError<'m> =
    | DuplicateJoin of 'm
    | MemberNotPresent of 'm
    | ClaimConflict of ClaimScope * holder: 'm
    | NotClaimHolder of ClaimId * requester: 'm
    | UnknownClaim of ClaimId
    | ClaimNotOrphaned of ClaimId
    | DuplicateClaimId of ClaimId
    | StaleClaimFence of ClaimId * presented: int64<fence> * current: int64<fence>
    | InvalidPurpose of string
    | InvalidStatement of string
    | UnknownLanding of LandingId
    | DuplicateLandingId of LandingId
    | NotLandingRequester of LandingId * 'm
    | LandingNotAtFrontOfQueue of LandingId
    | LandingNotInExpectedState of LandingId * expected: string
    /// A conductor-only command (`ReassignClaim`, `DelegateConductor`) was issued
    /// by a member whose `Authority.present` is not `Conductor _` (§4.2).
    | NotConductor of 'm

  // ── Id minting from entropy (never Guid.NewGuid, §7.1) ────────────────────

  module private Ids =
    let private hex (bytes: byte[]) =
      bytes |> Array.truncate 12 |> Array.map (fun b -> b.ToString "x2") |> String.concat ""

    let mintClaimId (entropy: Entropy) : ClaimId =
      ClaimId ("c-" + (if Array.isEmpty entropy then "0" else hex entropy))

    let mintLandingId (entropy: Entropy) : LandingId =
      LandingId ("l-" + (if Array.isEmpty entropy then "0" else hex entropy))

  // ── decide's helpers ────────────────────────────────────────────────────

  let private isPresent (state: CohortState<'m>) (who: 'm) =
    match Map.tryFind who state.Members with
    | Some { Presence = MemberPresence.Present } -> true
    | _ -> false

  /// Every `Held` claim of `who` becomes `Orphaned`, each with its own fence bump
  /// (property 3: fence is strictly increasing per claim id, not shared).
  let private orphanClaimsOf (who: 'm) (now: DateTime) (state: CohortState<'m>) : CohortState<'m> * CohortEvent<'m> list =
    let mutable fence = state.NextFence
    let mutable claims = state.Claims
    let events = ResizeArray()
    for KeyValue(cid, claim) in state.Claims do
      match claim.State with
      | ClaimState.Held holder when holder = who ->
        fence <- fence + 1L<fence>
        let updated = { claim with Fence = fence; State = ClaimState.Orphaned(holder, now) }
        claims <- Map.add cid updated claims
        events.Add(CohortEvent.ClaimOrphaned(cid, holder, fence))
      | _ -> ()
    { state with Claims = claims; NextFence = fence }, List.ofSeq events

  let private departMember (who: 'm) (now: DateTime) (state: CohortState<'m>) : CohortState<'m> * CohortEvent<'m> list =
    let record = state.Members.[who]
    let state1 = { state with Members = Map.add who { record with Presence = MemberPresence.Departed now } state.Members }
    let state2, orphanEvents = orphanClaimsOf who now state1
    state2, CohortEvent.MemberDeparted(who, now) :: orphanEvents

  /// Every claim the requester is presenting must currently be `Held` by them at
  /// the presented fence — checked at request time (early warning) and again at
  /// land time (the contract that matters, §7.2).
  let private validateLandingClaims (state: CohortState<'m>) (requester: 'm) (claims: (ClaimId * int64<fence>) list) : Result<unit, CohortError<'m>> =
    claims
    |> List.fold (fun acc (cid, presentedFence) ->
      match acc with
      | Error _ -> acc
      | Ok () ->
        match Map.tryFind cid state.Claims with
        | None -> Error(CohortError.UnknownClaim cid)
        | Some c ->
          match c.State with
          | ClaimState.Held holder when holder = requester ->
            if presentedFence <> c.Fence then Error(CohortError.StaleClaimFence(cid, presentedFence, c.Fence)) else Ok ()
          | _ -> Error(CohortError.NotClaimHolder(cid, requester)))
      (Ok ())

  /// If the new front of the queue is `Queued`, kick off its rebase. No-op
  /// otherwise (front already in flight, or queue empty).
  let private advanceQueue (state: CohortState<'m>) : CohortState<'m> * CohortEvent<'m> list * CohortEffect<'m> list =
    match state.Queue with
    | next :: _ ->
      match Map.tryFind next state.Landings with
      | Some req when req.State = LandingState.Queued ->
        let onto = state.IntegrationHead
        let rebasing = { req with State = LandingState.Rebasing onto }
        let newState = { state with Landings = Map.add next rebasing state.Landings }
        newState, [ CohortEvent.LandingStateChanged(next, rebasing.State) ], [ CohortEffect.Rebase(next, onto) ]
      | _ -> state, [], []
    | [] -> state, [], []

  let private requireAtFrontOfQueue (state: CohortState<'m>) (id: LandingId) : Result<unit, CohortError<'m>> =
    if state.Queue |> List.tryHead = Some id then Ok () else Error(CohortError.LandingNotAtFrontOfQueue id)

  // ── decide (§7.1) ───────────────────────────────────────────────────────

  /// The one pure decision function. No IO, no `DateTime.UtcNow`, no
  /// `Guid.NewGuid()`. `CohortOwner` (a future item's shell actor) is the only
  /// thing that calls this in production; every property in this file calls it
  /// directly.
  let decide (clock: Clock) (entropy: Entropy) (state: CohortState<'m>) (command: CohortCommand<'m>)
      : Result<CohortState<'m> * CohortEvent<'m> list * CohortEffect<'m> list, CohortError<'m>> =
    match command with

    | CohortCommand.Join(who, role, session) ->
      match Map.tryFind who state.Members with
      | Some { Presence = MemberPresence.Present } -> Error(CohortError.DuplicateJoin who)
      | _ ->
        let record = { Role = role; Presence = MemberPresence.Present; LastRenewal = clock; Session = session }
        let newState = { state with Members = Map.add who record state.Members }
        match state.Conductor with
        | None ->
          // v1 create_cohort semantics (§4.2): the first member to join an
          // empty-membership cohort becomes the conductor. There is no separate
          // CreateCohort command in v1 — this IS that binding.
          let bound = { newState with Conductor = Some who }
          Ok(bound, [ CohortEvent.MemberJoined(who, role, session); CohortEvent.ConductorBound who ], [])
        | Some _ ->
          Ok(newState, [ CohortEvent.MemberJoined(who, role, session) ], [])

    | CohortCommand.Depart who ->
      if not (isPresent state who) then Error(CohortError.MemberNotPresent who)
      else
        let newState, events = departMember who clock state
        Ok(newState, events, [])

    | CohortCommand.RenewLease who ->
      match Map.tryFind who state.Members with
      | Some ({ Presence = MemberPresence.Present } as record) ->
        let newState = { state with Members = Map.add who { record with LastRenewal = clock } state.Members }
        Ok(newState, [ CohortEvent.LeaseRenewed who ], [])
      | _ -> Error(CohortError.MemberNotPresent who)

    | CohortCommand.Tick ->
      let expired =
        state.Members
        |> Map.toList
        |> List.filter (fun (_, r) -> r.Presence = MemberPresence.Present && (clock - r.LastRenewal) >= leaseWindow)
        |> List.map fst
      let finalState, events =
        expired
        |> List.fold
          (fun (st, evs) who ->
            let st2, whoEvents = departMember who clock st
            st2, evs @ whoEvents)
          (state, [])
      Ok(finalState, events, [])

    | CohortCommand.AcquireClaim(who, scope, purposeRaw) ->
      if not (isPresent state who) then Error(CohortError.MemberNotPresent who)
      else
        match Purpose.tryCreate purposeRaw with
        | Error e -> Error(CohortError.InvalidPurpose e)
        | Ok purpose ->
          let conflict =
            state.Claims
            |> Map.toList
            |> List.tryPick (fun (_, c) ->
              match c.State with
              | ClaimState.Held holder when ClaimScope.overlaps c.Scope scope -> Some holder
              | _ -> None)
          match conflict with
          | Some holder -> Error(CohortError.ClaimConflict(scope, holder))
          | None ->
            let claimId = Ids.mintClaimId entropy
            if Map.containsKey claimId state.Claims then
              Error(CohortError.DuplicateClaimId claimId)
            else
              let fence = state.NextFence + 1L<fence>
              let claim = { Id = claimId; Fence = fence; Scope = scope; Purpose = purpose; Since = clock; State = ClaimState.Held who }
              let newState = { state with NextFence = fence; Claims = Map.add claimId claim state.Claims }
              Ok(newState, [ CohortEvent.ClaimAcquired(claimId, scope, who, fence) ], [])

    | CohortCommand.ReleaseClaim(who, claimId, presentedFence) ->
      match Map.tryFind claimId state.Claims with
      | None -> Error(CohortError.UnknownClaim claimId)
      | Some claim when presentedFence <> claim.Fence ->
        // Property 4: a stale fence is refused whatever else the command carries —
        // checked before the holder-identity check, not after.
        Error(CohortError.StaleClaimFence(claimId, presentedFence, claim.Fence))
      | Some claim ->
        match claim.State with
        | ClaimState.Held holder when holder = who ->
          let fence = state.NextFence + 1L<fence>
          let updated = { claim with Fence = fence; State = ClaimState.Released(who, clock) }
          let newState = { state with NextFence = fence; Claims = Map.add claimId updated state.Claims }
          Ok(newState, [ CohortEvent.ClaimReleased(claimId, who, fence) ], [])
        | _ -> Error(CohortError.NotClaimHolder(claimId, who))

    | CohortCommand.ReassignClaim(by, claimId, toMember) ->
      match Authority.present by state with
      | Authority.Conductor _ ->
        match Map.tryFind claimId state.Claims with
        | None -> Error(CohortError.UnknownClaim claimId)
        | Some claim ->
          match claim.State with
          | ClaimState.Orphaned _ ->
            if not (isPresent state toMember) then Error(CohortError.MemberNotPresent toMember)
            else
              // Property 1: reactivating an Orphaned claim must not create a
              // second simultaneously-Held claim over an overlapping scope —
              // the same exclusivity AcquireClaim enforces on a fresh claim.
              let conflict =
                state.Claims
                |> Map.toList
                |> List.tryPick (fun (otherId, c) ->
                  match c.State with
                  | ClaimState.Held holder when otherId <> claimId && ClaimScope.overlaps c.Scope claim.Scope -> Some holder
                  | _ -> None)
              match conflict with
              | Some holder -> Error(CohortError.ClaimConflict(claim.Scope, holder))
              | None ->
                let fence = state.NextFence + 1L<fence>
                let updated = { claim with Fence = fence; State = ClaimState.Held toMember }
                let newState = { state with NextFence = fence; Claims = Map.add claimId updated state.Claims }
                Ok(newState, [ CohortEvent.ClaimReassigned(claimId, toMember, fence) ], [])
          | _ -> Error(CohortError.ClaimNotOrphaned claimId)
      | _ -> Error(CohortError.NotConductor by)

    | CohortCommand.DelegateConductor(by, toMember) ->
      match Authority.present by state with
      | Authority.Conductor _ ->
        if not (isPresent state toMember) then Error(CohortError.MemberNotPresent toMember)
        else
          let newState = { state with Conductor = Some toMember }
          Ok(newState, [ CohortEvent.ConductorDelegated(by, toMember) ], [])
      | _ -> Error(CohortError.NotConductor by)

    | CohortCommand.ObserveSave(who, path) ->
      let violating =
        state.Claims
        |> Map.toList
        |> List.tryPick (fun (cid, c) ->
          match c.State with
          | ClaimState.Held holder when holder <> who && ClaimScope.overlaps c.Scope (ClaimScope.File path) -> Some(cid, holder)
          | _ -> None)
      match violating with
      | Some(cid, holder) -> Ok(state, [ CohortEvent.ClaimViolationObserved(cid, who, holder, path) ], [])
      | None -> Ok(state, [], [])

    | CohortCommand.RequestLanding(requester, claims, commits, statementRaw) ->
      if not (isPresent state requester) then Error(CohortError.MemberNotPresent requester)
      else
        match Statement.tryCreate statementRaw with
        | Error e -> Error(CohortError.InvalidStatement e)
        | Ok statement ->
          match validateLandingClaims state requester claims with
          | Error e -> Error e
          | Ok () ->
            let landingId = Ids.mintLandingId entropy
            if Map.containsKey landingId state.Landings then
              Error(CohortError.DuplicateLandingId landingId)
            else
              let req = {
                Id = landingId
                Requester = requester
                Claims = claims
                Commits = commits
                BaseAtQueue = state.IntegrationHead
                Statement = statement
                State = LandingState.Queued
              }
              let queued = { state with Landings = Map.add landingId req state.Landings; Queue = state.Queue @ [ landingId ] }
              let advanced, advEvents, advEffects = advanceQueue queued
              Ok(advanced, CohortEvent.LandingQueued(landingId, requester) :: advEvents, advEffects)

    | CohortCommand.RebaseCompleted(id, result) ->
      match Map.tryFind id state.Landings with
      | None -> Error(CohortError.UnknownLanding id)
      | Some req ->
        match requireAtFrontOfQueue state id with
        | Error e -> Error e
        | Ok () ->
          match req.State with
          | LandingState.Rebasing onto ->
            match result with
            | Ok newHead ->
              let verifying = { req with State = LandingState.Verifying(newHead, 0, 0) }
              let newState = { state with Landings = Map.add id verifying state.Landings }
              Ok(newState, [ CohortEvent.LandingStateChanged(id, verifying.State) ], [ CohortEffect.ComputeAffected(id, onto, newHead) ])
            | Error conflictFiles ->
              let blocked = { req with State = LandingState.Blocked(LandingBlocker.RebaseConflict conflictFiles, NextAction.RebaseAndResubmit) }
              let newState = { state with Landings = Map.add id blocked state.Landings }
              Ok(newState, [ CohortEvent.LandingStateChanged(id, blocked.State) ], [])
          | _ -> Error(CohortError.LandingNotInExpectedState(id, "Rebasing"))

    | CohortCommand.AffectedComputed(id, tests) ->
      match Map.tryFind id state.Landings with
      | None -> Error(CohortError.UnknownLanding id)
      | Some req ->
        match requireAtFrontOfQueue state id with
        | Error e -> Error e
        | Ok () ->
          match req.State with
          | LandingState.Verifying(onto, _, _) ->
            let n = List.length tests
            let verifying = { req with State = LandingState.Verifying(onto, n, n) }
            let newState = { state with Landings = Map.add id verifying state.Landings }
            Ok(newState, [ CohortEvent.LandingStateChanged(id, verifying.State) ], [ CohortEffect.RunTests(id, tests) ])
          | _ -> Error(CohortError.LandingNotInExpectedState(id, "Verifying"))

    | CohortCommand.TestsCompleted(id, failing) ->
      match Map.tryFind id state.Landings with
      | None -> Error(CohortError.UnknownLanding id)
      | Some req ->
        match requireAtFrontOfQueue state id with
        | Error e -> Error e
        | Ok () ->
          match req.State with
          | LandingState.Verifying(onto, affected, _) ->
            match failing with
            | [] ->
              let verifying = { req with State = LandingState.Verifying(onto, affected, 0) }
              let newState = { state with Landings = Map.add id verifying state.Landings }
              Ok(newState, [], [ CohortEffect.FastForward(id, onto) ])
            | fails ->
              let blocked = { req with State = LandingState.Blocked(LandingBlocker.FailingTests fails, NextAction.FixTests fails) }
              let newState = { state with Landings = Map.add id blocked state.Landings }
              Ok(newState, [ CohortEvent.LandingStateChanged(id, blocked.State) ], [])
          | _ -> Error(CohortError.LandingNotInExpectedState(id, "Verifying"))

    | CohortCommand.FastForwardCompleted(id, committedSha) ->
      match Map.tryFind id state.Landings with
      | None -> Error(CohortError.UnknownLanding id)
      | Some req ->
        match requireAtFrontOfQueue state id with
        | Error e -> Error e
        | Ok () ->
          match req.State with
          | LandingState.Verifying(onto, _, _) ->
            // Property 11: a landing verified against H1 never lands when the
            // head is H2 <> H1 — checked at land time, not queue time (§5.4, §7.2).
            if onto <> state.IntegrationHead then
              let blocked = { req with State = LandingState.Blocked(LandingBlocker.HeadMoved(onto, state.IntegrationHead), NextAction.RebaseAndResubmit) }
              let newState = { state with Landings = Map.add id blocked state.Landings }
              Ok(newState, [ CohortEvent.LandingStateChanged(id, blocked.State) ], [])
            else
              // Property 6: every claim in the request must still be Held by the
              // requester, at the presented fence, at land time.
              let claimHeldOk (cid, presentedFence) =
                match Map.tryFind cid state.Claims with
                | Some c ->
                  (match c.State with
                   | ClaimState.Held h -> h = req.Requester
                   | _ -> false)
                  && c.Fence = presentedFence
                | None -> false
              match req.Claims |> List.tryFind (claimHeldOk >> not) with
              | Some(staleId, _) ->
                let blocked = { req with State = LandingState.Blocked(LandingBlocker.StaleClaimFence staleId, NextAction.AwaitConductor) }
                let newState = { state with Landings = Map.add id blocked state.Landings }
                Ok(newState, [ CohortEvent.LandingStateChanged(id, blocked.State) ], [])
              | None ->
                let releaseFence, releasedClaims, releaseEventsRev =
                  req.Claims
                  |> List.fold
                    (fun (fence, claims: Map<ClaimId, Claim<'m>>, evs) (cid, _) ->
                      match Map.tryFind cid claims with
                      | Some c ->
                        let f2 = fence + 1L<fence>
                        let updated = { c with Fence = f2; State = ClaimState.Released(req.Requester, clock) }
                        f2, Map.add cid updated claims, CohortEvent.ClaimReleased(cid, req.Requester, f2) :: evs
                      | None -> fence, claims, evs)
                    (state.NextFence, state.Claims, [])
                let landed = { req with State = LandingState.Landed committedSha }
                let poppedQueue = state.Queue |> List.filter (fun x -> x <> id)
                let stateAfterLand = {
                  state with
                    NextFence = releaseFence
                    Claims = releasedClaims
                    Landings = Map.add id landed state.Landings
                    Queue = poppedQueue
                    IntegrationHead = committedSha
                }
                let advanced, advEvents, advEffects = advanceQueue stateAfterLand
                Ok(advanced,
                   (CohortEvent.LandingStateChanged(id, landed.State) :: CohortEvent.LandingLanded(id, committedSha) :: List.rev releaseEventsRev)
                   @ advEvents,
                   advEffects)
          | _ -> Error(CohortError.LandingNotInExpectedState(id, "Verifying"))

    | CohortCommand.WithdrawLanding(who, id) ->
      match Map.tryFind id state.Landings with
      | None -> Error(CohortError.UnknownLanding id)
      | Some req when req.Requester <> who -> Error(CohortError.NotLandingRequester(id, who))
      | Some req ->
        match req.State with
        | LandingState.Landed _
        | LandingState.Withdrawn -> Error(CohortError.LandingNotInExpectedState(id, "non-terminal"))
        | _ ->
          let wasAtFront = state.Queue |> List.tryHead = Some id
          let withdrawn = { req with State = LandingState.Withdrawn }
          let poppedQueue = state.Queue |> List.filter (fun x -> x <> id)
          let stateAfter = { state with Landings = Map.add id withdrawn state.Landings; Queue = poppedQueue }
          let advanced, advEvents, advEffects = if wasAtFront then advanceQueue stateAfter else stateAfter, [], []
          Ok(advanced,
             CohortEvent.LandingStateChanged(id, withdrawn.State) :: CohortEvent.LandingWithdrawn id :: advEvents,
             advEffects)

    | CohortCommand.SetIntegrationHead(by, head) ->
      match Authority.present by state with
      | Authority.Conductor _ ->
        let newState = { state with IntegrationHead = head }
        Ok(newState, [ CohortEvent.IntegrationConfigured head ], [])
      | _ -> Error(CohortError.NotConductor by)

    | CohortCommand.VetoLanding(by, id, reason) ->
      match Map.tryFind id state.Landings with
      | None -> Error(CohortError.UnknownLanding id)
      | Some req ->
        match req.State with
        | LandingState.Landed _
        | LandingState.Withdrawn -> Error(CohortError.LandingNotInExpectedState(id, "non-terminal"))
        | _ ->
          let blocked = { req with State = LandingState.Blocked(LandingBlocker.VetoedBy(by, reason), NextAction.AwaitConductor) }
          let newState = { state with Landings = Map.add id blocked state.Landings }
          Ok(newState, [ CohortEvent.LandingStateChanged(id, blocked.State); CohortEvent.LandingVetoed(id, by, reason) ], [])

  // ── The ledger IS the event store (§5.2) ───────────────────────────────

  type LedgerEntry<'m> = {
    /// Dense, cohort-monotonic; also the `CohortFrame.Version` (§5.2, §5.7 — the
    /// separate frame-version counter earlier revisions had is deleted, §1.6 #10).
    Seq: int64<ledgerSeq>
    Clock: Clock
    Entropy: Entropy
    Command: CohortCommand<'m>
    Events: CohortEvent<'m> list
  }

  type LedgerHead<'m when 'm: comparison> = {
    Seq: int64<ledgerSeq>
    State: CohortState<'m>
  }

  /// `CohortState` is never stored (§5.2) — this is the only way to get one.
  /// Folds `decide` over the recorded (Clock, Entropy, Command) triples; never
  /// runs git, never runs a test, never spawns a process. An entry whose command
  /// no longer applies cleanly (it should not happen against a ledger this
  /// module itself produced) leaves the fold's state unchanged rather than
  /// throwing, so a corrupt tail cannot make replay itself unusable.
  let replay (entries: LedgerEntry<'m> list) : CohortState<'m> =
    entries
    |> List.fold
      (fun state entry ->
        match decide entry.Clock entry.Entropy state entry.Command with
        | Ok(newState, _, _) -> newState
        | Error _ -> state)
      (CohortState.empty ())

  /// `replay` plus the `Seq` it landed on — `project`'s other input.
  let replayHead (entries: LedgerEntry<'m> list) : LedgerHead<'m> =
    let seq = entries |> List.tryLast |> Option.map (fun e -> e.Seq) |> Option.defaultValue 0L<ledgerSeq>
    { Seq = seq; State = replay entries }

  // ── The read model (§5.7) ──────────────────────────────────────────────

  [<Flags>]
  type FrameRegions =
    | NoRegions = 0
    | Members = 1
    | Claims = 2
    | Matrix = 4

  [<RequireQualifiedAccess>]
  type SeatState =
    | Present
    | Departed of since: DateTime

  /// One test outcome observation, per session, that `project` folds into the
  /// matrix bitplanes. `Member = None` is the integration session (row 0, §5.3).
  type SessionSnapshot<'m> = {
    Member: 'm option
    SessionId: string
    Generation: int64
    PassingTests: TestId list
    FailingTests: TestId list
    StaleTests: TestId list
  }

  /// Flat and index-aligned (§5.7): every renderer is an O(frame) array walk,
  /// never a `Map` lookup in the render loop. `Version` is the ledger `Seq` the
  /// frame was projected from — a scrubbed frame, a forked cohort and an MCP
  /// "re-read from v" name the same integer (§1.6 #10).
  type CohortFrame<'m> = {
    Version: int64<ledgerSeq>
    SessionGens: int64[]
    Dirty: FrameRegions
    /// The `Conductor` binding (§4.2), carried on the frame so a shell that
    /// only holds `CohortFrame` (never `CohortState`) can still resolve an
    /// `Authority` (Slice 3, cohort-integration-plan.md item 11) — see
    /// `Affordances.authorityOfMember`. `None` until the first member joins
    /// (mirrors `CohortState.Conductor`, §4.2).
    Conductor: 'm option
    MemberIds: 'm[]
    MemberRole: JoinableRole[]
    MemberSeat: SeatState[]
    ClaimIds: ClaimId[]
    ClaimScope: ClaimScope[]
    /// Index into `MemberIds`; -1 when the claim is not currently `Held`
    /// (Orphaned/Released) — never a nullable holder field.
    ClaimHolderIndex: int[]
    ClaimFence: int64<fence>[]
    ClaimState: ClaimState<'m>[]
    TestIds: TestId[]
    Pass: bool[][]
    Fail: bool[][]
    Stale: bool[][]
  }

  /// Pure. Identity of a frame is `(Version, SessionGens)` (property 10): the
  /// same ledger head and the same session generations always project the same
  /// frame — `Map.toArray`/`Array.sortBy` give every array a deterministic order,
  /// so two calls with equal inputs are structurally equal, not merely
  /// equivalent.
  let project (head: LedgerHead<'m>) (snapshots: SessionSnapshot<'m>[]) : CohortFrame<'m> =
    let members = head.State.Members |> Map.toArray
    let memberIds = members |> Array.map fst
    let memberIndexOf who = memberIds |> Array.tryFindIndex ((=) who) |> Option.defaultValue -1

    let claims = head.State.Claims |> Map.toArray

    let testIds =
      snapshots
      |> Array.collect (fun s -> (s.PassingTests @ s.FailingTests @ s.StaleTests) |> List.toArray)
      |> Array.distinct
      |> Array.sortBy (fun (TestId t) -> t)

    let bitmapFor (pick: SessionSnapshot<'m> -> TestId list) =
      snapshots
      |> Array.map (fun s ->
        let set = pick s |> Set.ofList
        testIds |> Array.map set.Contains)

    {
      Version = head.Seq
      SessionGens = snapshots |> Array.map (fun s -> s.Generation)
      Dirty = FrameRegions.Members ||| FrameRegions.Claims ||| FrameRegions.Matrix
      Conductor = head.State.Conductor
      MemberIds = memberIds
      MemberRole = members |> Array.map (fun (_, r) -> r.Role)
      MemberSeat =
        members
        |> Array.map (fun (_, r) ->
          match r.Presence with
          | MemberPresence.Present -> SeatState.Present
          | MemberPresence.Departed since -> SeatState.Departed since)
      ClaimIds = claims |> Array.map fst
      ClaimScope = claims |> Array.map (fun (_, c) -> c.Scope)
      ClaimHolderIndex =
        claims
        |> Array.map (fun (_, c) ->
          match c.State with
          | ClaimState.Held holder -> memberIndexOf holder
          | _ -> -1)
      ClaimFence = claims |> Array.map (fun (_, c) -> c.Fence)
      ClaimState = claims |> Array.map (fun (_, c) -> c.State)
      TestIds = testIds
      Pass = bitmapFor (fun s -> s.PassingTests)
      Fail = bitmapFor (fun s -> s.FailingTests)
      Stale = bitmapFor (fun s -> s.StaleTests)
    }
