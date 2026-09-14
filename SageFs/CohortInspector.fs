/// Phase 2 item 16 of sagefs-multiagent-vision.md (§6.5 "the inspector" —
/// "`/dashboard/inspect/<kind>/<id>` ... retargets a side panel ... with the
/// raw frame row — the `Claim` record, the member's `SeatState` and lease,
/// the test's `TestRunKey` and bitmap popcount, the span's stages — rendered
/// *verbatim* as a definition list; `/` opens an entity search").
///
/// The vision text describes selection as a `Ds.post` that retargets *this
/// tab's* stream (§6.5 "Selection is a POST, never a signal") — this island
/// instead ships the inspector as its own `GET` request/response page per
/// this item's brief, so it needs no SSE stream and is independently
/// deep-linkable/bookmarkable; wiring it into the cockpit's POST-retarget
/// pattern later is additive, not a rewrite of this module's pure core.
/// Likewise "server-side prefix trie" is the vision's engine choice for
/// autocomplete-as-you-type at cockpit scale; v1's `search` is a plain
/// substring scan — correct and small-cohort-cheap (§5.1's v1 scope is one
/// daemon-scoped cohort), swappable for a trie later without changing the
/// `SearchResult` contract.
///
/// Entity kinds and their sources, scoped to what the shipped read models
/// actually carry (checked against `Cohort.fs` before writing this):
///  - **member** / **claim** / **landing** — `Cohort.replay` over the ledger
///    (`DashboardInfra.ReadCohortLedger`) reconstructs the full `CohortState`
///    (`Cohort.fs:280`), which carries the RAW records the vision asks for
///    verbatim: `Claim<'m>` (with `Purpose`/`Since`, `Cohort.fs:219`) and
///    `LandingRequest<'m>` (with `Statement`/`State`, `Cohort.fs:266`) are
///    NOT reachable from `CohortFrame` alone — the frame's claim arrays drop
///    `Purpose`/`Since` (`Cohort.fs:966-972`), and the frame has no landing
///    fields at all (deferred per `Cohort.fs`'s own module doc, `Cohort.fs:42-47`).
///  - **test** — `CohortFrame.TestIds`/`Pass`/`Fail`/`Stale`
///    (`DashboardInfra.ReadCohortFrame`). The frame's bitplane rows are
///    session-INDEXED, not session-IDENTIFIED (`project`, `Cohort.fs:1011`,
///    keeps only `SessionGens: int64[]`, never the `SessionSnapshot.SessionId`/
///    `Member` that produced each row) — so a test's per-session identity
///    cannot be sourced from the frame. What CAN: exactly what the vision
///    text names for this row, "the test's ... bitmap popcount" — the pass/
///    fail/stale counts across all reporting sessions.
///  - **session** — the daemon's session list (`DashboardQueries.GetAllSessions`,
///    passed in already-fetched so this module's own functions stay pure and
///    synchronous) — `WorkerProtocol.SessionInfo` (`WorkerProtocol.fs:345`).
module SageFs.Server.CohortInspector

open System
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.WorkerProtocol
open SageFs.WorkflowTypes
open Falco.Markup
open SageFs.Server.DashboardFragments

[<RequireQualifiedAccess>]
type EntityKind =
  | Member
  | Claim
  | Landing
  | Test
  | Session

module EntityKind =
  let toUrlSegment = function
    | EntityKind.Member -> "member"
    | EntityKind.Claim -> "claim"
    | EntityKind.Landing -> "landing"
    | EntityKind.Test -> "test"
    | EntityKind.Session -> "session"

  /// The label shown on screen — capitalized, otherwise the same closed
  /// vocabulary as `toUrlSegment`. One exhaustive function per closed set,
  /// never a `%A`/`ToString` guess (repo doctrine).
  let label = function
    | EntityKind.Member -> "Member"
    | EntityKind.Claim -> "Claim"
    | EntityKind.Landing -> "Landing"
    | EntityKind.Test -> "Test"
    | EntityKind.Session -> "Session"

  /// Case-insensitive inverse of `toUrlSegment`. Total: an unrecognized
  /// segment is `None`, never an exception — the route handler turns that
  /// into a clean not-found view, never a 500.
  let tryParse (raw: string) : EntityKind option =
    match (if isNull raw then "" else raw.ToLowerInvariant()) with
    | "member" -> Some EntityKind.Member
    | "claim" -> Some EntityKind.Claim
    | "landing" -> Some EntityKind.Landing
    | "test" -> Some EntityKind.Test
    | "session" -> Some EntityKind.Session
    | _ -> None

