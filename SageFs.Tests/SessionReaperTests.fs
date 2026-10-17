/// Agents create sessions and often never stop them. The daemon stops a session an MCP connection made once that connection
/// has been gone, and the session idle, for the bound. Every time decision here is a pure function of the clock the test
/// passes in, so a failing seed replays.
module SageFs.Tests.SessionReaperTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.SessionReaper
open SageFs.Tests.ReaperTimeouts

/// A span of whole minutes, from a count. The numbers a test writes next to it are minutes by this name.
let private minutes (n: int) : TimeSpan = TimeSpan.FromMinutes (float n)

let private ago (n: int) : DateTime = now - minutes n

let private theBound = Bound.After bound

let private boundMinutes : int = int bound.TotalMinutes

/// A fact set where everything is read relative to `now`, in minutes ago.
let private factsOf (owner: OwnerFact) (work: SessionPhase) (idleMinutes: int) (daemonUpMinutes: int) : Facts =
  { Owner = owner
    Work = work
    LastActivity = ago idleMinutes
    DaemonStartedAt = ago daemonUpMinutes
    Now = now
    Bound = theBound }

let private gone (seenMinutesAgo: int) : OwnerFact = OwnerFact.NotConnected("agent-a", LastSeen.SeenAt(ago seenMinutesAgo))

let private isReap (decision: Decision) : bool =
  match decision with
  | Decision.Reap _ -> true
  | Decision.Keep _ -> false

/// The owner kinds a generated case picks from, by a number.
let private ownerOfKind (kind: int) (seenMinutesAgo: int) : OwnerFact =
  match kind % 4 with
  | 0 -> OwnerFact.NoOwnerRecord
  | 1 -> OwnerFact.Connected "agent-a"
  | 2 -> OwnerFact.NotConnected("agent-a", LastSeen.NeverSeen)
  | _ -> gone seenMinutesAgo

let private info (sid: string) (lastActive: DateTime) (status: WorkerProtocol.SessionLifecycleStatus) : WorkerProtocol.SessionInfo =
  { Id = WorkerProtocol.SessionId.validate sid |> function | Result.Ok id -> id | Result.Error e -> failwithf "bad id %s: %s" sid e
    Name = None
    Projects = []
    WorkingDirectory = "/work/" + sid
    SolutionRoot = None
    CreatedAt = ago (boundMinutes * 4)
    LastActivity = lastActive
    Status = status
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = AppRun.AppRunState.NotRunning
    Rebuild = LastRebuild.NeverRebuilt
    Reload = SessionReload.NoReloadYet
    Freshness = ReplFreshness.InSync }

let private ready : WorkerProtocol.SessionLifecycleStatus = WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 100; Port = None }

let private maker (sid: string) (connection: string) : Maker =
  { SessionId = sid; AgentName = "agent-" + connection; ConnectionId = connection }

let private stopsOk (log: ResizeArray<string>) : string -> Task<Result<string, SageFsError>> =
  fun sid ->
    lock log (fun () -> log.Add sid)
    Task.FromResult(Result.Ok(sprintf "Session '%s' stopped." sid))

