module SageFs.Tests.IntegrationSettleTests

/// What the landing gate does while the integration session is not ready, and what it tells each party when it stops waiting
/// (findings C-restart and C-pending of the first cohort run). The decision is `IntegrationSettle.judge`, the loop is
/// `IntegrationSettle.await`, and the simulation over the loop is in `IntegrationSettleSimTests`.
open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.IntegrationSettle

let private starting (text: string) : Phase = Phase.Starting (WorkerSaid.Said text)

let private restarting : Phase = Phase.Restarting WorkerSaid.Nothing

let private compiling : Phase = Phase.Building ("dotnet build", WorkerSaid.Said "compiling")

let private contains (needle: string) (haystack: string) : bool = haystack.Contains needle

/// Every refusal the module can write, for the texts every refusal has to get right.
let private everyRefusal : (string * Refusal) list =
  let window = Timeouts.cohortIntegrationStallWindow
  [ "stuck in startup", stuckRefusal "5bb94271" (starting "loading project 7 of 11") window
    "stuck restarting", stuckRefusal "5bb94271" restarting window
    "stuck building", stuckRefusal "5bb94271" compiling window
    "dead: faulted", deadRefusal "5bb94271" (describePhase (Phase.Faulted "out of memory"))
    "dead: refused", deadRefusal "5bb94271" "the rebuild failed: FS0001"
    "repointed", repointedRefusal ]

[<Tests>]
let judgeTests =
  testList "IntegrationSettle.judge: what a look at the session's phase means" [

    testCase "WHY — a session that is Ready has settled, so the gate goes on to classify its trust" <| fun _ ->
      judge None Phase.Ready |> Expect.equal "ready" Verdict.Settled
      judge (Some (starting "x")) Phase.Ready |> Expect.equal "ready after a startup phase" Verdict.Settled

    testCase "WHY — the first look at a startup phase is never a stall, because one look cannot show that nothing changed" <| fun _ ->
      judge None (starting "loading project 1") |> Expect.equal "first look" Verdict.KeepWaiting

    testCase "WHY — a different phase at the second look is progress, so the gate waits another window instead of cutting the restart off" <| fun _ ->
      judge (Some (starting "loading project 1")) (starting "loading project 2")
      |> Expect.equal "new progress text" Verdict.KeepWaiting
      judge (Some restarting) (starting "loading project 1")
      |> Expect.equal "restarting became starting" Verdict.KeepWaiting

    testCase "WHY — the same phase at two looks a window apart is a stall, and it carries the phase it stuck in" <| fun _ ->
      let phase = starting "loading project 7 of 11"
      judge (Some phase) phase |> Expect.equal "same text twice" (Verdict.Stuck phase)
      judge (Some restarting) restarting |> Expect.equal "restarting twice" (Verdict.Stuck restarting)
      judge (Some compiling) compiling |> Expect.equal "building twice" (Verdict.Stuck compiling)

    testCase "WHY — a rebuild in flight is never a stall, because its bound is the build runner's and a quiet build is a healthy one" <| fun _ ->
      judge (Some Phase.BuildInFlight) Phase.BuildInFlight |> Expect.equal "twice" Verdict.KeepWaiting
      judge None Phase.BuildInFlight |> Expect.equal "once" Verdict.KeepWaiting

    testCase "WHY — a dead session is reported as dead at once, with what it is, because waiting will not revive it" <| fun _ ->
      for dead in [ Phase.Faulted "boom"; Phase.HostCrashed; Phase.Stopped; Phase.Missing ] do
        judge None dead |> Expect.equal "dead on the first look" (Verdict.Dead dead)

    testCase "WHY — a first look is never Stuck for any phase, so a stall always costs at least two looks" <| fun _ ->
      let phases =
        [ restarting; starting "a"; Phase.Building ("b", WorkerSaid.Nothing); Phase.BuildInFlight; Phase.Ready
          Phase.Faulted "c"; Phase.HostCrashed; Phase.Stopped; Phase.Missing ]
      for phase in phases do
        match judge None phase with
        | Verdict.Stuck _ -> failtestf "%A was judged Stuck on its first look" phase
        | _ -> ()
  ]

