namespace SageFs.Features

// Dependency-free on purpose: compiled into SageFs.Core and embedded into the isolated FSI host (see the
// GuardCoverage entries in SageFs.Core.fsproj, FsiHostBuild.fs and FsiHost.fsproj), and the dashboard reads these
// types to say what a click was and was not protected against.

/// Why one method was left without guards. Every case is something a person can read on the row, and none of them is a
/// guess: a method is skipped because a rule says it must be, never because it was hard to patch and we hoped.
[<RequireQualifiedAccess>]
type SkipReason =
  /// It belongs to an assembly we did not compile or weave (the framework, a package). Its loops and its stack use are
  /// not checked.
  | NotOurCode of assembly: string
  /// `MoveNext` of an async or task state machine. A throw at its entry escapes the machine's own try/catch and ends the
  /// process, so a spinning `task` loop is only stopped by the deadline, and then abandoned.
  | AsyncStateMachine
  /// A `DynamicMethod`: Harmony cannot patch what has no stable identity.
  | DynamicMethod
  /// An open generic, or an instantiation of one. Not patched, so not claimed.
  | Generic
  /// Abstract, interface, extern or runtime-provided: there is no IL to guard here. The implementations that can be
  /// reached are listed in their own right.
  | NoBody
  /// Hot reload re-pointed this method and what it points at cannot be patched. Patching the old method would put the
  /// old code back over the reload, so it is refused.
  | DetouredByHotReload
  /// Harmony was asked and said no.
  | PatchRefused of detail: string
  /// Its IL could not be read.
  | UnreadableBody of detail: string
  /// The walk reached its bound (depth or how many methods) before it reached this method.
  | BudgetReached

/// One method that was seen and not guarded, with the reason.
type SkippedMethod = { Method: string; Reason: SkipReason }

/// What is guarded when guards are on.
type GuardedMethods =
  { /// Full names of the methods that got an entry guard and a back-edge guard.
    Methods: string list
    /// What was reachable and was not guarded, and why.
    Skipped: SkippedMethod list }

/// Why a click ran with no guards at all.
[<RequireQualifiedAccess>]
type NotGuardedReason =
  /// Guards are switched off for this evaluator.
  | SwitchedOff
  /// The getter was not run (there was no value, it did not return on an earlier click, or too many did not), so there
  /// was nothing to guard.
  | NothingRan
  /// Harmony cannot patch on this runtime or platform.
  | PatchingUnavailable of detail: string
  /// The getter itself is something the rules skip, so there was nothing to guard.
  | GetterSkipped of SkipReason
  /// Something threw while the guards were being put on, and they were taken off again.
  | PreparationFailed of detail: string

/// What one click was protected by. A DU, not a flag: `Guarded` always carries what it covered and what it did not.
[<RequireQualifiedAccess>]
type GuardCoverage =
  | Guarded of GuardedMethods
  | NotGuarded of NotGuardedReason

/// What the guards did during the click.
[<RequireQualifiedAccess>]
type GuardTrip =
  /// The getter returned, threw its own exception, or was cut off by something other than a guard.
  | NotTripped
  /// A guard saw the stack running out and threw instead of letting the process die.
  | StackLimitReached
  /// A loop guard stopped the getter after the watchdog's deadline.
  | LoopStopped

/// Everything a click reports about its guards: shown beside the value, or the reason for no value.
type GuardOutcome = { Coverage: GuardCoverage; Trip: GuardTrip }

module SkipReason =

  /// One short phrase, for counting skips of the same kind together.
  let describe (reason: SkipReason) : string =
    match reason with
    | SkipReason.NotOurCode assembly -> sprintf "code in %s, which SageFs does not own" assembly
    | SkipReason.AsyncStateMachine -> "an async or task state machine"
    | SkipReason.DynamicMethod -> "a dynamic method"
    | SkipReason.Generic -> "a generic method"
    | SkipReason.NoBody -> "a call with no body to guard (abstract, interface or extern)"
    | SkipReason.DetouredByHotReload -> "a method hot reload re-pointed and the new code cannot be patched"
    | SkipReason.PatchRefused detail -> sprintf "a method the patcher refused (%s)" detail
    | SkipReason.UnreadableBody detail -> sprintf "a method whose code could not be read (%s)" detail
    | SkipReason.BudgetReached -> "methods past the search limit"

module NotGuardedReason =

  let describe (reason: NotGuardedReason) : string =
    match reason with
    | NotGuardedReason.SwitchedOff -> "guards are switched off"
    | NotGuardedReason.NothingRan -> "the getter was not run"
    | NotGuardedReason.PatchingUnavailable detail -> sprintf "code cannot be patched here (%s)" detail
    | NotGuardedReason.GetterSkipped skip -> sprintf "the getter is %s" (SkipReason.describe skip)
    | NotGuardedReason.PreparationFailed detail -> sprintf "the guards could not be put on (%s)" detail

module GuardCoverage =

  /// Skips grouped by what they are, most common first, so a getter that reaches fifty framework methods says so once.
  let private skippedSummary (skipped: SkippedMethod list) : string =
    skipped
    |> List.groupBy (fun s -> SkipReason.describe s.Reason)
    |> List.sortBy (fun (text, group) -> -(List.length group), text)
    |> List.map (fun (text, group) -> sprintf "%d x %s" (List.length group) text)
    |> String.concat "; "

  /// The line the row shows.
  let describe (coverage: GuardCoverage) : string =
    match coverage with
    | GuardCoverage.NotGuarded reason -> sprintf "not guarded: %s" (NotGuardedReason.describe reason)
    | GuardCoverage.Guarded covered ->
      let counted = List.length covered.Methods
      let noun = match counted with 1 -> "method" | _ -> "methods"
      match covered.Skipped with
      | [] -> sprintf "guarded: stack and loops checked in %d %s" counted noun
      | skipped ->
        sprintf "guarded: stack and loops checked in %d %s; not guarded: %s" counted noun (skippedSummary skipped)

  /// The line the row shows when a guard fired, or nothing when none did.
  let describeTrip (trip: GuardTrip) : string =
    match trip with
    | GuardTrip.NotTripped -> ""
    | GuardTrip.StackLimitReached -> "stopped by a guard: the getter recursed until the stack was nearly out"
    | GuardTrip.LoopStopped -> "stopped by a guard: the getter was still looping at the deadline"

  /// How many methods are guarded, 0 when none.
  let guardedCount (coverage: GuardCoverage) : int =
    match coverage with
    | GuardCoverage.Guarded covered -> List.length covered.Methods
    | GuardCoverage.NotGuarded _ -> 0