[<Tests>]
let decisionTests =
  testList "Session reaper: the decision" [

    testProperty "a session with no owner record is never reaped, for any times at all" <| fun (idle: NonNegativeInt) (up: NonNegativeInt) (working: bool) ->
      let work = match working with | true -> SessionPhase.Working | false -> SessionPhase.AtRest
      decide (factsOf OwnerFact.NoOwnerRecord work idle.Get up.Get) |> isReap |> not

    testProperty "an owner that is connected is never reaped, for any times at all" <| fun (idle: NonNegativeInt) (up: NonNegativeInt) ->
      decide (factsOf (OwnerFact.Connected "agent-a") SessionPhase.AtRest idle.Get up.Get) |> isReap |> not

    testProperty "the same facts always give the same decision" <| fun (kind: int) (seen: NonNegativeInt) (idle: NonNegativeInt) (up: NonNegativeInt) ->
      let facts = factsOf (ownerOfKind (abs (kind % 4)) seen.Get) SessionPhase.AtRest idle.Get up.Get
      decide facts = decide facts

    testProperty "a reap means the owner was gone for the bound AND the session idle for the bound, both counted from no earlier than the daemon's own start" <| fun (kind: int) (seen: NonNegativeInt) (idle: NonNegativeInt) (up: NonNegativeInt) ->
      let facts = factsOf (ownerOfKind (abs (kind % 4)) seen.Get) SessionPhase.AtRest idle.Get up.Get
      match decide facts with
      | Decision.Keep _ -> true
      | Decision.Reap why ->
        why.OwnerGoneFor >= bound && why.IdleFor >= bound && (now - facts.DaemonStartedAt) >= bound

    testProperty "a restart never shortens the wait: a daemon started later reaps no sooner than one started earlier" <| fun (kind: int) (seen: NonNegativeInt) (idle: NonNegativeInt) (up: NonNegativeInt) (later: NonNegativeInt) ->
      let owner = ownerOfKind (abs (kind % 4)) seen.Get
      // `up` minutes of uptime against `up - later` minutes: the second daemon started more recently.
      let longer = factsOf owner SessionPhase.AtRest idle.Get up.Get
      let shorter = factsOf owner SessionPhase.AtRest idle.Get (max 0 (up.Get - later.Get))
      match decide shorter, decide longer with
      | Decision.Reap _, Decision.Keep _ -> false
      | _ -> true

    testProperty "work in flight is never reaped" <| fun (seen: NonNegativeInt) (idle: NonNegativeInt) (up: NonNegativeInt) ->
      decide (factsOf (gone seen.Get) SessionPhase.Working idle.Get up.Get) |> isReap |> not

    testProperty "a stopped session is left alone" <| fun (seen: NonNegativeInt) (idle: NonNegativeInt) (up: NonNegativeInt) ->
      decide (factsOf (gone seen.Get) SessionPhase.Stopped idle.Get up.Get) |> isReap |> not

    testProperty "a disabled bound reaps nothing, whatever the times" <| fun (kind: int) (seen: NonNegativeInt) (idle: NonNegativeInt) (up: NonNegativeInt) ->
      let facts = { factsOf (ownerOfKind (abs (kind % 4)) seen.Get) SessionPhase.AtRest idle.Get up.Get with Bound = Bound.Disabled }
      decide facts = Decision.Keep KeepWhy.Disabled

    testCase "WHY — a restart an hour ago does not make a session used an hour ago look abandoned for days" <| fun _ ->
      // The owner was last seen three days ago by an earlier daemon. This daemon has been up an hour. The session was last used
      // three days ago too. Presence is not persisted, so "gone since three days ago" would be a claim this daemon cannot make.
      let facts = factsOf (gone (3 * 24 * 60)) SessionPhase.AtRest (3 * 24 * 60) 60
      match decide facts with
      | Decision.Keep(KeepWhy.OwnerGoneButNotLongEnough(goneFor, _)) -> goneFor |> Expect.equal "gone only since this daemon started" (minutes 60)
      | other -> failtestf "a daemon up an hour must not reap, got %A" other

    testCase "WHY — an owner this daemon never saw is gone since the daemon started, not since the epoch" <| fun _ ->
      let started = ago 90
      goneSince started LastSeen.NeverSeen |> Expect.equal "the daemon's own start" started

    testCase "WHY — an owner last seen after the daemon started is gone since then" <| fun _ ->
      let started = ago 600
      goneSince started (LastSeen.SeenAt(ago 30)) |> Expect.equal "the last time it was seen" (ago 30)

    testCase "WHY — an owner last seen before the daemon started is gone since the daemon started" <| fun _ ->
      let started = ago 600
      goneSince started (LastSeen.SeenAt(ago 6000)) |> Expect.equal "the daemon's own start" started

    testCase "WHY — the session is reaped at exactly the bound, not one minute before" <| fun _ ->
      let up = boundMinutes * 3
      decide (factsOf (gone boundMinutes) SessionPhase.AtRest boundMinutes up) |> isReap |> Expect.isTrue "at the bound"
      decide (factsOf (gone (boundMinutes - 1)) SessionPhase.AtRest boundMinutes up) |> isReap |> Expect.isFalse "owner gone one minute short"
      decide (factsOf (gone boundMinutes) SessionPhase.AtRest (boundMinutes - 1) up) |> isReap |> Expect.isFalse "session idle one minute short"

    testCase "WHY — an owner gone long enough but a session used a minute ago is kept as active recently" <| fun _ ->
      match decide (factsOf (gone (boundMinutes * 2)) SessionPhase.AtRest 1 (boundMinutes * 3)) with
      | Decision.Keep(KeepWhy.ActiveRecently _) -> ()
      | other -> failtestf "expected ActiveRecently, got %A" other

    testCase "WHY — a clock that runs backwards is never a reason to reap" <| fun _ ->
      // Last activity and last seen in the future of `now`: negative ages. Fail closed.
      let facts = { factsOf (OwnerFact.NotConnected("agent-a", LastSeen.SeenAt(now + bound))) SessionPhase.AtRest 0 (boundMinutes * 3) with LastActivity = now + bound }
      decide facts |> isReap |> Expect.isFalse "a negative age is no age"

    testCase "WHY — a bound of zero or less means off, so SAGEFS_ABANDONED_SESSION_HOURS=0 disables reaping" <| fun _ ->
      Bound.ofTimeSpan TimeSpan.Zero |> Expect.equal "zero is off" Bound.Disabled
      Bound.ofTimeSpan (-(minutes 60)) |> Expect.equal "negative is off" Bound.Disabled
      Bound.ofTimeSpan bound |> Expect.equal "a positive span is a bound" (Bound.After bound)

    testCase "WHY — every reason a session is kept says something different, so a person can tell them apart" <| fun _ ->
      let reasons =
        [ KeepWhy.Disabled
          KeepWhy.NoOwnerRecord
          KeepWhy.OwnerConnected
          KeepWhy.WorkInFlight
          KeepWhy.AlreadyStopped
          KeepWhy.OwnerGoneButNotLongEnough(minutes 5, bound)
          KeepWhy.ActiveRecently(minutes 5, bound) ]
      let texts = reasons |> List.map KeepWhy.describe
      texts |> List.forall (fun t -> t <> "") |> Expect.isTrue "every reason has words"
      texts |> List.distinct |> List.length |> Expect.equal "no two reasons read the same" (List.length texts)

    testCase "WHY — the reap reason names the agent, how long the owner was gone and how long the session was idle" <| fun _ ->
      let why = { AgentName = "claude-7"; OwnerGoneFor = minutes 800; IdleFor = minutes 1500; Bound = bound }
      let text = ReapWhy.describe why
      text |> Expect.stringContains "names the agent" "claude-7"
      text |> Expect.stringContains "says how long the owner was gone" "13h 20m"
      text |> Expect.stringContains "says how long it was idle" "1d 1h"
      text |> Expect.stringContains "says the bound" "12h"
  ]