/// One row of the rendered definition list, in display order.
type Field = { Label: string; Value: string }

[<RequireQualifiedAccess>]
type InspectorModel =
  | Found of kind: EntityKind * id: string * title: string * fields: Field list
  /// `kind = None` means the URL segment itself didn't name a known kind;
  /// `Some kind` means the kind was fine but no entity of that kind has
  /// this id. Both render the same clean "not found" view — the caller
  /// (an agent, a human) doesn't need the distinction to know what to try
  /// next, but tests can still tell them apart.
  | NotFound of kind: EntityKind option * id: string

type SearchResult = { Kind: EntityKind; Id: string; Label: string }

// ── id <-> MemberId — the exact inverse of `MemberId.display`
// (`MemberTable.fs:37-40`), never redefined there since that file is
// owned by other work in this campaign. `Minted` renders verbatim in
// `display`, so anything without a recognized prefix round-trips as
// `Minted` — total, no parse failure. ─────────────────────────────────
let private parseMemberId (raw: string) : MemberId =
  if raw.StartsWith("browser:", StringComparison.Ordinal) then
    MemberId.Browser(raw.Substring 8)
  elif raw.StartsWith("mcp:", StringComparison.Ordinal) then
    MemberId.Mcp(raw.Substring 4)
  else
    MemberId.Minted raw

// ── Exhaustive label functions — closed DUs, one to-string each, no
// magic strings and no `%A` (a case rename becomes a compile error at
// exactly the match this file needs to update, never a silent label
// change). ───────────────────────────────────────────────────────────
let private roleLabel = function
  | JoinableRole.Implementer -> "Implementer"
  | JoinableRole.Verifier -> "Verifier"
  | JoinableRole.Observer -> "Observer"

let private presenceLabel = function
  | MemberPresence.Present -> "Present"
  | MemberPresence.Departed since -> sprintf "Departed (since %s)" (since.ToString "u")

let private scopeLabel = function
  | ClaimScope.File p -> sprintf "File: %s" p
  | ClaimScope.Project p -> sprintf "Project: %s" p

let private claimStateLabel = function
  | ClaimState.Held holder -> sprintf "Held by %s" (MemberId.display holder)
  | ClaimState.Orphaned(prev, since) -> sprintf "Orphaned (previously %s, since %s)" (MemberId.display prev) (since.ToString "u")
  | ClaimState.Released(by, at) -> sprintf "Released by %s at %s" (MemberId.display by) (at.ToString "u")

let private testIdsLabel (tests: TestId list) : string =
  match tests with
  | [] -> "(none)"
  | _ -> tests |> List.map (fun (TestId t) -> t) |> String.concat ", "

let private nextActionLabel = function
  | NextAction.RebaseAndResubmit -> "Rebase and resubmit"
  | NextAction.AwaitConductor -> "Await conductor"
  | NextAction.FixTests tests -> sprintf "Fix tests: %s" (testIdsLabel tests)
  | NextAction.Withdraw -> "Withdraw"

let private blockerLabel = function
  | LandingBlocker.RebaseConflict files -> sprintf "Rebase conflict in %s" (String.concat ", " files)
  | LandingBlocker.FailingTests tests -> sprintf "Failing tests: %s" (testIdsLabel tests)
  | LandingBlocker.StaleClaimFence(ClaimId cid) -> sprintf "Stale claim fence on %s" cid
  | LandingBlocker.HeadMoved(from_, to_) -> sprintf "Head moved from %s to %s" from_ to_
  | LandingBlocker.VetoedBy(by, reason) -> sprintf "Vetoed by %s: %s" (MemberId.display by) reason
  | LandingBlocker.Inconclusive reason -> sprintf "Verification inconclusive: %s" reason

let private landingStateLabel = function
  | LandingState.Queued -> "Queued"
  | LandingState.Rebasing onto -> sprintf "Rebasing onto %s" onto
  | LandingState.Verifying(baseHead, rebasedHead, affected, running) ->
    sprintf "Verifying (base %s, rebased %s, %d/%d tests still running)" baseHead rebasedHead running affected
  | LandingState.Blocked(blocker, next) -> sprintf "Blocked — %s (next: %s)" (blockerLabel blocker) (nextActionLabel next)
  | LandingState.Landed sha -> sprintf "Landed at %s" sha
  | LandingState.Withdrawn -> "Withdrawn"

let private workflowLabel = function
  | SessionWorkflow.Interactive -> "Interactive"
  | SessionWorkflow.WebLive _ -> "WebLive"

// ── Per-kind field projections — each `Map`/array lookup fails to
// `None`, never an exception, so `inspect` below has exactly one place
// (the final match) that turns "no such entity" into `NotFound`. ──────

