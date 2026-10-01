namespace SageFs.Features

// Dependency-free apart from System, on purpose: compiled into SageFs.Core and embedded into the isolated FSI host (see
// the GuardRuntime entries in SageFs.Core.fsproj, FsiHostBuild.fs and FsiHost.fsproj). The patched code calls
// `Guard.EnterEnsure` and `Guard.Check` by name, so both are public static methods on a public class, and they must stay
// that way.

open System
open System.Runtime.CompilerServices
open System.Threading

/// Whether a call moved the cell, so the global count moves with it exactly once.
[<RequireQualifiedAccess>]
type GuardCellMove =
  | Changed
  | Unchanged

/// Whether a check on the cell's thread throws.
[<RequireQualifiedAccess>]
type GuardCellStop =
  | Requested
  | NotRequested

/// Where one contained thread stands: running, asked to stop, or done. The evaluator's watchdog asks at the deadline and
/// the thread's own end retires it.
[<AllowNullLiteral>]
type GuardCell() =
  // 0 running, 1 stop requested, 2 retired. Moved with Interlocked.
  let mutable state = 0

  /// Asks the thread to stop. `Changed` when this call is the one that asked, so the caller counts it once.
  member _.RequestStop() : GuardCellMove =
    match Interlocked.CompareExchange(&state, 1, 0) with
    | 0 -> GuardCellMove.Changed
    | _ -> GuardCellMove.Unchanged

  /// The thread is done. `Changed` when a stop had been asked for and not yet let go, so the caller un-counts it once.
  member _.Retire() : GuardCellMove =
    match Interlocked.Exchange(&state, 2) with
    | 1 -> GuardCellMove.Changed
    | _ -> GuardCellMove.Unchanged

  /// Whether a check on this cell's thread throws right now.
  member _.StopIsRequested : GuardCellStop =
    match Volatile.Read(&state) with
    | 1 -> GuardCellStop.Requested
    | _ -> GuardCellStop.NotRequested

/// Thrown by a check. It is an ordinary exception, so a user's `try ... with _ -> ()` can swallow it, and that is why
/// the cell stays set: the next check on the same thread throws again.
type GuardAbortedException() =
  inherit Exception("SageFs: contained code was stopped because it ran past its deadline")

/// How many cells have a stop requested and have not retired. The hot path is one load of this: while it is zero, which
/// is every moment nobody is being stopped, a check does nothing else.
module GuardPending =
  let mutable count = 0

[<AbstractClass; Sealed>]
type Guard =
  /// The cell of the contained thread, null on every other thread. A guard on another thread never throws for it.
  [<ThreadStatic; DefaultValue>]
  static val mutable private current: GuardCell

  /// The slow half of a check, kept out of line so the fast half stays small enough to inline.
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member private Poll() : unit =
    match Guard.current with
    | null -> ()
    | cell ->
      match cell.StopIsRequested with
      | GuardCellStop.Requested -> raise (GuardAbortedException())
      | GuardCellStop.NotRequested -> ()

  /// Before a jump back: throws if this thread was asked to stop.
  [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
  static member Check() : unit =
    match Volatile.Read(&GuardPending.count) with
    | 0 -> ()
    | _ -> Guard.Poll()

  /// At the start of a method: turns an overflow that would end the process into an
  /// `InsufficientExecutionStackException` a caller can catch, then does what `Check` does. The runtime throws it while
  /// about 128 KB are still free, so the thread must have more than that to begin with.
  [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
  static member EnterEnsure() : unit =
    RuntimeHelpers.EnsureSufficientExecutionStack()
    Guard.Check()

  /// Makes `cell` the one this thread answers to. Call it on the contained thread, before the contained code runs.
  static member Bind(cell: GuardCell) : unit = Guard.current <- cell

  /// This thread answers to no cell any more.
  static member Unbind() : unit = Guard.current <- null

  // The next three are never inlined by the F# compiler into another assembly. Their bodies take the address of a
  // module-level value, and a copy of that in the tests' or the simulation's IL names a start-up type that is not there
  // (TypeLoadException in a Release build). They are not on the hot path: the JIT still inlines `Check`.

  /// The watchdog: from now on a check on the cell's thread throws. Safe to call twice, and after the cell retired.
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member RequestStop(cell: GuardCell) : unit =
    match cell.RequestStop() with
    | GuardCellMove.Changed -> Interlocked.Increment(&GuardPending.count) |> ignore
    | GuardCellMove.Unchanged -> ()

  /// The thread is done. Whatever was requested is let go, once.
  [<MethodImpl(MethodImplOptions.NoInlining)>]
  static member Retire(cell: GuardCell) : unit =
    match cell.Retire() with
    | GuardCellMove.Changed -> Interlocked.Decrement(&GuardPending.count) |> ignore
    | GuardCellMove.Unchanged -> ()

  /// How many cells are asking for a stop right now.
  static member Pending
    with [<MethodImpl(MethodImplOptions.NoInlining)>] get () : int = Volatile.Read(&GuardPending.count)
