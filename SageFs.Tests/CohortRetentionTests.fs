module SageFs.Tests.CohortRetentionTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Cohort

// The cohort's SETTLED artifacts — orphaned/released claims, departed members,
// settled (Blocked/Landed/Withdrawn) landings — used to live in `CohortState`
// forever: the only exit from `Orphaned` was a conductor `ReassignClaim`, and
// nothing ever removed a `Released` claim, a `Departed` member or a settled
// landing. The live daemon showed 43 orphaned claims, 4 departed members and
// 9 blocked landings, hours old, all still listed. `Retention.sweep` (run by
// `Tick`) is the pure decision that closes it; these tests pin its invariants
// and freeze the old behavior as a twin that provably violates them.

let private t0 = DateTime(2026, 1, 1, 12, 0, 0)
let private retention = settledRetention
let private second = TimeSpan.FromSeconds 1.0

let private ent (n: int) : Entropy = [| byte (n &&& 0xff); byte ((n >>> 8) &&& 0xff) |]

let private ok (clock: DateTime) (n: int) (st: CohortState<string>) (cmd: CohortCommand<string>) =
  match decide clock (ent n) st cmd with
  | Ok(st', evs, _) -> st', evs
  | Error e -> failwithf "unexpected refusal of %A: %A" cmd e

let private tryStep (clock: DateTime) (n: int) (st: CohortState<string>) (cmd: CohortCommand<string>) =
  match decide clock (ent n) st cmd with
  | Ok(st', _, _) -> st'
  | Error _ -> st

let private check (what: string) (holds: bool) =
  if not holds then failwithf "violated: %s" what

let private claimIdOf (st: CohortState<string>) (scope: ClaimScope) : ClaimId =
  st.Claims |> Map.toList |> List.pick (fun (id, c) -> if c.Scope = scope then Some id else None)

// ── arbitrary histories, decoded from FsCheck bytes and folded through the REAL
//    decide (refusals are skipped, exactly as in production) ─────────────────

module private History =
  let members = [| "a"; "b"; "c"; "d" |]
  let files = [| "/r/x.fs"; "/r/y.fs"; "/r/z.fs" |]

  type Op =
    | Join of string
    | Depart of string
    | Acquire of string * string
    | ReleaseNth of int
    | Advance of TimeSpan
    | RequestLanding of string
    | LandingOutcome of idx: int * mode: int

  let decode (k: byte, p: byte, q: byte) : Op =
    let m = members.[int p % members.Length]
    match int k % 7 with
    | 0 -> Join m
    | 1 -> Depart m
    | 2 -> Acquire(m, files.[int q % files.Length])
    | 3 -> ReleaseNth(int p)
    | 4 -> Advance(TimeSpan.FromMinutes(float (int q % 25)))
    | 5 -> RequestLanding m
    | _ -> LandingOutcome(int p, int q)

  let record (raw: (byte * byte * byte) list) : LedgerEntry<string> list * DateTime =
    let step (st: CohortState<string>, entries: LedgerEntry<string> list, clock: DateTime, n: int) (op: Op) =
      let n = n + 1
      let attempt (cmd: CohortCommand<string>) =
        match decide clock (ent n) st cmd with
        | Ok(st', evs, _) ->
          let entry : LedgerEntry<string> =
            { Seq = LanguagePrimitives.Int64WithMeasure(int64 n); Clock = clock; Entropy = ent n; Command = cmd; Events = evs }
          st', entry :: entries, clock, n
        | Error _ -> st, entries, clock, n
      match op with
      | Advance d -> st, entries, clock + d, n
      | Join m -> attempt (CohortCommand.Join(m, JoinableRole.Implementer, None))
      | Depart m -> attempt (CohortCommand.Depart m)
      | Acquire(m, f) -> attempt (CohortCommand.AcquireClaim(m, ClaimScope.File f, "editing"))
      | ReleaseNth p ->
        let held =
          st.Claims |> Map.toList
          |> List.choose (fun (id, c) -> match c.State with ClaimState.Held h -> Some(h, id, c.Fence) | _ -> None)
        match held with
        | [] -> st, entries, clock, n
        | _ ->
          let h, id, fence = held.[p % held.Length]
          attempt (CohortCommand.ReleaseClaim(h, id, fence))
      | RequestLanding m -> attempt (CohortCommand.RequestLanding(m, [], [ "abc" ], "land it"))
      | LandingOutcome(idx, mode) ->
        let landings = st.Landings |> Map.toList
        match landings with
        | [] -> st, entries, clock, n
        | _ ->
          let id, l = landings.[idx % landings.Length]
          match mode % 4 with
          | 0 -> attempt (CohortCommand.RebaseCompleted(id, Error [ "f.fs" ]))
          | 1 -> attempt (CohortCommand.WithdrawLanding(l.Requester, id))
          | 2 -> attempt (CohortCommand.VetoLanding(l.Requester, id, "no"))
          | _ -> attempt (CohortCommand.ResolveVeto(st.Conductor |> Option.defaultValue "a", id))
    let _, entries, clock, _ =
      raw |> List.map decode |> List.fold step (CohortState.empty (), [], t0, 0)
    List.rev entries, clock

  let run (raw: (byte * byte * byte) list) : CohortState<string> * DateTime =
    let entries, clock = record raw
    replay entries, clock

// ── the invariant every sweep must satisfy, as a checkable function ────────

let private claimMentions (m: string) (c: Claim<string>) =
  match c.State with
  | ClaimState.Held h -> h = m
  | ClaimState.Orphaned(h, _) -> h = m
  | ClaimState.Released(by, _) -> by = m

/// Everything that should NOT still be in `st` at `now`. Empty = bounded.
let private boundedViolations (now: DateTime) (st: CohortState<string>) : string list =
  let aged (since: DateTime) = now - since >= retention
  [ for KeyValue(id, c) in st.Claims do
      match c.State with
      | ClaimState.Orphaned(_, since) when aged since -> yield sprintf "orphaned claim %A past retention" id
      | ClaimState.Released(_, at) when aged at -> yield sprintf "released claim %A past retention" id
      | _ -> ()
    let scopes = st.Claims |> Map.toList |> List.groupBy (fun (_, c) -> c.Scope)
    for scope, group in scopes do
      let held = group |> List.exists (fun (_, c) -> match c.State with ClaimState.Held _ -> true | _ -> false)
      let orphans = group |> List.filter (fun (_, c) -> match c.State with ClaimState.Orphaned _ -> true | _ -> false)
      if held && not orphans.IsEmpty then yield sprintf "orphan on %A shadowed by a live Held claim" scope
      if orphans.Length > 1 then yield sprintf "%d orphans on the same scope %A" orphans.Length scope
    for KeyValue(id, l) in st.Landings do
      match l.Settlement, l.State with
      | LandingSettlement.SettledAt at, LandingState.Blocked(LandingBlocker.VetoedBy _, NextAction.AwaitConductor) -> ()
      | LandingSettlement.SettledAt at, _ when aged at -> yield sprintf "settled landing %A past retention" id
      | _ -> ()
    for KeyValue(m, r) in st.Members do
      match r.Presence with
      | MemberPresence.Departed since when aged since && st.Conductor <> Some m ->
        let referenced =
          (st.Claims |> Map.exists (fun _ c -> claimMentions m c))
          || (st.Landings |> Map.exists (fun _ l -> l.Requester = m))
        if not referenced then yield sprintf "departed member %s past retention" m
      | _ -> () ]

// ── the coordinator's live scene, scaled: N members each held a claim, all
//    went silent (orphaned), plus N blocked landings and N departed members ──

let private staleScene (n: int) : CohortState<string> * DateTime =
  let names = [ for i in 1 .. n -> sprintf "agent-%d" i ]
  let s0 = CohortState.empty ()
  let s1 = names |> List.fold (fun (s, k) m -> fst (ok t0 k s (CohortCommand.Join(m, JoinableRole.Implementer, None))), k + 1) (s0, 1) |> fst
  let s2 =
    names
    |> List.fold (fun (s, k) m -> fst (ok t0 k s (CohortCommand.AcquireClaim(m, ClaimScope.File (sprintf "/r/%s.fs" m), "editing"))), k + 1) (s1, 100)
    |> fst
  // every member requests a landing that then hits a rebase conflict (Blocked,
  // popped) just before the cohort goes silent — so these settle WITHIN the
  // window of the departure tick below, like the claims that orphan on it
  let settleClock = t0 + leaseWindow + TimeSpan.FromSeconds 30.0
  let s3 =
    names
    |> List.fold
      (fun (s, k) m ->
        let s', _ = ok settleClock k s (CohortCommand.RequestLanding(m, [], [ "abc" ], "land it"))
        let lid = s'.Landings |> Map.toList |> List.map fst |> List.find (fun id -> s'.Landings.[id].Requester = m)
        match decide settleClock (ent k) s' (CohortCommand.RebaseCompleted(lid, Error [ "conflict.fs" ])) with
        | Ok(s'', _, _) -> s'', k + 1
        | Error _ -> s', k + 1)
      (s2, 200)
    |> fst
  // the whole cohort goes silent past the lease: everyone departs, every claim orphans
  let silent = t0 + leaseWindow + TimeSpan.FromMinutes 1.0
  let s4, _ = ok silent 900 s3 CohortCommand.Tick
  s4, silent

[<Tests>]
let retentionTests =
  testList "Cohort retention — settled artifacts are pruned (Tick → Retention.sweep)" [

    test "an orphaned claim survives inside the retention window and is pruned once it elapses" {
      let s, silent = staleScene 1
      let cid = claimIdOf s (ClaimScope.File "/r/agent-1.fs")
      match s.Claims.[cid].State with
      | ClaimState.Orphaned _ -> ()
      | other -> failtestf "scene should have orphaned the claim, got %A" other
      let justInside, _ = ok (silent + retention - second) 901 s CohortCommand.Tick
      justInside.Claims |> Map.containsKey cid
      |> Expect.isTrue "still inside the window, the conductor may yet reassign it"
      let atExpiry, events = ok (silent + retention) 902 s CohortCommand.Tick
      atExpiry.Claims |> Map.containsKey cid
      |> Expect.isFalse "at the end of the window the orphaned claim must be gone"
      events
      |> List.exists (function CohortEvent.ClaimPruned(c, ClaimPruneReason.OrphanedPastRetention _) -> c = cid | _ -> false)
      |> Expect.isTrue "the prune must be a recorded event carrying WHY"
    }

    test "a released claim stops lingering after the window (Released was never removed from state)" {
      let s0, _ = ok t0 1 (CohortState.empty ()) (CohortCommand.Join("a", JoinableRole.Implementer, None))
      let s1, _ = ok t0 2 s0 (CohortCommand.AcquireClaim("a", ClaimScope.File "/r/a.fs", "editing"))
      let cid = claimIdOf s1 (ClaimScope.File "/r/a.fs")
      let s2, _ = ok t0 3 s1 (CohortCommand.ReleaseClaim("a", cid, s1.Claims.[cid].Fence))
      // keep 'a' alive so only the Released claim ages
      let s3, _ = ok (t0 + retention) 4 s2 (CohortCommand.RenewLease "a")
      let s4, _ = ok (t0 + retention) 5 s3 CohortCommand.Tick
      s4.Claims |> Map.isEmpty |> Expect.isTrue "a released claim past the window is dead weight"
    }

    test "THE LIVE SCENE: 43 orphaned claims, departed members and blocked landings all drain after the window" {
      let s, silent = staleScene 43
      (s.Claims |> Map.count) |> Expect.equal "the scene really has 43 orphaned claims" 43
      (s.Landings |> Map.count) |> Expect.equal "and 43 landings" 43
      let drained, _ = ok (silent + retention) 903 s CohortCommand.Tick
      drained.Claims |> Map.count |> Expect.equal "no orphaned claim outlives the window" 0
      drained.Landings |> Map.count |> Expect.equal "no settled landing outlives the window" 0
      // only the conductor's own seat may remain (Authority hangs off it)
      drained.Members |> Map.toList |> List.map fst
      |> Expect.equal "only the conductor's seat survives" [ "agent-1" ]
    }

    test "a new Held claim on the same file supersedes the old orphan immediately, without waiting out the window" {
      let s, silent = staleScene 1
      let orphan = claimIdOf s (ClaimScope.File "/r/agent-1.fs")
      let later = silent + TimeSpan.FromMinutes 1.0
      let s1, _ = ok later 10 s (CohortCommand.Join("newcomer", JoinableRole.Implementer, None))
      let s2, _ = ok later 11 s1 (CohortCommand.AcquireClaim("newcomer", ClaimScope.File "/r/agent-1.fs", "taking over"))
      let fresh = s2.Claims |> Map.toList |> List.pick (fun (id, c) -> match c.State with ClaimState.Held _ -> Some id | _ -> None)
      let s3, events = ok later 12 s2 CohortCommand.Tick
      s3.Claims |> Map.containsKey orphan |> Expect.isFalse "the shadowed orphan is dead weight the moment a live claim covers the same file"
      s3.Claims |> Map.containsKey fresh |> Expect.isTrue "the live claim is untouched"
      events
      |> List.exists (function CohortEvent.ClaimPruned(c, ClaimPruneReason.SupersededBy by) -> c = orphan && by = fresh | _ -> false)
      |> Expect.isTrue "the event names the claim that superseded it"
    }

    test "two orphans on the same file collapse to the newest one (several orphans per file was the live symptom)" {
      let t = t0
      let s0, _ = ok t 1 (CohortState.empty ()) (CohortCommand.Join("a", JoinableRole.Implementer, None))
      let s1, _ = ok t 2 s0 (CohortCommand.Join("b", JoinableRole.Implementer, None))
      let s2, _ = ok t 3 s1 (CohortCommand.AcquireClaim("a", ClaimScope.File "/r/f.fs", "first"))
      let first = claimIdOf s2 (ClaimScope.File "/r/f.fs")
      let s3, _ = ok t 4 s2 (CohortCommand.Depart "a")
      let s4, _ = ok t 5 s3 (CohortCommand.AcquireClaim("b", ClaimScope.File "/r/f.fs", "second"))
      let second' = s4.Claims |> Map.toList |> List.pick (fun (id, c) -> if id <> first then Some id else None)
      let s5, _ = ok t 6 s4 (CohortCommand.Depart "b")
      let s6, _ = ok t 7 s5 CohortCommand.Tick
      s6.Claims |> Map.toList |> List.map fst
      |> Expect.equal "only the newest orphan on the file remains" [ second' ]
    }

    test "the conductor's seat is never purged, however long it has been departed" {
      let s, silent = staleScene 3
      let drained, _ = ok (silent + retention + retention + retention) 904 s CohortCommand.Tick
      drained.Members |> Map.containsKey "agent-1"
      |> Expect.isTrue "authority resolves through the conductor's seat; purging it would orphan the binding"
    }

    test "a departed member still named by a landing awaiting the conductor is not purged" {
      let s0, _ = ok t0 1 (CohortState.empty ()) (CohortCommand.Join("boss", JoinableRole.Implementer, None))
      let s1, _ = ok t0 2 s0 (CohortCommand.Join("req", JoinableRole.Implementer, None))
      let s2, _ = ok t0 3 s1 (CohortCommand.RequestLanding("req", [], [ "abc" ], "land it"))
      let lid = s2.Landings |> Map.toList |> List.head |> fst
      let s3, _ = ok t0 4 s2 (CohortCommand.VetoLanding("boss", lid, "hold on"))
      let s4, _ = ok t0 5 s3 (CohortCommand.Depart "req")
      let s5, _ = ok (t0 + retention + retention) 6 s4 CohortCommand.Tick
      s5.Landings |> Map.containsKey lid
      |> Expect.isTrue "a veto awaiting the conductor is an actionable decision, not settled history"
      s5.Members |> Map.containsKey "req"
      |> Expect.isTrue "a member a surviving landing still names must not be purged out from under it"
    }

    test "resolving a veto un-settles the landing so it cannot be pruned while queued again" {
      let s0, _ = ok t0 1 (CohortState.empty ()) (CohortCommand.Join("boss", JoinableRole.Implementer, None))
      let s1, _ = ok t0 2 s0 (CohortCommand.RequestLanding("boss", [], [ "abc" ], "land it"))
      let lid = s1.Landings |> Map.toList |> List.head |> fst
      let s2, _ = ok t0 3 s1 (CohortCommand.VetoLanding("boss", lid, "hold"))
      s2.Landings.[lid].Settlement |> Expect.notEqual "a vetoed landing is settled" LandingSettlement.Unsettled
      let s3, _ = ok t0 4 s2 (CohortCommand.ResolveVeto("boss", lid))
      s3.Landings.[lid].Settlement |> Expect.equal "a re-queued landing is live again" LandingSettlement.Unsettled
    }

    test "TWIN WITH TEETH: the pre-fix decision (never prune) violates the bounded invariant on the live scene" {
      let s, silent = staleScene 43
      let later = silent + retention
      let twin, twinEvents = Retention.neverPrunesTwin later s
      twinEvents |> Expect.isEmpty "the twin emits nothing — it is the old behavior"
      boundedViolations later twin
      |> List.isEmpty
      |> Expect.isFalse "43 orphans past the window remain: the frozen old behavior violates BOUNDED"
      let real, _ = Retention.sweep later s
      boundedViolations later real |> Expect.isEmpty "the real sweep leaves nothing prunable behind"
    }

    // ── properties over arbitrary histories ─────────────────────────────

    testProperty "sweep leaves nothing prunable behind, never touches live state, and is idempotent" <|
      fun (raw: (byte * byte * byte) list) (afterMinutes: byte) ->
        let st, clock = History.run raw
        let now = clock + TimeSpan.FromMinutes(float afterMinutes * 2.0)
        let swept, events = Retention.sweep now st
        let again, againEvents = Retention.sweep now swept
        let violations = boundedViolations now swept
        let liveKept =
          (st.Claims |> Map.forall (fun id c -> match c.State with ClaimState.Held _ -> Map.containsKey id swept.Claims | _ -> true))
          && (st.Members |> Map.forall (fun m r -> match r.Presence with MemberPresence.Present -> Map.containsKey m swept.Members | _ -> true))
          && (st.Landings |> Map.forall (fun id l -> match l.Settlement with LandingSettlement.Unsettled -> Map.containsKey id swept.Landings | _ -> true))
        let removedAreReported =
          let removedClaims = st.Claims |> Map.toList |> List.filter (fun (id, _) -> not (Map.containsKey id swept.Claims)) |> List.map fst
          let prunedClaims = events |> List.choose (function CohortEvent.ClaimPruned(id, _) -> Some id | _ -> None)
          List.sort removedClaims = List.sort prunedClaims
        check (sprintf "nothing prunable may remain: %A" violations) violations.IsEmpty
        check "a Held claim / Present member / live landing was pruned" liveKept
        check "sweep must be idempotent" (again = swept && againEvents.IsEmpty)
        check "every removed claim must be a reported ClaimPruned" removedAreReported
        true

    testProperty "settlement is stamped iff the landing is terminal (Blocked/Landed/Withdrawn), by construction" <|
      fun (raw: (byte * byte * byte) list) ->
        let st, _ = History.run raw
        st.Landings
        |> Map.forall (fun _ l ->
          match l.State, l.Settlement with
          | (LandingState.Queued | LandingState.Rebasing _ | LandingState.Verifying _), LandingSettlement.Unsettled -> true
          | (LandingState.Blocked _ | LandingState.Landed _ | LandingState.Withdrawn), LandingSettlement.SettledAt _ -> true
          | _ -> false)

    testProperty "replaying the recorded ledger reproduces the pruned state (sweep is a pure function of state + clock)" <|
      fun (raw: (byte * byte * byte) list) (afterMinutes: byte) ->
        let entries, clock = History.record raw
        let tickAt = clock + TimeSpan.FromMinutes(float afterMinutes * 2.0) + retention
        let live =
          match decide tickAt (ent 60000) (replay entries) CohortCommand.Tick with
          | Ok(s, _, _) -> s
          | Error _ -> replay entries
        let recorded =
          let tick : LedgerEntry<string> =
            { Seq = LanguagePrimitives.Int64WithMeasure 0L; Clock = tickAt; Entropy = ent 60000; Command = CohortCommand.Tick; Events = [] }
          entries @ [ tick ]
        replay recorded = live
  ]