let private memberFields (mid: MemberId) (state: CohortState<MemberId>) : Field list option =
  match Map.tryFind mid state.Members with
  | None -> None
  | Some record ->
    let claimsHeld =
      state.Claims
      |> Map.toList
      |> List.choose (fun (ClaimId cid, c) ->
        match c.State with
        | ClaimState.Held holder when holder = mid -> Some cid
        | _ -> None)
      |> List.sort
    let landingsRequested =
      state.Landings
      |> Map.toList
      |> List.choose (fun (LandingId lid, r) -> if r.Requester = mid then Some lid else None)
      |> List.sort
    let isConductor = state.Conductor = Some mid
    Some [
      { Label = "Id"; Value = MemberId.display mid }
      { Label = "Role"; Value = roleLabel record.Role }
      { Label = "Presence"; Value = presenceLabel record.Presence }
      { Label = "Conductor"; Value = if isConductor then "yes" else "no" }
      { Label = "Session"; Value = record.Session |> Option.defaultValue "(none)" }
      { Label = "Last renewal"; Value = record.LastRenewal.ToString "u" }
      { Label = "Claims held"; Value = if claimsHeld.IsEmpty then "(none)" else String.concat ", " claimsHeld }
      { Label = "Landings requested"; Value = if landingsRequested.IsEmpty then "(none)" else String.concat ", " landingsRequested }
    ]

let private claimFields (cid: ClaimId) (state: CohortState<MemberId>) : Field list option =
  match Map.tryFind cid state.Claims with
  | None -> None
  | Some c ->
    let (ClaimId raw) = cid
    Some [
      { Label = "Id"; Value = raw }
      { Label = "Scope"; Value = scopeLabel c.Scope }
      { Label = "Fence"; Value = string c.Fence }
      { Label = "Purpose"; Value = Purpose.value c.Purpose }
      { Label = "Since"; Value = c.Since.ToString "u" }
      { Label = "State"; Value = claimStateLabel c.State }
    ]

let private landingFields (lid: LandingId) (state: CohortState<MemberId>) : Field list option =
  match Map.tryFind lid state.Landings with
  | None -> None
  | Some r ->
    let (LandingId raw) = lid
    let claimsPresented =
      match r.Claims with
      | [] -> "(none)"
      | claims -> claims |> List.map (fun (ClaimId cid, fence) -> sprintf "%s@%s" cid (string fence)) |> String.concat ", "
    Some [
      { Label = "Id"; Value = raw }
      { Label = "Requester"; Value = MemberId.display r.Requester }
      { Label = "Claims presented"; Value = claimsPresented }
      { Label = "Commits"; Value = if r.Commits.IsEmpty then "(none)" else String.concat ", " r.Commits }
      { Label = "Base at queue"; Value = r.BaseAtQueue }
      { Label = "Statement"; Value = Statement.value r.Statement }
      { Label = "State"; Value = landingStateLabel r.State }
    ]

/// The test row's identity plus its bitmap popcount (vision §6.5's own
/// words for this row) — never per-session identity, which the frame does
/// not carry (see this module's doc comment).
let private testFields (tid: TestId) (frame: CohortFrame<MemberId>) : Field list option =
  let (TestId raw) = tid
  match frame.TestIds |> Array.tryFindIndex ((=) tid) with
  | None -> None
  | Some idx ->
    let popcount (rows: bool[][]) = rows |> Array.sumBy (fun row -> if row.[idx] then 1 else 0)
    Some [
      { Label = "Id"; Value = raw }
      { Label = "Frame version"; Value = string frame.Version }
      { Label = "Sessions reporting"; Value = string frame.Pass.Length }
      { Label = "Passing"; Value = string (popcount frame.Pass) }
      { Label = "Failing"; Value = string (popcount frame.Fail) }
      { Label = "Stale"; Value = string (popcount frame.Stale) }
    ]

let private sessionFields (sid: string) (sessions: SessionInfo list) : Field list option =
  sessions
  |> List.tryFind (fun s -> SessionId.value s.Id = sid)
  |> Option.map (fun s ->
    [
      { Label = "Id"; Value = SessionId.value s.Id }
      { Label = "Name"; Value = s.Name |> Option.defaultValue "(unnamed)" }
      { Label = "Working directory"; Value = s.WorkingDirectory }
      { Label = "Solution root"; Value = s.SolutionRoot |> Option.defaultValue "(none)" }
      { Label = "Created"; Value = s.CreatedAt.ToString "u" }
      { Label = "Last activity"; Value = s.LastActivity.ToString "u" }
      { Label = "Status"; Value = SessionLifecycleStatus.label s.Status }
      { Label = "Workflow"; Value = workflowLabel s.Workflow }
      { Label = "Active project"; Value = s.ActiveProject |> Option.defaultValue "(none)" }
      { Label = "Projects"; Value = if s.Projects.IsEmpty then "(none)" else String.concat ", " s.Projects }
      { Label = "App"; Value = SageFs.AppRun.describeState s.App }
    ])