[<Tests>]
let awaitTests =
  testList "IntegrationSettle.await: parking in slices of the stall window" [

    testAsync "WHY — every park the gate makes is one stall window long, never a deadline of its own choosing" {
      let window = Timeouts.cohortIntegrationStallWindow
      let parks = ResizeArray<TimeSpan>()
      let ports : Ports =
        { AwaitReady = fun slice -> async { parks.Add slice; return (match parks.Count with 3 -> Wake.Ready | _ -> Wake.SliceElapsed) }
          Observe = fun () -> async { return starting (sprintf "step %d" parks.Count) } }
      let! outcome = IntegrationSettle.await window ports
      outcome |> Expect.equal "settled on the third park" Outcome.Settled
      parks |> Seq.toList |> Expect.equal "three parks, each one window" [ window; window; window ]
    }

    testAsync "WHY — a wait that has made no progress between two looks ends as StuckIn the phase, and names the window" {
      let window = Timeouts.cohortIntegrationStallWindow
      let phase = starting "loading project 7 of 11"
      let ports : Ports =
        { AwaitReady = fun _ -> async { return Wake.SliceElapsed }
          Observe = fun () -> async { return phase } }
      let! outcome = IntegrationSettle.await window ports
      outcome |> Expect.equal "stuck in the phase" (Outcome.StuckIn (phase, window))
    }

    testAsync "WHY — a dead session seen at the end of a slice ends the wait as DiedAs, without a second look" {
      let looks = ref 0
      let ports : Ports =
        { AwaitReady = fun _ -> async { return Wake.SliceElapsed }
          Observe = fun () -> async { looks.Value <- looks.Value + 1; return Phase.HostCrashed } }
      let! outcome = IntegrationSettle.await Timeouts.cohortIntegrationStallWindow ports
      outcome |> Expect.equal "died" (Outcome.DiedAs Phase.HostCrashed)
      looks.Value |> Expect.equal "one look" 1
    }

    testAsync "WHY — the session manager's own refusal ends the wait with its reason untouched" {
      let ports : Ports =
        { AwaitReady = fun _ -> async { return Wake.Refused "the rebuild failed: FS0001" }
          Observe = fun () -> async { return failwith "a refused wait is not observed" } }
      let! outcome = IntegrationSettle.await Timeouts.cohortIntegrationStallWindow ports
      outcome |> Expect.equal "refused" (Outcome.Refused "the rebuild failed: FS0001")
    }
  ]

