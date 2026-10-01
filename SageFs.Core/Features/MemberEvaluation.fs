namespace SageFs.Features

// Dependency-free apart from System, Timeouts, ThreadSandbox and LiveValueTree, on purpose: this file is compiled
// into SageFs.Core and also embedded into the isolated FSI host, where a click runs the getter. See the
// LiveValueTree entries in SageFs.Core.fsproj and FsiHost.fsproj.

open System
open System.Reflection
open System.Runtime.CompilerServices
open System.Threading
open SageFs
open SageFs.Features.LiveValueTree

/// What a click on a "not evaluated" row does. The getter is the user's code, so it runs on a dedicated thread the
/// caller can give up on: under a deadline, with `Thread.Interrupt` for a wait it can free, and (on Linux x86-64)
/// inside the thread sandbox, which stops the network, file writes and new processes. Every way it can go wrong comes
/// back as a `MemberFailure`, never as a hang.
///
/// What this does NOT stop: a spin or a stack overflow in code nothing guards (the deadline abandons the thread, and
/// an overflow ends the process), an in-memory effect, and any of that in a platform with no filter. A getter that
/// never returns is abandoned, remembered by its target so it is not run again, and counted; past the cap nothing more
/// is run until the session restarts.
module MemberEvaluation =

  /// The stack a getter gets. Generous on purpose: a guard that checks the stack before it overflows needs room to
  /// throw, and a getter that recurses should hit that guard, not the end of a small stack.
  let [<Literal>] GetterStackBytes = 1048576

  type Limits = {
    /// How long the getter may run before it is given up on.
    Deadline: TimeSpan
    /// How long a getter that was interrupted gets to leave its wait before its thread is abandoned.
    InterruptGrace: TimeSpan
    /// How many abandoned getters are tolerated; past this nothing more is run.
    MaxAbandoned: int
  }

  /// Runs the work on whatever thread the policy needs and says what came of it. `unfiltered` runs it directly,
  /// `filtered` inside the thread sandbox.
  type Sandboxing = (unit -> Result<obj, exn>) -> SandboxOutcome<Result<obj, exn>>

  /// No filter: the work runs on the calling thread. For a platform with no filter, chosen by the host and said in
  /// the pane, never a fallback this module takes on its own.
  let unfiltered : Sandboxing =
    fun work ->
      try Completed (work ())
      with e -> Threw e

  /// The work runs on a fresh thread under the policy's filter, or does not run at all.
  let filtered (policy: SandboxPolicy) : Sandboxing =
    fun work -> ThreadSandbox.run policy work

  /// The product's limits: the deadline and grace come from `Timeouts`, the cap is how many stuck getters a session
  /// puts up with before it says to restart.
  let productLimits : Limits =
    { Deadline = Timeouts.memberEvaluationDeadline
      InterruptGrace = Timeouts.memberEvaluationGrace
      MaxAbandoned = Timeouts.maxAbandonedMembers }

  let private describeFailure (ex: exn) : string =
    match ex.GetBaseException() with
    | null -> ex.Message
    | inner -> inner.Message

  /// Runs getters one click at a time. One per host: it carries the count of abandoned threads and the values whose
  /// getters did not return.
  type Evaluator(limits: Limits, sandbox: Sandboxing) =
    let abandoned = ref 0
    /// The getters, by name, that did not return on each value. Weak on the value, so a value the session lets go of
    /// is not kept alive by a click that gave up on it. Per getter, so one stuck getter does not stop the others.
    let unresponsive = ConditionalWeakTable<obj, Collections.Generic.HashSet<string>>()
    let isQuarantined (target: obj) (name: string) =
      match unresponsive.TryGetValue target with
      | true, names -> lock names (fun () -> names.Contains name)
      | false, _ -> false
    let quarantine (target: obj) (name: string) =
      let names = unresponsive.GetValue(target, fun _ -> Collections.Generic.HashSet<string>())
      lock names (fun () -> names.Add name |> ignore)

    member _.Run (property: PropertyInfo) (target: obj) : Result<obj, MemberFailure> =
      match isNull target with
      | true -> Result.Error (MemberFailure.MemberNotContained "there is no value to read this from")
      | false ->
      match Volatile.Read(&abandoned.contents) >= limits.MaxAbandoned with
      | true ->
        Result.Error (MemberFailure.MemberNotContained "too many earlier getters never returned, so no more are run until you restart the session")
      | false ->
      match isQuarantined target property.Name with
      | true ->
        Result.Error (MemberFailure.MemberNotContained "this getter did not return on an earlier click, so it is not run again on this value")
      | false ->
        let worker : (Thread | null) ref = ref null
        let outcome : SandboxOutcome<Result<obj, exn>> ref =
          ref (Threw (InvalidOperationException "the getter's thread ended without a result"))
        use finished = new ManualResetEventSlim(false)
        let work () : Result<obj, exn> =
          worker.Value <- Thread.CurrentThread
          try Result.Ok (property.GetValue target)
          with
          | :? TargetInvocationException as e ->
            (match e.InnerException with
             | null -> Result.Error (e :> exn)
             | inner -> Result.Error inner)
          | e -> Result.Error e
        let body () =
          try
            outcome.Value <- (try sandbox work with e -> Threw e)
          finally
            finished.Set()
        let thread = Thread(ThreadStart body, GetterStackBytes, IsBackground = true, Name = "sagefs-member-eval")
        thread.Start()
        match finished.Wait limits.Deadline with
        | true ->
          (match outcome.Value with
           | Completed (Result.Ok value) -> Result.Ok value
           | Completed (Result.Error ex) -> Result.Error (MemberFailure.MemberThrew (describeFailure ex))
           | Threw ex -> Result.Error (MemberFailure.MemberThrew (describeFailure ex))
           | Unavailable reason -> Result.Error (MemberFailure.MemberNotContained (SandboxUnavailable.describe reason)))
        | false ->
          // A wait is freed by Interrupt. A spin is not, and the thread is given up on and counted.
          (match worker.Value with
           | null -> ()
           | running -> running.Interrupt())
          (match finished.Wait limits.InterruptGrace with
           | true -> ()
           | false ->
             quarantine target property.Name
             Interlocked.Increment(&abandoned.contents) |> ignore)
          Result.Error MemberFailure.MemberTimedOut

  let create (limits: Limits) (sandbox: Sandboxing) : Evaluator = Evaluator(limits, sandbox)