/// Pure: `(kind, id, frame, ledger, sessions) -> InspectorModel` — the same
/// input always projects the same model (`Cohort.replay` is itself pure,
/// §7.1). No IO here; the caller fetches `frame`/`ledgerEntries`/`sessions`
/// before calling in (mirrors `CohortLanes.project`'s own "ledger entries
/// in, model out" shape).
let inspect
  (kind: EntityKind)
  (id: string)
  (frame: CohortFrame<MemberId>)
  (ledgerEntries: LedgerEntry<MemberId> list)
  (sessions: SessionInfo list)
  : InspectorModel =
  let fieldsOpt =
    match kind with
    | EntityKind.Member -> memberFields (parseMemberId id) (replay ledgerEntries)
    | EntityKind.Claim -> claimFields (ClaimId id) (replay ledgerEntries)
    | EntityKind.Landing -> landingFields (LandingId id) (replay ledgerEntries)
    | EntityKind.Test -> testFields (TestId id) frame
    | EntityKind.Session -> sessionFields id sessions
  match fieldsOpt with
  | Some fields -> InspectorModel.Found(kind, id, sprintf "%s %s" (EntityKind.label kind) id, fields)
  | None -> InspectorModel.NotFound(Some kind, id)

/// `inspect` plus the URL-segment parse the route handler needs — an
/// unrecognized `kind` segment is a clean not-found, never a 500.
let inspectRaw
  (kindRaw: string)
  (id: string)
  (frame: CohortFrame<MemberId>)
  (ledgerEntries: LedgerEntry<MemberId> list)
  (sessions: SessionInfo list)
  : InspectorModel =
  match EntityKind.tryParse kindRaw with
  | Some kind -> inspect kind id frame ledgerEntries sessions
  | None -> InspectorModel.NotFound(None, id)

/// Server-side substring search (the vision's "prefix trie" is a later
/// engine swap for cockpit scale — see this module's doc comment) across
/// every entity kind the read models support. An empty/whitespace query
/// returns no results rather than dumping the whole cohort — deterministic
/// and cheap either way (§5.1's v1 scope: one small daemon-scoped cohort).
/// Results are sorted by (kind label, id) so the same inputs always render
/// in the same order.
let search
  (query: string)
  (frame: CohortFrame<MemberId>)
  (ledgerEntries: LedgerEntry<MemberId> list)
  (sessions: SessionInfo list)
  : SearchResult list =
  let q = if isNull query then "" else query.Trim()
  if q.Length = 0 then
    []
  else
    let matches (s: string) = not (isNull s) && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
    let memberResults =
      frame.MemberIds
      |> Array.choose (fun m ->
        let display = MemberId.display m
        if matches display then Some { Kind = EntityKind.Member; Id = display; Label = display } else None)
      |> Array.toList
    let claimResults =
      Array.zip frame.ClaimIds frame.ClaimScope
      |> Array.choose (fun (ClaimId cid, scope) ->
        let label = sprintf "%s (%s)" cid (scopeLabel scope)
        if matches cid || matches label then Some { Kind = EntityKind.Claim; Id = cid; Label = label } else None)
      |> Array.toList
    let landingResults =
      (replay ledgerEntries).Landings
      |> Map.toList
      |> List.choose (fun (LandingId lid, r) ->
        let label = sprintf "%s — %s" lid (Statement.value r.Statement)
        if matches lid || matches label then Some { Kind = EntityKind.Landing; Id = lid; Label = label } else None)
    let testResults =
      frame.TestIds
      |> Array.choose (fun (TestId t) -> if matches t then Some { Kind = EntityKind.Test; Id = t; Label = t } else None)
      |> Array.toList
    let sessionResults =
      sessions
      |> List.choose (fun s ->
        let sid = SessionId.value s.Id
        let name = s.Name |> Option.defaultValue ""
        let label = sprintf "%s %s" sid (if name = "" then s.WorkingDirectory else name)
        if matches sid || matches name || matches s.WorkingDirectory then
          Some { Kind = EntityKind.Session; Id = sid; Label = label }
        else
          None)
    memberResults @ claimResults @ landingResults @ testResults @ sessionResults
    |> List.sortBy (fun r -> EntityKind.label r.Kind, r.Id)

