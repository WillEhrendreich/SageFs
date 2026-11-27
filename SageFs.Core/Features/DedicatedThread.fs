/// Work that must start now, on a thread of its own.
///
/// `Task.Run` and `Async.StartAsTask` queue their first step to the thread pool. A pool with no free
/// thread — this machine builds and serves beside the gate — can hold that step for longer than any
/// deadline the work itself respects, and then the work never starts at all rather than starting
/// late. For a live-binding click that meant the pane showed "evaluating" forever and the getter's
/// own deadline never began counting, so no reason was ever reported.
///
/// `TaskCreationOptions.LongRunning` asks the default scheduler for a dedicated thread, which is what
/// a click that may wait a whole deadline needs. Use this rather than `Task.Run` when "started" has
/// to mean started.
module SageFs.Features.DedicatedThread

open System.Threading

/// Start `work` on a dedicated background thread named for what it is doing.
let start (name: string) (work: unit -> unit) : unit =
  let thread = Thread((fun () -> work ()), IsBackground = true, Name = name)
  thread.Start()
