module SageFs.Tests.CreateSessionDedupeTests

open System
open System.Diagnostics
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol

/// DST Phase-3 Brief B7 — locks in the roast's session-creation TOCTOU fix
/// (§4: "session-creation TOCTOU dedupe") as a property.
///
/// `ManagerState.tryFindDuplicate` (SessionManager.fs:198-211) is the pure
/// dedupe oracle the mailbox owner consults before adding a new session
/// (the `CreateSession` branch, SessionManager.fs:906-919). Because the
/// mailbox processes one `SessionCommand` at a time, folding requests
/// through `tryFindDuplicate -> addSession` one at a time is a faithful
/// model of the owner's own strict serialization — not a re-simulation of
/// concurrency, since the fix already makes the decision atomic by being
/// made by a single serialized owner rather than by the CQRS snapshot read
/// in Mcp.fs (which stays merely advisory).
///
/// `tryFindDuplicate` is pure and synchronous, so this is a property test
/// over the real function directly: no mailbox/actor/process machinery is
/// needed to exercise the decision the fix relies on.
///
/// Per the DST Phase-3 plan (dst-phase3-plan.md, Brief B7): "the dedupe
/// decision is already pure and serialized at the owner, so this is a
/// property test, not a full concurrency sim — one small file."

let private propConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

// ── Fixtures ──────────────────────────────────────────────────────────

/// The two dedupe-relevant fields of a CreateSession request
/// (SessionManager.fs:202).
type private CreateRequest = { Dir: string; Projects: string list }

let private targetsOfProjects (projects: string list) =
  projects |> List.map (fun path -> SessionProjectTarget.Project path)

/// Mirrors `tryFindDuplicate`'s own equivalence (SessionManager.fs:206-209):
/// case-insensitive directory, order-independent project set.
let private key (r: CreateRequest) = r.Dir.ToLowerInvariant(), List.sort r.Projects

let private mkSessionInfo (id: SessionId) (r: CreateRequest) (status: SessionStatus) : SessionInfo =
  { Id = id
    Name = None
    Projects = r.Projects
    WorkingDirectory = r.Dir
    SolutionRoot = None
    CreatedAt = DateTime.MinValue
    LastActivity = DateTime.MinValue
    Status = SessionLifecycleStatus.ofWorkerReport (SessionLifecycleStatus.Ready { Pid = 100; Port = None }) status
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = AppRun.AppRunState.NotRunning }

let private mkManagedSession (id: SessionId) (r: CreateRequest) (status: SessionStatus) : ManagedSession =
  let proxy : SessionProxy =
    fun _ -> async { return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "test") }
  { Info = mkSessionInfo id r status
    Process = Process.GetCurrentProcess()
    Proxy = proxy
    WorkerBaseUrl = ""
    Targets = targetsOfProjects r.Projects
    WorkingDir = r.Dir
    AutoOpenNamespaces = true
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    RestartState = RestartPolicy.emptyState
    AppGeneration = AppRun.AppSlot.initial.Generation
    AdoptedCore = None
    ProjectRoles = [] }

/// A small, deliberately-colliding pool: directories differing only by case
/// (exercises SessionManager.fs:207's case-insensitive compare) and project
/// lists that are the same set in a different order (exercises
/// SessionManager.fs:209's order-independent compare), so seeded batches
/// collide often instead of vacuously never colliding.
let private dirPool = [| "/repo/a"; "/REPO/A"; "/repo/b"; "/repo/c"; "/other/dir" |]

let private projectPool =
  [| [ "A.fsproj" ]
     [ "A.fsproj"; "B.fsproj" ]
     [ "B.fsproj"; "A.fsproj" ]
     [ "C.fsproj" ] |]

