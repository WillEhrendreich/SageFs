namespace SageFs

open System.Threading

/// Whether the session manager's own loop is keeping up. Its own file, compiled
/// ahead of DaemonHealth, so `overallStatus` can read it; the watchdog that
/// produces it needs WorkerSpawn and compiles much later.
[<RequireQualifiedAccess>]
type SupervisorHealth =
  | Healthy
  | Degraded of reason: string

/// The daemon's one reading of its supervisor's health, written by the daemon
/// when the watchdog reports a change and read by every health surface (the
/// dashboard tick and `/health`). Same shape as MemoryPressureWatch: health
/// surfaces that have no handle on the session manager ask it instead.
module SupervisorHealthWatch =
  let private cell : SupervisorHealth ref = ref SupervisorHealth.Healthy

  let report (health: SupervisorHealth) : unit =
    Interlocked.Exchange(cell, health) |> ignore

  let current () : SupervisorHealth =
    Volatile.Read(&cell.contents)