[<Tests>]
let refusalTests =
  testList "IntegrationSettle: what a refusal tells each party" [

    testCase "WHY — no refusal tells a member to run a conductor's tool, because a member cannot (finding C-pending)" <| fun _ ->
      for (name, refusal) in everyRefusal do
        refusal.ForMember |> contains "set_integration_ref" |> Expect.isFalse (sprintf "%s: the member text must not name set_integration_ref" name)
        refusal.ForMember |> contains "hard_reset_fsi_session" |> Expect.isFalse (sprintf "%s: the member text must not name hard_reset_fsi_session" name)

    testCase "WHY — every refusal gives a member the same next action, withdraw and request again, in words it can run" <| fun _ ->
      for (name, refusal) in everyRefusal do
        refusal.ForMember |> Expect.stringContains (sprintf "%s: withdraw" name) "withdraw_landing"
        refusal.ForMember |> Expect.stringContains (sprintf "%s: request again after the status" name) "get_cohort_status"

    testCase "WHY — a stuck session's text tells the conductor what the session is doing and for how long" <| fun _ ->
      let window = Timeouts.cohortIntegrationStallWindow
      let refusal = stuckRefusal "5bb94271" (starting "loading project 7 of 11") window
      refusal.ForConductor |> Expect.stringContains "the phase" "loading project 7 of 11"
      refusal.ForConductor |> Expect.stringContains "the session" "5bb94271"
      refusal.ForConductor |> Expect.stringContains "the window in seconds" (sprintf "%.0f s" window.TotalSeconds)
      refusal.What |> Expect.stringContains "the same facts in the shared sentence" "loading project 7 of 11"

    testCase "WHY — no refusal repeats the fixed-deadline wording that sent a member to a tool it does not own (finding C-restart)" <| fun _ ->
      for (name, refusal) in everyRefusal do
        let text = render refusal
        text |> contains "timed out during restart" |> Expect.isFalse (sprintf "%s: no deadline wording" name)
        text |> contains "Try again or use hard_reset_fsi_session" |> Expect.isFalse (sprintf "%s: no advice meant for a person at a keyboard" name)

    testCase "WHY — a rendered refusal carries all three parts, so the status reader sees what happened and what each party does" <| fun _ ->
      for (name, refusal) in everyRefusal do
        let text = render refusal
        text |> Expect.stringContains (sprintf "%s: what" name) refusal.What
        text |> Expect.stringContains (sprintf "%s: member" name) refusal.ForMember
        text |> Expect.stringContains (sprintf "%s: conductor" name) refusal.ForConductor

    testCase "WHY — a settled wait has no refusal text, and every other outcome has one" <| fun _ ->
      let window = Timeouts.cohortIntegrationStallWindow
      describeOutcome "s1" Outcome.Settled |> Expect.isNone "settled"
      for outcome in [ Outcome.StuckIn (starting "x", window); Outcome.DiedAs Phase.Missing; Outcome.Refused "why" ] do
        describeOutcome "s1" outcome |> Expect.isSome (sprintf "%A has a text" outcome)

    testCase "WHY — a phase is described with its progress text, so the conductor reads what the worker last said" <| fun _ ->
      describePhase (starting "loading project 7 of 11") |> Expect.stringContains "progress" "loading project 7 of 11"
      describePhase (Phase.Faulted "out of memory") |> Expect.stringContains "reason" "out of memory"
  ]

[<Tests>]
let awaitChangeTests =
  testList "IntegrationSettle.awaitChange: a landing that arrives while the session is pending waits for it (finding C-pending)" [

    testAsync "WHY — a value that is ready already answers at once, without waiting for any change" {
      let changed = Event<unit>()
      let! value = IntegrationSettle.awaitChange changed.Publish (fun () -> "started") (fun v -> v <> "pending")
      value |> Expect.equal "ready" "started"
    }

    testCase "WHY — a pending value holds the landing until the change that starts it, and the waiter then reads the new value" <| fun _ ->
      let changed = Event<unit>()
      let value = ref "pending"
      let waiter =
        IntegrationSettle.awaitChange changed.Publish (fun () -> value.Value) (fun v -> v <> "pending")
        |> Started.asTask
      waiter.IsCompleted |> Expect.isFalse "still waiting while the value is pending"
      changed.Trigger ()
      waiter.IsCompleted |> Expect.isFalse "a change that leaves it pending does not release it"
      value.Value <- "started"
      changed.Trigger ()
      waiter.Wait TestTimeouts.patience |> Expect.isTrue "released by the change that started it"
      waiter.Result |> Expect.equal "the started value" "started"

    testCase "WHY — a change that lands between the read and the park is not missed, because the subscription is made before the read" <| fun _ ->
      let changed = Event<unit>()
      let value = ref "pending"
      let reads = ref 0
      // The change happens INSIDE the first read, after the value was read as pending and before the park.
      let read () =
        reads.Value <- reads.Value + 1
        let seen = value.Value
        match reads.Value with
        | 1 ->
          value.Value <- "started"
          changed.Trigger ()
        | _ -> ()
        seen
      let waiter = IntegrationSettle.awaitChange changed.Publish read (fun v -> v <> "pending") |> Started.asTask
      waiter.Wait TestTimeouts.patience |> Expect.isTrue "the early change released the wait"
      waiter.Result |> Expect.equal "the started value" "started"

    testCase "WHY — a failed integration also releases the wait, because the gate then reports the failure instead of holding forever" <| fun _ ->
      let changed = Event<unit>()
      let value = ref "pending"
      let waiter = IntegrationSettle.awaitChange changed.Publish (fun () -> value.Value) (fun v -> v <> "pending") |> Started.asTask
      value.Value <- "failed: the worktree build failed"
      changed.Trigger ()
      waiter.Wait TestTimeouts.patience |> Expect.isTrue "released"
      waiter.Result |> Expect.stringContains "the failure is what the waiter reads" "failed"
  ]