/// SessionStatus restricted to the non-Stopped cases. Production never
/// leaves a Stopped-status session sitting in `ManagerState.Sessions` — every
/// path that transitions a session to Stopped calls `ManagerState.removeSession`
/// in the same step (SessionManager.fs:973, :1408, :1512), so "present in the
/// map" and "live (non-Stopped)" coincide in every reachable state. This pool
/// is used to build ManagerStates that reflect that reachable state space.
///
/// `tryFindDuplicate` itself applies NO status filter at all — see the
/// dedicated characterization test below, which demonstrates that directly
/// against the real function rather than assuming it.
let private liveStatuses =
  [| SessionStatus.Starting; SessionStatus.Ready; SessionStatus.Evaluating
     SessionStatus.Faulted; SessionStatus.Restarting |]

let private testSessionId (hex: string) =
  match SessionId.validate hex with
  | Ok sid -> sid
  | Error e -> failwithf "test bug: invalid session ID '%s': %s" hex e

let private randomRequest (rnd: Random) : CreateRequest =
  { Dir = dirPool.[rnd.Next(dirPool.Length)]
    Projects = projectPool.[rnd.Next(projectPool.Length)] }

let private randomStatus (rnd: Random) = liveStatuses.[rnd.Next(liveStatuses.Length)]

/// A seeded batch of (request, status) entries plus one query request — a
/// pure function of the seed, so any failure replays exactly (same
/// dependency-free `System.Random` convention as `SageFs.Simulation.Generators.fromSeed`).
let private fromSeed (seed: int) =
  let rnd = Random(seed)
  let n = rnd.Next(0, 12)
  let entries = [ for _ in 1 .. n -> randomRequest rnd, randomStatus rnd ]
  let query = randomRequest rnd
  entries, query

/// Builds a ManagerState from seeded entries, one fresh SessionId per entry.
let private buildState (entries: (CreateRequest * SessionStatus) list) =
  entries
  |> List.mapi (fun i (r, s) -> testSessionId (sprintf "%08x" (i + 1)), r, s)
  |> List.fold (fun st (id, r, s) -> ManagerState.addSession id (mkManagedSession id r s) st) ManagerState.empty

/// Models the mailbox's own `CreateSession` branch (SessionManager.fs:906-919):
/// consult the real dedupe oracle, and only add when it found no collision.
/// A rejected request leaves the state unchanged — mirroring
/// `reply.Reply(Error (SageFsError.DuplicateSession ...)); return state`.
let private applyCreateSerialized (st: ManagerState) (i: int, r: CreateRequest) : ManagerState =
  match ManagerState.tryFindDuplicate (targetsOfProjects r.Projects) r.Dir st with
  | Some _existingId -> st
  | None ->
    let id = testSessionId (sprintf "%08x" (1000 + i))
    ManagerState.addSession id (mkManagedSession id r SessionStatus.Ready) st

// ── Tests ────────────────────────────────────────────────────────────

