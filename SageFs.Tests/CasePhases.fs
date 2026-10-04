/// Where a slow case's seconds go.
///
/// A case that spawns a host, builds a project and waits for an app has no stopwatch in the gate log: the log says
/// "this case took 15 seconds" and nothing about which part. With SAGEFS_PHASE_TRACE set, a case that marks its
/// phases prints one line per phase, the time since the case started and an id for the case, so concurrent cases
/// can be told apart. Off by default, so a normal run's output is unchanged.
module SageFs.Tests.CasePhases

open System
open System.Diagnostics

let environmentVariable = "SAGEFS_PHASE_TRACE"

let enabled =
  match Environment.GetEnvironmentVariable environmentVariable with
  | null | "" -> false
  | _ -> true

/// `clock` is the case's own stopwatch, started where the case starts. Prints cumulative milliseconds.
let mark (clock: Stopwatch) (name: string) : unit =
  match enabled with
  | true -> eprintfn "PHASE case=%08x %-18s %7.0fms" (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode clock) name clock.Elapsed.TotalMilliseconds
  | false -> ()
