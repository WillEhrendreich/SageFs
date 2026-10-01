/// The `around` step of a click. Before the getter runs the evaluator asks for guards, at the deadline it asks the
/// getter's thread to stop (and frees a wait), and it takes the guards off when the click is over, even when the
/// getter threw, was stopped, or never came back. What a click was protected by comes back with the value, so the row
/// can say it. These cases hand the evaluator a stand-in preparer and a getter with hand-written checks, so they run
/// in this process and never need Harmony; the real patcher is checked in its own cases and in child processes.
module SageFs.Tests.MemberEvaluationGuardTests

open System
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.LiveValueTree
open SageFs.Features.MemberEvaluation

/// Counts how often a click let its guards go, and says when.
type Releases() =
  let mutable count = 0
  let released = new ManualResetEventSlim(false)
  member _.Count = Volatile.Read(&count)
  member _.Released = released
  member _.Release() : unit =
    Interlocked.Increment(&count) |> ignore
    released.Set()

/// Getters that behave as woven code does, with a check written by hand where a woven loop has one.
type Probe(release: ManualResetEventSlim, inside: ManualResetEventSlim) =
  member _.Quick = 5
  member _.Boom : int = failwith "boom"
  /// A loop with a check in it: stops when its thread is asked to.
  member _.CheckedSpin : int =
    inside.Set()
    while not release.IsSet do
      Guard.Check()
    0
  /// The same, inside a catch-all: the check throws again on the next turn, outside it.
  member _.CheckedSpinSwallowed : int =
    inside.Set()
    while not release.IsSet do
      try
        Guard.Check()
      with _ -> ()
      Guard.Check()
    0
  /// What a guard throws when the stack is nearly out.
  member _.Overflows : int = raise (InsufficientExecutionStackException "deep")
  /// A loop nothing checks: only the deadline gives up on it, and the thread is left running.
  member _.UncheckedSpin : int =
    inside.Set()
    while not release.IsSet do
      Thread.SpinWait 100
    0
  /// A wait that Interrupt frees.
  member _.Waits : int =
    inside.Set()
    try
      release.Wait()
      0
    with :? ThreadInterruptedException -> 0

let private limits (maxAbandoned: int) =
  { Deadline = TestTimeouts.blockedGetterBudget
    InterruptGrace = TestTimeouts.interruptGrace
    MaxAbandoned = maxAbandoned }

let private coverage : GuardCoverage =
  GuardCoverage.Guarded { Methods = [ "Probe.get_CheckedSpin" ]; Skipped = [] }

let private preparing (releases: Releases) : Guarding =
  GuardsOn (fun _ _ -> { Coverage = coverage; Release = releases.Release })

let private probe () = Probe(new ManualResetEventSlim(false), new ManualResetEventSlim(false))

let private property (name: string) = typeof<Probe>.GetProperty name

let private evaluate (guarding: Guarding) (name: string) (target: Probe) : MemberEvaluated =
  let evaluator = Evaluator(limits 4, unfiltered, guarding)
  evaluator.RunGuarded (property name) (box target)