open SageFs.WorkerProtocol

let private worker : WorkerHandle = { Pid = 7; Port = Some 6000 }

let private statuses : SessionLifecycleStatus list =
  [ SessionLifecycleStatus.Starting { Pid = 7; Port = None }
    SessionLifecycleStatus.Ready worker
    SessionLifecycleStatus.Evaluating worker
    SessionLifecycleStatus.Building ("compiling", worker)
    SessionLifecycleStatus.Faulted (FaultReason.report "boom")
    SessionLifecycleStatus.Restarting PreviousWorker.ColdStart
    SessionLifecycleStatus.Stopped ]

let private rebuilds : LastRebuild list =
  [ LastRebuild.NeverRebuilt
    LastRebuild.Latest (RebuildOutcome.InProgress DateTime.UtcNow)
    LastRebuild.Latest (RebuildOutcome.Succeeded DateTime.UtcNow)
    LastRebuild.Latest (RebuildOutcome.FailedStillServing (SageFsError.HardResetFailed "x", DateTime.UtcNow)) ]

[<Tests>]
let settlePhaseTests =
  testList "Verification.SessionTrust.settlePhase: the phase the wait reads from the daemon's own record" [

    testCase "WHY — the wait calls a session Ready exactly when the session manager would answer a parked caller Ready, so the two never disagree" <| fun _ ->
      for status in statuses do
        for rebuild in rebuilds do
          let phase = Verification.SessionTrust.settlePhase (Some (status, rebuild)) None
          let managerSaysReady = (ReadyWait.ofSession status rebuild = ReadyWait.Verdict.Ready)
          (phase = Phase.Ready) |> Expect.equal (sprintf "%A with %A" status rebuild) managerSaysReady

    testCase "WHY — a rebuild in flight is BuildInFlight whatever the lifecycle says, because its bound is the build runner's" <| fun _ ->
      let rebuilding = LastRebuild.Latest (RebuildOutcome.InProgress DateTime.UtcNow)
      for status in [ SessionLifecycleStatus.Ready worker; SessionLifecycleStatus.Restarting PreviousWorker.ColdStart; SessionLifecycleStatus.Starting { Pid = 7; Port = None } ] do
        Verification.SessionTrust.settlePhase (Some (status, rebuilding)) None |> Expect.equal (sprintf "%A" status) Phase.BuildInFlight

    testCase "WHY — a stopped session is Stopped even with a rebuild running, and no session at all is Missing" <| fun _ ->
      let rebuilding = LastRebuild.Latest (RebuildOutcome.InProgress DateTime.UtcNow)
      Verification.SessionTrust.settlePhase (Some (SessionLifecycleStatus.Stopped, rebuilding)) None |> Expect.equal "stopped" Phase.Stopped
      Verification.SessionTrust.settlePhase None None |> Expect.equal "missing" Phase.Missing

    testCase "WHY — the worker's last progress text rides on the startup phases, so a phase that says something new is progress" <| fun _ ->
      let phaseWith progress = Verification.SessionTrust.settlePhase (Some (SessionLifecycleStatus.Starting { Pid = 7; Port = None }, LastRebuild.NeverRebuilt)) progress
      (phaseWith (Some "loading project 1") <> phaseWith (Some "loading project 2")) |> Expect.isTrue "different text, different phase"
      (phaseWith (Some "loading project 1") = phaseWith (Some "loading project 1")) |> Expect.isTrue "same text, same phase"
      judge (Some (phaseWith (Some "loading project 1"))) (phaseWith (Some "loading project 2")) |> Expect.equal "progress" Verdict.KeepWaiting
  ]
