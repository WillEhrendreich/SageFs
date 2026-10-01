namespace SageFs.Features

// Dependency-free on purpose: compiled into SageFs.Core and embedded into the isolated FSI host (see the ClickLifecycle
// entries in SageFs.Core.fsproj, FsiHostBuild.fs and FsiHost.fsproj), and folded by the guard simulation.

/// Where one click stands. The click (waiting on its getter's thread) and the thread (running the getter) move it
/// without seeing each other, so the rule for who lets the guards go is one pure function both of them call.
[<RequireQualifiedAccess>]
type ClickPhase =
  /// The click is waiting and the thread is running.
  | Running
  /// The click gave up on the thread. Its guards stay on until the thread ends, because the thread may still be running
  /// guarded code, and a guard taken off under it would let it run unchecked.
  | Abandoned
  /// The thread has ended.
  | Ended

/// What happened to the click.
[<RequireQualifiedAccess>]
type ClickEvent =
  /// The getter's thread left its work, by a value, a throw, a guard or an interrupt.
  | ThreadEnded
  /// The click stopped waiting: the deadline and the grace after it went by and the thread is still there.
  | GaveUp

/// Who lets the guards go because of an event.
[<RequireQualifiedAccess>]
type GuardDuty =
  /// Whoever made the event happen lets them go, now: the thread ended, and the click gave up on it just before.
  | ReleaseNow
  /// Not yet. The click is done with them and the thread still may use them, so the thread's end releases them.
  | LeaveToTheThread
  /// Not yet. The thread is gone and the click has not looked yet, so the click releases them when it does.
  | LeaveToTheClick
  /// This event changes nothing about the guards (it was already settled, or it cannot follow the one before).
  | NothingToDo

module ClickLifecycle =

  /// The phase after an event, and who owes the guards' release.
  ///
  /// - The thread ends first: the click releases when it sees that.
  /// - The click gives up first: the guards stay for the thread, and the thread releases them when it ends.
  /// - The thread ended in the instant between the click's last look and its verdict: nothing was abandoned, and the
  ///   click releases now.
  let step (phase: ClickPhase) (event: ClickEvent) : ClickPhase * GuardDuty =
    match phase, event with
    | ClickPhase.Running, ClickEvent.ThreadEnded -> ClickPhase.Ended, GuardDuty.LeaveToTheClick
    | ClickPhase.Running, ClickEvent.GaveUp -> ClickPhase.Abandoned, GuardDuty.LeaveToTheThread
    | ClickPhase.Abandoned, ClickEvent.ThreadEnded -> ClickPhase.Ended, GuardDuty.ReleaseNow
    | ClickPhase.Ended, ClickEvent.GaveUp -> ClickPhase.Ended, GuardDuty.ReleaseNow
    | ClickPhase.Ended, ClickEvent.ThreadEnded
    | ClickPhase.Abandoned, ClickEvent.GaveUp -> phase, GuardDuty.NothingToDo