[<Tests>]
let memberEvaluationGuardTests =
  testList "a click and its guards" [

    testCase "WHY — a getter that returns comes back with what was guarded, and the guards are let go once" <| fun _ ->
      let releases = Releases()
      let evaluated = evaluate (preparing releases) "Quick" (probe ())
      evaluated.Outcome |> Expect.equal "the value" (Ok (box 5))
      evaluated.Guards |> Expect.equal "what protected it, and no guard fired" { Coverage = coverage; Trip = GuardTrip.NotTripped }
      releases.Count |> Expect.equal "released once" 1

    testCase "WHY — the guards are let go when the getter threw" <| fun _ ->
      let releases = Releases()
      let evaluated = evaluate (preparing releases) "Boom" (probe ())
      evaluated.Outcome |> Expect.equal "the getter's own message" (Error (MemberFailure.MemberThrew "boom"))
      releases.Count |> Expect.equal "released once" 1

    testCase "WHY — with guards switched off the click runs as before and says it was not guarded" <| fun _ ->
      let evaluated = evaluate GuardsOff "Quick" (probe ())
      evaluated.Outcome |> Expect.equal "the value" (Ok (box 5))
      evaluated.Guards |> Expect.equal "the row says why" { Coverage = GuardCoverage.NotGuarded NotGuardedReason.SwitchedOff; Trip = GuardTrip.NotTripped }

    testCase "WHY — a preparer that throws leaves the getter running, and the row says the guards could not be put on" <| fun _ ->
      let evaluated = evaluate (GuardsOn (fun _ _ -> failwith "no patching here")) "Quick" (probe ())
      evaluated.Outcome |> Expect.equal "the getter still ran" (Ok (box 5))
      match evaluated.Guards.Coverage with
      | GuardCoverage.NotGuarded (NotGuardedReason.PreparationFailed detail) -> detail |> Expect.stringContains "with the reason" "no patching here"
      | other -> failtestf "expected a preparation failure, got %A" other

    testCase "WHY — a loop with a check is stopped at the deadline, the click says it timed out, and the stop is on the row" <| fun _ ->
      let releases = Releases()
      let evaluated = evaluate (preparing releases) "CheckedSpin" (probe ())
      evaluated.Outcome |> Expect.equal "timed out" (Error MemberFailure.MemberTimedOut)
      evaluated.Guards.Trip |> Expect.equal "a loop guard stopped it" GuardTrip.LoopStopped
      releases.Count |> Expect.equal "the guards are let go, the thread being gone" 1

    testCase "WHY — a catch-all in the getter does not keep it running past the deadline" <| fun _ ->
      let releases = Releases()
      let evaluated = evaluate (preparing releases) "CheckedSpinSwallowed" (probe ())
      evaluated.Outcome |> Expect.equal "timed out" (Error MemberFailure.MemberTimedOut)
      evaluated.Guards.Trip |> Expect.equal "stopped" GuardTrip.LoopStopped

    testCase "WHY — a getter that ran the stack out comes back as a throw that says so, with the stack guard on the row" <| fun _ ->
      let releases = Releases()
      let evaluated = evaluate (preparing releases) "Overflows" (probe ())
      match evaluated.Outcome with
      | Error (MemberFailure.MemberThrew message) -> message |> Expect.stringContains "says it was the stack" "stack"
      | other -> failtestf "expected a throw, got %A" other
      evaluated.Guards.Trip |> Expect.equal "the stack guard" GuardTrip.StackLimitReached
      releases.Count |> Expect.equal "released once" 1

    testCase "WHY — a wait is freed by Interrupt, no guard is claimed, and the guards are let go" <| fun _ ->
      let releases = Releases()
      let evaluated = evaluate (preparing releases) "Waits" (probe ())
      evaluated.Outcome |> Expect.equal "timed out" (Error MemberFailure.MemberTimedOut)
      evaluated.Guards.Trip |> Expect.equal "nothing tripped: the wait was freed" GuardTrip.NotTripped
      releases.Count |> Expect.equal "released once" 1

    testCase "WHY — a getter nothing checks is abandoned, and its guards stay on until its thread ends, then come off once" <| fun _ ->
      let releases = Releases()
      let release = new ManualResetEventSlim(false)
      let target = Probe(release, new ManualResetEventSlim(false))
      let evaluated = evaluate (preparing releases) "UncheckedSpin" target
      try
        evaluated.Outcome |> Expect.equal "timed out" (Error MemberFailure.MemberTimedOut)
        evaluated.Guards.Trip |> Expect.equal "no guard stopped it" GuardTrip.NotTripped
        releases.Count |> Expect.equal "the abandoned thread may still run guarded code, so the guards stay" 0
      finally
        release.Set()
      releases.Released.Wait TestTimeouts.patienceBrief |> Expect.isTrue "when the thread ends the guards come off"
      releases.Count |> Expect.equal "once" 1

    testSequenced (
      testCase "WHY — a click leaves no stop pending behind it, whether the loop was stopped, the wait freed or the getter returned" (fun _ ->
        let before = Guard.Pending
        for name in [ "Quick"; "CheckedSpin"; "Waits"; "Overflows" ] do
          evaluate (preparing (Releases())) name (probe ()) |> ignore
        Guard.Pending |> Expect.equal "the count is where it was" before))
  ]
