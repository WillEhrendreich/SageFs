module SageFs.Tests.TrunkFollowTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.WorkerProtocol
open SageFs.Features
open SageFs.Features.TrunkFollow

// -- fixtures ---------------------------------------------------------------------------------------------------------------

let private landing (n: int) : LandedLanding = { Landing = LandingId (sprintf "l-%d" n); Commit = sprintf "c-%d" n }

let private pendingFacts : ReloadFacts =
  { Case = ReloadCase.PatchPending
    Patched = 0
    Considered = 1
    Message = "applied"
    SuggestedAction = ""
    Mechanism = ReloadOutcome.PatchMechanism.MetadataDelta
    Declarations = [] }

let private file (name: string) : SavedFile = { Path = Path.Combine("/trunk", name); Kind = SaveKind.Changed }

let private payload (kind: string) (outcome: string) (mechanism: string) (reasons: string) : string =
  sprintf
    """{"type":"%s","outcome":"%s","patched":0,"considered":1,"message":"m","suggestedAction":"","reasons":[%s]%s}"""
    kind outcome reasons (match mechanism with "" -> "" | name -> sprintf ""","mechanism":"%s" """ name)

// -- the machine, by example ---------------------------------------------------------------------------------------------------

let private servingSession = { Session = "s1"; State = TrunkSessionState.Serving }

let private followedByPatch (n: int) : TrunkMachine =
  let machine, _ = step initial (TrunkEvent.Landed (landing n))
  let machine, _ = step machine (TrunkEvent.Moved ((landing n).Landing, TrunkMove.Moved ([ file "Handlers.fs" ], [ servingSession ])))
  let verdict : FileVerdict = { File = "Handlers.fs"; Outcome = FileOutcome.Reloaded (pendingFacts, []) }
  let machine, _ = step machine (TrunkEvent.Answered ((landing n).Landing, "s1", SessionOutcome.Delivered [ verdict ]))
  machine

