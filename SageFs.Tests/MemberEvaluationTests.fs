/// What a click on a "not evaluated" row does. The getter is the user's code, so it runs on a thread that can be
/// given up on, under a deadline, with `Thread.Interrupt` for a wait it can free, and (on Linux x86-64) a filter that
/// stops the network, file writes and new processes. Every way it can go wrong comes back as a reason, never a hang.
module SageFs.Tests.MemberEvaluationTests

open System
open System.IO
open System.Reflection
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveValueTree
open SageFs.Features.MemberEvaluation

/// A target whose getters do what each case needs. The events are the cases' own, so nothing is shared.
type Gadget(release: ManualResetEventSlim, started: ManualResetEventSlim, sawInterrupt: ManualResetEventSlim) =
  member _.Quick = 5
  member _.Boom : int = failwith "boom"
  /// Blocks on a wait. `Thread.Interrupt` frees it.
  member _.WaitsForever : int =
    started.Set()
    try
      release.Wait()
      0
    with :? ThreadInterruptedException ->
      sawInterrupt.Set()
      0
  /// Spins until released. Nothing but the flag stops it.
  member _.Spins : int =
    started.Set()
    while not release.IsSet do
      Thread.SpinWait 100
    0

/// Writes a file, which the sandbox policy must stop.
type Writer(path: string) =
  member _.Write : int =
    File.WriteAllText(path, "x")
    1

let private limits (maxAbandoned: int) =
  { Deadline = TestTimeouts.blockedGetterBudget
    InterruptGrace = TestTimeouts.interruptGrace
    MaxAbandoned = maxAbandoned }

let private gadget () = Gadget(new ManualResetEventSlim(false), new ManualResetEventSlim(false), new ManualResetEventSlim(false))

let private property (name: string) = typeof<Gadget>.GetProperty name

let private notContained (result: Result<obj, MemberFailure>) =
  match result with
  | Error (MemberFailure.MemberNotContained why) -> why
  | other -> failtestf "expected a refusal to contain, got %A" other

[<Tests>]
let memberEvaluationTests =
  testList "evaluating one member" [

    testCase "WHY — a getter that returns is run and its value comes back" <| fun _ ->
      let evaluator = create (limits 2) unfiltered
      evaluator.Run (property "Quick") (box (gadget ())) |> Expect.equal "the value" (Ok (box 5))

    testCase "WHY — a getter that throws comes back with its own message, not a wrapper's" <| fun _ ->
      let evaluator = create (limits 2) unfiltered
      match evaluator.Run (property "Boom") (box (gadget ())) with
      | Error (MemberFailure.MemberThrew message) -> message |> Expect.equal "the message" "boom"
      | other -> failtestf "expected a throw, got %A" other

    testCase "WHY — a getter blocked on a wait is freed by Interrupt and reported as timed out" <| fun _ ->
      let release = new ManualResetEventSlim(false)
      let sawInterrupt = new ManualResetEventSlim(false)
      let blocked = Gadget(release, new ManualResetEventSlim(false), sawInterrupt)
      let evaluator = create (limits 2) unfiltered
      evaluator.Run (property "WaitsForever") (box blocked) |> Expect.equal "timed out" (Error MemberFailure.MemberTimedOut)
      sawInterrupt.Wait TestTimeouts.patienceBrief |> Expect.isTrue "the getter saw the interrupt, so its thread is free"

    testCase "WHY — a getter that spins is given up on at the deadline, and the same target is not run again" <| fun _ ->
      let release = new ManualResetEventSlim(false)
      let spinner = Gadget(release, new ManualResetEventSlim(false), new ManualResetEventSlim(false))
      let evaluator = create (limits 2) unfiltered
      try
        evaluator.Run (property "Spins") (box spinner) |> Expect.equal "timed out" (Error MemberFailure.MemberTimedOut)
        let second = notContained (evaluator.Run (property "Spins") (box spinner))
        second |> Expect.stringContains "says why" "earlier click"
      finally
        release.Set()

    testCase "WHY — past the cap of abandoned getters, nothing else is run until the session restarts" <| fun _ ->
      let release = new ManualResetEventSlim(false)
      let first = Gadget(release, new ManualResetEventSlim(false), new ManualResetEventSlim(false))
      let second = Gadget(release, new ManualResetEventSlim(false), new ManualResetEventSlim(false))
      let evaluator = create (limits 1) unfiltered
      try
        evaluator.Run (property "Spins") (box first) |> Expect.equal "the first is abandoned" (Error MemberFailure.MemberTimedOut)
        let refusal = notContained (evaluator.Run (property "Quick") (box second))
        refusal |> Expect.stringContains "says what to do" "restart"
      finally
        release.Set()

    testCase "WHY — when the sandbox cannot be installed the getter is not run, and the refusal says why" <| fun _ ->
      let started = new ManualResetEventSlim(false)
      let target = Gadget(new ManualResetEventSlim(true), started, new ManualResetEventSlim(false))
      let unavailable : Sandboxing = fun _ -> Unavailable NotLinux
      let evaluator = create (limits 2) unavailable
      let why = notContained (evaluator.Run (property "WaitsForever") (box target))
      why |> Expect.stringContains "names the reason" (SandboxUnavailable.describe NotLinux)
      started.IsSet |> Expect.isFalse "the getter never started"

    testCase "WHY — on Linux x86-64 a getter that writes a file is stopped by the filter" <| fun _ ->
      match ThreadSandbox.availability () with
      | Error reason -> skiptest (SandboxUnavailable.describe reason)
      | Ok _ ->
        let path = Path.Combine(Path.GetTempPath(), sprintf "sagefs-member-eval-%s.txt" (Guid.NewGuid().ToString "N"))
        try
          let evaluator = create (limits 2) (filtered SandboxPolicy.NoNetworkNoWritesNoSpawn)
          match evaluator.Run (typeof<Writer>.GetProperty "Write") (box (Writer path)) with
          | Error (MemberFailure.MemberThrew _) -> File.Exists path |> Expect.isFalse "the file was not written"
          | other -> failtestf "expected the write to be denied, got %A" other
        finally
          if File.Exists path then File.Delete path
  ]
