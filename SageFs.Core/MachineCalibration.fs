namespace SageFs

open System
open System.Collections.Generic
open System.Diagnostics
open System.Text

/// A fixed single-thread workload that looks like compiler work (string hashing, dictionary and
/// sort churn, string building), timed. It is the one number that says how fast THIS CPU is for
/// the kind of work a SageFs start is made of, because core count and memory do not: a 2009 four
/// core and a 2024 four core read the same on both and differ by several times in speed.
///
/// This file has no dependency on the rest of SageFs on purpose. `scripts/machine-bench.fsx`
/// `#load`s it, so the benchmark and the product measure the same workload and their numbers
/// can be compared.
module MachineCalibration =

  /// How many keys the workload inserts. Chosen so the workload takes about 20 ms on a fast
  /// machine (a 2020 desktop core) and a few tenths of a second on a slow one: long enough that
  /// the timer's own noise is small, short enough to run at daemon start without being noticed.
  [<Literal>]
  let private KeyCount = 150000

  /// The distinct keys the workload cycles through. A prime, so `i * Stride % KeySpace` revisits
  /// keys in a scattered order and the dictionary sees both inserts and updates.
  [<Literal>]
  let private KeySpace = 50021

  /// Steps through the key space. Also prime, so it does not share a factor with `KeySpace`.
  [<Literal>]
  let private Stride = 7919

  /// How many times the workload runs after the first. The first run pays the JIT, which a cold
  /// SageFs start pays too, so it is reported on its own. The steady figure is the lowest of
  /// these, because noise only ever adds time.
  [<Literal>]
  let private SteadyRuns = 2

  /// What one measurement says. Both numbers are milliseconds of wall clock for the same
  /// workload; `FirstMs` includes the JIT of the workload itself.
  type Measurement =
    { FirstMs: float
      SteadyMs: float }

  let private workload () : float =
    let timer = Stopwatch.StartNew()
    let counts = Dictionary<string, int>()
    let mutable churn = 0
    for i in 0 .. KeyCount do
      let key = "key" + string (i * Stride % KeySpace)
      match counts.TryGetValue key with
      | true, seen -> counts.[key] <- seen + i
      | false, _ -> counts.[key] <- i
      churn <- churn + key.GetHashCode() % 7
    let rows = counts |> Seq.map (fun pair -> pair.Key + ":" + string pair.Value) |> Seq.toArray
    Array.sortInPlace rows
    let builder = StringBuilder()
    for row in rows do
      builder.Append(row.Length) |> ignore
    ignore (churn + builder.Length)
    timer.Elapsed.TotalMilliseconds

  /// Run the workload and report. Blocks the calling thread for the length of the workload.
  let measure () : Measurement =
    let first = workload ()
    let steady = [ for _ in 1 .. SteadyRuns -> workload () ] |> List.min
    { FirstMs = first; SteadyMs = steady }