[<Tests>]
let trunkFollowTests =
  testList "TrunkFollow" [

    testList "which cohort events reach the trunk" [
      testCase "only a landing that landed" <| fun _ ->
        let l = (landing 1).Landing
        ofCohortEvent (CohortEvent<string>.LandingLanded (l, "c-1"))
        |> Expect.equal "LandingLanded is the one" (Some (TrunkEvent.Landed (landing 1)))
        [ CohortEvent<string>.LandingQueued (l, "alice")
          CohortEvent<string>.LandingStateChanged (l, LandingState.Verifying ("h", "r", 1, 0))
          CohortEvent<string>.LandingStateChanged (l, LandingState.Blocked (LandingBlocker.FailingTests [], NextAction.Withdraw))
          CohortEvent<string>.LandingStateChanged (l, LandingState.Landed "c-1")
          CohortEvent<string>.LandingWithdrawn l
          CohortEvent<string>.LandingVetoed (l, "bob", "no") ]
        |> List.choose ofCohortEvent
        |> Expect.isEmpty "a landing that is queued, verified, blocked, withdrawn or vetoed never reaches the trunk"
    ]

    testList "one landing, step by step" [
      testCase "a landing that lands while idle asks for the checkout to be moved" <| fun _ ->
        let machine, effects = step initial (TrunkEvent.Landed (landing 1))
        effects |> Expect.equal "move the trunk" [ TrunkEffect.MoveTrunk (landing 1) ]
        machine.Phase |> Expect.equal "and is moving" (Phase.Moving (landing 1))

      testCase "the same landing twice is followed once" <| fun _ ->
        let moving, _ = step initial (TrunkEvent.Landed (landing 1))
        let again, effects = step moving (TrunkEvent.Landed (landing 1))
        effects |> Expect.isEmpty "nothing new"
        again |> Expect.equal "the machine is as it was" moving

      testCase "a landing that lands while another is followed waits its turn" <| fun _ ->
        let moving, _ = step initial (TrunkEvent.Landed (landing 1))
        let queued, effects = step moving (TrunkEvent.Landed (landing 2))
        effects |> Expect.isEmpty "no second move while the first is in flight"
        queued.Queued |> Expect.equal "queued" [ landing 2 ]

      testCase "a serving session is told which files changed, and nothing else is asked" <| fun _ ->
        let moving, _ = step initial (TrunkEvent.Landed (landing 1))
        let files = [ file "Handlers.fs"; file "Other.fs" ]
        let _, effects = step moving (TrunkEvent.Moved ((landing 1).Landing, TrunkMove.Moved (files, [ servingSession ])))
        effects |> Expect.equal "deliver to the one session" [ TrunkEffect.Deliver (landing 1, "s1", files) ]

      testCase "a session with no app is recorded as having nothing to update, and the next landing starts" <| fun _ ->
        let moving, _ = step initial (TrunkEvent.Landed (landing 1))
        let queued, _ = step moving (TrunkEvent.Landed (landing 2))
        let next, effects =
          step queued (TrunkEvent.Moved ((landing 1).Landing, TrunkMove.Moved ([ file "Handlers.fs" ], [ { Session = "s1"; State = TrunkSessionState.NoRunningApp } ])))
        next.Records
        |> List.map (fun r -> r.Verdict)
        |> Expect.equal "recorded" [ TrunkVerdict.Followed [ { Session = "s1"; Outcome = SessionOutcome.NoApp } ] ]
        effects |> Expect.equal "and the queued landing is next" [ TrunkEffect.MoveTrunk (landing 2) ]

      testCase "an answer for a landing that is not in flight is dropped" <| fun _ ->
        let machine = followedByPatch 1
        let after, effects = step machine (TrunkEvent.Answered ((landing 9).Landing, "s1", SessionOutcome.NoApp))
        effects |> Expect.isEmpty "nothing"
        after |> Expect.equal "nothing changed" machine

      testCase "a pending patch is settled by the report that follows, and only a patch of the same kind" <| fun _ ->
        let machine = followedByPatch 1
        let settled = { pendingFacts with Case = ReloadCase.Patched; Patched = 1 }
        let otherKind = { settled with Mechanism = ReloadOutcome.PatchMechanism.Detour }
        let untouched, _ = step machine (TrunkEvent.ReloadReported ("s1", otherKind))
        untouched |> Expect.equal "a detour's report does not settle a delta's patch" machine
        let after, _ = step machine (TrunkEvent.ReloadReported ("s1", settled))
        match (List.head after.Records).Verdict with
        | TrunkVerdict.Followed [ { Outcome = SessionOutcome.Delivered [ { Outcome = FileOutcome.Reloaded (facts, _) } ] } ] ->
          facts.Case |> Expect.equal "settled" ReloadCase.Patched
        | other -> failtestf "unexpected %A" other

      testCase "a report about another session is not this session's" <| fun _ ->
        let machine = followedByPatch 1
        let after, _ = step machine (TrunkEvent.ReloadReported ("elsewhere", { pendingFacts with Case = ReloadCase.Patched }))
        after |> Expect.equal "unchanged" machine

      testCase "the machine keeps at most recordLimit records, the newest" <| fun _ ->
        let one (machine: TrunkMachine) (n: int) =
          let m, _ = step machine (TrunkEvent.Landed (landing n))
          let m, _ = step m (TrunkEvent.Moved ((landing n).Landing, TrunkMove.Moved ([], [])))
          m
        let machine = [ 1 .. recordLimit + 5 ] |> List.fold one initial
        List.length machine.Records |> Expect.equal "bounded" recordLimit
        (List.last machine.Records).Landing |> Expect.equal "the newest is kept" (landing (recordLimit + 5)).Landing
    ]

    testList "what a payload says" [
      testCase "a pending delta patch is a verdict with its mechanism" <| fun _ ->
        match outcomeOfPayload (payload "pending" "PatchPending" "metadata-delta" "") with
        | FileOutcome.Reloaded (facts, causes) ->
          facts.Case |> Expect.equal "case" ReloadCase.PatchPending
          facts.Mechanism |> Expect.equal "mechanism" ReloadOutcome.PatchMechanism.MetadataDelta
          causes |> Expect.isEmpty "no causes"
        | other -> failtestf "unexpected %A" other

      testCase "a restart carries the causes the worker named" <| fun _ ->
        let reasons = """{"case":"TypeShapeChanged","message":"the shape of type Shape changed\nand more","suggestedAction":""}"""
        match outcomeOfPayload (payload "restarted" "Restarted" "" reasons) with
        | FileOutcome.Reloaded (facts, causes) ->
          facts.Case |> Expect.equal "case" ReloadCase.Restarted
          causes |> List.map (fun c -> c.Case) |> Expect.equal "named" [ "TypeShapeChanged" ]
        | other -> failtestf "unexpected %A" other

      testCase "something that is not a verdict says why" <| fun _ ->
        match outcomeOfPayload "not json" with
        | FileOutcome.NoVerdict reason -> reason |> Expect.isNotEmpty "a reason"
        | other -> failtestf "unexpected %A" other
        match outcomeOfPayload """{"type":"none"}""" with
        | FileOutcome.NoVerdict _ -> ()
        | other -> failtestf "unexpected %A" other
    ]

    testList "what the status says" [
      testCase "a landing followed into a running app names the file, the case and the mechanism" <| fun _ ->
        let lines = statusLines (followedByPatch 1)
        lines |> List.length |> Expect.equal "one line" 1
        lines.Head |> Expect.stringContains "the landing" "trunk l-1:"
        lines.Head |> Expect.stringContains "the file" "Handlers.fs PatchPending by metadata-delta"
        lines.Head |> Expect.stringContains "the session" "session s1"

      testCase "a restart names its cause" <| fun _ ->
        let facts = { pendingFacts with Case = ReloadCase.Restarted; Mechanism = ReloadOutcome.PatchMechanism.NoPatch }
        let verdict : FileVerdict =
          { File = "Rude.fs"; Outcome = FileOutcome.Reloaded (facts, [ { Case = "TypeShapeChanged"; Message = "the shape of type Shape changed\nsecond line" } ]) }
        let moving, _ = step initial (TrunkEvent.Landed (landing 1))
        let delivering, _ = step moving (TrunkEvent.Moved ((landing 1).Landing, TrunkMove.Moved ([ file "Rude.fs" ], [ servingSession ])))
        let done', _ = step delivering (TrunkEvent.Answered ((landing 1).Landing, "s1", SessionOutcome.Delivered [ verdict ]))
        (statusLines done').Head |> Expect.stringContains "the restart and its cause" "Rude.fs Restarted (TypeShapeChanged: the shape of type Shape changed)"

      testCase "a session with no app is told to rebuild before run_app" <| fun _ ->
        let moving, _ = step initial (TrunkEvent.Landed (landing 1))
        let done', _ = step moving (TrunkEvent.Moved ((landing 1).Landing, TrunkMove.Moved ([ file "Handlers.fs" ], [ { Session = "s1"; State = TrunkSessionState.NoRunningApp } ])))
        let line = (statusLines done').Head
        line |> Expect.stringContains "says so" "no running app to update"
        line |> Expect.stringContains "says what to do" "rebuild the session before run_app"

      testCase "a landing in flight is shown as being followed, and a queued one as queued" <| fun _ ->
        let moving, _ = step initial (TrunkEvent.Landed (landing 1))
        let queued, _ = step moving (TrunkEvent.Landed (landing 2))
        match statusLines queued with
        | [ first; second ] ->
          first |> Expect.stringContains "the one in flight" "trunk l-1: following"
          second |> Expect.stringContains "the one waiting" "trunk l-2: queued"
        | other -> failtestf "unexpected %A" other
    ]
  ]

// -- which sessions are the trunk's ---------------------------------------------------------------------------------------------

let private session (id: string) (dir: string) (status: SessionLifecycleStatus) (app: AppRun.AppRunState) : SessionInfo =
  { Id = (match SessionId.validate id with Result.Ok s -> s | Result.Error e -> failwith e)
    Name = None
    Projects = []
    WorkingDirectory = dir
    SolutionRoot = None
    CreatedAt = DateTime.MinValue
    LastActivity = DateTime.MinValue
    Status = status
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = app
    Rebuild = LastRebuild.NeverRebuilt
    Reload = SessionReload.NoReloadYet
    Freshness = ReplFreshness.InSync }

let private ready = SessionLifecycleStatus.Ready { Pid = 1; Port = None }

let private running : AppRun.AppRunState =
  AppRun.AppRunState.Running
    { RunId = "r1"; Project = "p"; EntryPoint = "e"; Endpoint = AppRun.AppEndpoint.NoServer; StartedAt = DateTime.MinValue }

[<Tests>]
let trunkSessionTests =
  testList "TrunkSessions" [
    testCase "a trunk session is a session whose working directory is the trunk checkout, and nothing else" <| fun _ ->
      let trunk = Path.Combine(Path.GetTempPath(), "cohort-trunk")
      let sessions =
        [ session "aaaaaaa1" trunk ready running
          session "aaaaaaa2" (trunk + string Path.DirectorySeparatorChar) ready running
          session "aaaaaaa3" (Path.Combine(Path.GetTempPath(), "cohort-integration")) ready running
          session "aaaaaaa4" (Path.Combine(trunk, "nested")) ready running ]
      TrunkSessions.sessionsIn trunk sessions
      |> List.map (fun s -> SessionId.value s.Id)
      |> Expect.equal "the exact directory, with or without a trailing separator" [ "aaaaaaa1"; "aaaaaaa2" ]

    testCase "a ready session with a running app is serving; without one it has no app to update" <| fun _ ->
      match TrunkSessions.read (session "aaaaaaa1" "/t" ready running) with
      | TrunkSessions.Reading.Settled { State = TrunkSessionState.Serving } -> ()
      | other -> failtestf "unexpected %A" other
      for app in [ AppRun.AppRunState.NotRunning
                   AppRun.AppRunState.Exited ("p", 0, DateTime.MinValue)
                   AppRun.AppRunState.Crashed ("p", "boom", DateTime.MinValue) ] do
        match TrunkSessions.read (session "aaaaaaa1" "/t" ready app) with
        | TrunkSessions.Reading.Settled { State = TrunkSessionState.NoRunningApp } -> ()
        | other -> failtestf "%A should read as no running app, got %A" app other

    testCase "a session that is starting, building or restarting, or whose app is starting, is waited on, never read as having no app" <| fun _ ->
      let handle = { Pid = 1; Port = None }
      for status in [ SessionLifecycleStatus.Starting handle
                      SessionLifecycleStatus.Building ("rebuild", handle)
                      SessionLifecycleStatus.Restarting (PreviousWorker.ofPid (Some 1)) ] do
        match TrunkSessions.read (session "aaaaaaa1" "/t" status AppRun.AppRunState.NotRunning) with
        | TrunkSessions.Reading.Settling _ -> ()
        | other -> failtestf "%A should be settling, got %A" status other
      match TrunkSessions.read (session "aaaaaaa1" "/t" ready (AppRun.AppRunState.Starting ("p", AppRun.StartPhase.LaunchingEntryPoint, DateTime.MinValue))) with
      | TrunkSessions.Reading.Settling _ -> ()
      | other -> failtestf "an app that is starting should be settling, got %A" other

    testCase "a faulted, crashed or stopped session is unavailable and says why" <| fun _ ->
      for status in [ SessionLifecycleStatus.Faulted (FaultReason.report "the build failed"); SessionLifecycleStatus.Stopped ] do
        match TrunkSessions.read (session "aaaaaaa1" "/t" status AppRun.AppRunState.NotRunning) with
        | TrunkSessions.Reading.Settled { State = TrunkSessionState.Unavailable reason } -> reason |> Expect.isNotEmpty "a reason"
        | other -> failtestf "%A should be unavailable, got %A" status other
  ]