[<Tests>]
let reaperTests =
  testList "Session reaper: the sweep" [

    testTask "WHY — a session whose owner is gone and which is idle past the bound is stopped through the stop path, once" {
      let reaper = Reaper(ago (boundMinutes * 4), theBound)
      let stops = ResizeArray<string>()
      let makers = [ maker "aa000001" "conn-1" ]
      let mutable sessions = [ info "aa000001" (ago (boundMinutes * 2)) ready ]
      let isConnected (_: string) = false
      // The first sweep: the daemon has been up four bounds, the owner was never seen by it, so it is gone since the start.
      let! first = reaper.SweepAsync(now, makers, isConnected, stopsOk stops, sessions)
      // The stop path removed the session, as the real one does.
      sessions <- []
      let! second = reaper.SweepAsync(now + minutes 1, makers, isConnected, stopsOk stops, sessions)
      List.ofSeq stops |> Expect.equal "stopped exactly once" [ "aa000001" ]
      match first, second with
      | SweepOutcome.Swept [ attempt ], SweepOutcome.Swept [] -> attempt.Outcome |> Expect.isOk "the stop worked"
      | other -> failtestf "expected one reap then none, got %A" other
    }

    testTask "WHY — a session with no ledger record is untouched, however old and idle" {
      let reaper = Reaper(ago (boundMinutes * 40), theBound)
      let stops = ResizeArray<string>()
      let sessions = [ info "bb000002" (ago (boundMinutes * 30)) ready ]
      let! outcome = reaper.SweepAsync(now, [], (fun _ -> false), stopsOk stops, sessions)
      List.ofSeq stops |> Expect.isEmpty "nothing was stopped"
      outcome |> Expect.equal "swept and found nothing" (SweepOutcome.Swept [])
    }

    testTask "WHY — a connected owner keeps its idle session, and the moment it was last seen is remembered" {
      let reaper = Reaper(ago (boundMinutes * 40), theBound)
      let stops = ResizeArray<string>()
      let makers = [ maker "cc000003" "conn-3" ]
      let sessions = [ info "cc000003" (ago (boundMinutes * 30)) ready ]
      // The owner is here at `now`. It leaves. Half a bound later it is not a reason yet; a bound later it is.
      let! _ = reaper.SweepAsync(now, makers, (fun _ -> true), stopsOk stops, sessions)
      let! _ = reaper.SweepAsync(now + minutes (boundMinutes / 2), makers, (fun _ -> false), stopsOk stops, sessions)
      List.ofSeq stops |> Expect.isEmpty "gone half a bound is not long enough"
      let! _ = reaper.SweepAsync(now + bound, makers, (fun _ -> false), stopsOk stops, sessions)
      List.ofSeq stops |> Expect.equal "gone a bound since it was last seen" [ "cc000003" ]
    }

    testTask "WHY — a stop that fails is reported, is not counted as reaped, and is tried again on the next sweep" {
      let reaper = Reaper(ago (boundMinutes * 4), theBound)
      let attempts = ResizeArray<string>()
      let failing : string -> Task<Result<string, SageFsError>> =
        fun sid ->
          lock attempts (fun () -> attempts.Add sid)
          Task.FromResult(Result.Error(SageFsError.SessionStopFailed(sid, "the supervisor did not answer")))
      let makers = [ maker "dd000004" "conn-4" ]
      let sessions = [ info "dd000004" (ago (boundMinutes * 2)) ready ]
      let! first = reaper.SweepAsync(now, makers, (fun _ -> false), failing, sessions)
      let! _ = reaper.SweepAsync(now + minutes 1, makers, (fun _ -> false), failing, sessions)
      List.ofSeq attempts |> Expect.equal "tried on both sweeps" [ "dd000004"; "dd000004" ]
      reaper.Reaped |> Expect.isEmpty "nothing was recorded as reaped"
      match first with
      | SweepOutcome.Swept [ attempt ] -> attempt.Outcome |> Expect.isError "the failure is carried, not swallowed"
      | other -> failtestf "expected one failed attempt, got %A" other
    }

    testTask "WHY — a sweep that starts while another is still stopping a session does nothing, so one session is never stopped twice" {
      let reaper = Reaper(ago (boundMinutes * 4), theBound)
      let release = TaskCompletionSource<unit>()
      let started = TaskCompletionSource<unit>()
      let calls = ref 0
      let slow : string -> Task<Result<string, SageFsError>> =
        fun sid ->
          task {
            System.Threading.Interlocked.Increment calls |> ignore
            started.TrySetResult () |> ignore
            do! release.Task
            return Result.Ok(sprintf "Session '%s' stopped." sid)
          }
      let makers = [ maker "ee000005" "conn-5" ]
      let sessions = [ info "ee000005" (ago (boundMinutes * 2)) ready ]
      let firstSweep = reaper.SweepAsync(now, makers, (fun _ -> false), slow, sessions)
      do! started.Task
      let! overlapping = reaper.SweepAsync(now, makers, (fun _ -> false), slow, sessions)
      overlapping |> Expect.equal "the overlapping sweep stood aside" SweepOutcome.AlreadySweeping
      release.SetResult ()
      let! _ = firstSweep
      calls.Value |> Expect.equal "one stop call" 1
    }

    testTask "WHY — a reaped session is on the record with the agent, how long the owner was gone and how long it was idle" {
      let reaper = Reaper(ago (boundMinutes * 4), theBound)
      let makers = [ maker "ff000006" "conn-6" ]
      let sessions = [ info "ff000006" (ago (boundMinutes * 2)) ready ]
      let! _ = reaper.SweepAsync(now, makers, (fun _ -> false), stopsOk (ResizeArray()), sessions)
      match reaper.Reaped with
      | [ reaped ] ->
        reaped.SessionId |> Expect.equal "the session" "ff000006"
        reaped.AgentName |> Expect.equal "the agent" "agent-conn-6"
        reaped.Why.OwnerGoneFor |> Expect.equal "gone since the daemon started" (minutes (boundMinutes * 4))
        reaped.Why.IdleFor |> Expect.equal "idle since its last activity" (minutes (boundMinutes * 2))
      | other -> failtestf "expected one reaped session, got %A" other
    }

    testTask "WHY — a disabled bound stops nothing and says so for each agent-made session" {
      let reaper = Reaper(ago (boundMinutes * 4), Bound.Disabled)
      let stops = ResizeArray<string>()
      let makers = [ maker "aa000007" "conn-7" ]
      let sessions = [ info "aa000007" (ago (boundMinutes * 2)) ready ]
      let! _ = reaper.SweepAsync(now, makers, (fun _ -> false), stopsOk stops, sessions)
      List.ofSeq stops |> Expect.isEmpty "off means off"
      match reaper.Assess(now, makers, (fun _ -> false), sessions) with
      | [ assessment ] -> assessment.Decision |> Expect.equal "kept because it is off" (Decision.Keep KeepWhy.Disabled)
      | other -> failtestf "expected one assessment, got %A" other
    }

    testCase "WHY — evaluating, building, starting and restarting sessions count as work in flight, and a stopped one as stopped" <| fun _ ->
      let handle : WorkerProtocol.WorkerHandle = { Pid = 100; Port = None }
      phaseOf (WorkerProtocol.SessionLifecycleStatus.Evaluating handle) |> Expect.equal "evaluating" SessionPhase.Working
      phaseOf (WorkerProtocol.SessionLifecycleStatus.Building("rebuild", handle)) |> Expect.equal "building" SessionPhase.Working
      phaseOf (WorkerProtocol.SessionLifecycleStatus.Starting handle) |> Expect.equal "starting" SessionPhase.Working
      phaseOf ready |> Expect.equal "ready" SessionPhase.AtRest
      phaseOf WorkerProtocol.SessionLifecycleStatus.Stopped |> Expect.equal "stopped" SessionPhase.Stopped

    testCase "WHY — the list line says whether the owner is here and how long the session has been idle, so a person sees what would be reaped" <| fun _ ->
      let reaper = Reaper(ago (boundMinutes * 4), theBound)
      let makers = [ maker "aa000008" "conn-8"; maker "bb000009" "conn-9" ]
      let sessions =
        [ info "aa000008" (ago 90) ready
          info "bb000009" (ago (boundMinutes * 2)) ready
          info "cc00000a" (ago 5) ready ]
      let lines =
        reaper.Assess(now, makers, (fun c -> c = "conn-8"), sessions)
        |> List.map (fun a -> a.SessionId, Assessment.listLine a)
        |> Map.ofList
      lines.["aa000008"] |> Expect.stringContains "connected owner" "still here"
      lines.["bb000009"] |> Expect.stringContains "gone owner is named" "agent-conn-9"
      lines.["bb000009"] |> Expect.stringContains "gone owner has an age" "gone for"
      lines.["cc00000a"] |> Expect.equal "a session a person made has no line" ""
  ]