[<Tests>]
let tests =
  testList "CreateSession dedupe (TOCTOU fix, SessionManager.fs:906-919)" [

    testPropertyWithConfig propConfig
      "tryFindDuplicate finds a match iff a live session shares (dir, projects)" <|
      fun (seed: int) ->
        let entries, query = fromSeed seed
        let state = buildState entries
        let found = ManagerState.tryFindDuplicate (targetsOfProjects query.Projects) query.Dir state
        let expectedCollision = entries |> List.exists (fun (r, _) -> key r = key query)
        match found with
        | Some _ -> expectedCollision
        | None -> not expectedCollision

    testPropertyWithConfig propConfig
      "a found duplicate's id names a session that actually collides on (dir, projects)" <|
      fun (seed: int) ->
        let entries, query = fromSeed seed
        let state = buildState entries
        match ManagerState.tryFindDuplicate (targetsOfProjects query.Projects) query.Dir state with
        | None -> true
        | Some foundId ->
          match ManagerState.tryGetSession foundId state with
          | None -> false
          | Some found ->
            key { Dir = found.Info.WorkingDirectory; Projects = found.Info.Projects } = key query

    testPropertyWithConfig propConfig
      "serialized creates through the dedupe never produce two live sessions for one (projects, dir) — the TOCTOU this fix closes" <|
      fun (seed: int) ->
        let entries, _query = fromSeed seed
        let requests = entries |> List.map fst |> List.mapi (fun i r -> i, r)
        let finalState = requests |> List.fold applyCreateSerialized ManagerState.empty
        finalState.Sessions
        |> Map.toList
        |> List.map (fun (_, s) -> key { Dir = s.Info.WorkingDirectory; Projects = s.Info.Projects })
        |> List.countBy id
        |> List.forall (fun (_, count) -> count = 1)

    testCase "the property has teeth: an undeduped fold DOES allow two live sessions for the same (dir, projects); the dedupe-gated fold does not" <| fun _ ->
      let r = { Dir = "/repo/dupe"; Projects = [ "A.fsproj" ] }
      let requests = [ 0, r; 1, r; 2, r ]

      // The check-then-act twin: always adds, never consults the oracle —
      // models the CQRS advisory guard (Mcp.fs) that the fix moved off of.
      let applyCreateUnguarded (st: ManagerState) (i: int, req: CreateRequest) =
        let id = testSessionId (sprintf "%08x" (2000 + i))
        ManagerState.addSession id (mkManagedSession id req SessionStatus.Ready) st

      let unguardedCount =
        (ManagerState.empty, requests) ||> List.fold applyCreateUnguarded
        |> fun st -> st.Sessions |> Map.count
      let guardedCount =
        (ManagerState.empty, requests) ||> List.fold applyCreateSerialized
        |> fun st -> st.Sessions |> Map.count

      unguardedCount |> Expect.equal "the unguarded twin lets all three duplicate creates through" 3
      guardedCount |> Expect.equal "the real dedupe-gated fold keeps exactly one live session" 1

    testCase "the error path returns SageFsError.DuplicateSession naming the existing session (SessionManager.fs:914)" <| fun _ ->
      let existing = { Dir = "/repo/a"; Projects = [ "A.fsproj" ] }
      let existingId = testSessionId "aaaaaaaa"
      let state =
        ManagerState.addSession existingId (mkManagedSession existingId existing SessionStatus.Ready) ManagerState.empty
      // Case-different dir, reordered projects — still the same dedupe key.
      let dupe = { Dir = "/REPO/A"; Projects = [ "A.fsproj" ] }
      match ManagerState.tryFindDuplicate (targetsOfProjects dupe.Projects) dupe.Dir state with
      | None -> failtest "expected tryFindDuplicate to find the existing session"
      | Some foundId ->
        match SageFsError.DuplicateSession(SessionId.value foundId, dupe.Dir) with
        | SageFsError.DuplicateSession(id, dir) ->
          id |> Expect.equal "names the existing session" (SessionId.value existingId)
          dir |> Expect.equal "carries the rejected working directory" dupe.Dir
        | _ -> failtest "expected DuplicateSession"

    testCase "tryFindDuplicate performs no status filtering itself — liveness is the removeSession invariant, not this predicate" <| fun _ ->
      // Characterization test, not a claim about reachable production state:
      // production never leaves a Stopped-status session in the map (every
      // Stop path calls removeSession in the same step — SessionManager.fs:973,
      // :1408, :1512), but tryFindDuplicate's own equality check
      // (SessionManager.fs:202-211) never looks at Status at all, so a
      // Stopped-status entry would still be reported as a match. Documented
      // here so "live (non-Stopped)" is never mistaken for a filter
      // tryFindDuplicate applies itself.
      let r = { Dir = "/repo/stopped"; Projects = [ "A.fsproj" ] }
      let id = testSessionId "deadbeef"
      let state = ManagerState.addSession id (mkManagedSession id r SessionStatus.Stopped) ManagerState.empty
      ManagerState.tryFindDuplicate (targetsOfProjects r.Projects) r.Dir state
      |> Expect.equal "tryFindDuplicate matches by (dir, projects) alone, regardless of Status" (Some id)
  ]
