namespace SageFs.Features

open System
open System.Collections.Concurrent

/// The daemon's live anomaly detectors: one `HealthAnomaly.State` per signal,
/// kept across the samples the daemon already takes.
///
/// HealthAnomaly itself is pure (state in, verdict out). This is the one place
/// that remembers, so a caller can just say "here's another reading" on the
/// tick it already has. Everything here is bounded: one small record per
/// signal, and a signal costs nothing until something samples it.
///
/// It exists because of two real incidents where the daemon was badly degraded
/// and said nothing: its request thread stuck for a minute at a time while
/// /health timed out, and its own RSS at 51.7GB of a 62GB machine, which only
/// came to light because the OS started killing other things.
module HealthWatch =

  let private states = ConcurrentDictionary<string, HealthAnomaly.State>()
  let private verdicts = ConcurrentDictionary<string, HealthAnomaly.Verdict>()

  /// Record one reading and get the verdict for that signal. Thread-safe:
  /// samples arrive from timers, request threads and the session manager.
  let observe (signal: HealthAnomaly.SignalId) (value: float) (at: DateTimeOffset) : HealthAnomaly.Verdict =
    let key = HealthAnomaly.signalName signal
    let mutable verdict = HealthAnomaly.Verdict.InsufficientHistory
    states.AddOrUpdate(
      key,
      (fun _ ->
        let next, v = HealthAnomaly.step HealthAnomaly.defaultParams signal { Value = value; At = at } HealthAnomaly.initial
        verdict <- v
        next),
      (fun _ previous ->
        let next, v = HealthAnomaly.step HealthAnomaly.defaultParams signal { Value = value; At = at } previous
        verdict <- v
        next)
    )
    |> ignore
    verdicts[key] <- verdict
    verdict

  /// Every signal's latest verdict, worst first, with the ones that have
  /// nothing to say left out.
  let troubled () : HealthAnomaly.Verdict list =
    verdicts.Values
    |> Seq.filter (fun verdict ->
      match verdict with
      | HealthAnomaly.Verdict.Broken _
      | HealthAnomaly.Verdict.Drifting _ -> true
      | HealthAnomaly.Verdict.Normal
      | HealthAnomaly.Verdict.InsufficientHistory -> false)
    |> Seq.sortBy (fun verdict ->
      match verdict with
      | HealthAnomaly.Verdict.Broken _ -> 0
      | _ -> 1)
    |> List.ofSeq

  /// For tests, and for a daemon that wants to start clean.
  let reset () =
    states.Clear()
    verdicts.Clear()
