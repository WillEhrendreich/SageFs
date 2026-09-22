/// Not a gate, a measurement, so it isn't registered as a test. Run it from a
/// SageFs session on SageFs.Tests when the tradeoff needs checking again:
/// `SageFs.Tests.TieredCompilationCostBenchmark.run HostRuntime.Net11 |> Async.AwaitTask |> Async.RunSynchronously;;`
/// The numbers go in read-tracking-costs.md. The two settings take turns every
/// rep, so a machine that gets busier halfway through slows both down, not
/// just one.
module SageFs.Tests.TieredCompilationCostBenchmark

open System.Diagnostics
open SageFs.Tests.HotReloadStateHarness
open SageFs.Middleware.ValueReads

let private configure (choice: TieringChoice) (runDir: string) =
  SageFs.SettingsStore.setKey
    (SageFs.SettingsStore.repoPath runDir)
    SageFs.SessionAgent.tieredCompilationSetting.Key
    (TieringChoice.name choice)
  |> ignore

/// How many requests the app gets before the timed ones, so the first-call
/// JIT isn't in the number.
[<Literal>]
let private PrimeCalls = 200

[<Literal>]
let private TimedCalls = 2000

[<Literal>]
let private Reps = 5

type private Sample =
  { Choice: TieringChoice
    Rep: int
    WarmupMs: float
    MicrosPerRequest: float }

/// One app run: wall clock from spawn to the app answering, then µs per
/// request to its plain `bump` route once it's warm. Request latency is what
/// an app's user feels, so that's the steady-state number.
let private sample (runtime: HostRuntime) (choice: TieringChoice) (rep: int) = task {
  let spawn = Stopwatch.StartNew()
  let! app = startConfigured runtime (configure choice)
  spawn.Stop()
  try
    for _ in 1 .. PrimeCalls do
      let! _ = get app "bump"
      ()
    let timed = Stopwatch.StartNew()
    for _ in 1 .. TimedCalls do
      let! _ = get app "bump"
      ()
    timed.Stop()
    return
      { Choice = choice
        Rep = rep
        WarmupMs = spawn.Elapsed.TotalMilliseconds
        MicrosPerRequest = timed.Elapsed.TotalMilliseconds * 1000.0 / float TimedCalls }
  finally
    stop app
}

let private median (xs: float list) =
  let sorted = List.sort xs
  sorted.[List.length sorted / 2]

/// Runs the benchmark and prints each rep and the medians.
let run (runtime: HostRuntime) =
  task {
    let samples = ResizeArray<Sample>()
    for rep in 1 .. Reps do
      for choice in TieringChoice.all do
        let! s = sample runtime choice rep
        samples.Add s
        printfn "rep %d %s: warmup %.0f ms, %.1f µs/request" rep (TieringChoice.name choice) s.WarmupMs s.MicrosPerRequest
    for choice in TieringChoice.all do
      let mine = samples |> Seq.filter (fun s -> s.Choice = choice) |> List.ofSeq
      printfn
        "MEDIAN %s over %d reps: warmup %.0f ms, %.1f µs/request"
        (TieringChoice.name choice)
        (List.length mine)
        (mine |> List.map (fun s -> s.WarmupMs) |> median)
        (mine |> List.map (fun s -> s.MicrosPerRequest) |> median)
  }