// ── Rendering — Falco.Markup, the same `textEnc`/`attrEnc` escaping
// discipline `DashboardFragments`/`CohortTerritory`/`CohortLanes` use
// (`DashboardFragments.fs:28-52`'s `htmlEscape`, reused here rather than
// reimplemented). Deterministic: the same `InspectorModel`/query+results
// always renders byte-identical markup. ───────────────────────────────

let private renderFieldList (fields: Field list) : XmlNode =
  Elem.create "dl" [] (
    fields
    |> List.collect (fun f ->
      [ Elem.create "dt" [] [ textEnc f.Label ]
        Elem.create "dd" [] [ textEnc f.Value ] ]))

/// The inspector's content — a title plus the entity's raw fields as a
/// definition list, or a clean not-found message. No page chrome; see
/// `renderInspectorPage` for the full document the route serves.
let renderInspector (model: InspectorModel) : XmlNode =
  match model with
  | InspectorModel.Found(kind, _id, title, fields) ->
    Elem.div [ testid "inspector-found" ] [
      Elem.h2 [] [ textEnc title ]
      Elem.p [] [ textEnc (sprintf "kind: %s" (EntityKind.toUrlSegment kind)) ]
      renderFieldList fields
    ]
  | InspectorModel.NotFound(kindOpt, id) ->
    Elem.div [ testid "inspector-not-found" ] [
      Elem.h2 [] [ Text.raw "Not found" ]
      Elem.p [] [
        textEnc (
          match kindOpt with
          | Some kind -> sprintf "No %s with id '%s'." ((EntityKind.label kind).ToLowerInvariant()) id
          | None -> sprintf "Unrecognized entity kind for id '%s'." id)
      ]
    ]

let private inspectHref (kind: EntityKind) (id: string) : string =
  attrEnc (sprintf "/dashboard/inspect/%s/%s" (EntityKind.toUrlSegment kind) (Uri.EscapeDataString id))

/// The search box plus its results — content only, see `renderSearchPage`
/// for the full document.
let renderSearchResults (query: string) (results: SearchResult list) : XmlNode =
  Elem.div [ testid "inspector-search" ] [
    Elem.h2 [] [ Text.raw "Search" ]
    Elem.form [ Attr.action "/dashboard/inspect"; Attr.method "get" ] [
      Elem.input [ Attr.type' "text"; Attr.name "q"; Attr.value (attrEnc query); Attr.placeholder "member, claim, landing, test, or session id" ]
      Elem.button [ Attr.type' "submit" ] [ Text.raw "Search" ]
    ]
    (match results, query.Trim() with
     | [], "" -> Elem.p [] [ Text.raw "Type to search members, claims, landings, tests, and sessions." ]
     | [], _ -> Elem.p [] [ Text.raw "No matches." ]
     | _ ->
       Elem.ul [ testid "inspector-search-results" ] (
         results
         |> List.map (fun r ->
           Elem.li [] [
             Elem.a [ Attr.href (inspectHref r.Kind r.Id) ] [
               textEnc (sprintf "[%s] %s" (EntityKind.label r.Kind) r.Label)
             ]
           ])))
  ]

let private pageShell (title: string) (content: XmlNode) : XmlNode =
  Elem.html [] [
    Elem.head [] [
      Elem.title [] [ textEnc title ]
      Elem.link [ Attr.rel "stylesheet"; Attr.href "/dashboard/dashboard.css" ]
    ]
    Elem.body [] [
      Elem.div [ Attr.style "padding: 1rem; font-family: 'JetBrains Mono', monospace;" ] [
        Elem.p [] [
          Elem.a [ Attr.href "/dashboard" ] [ Text.raw "&larr; Dashboard" ]
          Text.raw "&nbsp;&nbsp;"
          Elem.a [ Attr.href "/dashboard/inspect" ] [ Text.raw "Search" ]
        ]
        content
      ]
    ]
  ]

/// The full page `GET /dashboard/inspect/<kind>/<id>` serves.
let renderInspectorPage (model: InspectorModel) : XmlNode =
  let title =
    match model with
    | InspectorModel.Found(_, _, t, _) -> t
    | InspectorModel.NotFound(_, id) -> sprintf "Not found: %s" id
  pageShell title (renderInspector model)

/// The full page `GET /dashboard/inspect` serves.
let renderSearchPage (query: string) (results: SearchResult list) : XmlNode =
  pageShell "Inspector search" (renderSearchResults query results)
