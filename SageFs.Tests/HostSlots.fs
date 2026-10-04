/// How many fixture hosts one test process runs at once.
///
/// A host case is a real SageFs.Host, an FSI session and an app, on a run dir and a reserved port of its own, so the cases
/// of a suite do not depend on each other and can run side by side. What bounds them is the machine, not the cases: a host
/// is about a core and a half while it starts, and the other tiers are running too. Every body that starts a host takes a
/// slot first and gives it back however it ends, so however many cases Expecto starts at once, only `Limit` hosts exist.
/// A case that waits for a slot is parked on an await and holds no thread.
module SageFs.Tests.HostSlots

open System.Threading
open System.Threading.Tasks

type Slots =
  { Limit: int
    Gate: SemaphoreSlim }

let create (limit: int) : Slots = { Limit = limit; Gate = new SemaphoreSlim(limit) }

/// Run `body` once a slot is free, and give the slot back when it ends, whether it returned or failed.
let run (slots: Slots) (body: unit -> Task<'a>) : Task<'a> = task {
  do! slots.Gate.WaitAsync()
  try
    return! body ()
  finally
    slots.Gate.Release() |> ignore
}

/// This process's slots.
let shared : Slots = create TestMagnitudes.concurrentHosts

/// `run` on this process's slots: the one a host case's whole life (start, body, stop) goes in.
let withSlot (body: unit -> Task<'a>) : Task<'a> = run shared body
