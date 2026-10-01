namespace SageFs.Features

// Dependency-free apart from System, Timeouts, ThreadSandbox, the guard files and LiveValueTree, on purpose: this file is
// compiled into SageFs.Core and also embedded into the isolated FSI host, where a click runs the getter. See the
// LiveValueTree and Guard entries in SageFs.Core.fsproj and FsiHost.fsproj.

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
/// Guards (layer 2) are the `around` step: before the getter runs, the code it can reach gets an entry guard and a
/// back-edge guard (a stack check and a stop flag); at the deadline the evaluator sets the flag, so a loop or a
/// recursion in that code ends within milliseconds, even inside a catch-all; and the guards come off when the click is
/// over. What a click was protected by comes back with the value (`MemberEvaluated`).
///
/// What this does NOT stop: a spin in code nothing guards (the deadline abandons the thread, and the guards stay on for
/// as long as that thread runs), a loop in a library or an async state machine, a stack overflow in code nothing guards
/// (it ends the process), an in-memory effect, and any of that in a platform with no filter. A getter that never
/// returns is abandoned, remembered by its target so it is not run again, and counted; past the cap nothing more is
/// run until the session restarts.
module MemberEvaluation =

  /// The stack a getter gets. Generous on purpose: a guard that checks the stack before it overflows needs room to
  /// throw (the runtime throws while about 128 KB are still free), and a getter that recurses should hit that guard,
  /// not the end of a small stack.
  let [<Literal>] GetterStackBytes = 1048576

  type Limits = {
    /// How long the getter may run before it is given up on.
    Deadline: TimeSpan
    /// How long a getter that was asked to stop (a stop flag for guarded code, an interrupt for a wait) gets to leave
    /// before its thread is abandoned.
    InterruptGrace: TimeSpan
    /// How many abandoned getters are tolerated; past this nothing more is run.
    MaxAbandoned: int
  }

  /// Runs the work on whatever thread the policy needs and says what came of it. `unfiltered` runs it directly,
  /// `filtered` inside the thread sandbox.
  type Sandboxing = (unit -> Result<obj, exn>) -> SandboxOutcome<Result<obj, exn>>

  /// Puts guards on what a getter can reach, for one click: the property, and the value it is read on. The lease says
  /// what is guarded and takes the guards off again.
  type GuardPreparer = PropertyInfo -> obj -> GuardLease

  /// Whether a click gets guards. A DU, not a flag, so the way to have them carries the way to get them.
  type Guarding =
    | GuardsOff
    | GuardsOn of GuardPreparer

  /// What a click came to, and what it was protected by.
  type MemberEvaluated =
    { Outcome: Result<obj, MemberFailure>
      Guards: GuardOutcome }

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

  /// What a getter that ran the stack out is told, in place of the runtime's own text.
  let private stackLimitMessage =
    "the getter recursed until the stack was nearly out, so it was stopped before it could end the process"

  /// Whether a guard is what ended the getter, read from what it threw, however many wrappers it came through.
  let rec private tripOf (ex: (exn | null)) : GuardTrip =
    match ex with
    | null -> GuardTrip.NotTripped
    | :? GuardAbortedException -> GuardTrip.LoopStopped
    | :? InsufficientExecutionStackException -> GuardTrip.StackLimitReached
    | other -> tripOf other.InnerException

  let private tripOfOutcome (outcome: SandboxOutcome<Result<obj, exn>>) : GuardTrip =
    match outcome with
    | Completed (Result.Error ex)
    | Threw ex -> tripOf ex
    | Completed (Result.Ok _)
    | Unavailable _ -> GuardTrip.NotTripped

  /// Where a click stands, so the thread and the click agree on who lets the guards go.
  type private ClickPhase =
    | Running
    /// The click gave up on the thread. Its guards stay on until the thread ends, because it may still be running
    /// guarded code, and a guard that was taken off under it would let it run unchecked.
    | Abandoned
    | Ended

  /// Runs getters one click at a time. One per host: it carries the count of abandoned threads and the values whose
  /// getters did not return.
  type Evaluator(limits: Limits, sandbox: Sandboxing, guarding: Guarding) =
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

    let refused (why: string) : MemberEvaluated =
      { Outcome = Result.Error (MemberFailure.MemberNotContained why)
        Guards = { Coverage = GuardCoverage.NotGuarded NotGuardedReason.NothingRan; Trip = GuardTrip.NotTripped } }

    /// Asks for guards. A preparer that throws leaves the getter running without them, and the row says so.
    let prepare (property: PropertyInfo) (target: obj) : GuardLease =
      match guarding with
      | GuardsOff -> { Coverage = GuardCoverage.NotGuarded NotGuardedReason.SwitchedOff; Release = ignore }
      | GuardsOn preparer ->
        try preparer property target
        with e ->
          { Coverage = GuardCoverage.NotGuarded (NotGuardedReason.PreparationFailed (describeFailure e)); Release = ignore }

    new(limits: Limits, sandbox: Sandboxing) = Evaluator(limits, sandbox, GuardsOff)

    member _.RunGuarded (property: PropertyInfo) (target: obj) : MemberEvaluated =
      match isNull target with
      | true -> refused "there is no value to read this from"
      | false ->
      match Volatile.Read(&abandoned.contents) >= limits.MaxAbandoned with
      | true ->
        refused "too many earlier getters never returned, so no more are run until you restart the session"
      | false ->
      match isQuarantined target property.Name with
      | true ->
        refused "this getter did not return on an earlier click, so it is not run again on this value"
      | false ->
        let lease = prepare property target
        // Whoever comes last lets the guards go, and only once: the click, or the thread it abandoned.
        let released = ref 0
        let letGo () =
          match Interlocked.Exchange(&released.contents, 1) with
          | 0 -> (try lease.Release () with _ -> ())
          | _ -> ()
        let phase = ref ClickPhase.Running
        // Moves the click to `next` unless it already left `Running`, and says where it was.
        let moveTo (next: ClickPhase) : ClickPhase =
          lock phase (fun () ->
            let before = phase.Value
            match before with
            | ClickPhase.Running -> phase.Value <- next
            | ClickPhase.Abandoned
            | ClickPhase.Ended -> ()
            before)
        let cell = GuardCell()
        let worker : (Thread | null) ref = ref null
        let outcome : SandboxOutcome<Result<obj, exn>> ref =
          ref (Threw (InvalidOperationException "the getter's thread ended without a result"))
        use finished = new ManualResetEventSlim(false)
        let work () : Result<obj, exn> =
          worker.Value <- Thread.CurrentThread
          // The sandbox may run this on a thread of its own, so the cell is bound here, on the thread that reads.
          Guard.Bind cell
          try
            try Result.Ok (property.GetValue target)
            with
            | :? TargetInvocationException as e ->
              (match e.InnerException with
               | null -> Result.Error (e :> exn)
               | inner -> Result.Error inner)
            | e -> Result.Error e
          finally
            Guard.Unbind()
        let body () =
          try
            outcome.Value <- (try sandbox work with e -> Threw e)
          finally
            Guard.Retire cell
            let before = moveTo ClickPhase.Ended
            finished.Set()
            match before with
            | ClickPhase.Abandoned -> letGo ()
            | ClickPhase.Running
            | ClickPhase.Ended -> ()
        let thread = Thread(ThreadStart body, GetterStackBytes, IsBackground = true, Name = "sagefs-member-eval")
        thread.Start()
        let evaluated (result: Result<obj, MemberFailure>) (trip: GuardTrip) : MemberEvaluated =
          { Outcome = result; Guards = { Coverage = lease.Coverage; Trip = trip } }
        match finished.Wait limits.Deadline with
        | true ->
          letGo ()
          let trip = tripOfOutcome outcome.Value
          let failure (ex: exn) =
            match trip with
            | GuardTrip.StackLimitReached -> MemberFailure.MemberThrew stackLimitMessage
            | GuardTrip.NotTripped
            | GuardTrip.LoopStopped -> MemberFailure.MemberThrew (describeFailure ex)
          (match outcome.Value with
           | Completed (Result.Ok value) -> evaluated (Result.Ok value) trip
           | Completed (Result.Error ex) -> evaluated (Result.Error (failure ex)) trip
           | Threw ex -> evaluated (Result.Error (failure ex)) trip
           | Unavailable reason ->
             evaluated (Result.Error (MemberFailure.MemberNotContained (SandboxUnavailable.describe reason))) trip)
        | false ->
          // The watchdog: guarded code on the thread throws at its next check. A wait is freed by Interrupt. A spin in
          // code nothing guards is neither, and the thread is given up on and counted.
          Guard.RequestStop cell
          (match worker.Value with
           | null -> ()
           | running -> running.Interrupt())
          match finished.Wait limits.InterruptGrace with
          | true ->
            letGo ()
            evaluated (Result.Error MemberFailure.MemberTimedOut) (tripOfOutcome outcome.Value)
          | false ->
            match moveTo ClickPhase.Abandoned with
            | ClickPhase.Running ->
              quarantine target property.Name
              Interlocked.Increment(&abandoned.contents) |> ignore
              evaluated (Result.Error MemberFailure.MemberTimedOut) GuardTrip.NotTripped
            | ClickPhase.Abandoned
            | ClickPhase.Ended ->
              // It finished in the instant between the wait and the verdict: nothing is abandoned.
              letGo ()
              evaluated (Result.Error MemberFailure.MemberTimedOut) (tripOfOutcome outcome.Value)

    member this.Run (property: PropertyInfo) (target: obj) : Result<obj, MemberFailure> =
      (this.RunGuarded property target).Outcome

  /// An evaluator that runs getters with no guards.
  let create (limits: Limits) (sandbox: Sandboxing) : Evaluator = Evaluator(limits, sandbox, GuardsOff)

  /// An evaluator that guards what each clicked getter can reach, with Harmony, for the length of the click.
  let createGuarded (limits: Limits) (sandbox: Sandboxing) : Evaluator =
    Evaluator(limits, sandbox, GuardsOn GuardPatcher.prepare)
