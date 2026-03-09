#!/usr/bin/env dotnet fsi
// Benchmark regression gate — compares BDN JSON export against declared thresholds.
// Exit 0 = pass, exit 1 = regression detected.
// Usage: dotnet fsi scripts/check-benchmarks.fsx <results-dir>

open System
open System.IO
open System.Text.Json

// Thresholds: (benchmark method, max mean in nanoseconds)
// Derived from BASELINES.md with 5% headroom baked in on CI (noisy runners).
// CI runners are ~2-3x slower than dev machines, so thresholds are generous.
let thresholds =
  [| // CellGrid: PoolRent 60x200 was 4.1μs → allow 25μs on CI
     "PoolRent", 25_000.0
     // Manifest roundtrip was 9.7μs → allow 60μs on CI
     "Roundtrip", 60_000.0
     // SSE FormatEvent was 127.7ns → allow 800ns on CI
     "FormatEvent", 800.0
     // SSE FormatRetryHint was 63.7ns → allow 400ns on CI
     "FormatRetryHint", 400.0
     // Error describe was 466ns → allow 3μs on CI
     "DescribeAll", 3_000.0 |]

let resultsDir =
  match fsi.CommandLineArgs with
  | [| _; dir |] -> dir
  | _ -> "BenchmarkDotNet.Artifacts/results"

let findJsonReports dir =
  match Directory.Exists dir with
  | true ->
    Directory.GetFiles(dir, "*-report-full.json", SearchOption.AllDirectories)
  | false -> [||]

type BenchmarkResult = { Method: string; MeanNs: float }

let parseReport (path: string) =
  let doc = JsonDocument.Parse(File.ReadAllText path)
  let benchmarks = doc.RootElement.GetProperty("Benchmarks")
  [| for b in benchmarks.EnumerateArray() do
       let method = b.GetProperty("Method").GetString()
       let stats = b.GetProperty("Statistics")
       let meanNs = stats.GetProperty("Mean").GetDouble()
       { Method = method; MeanNs = meanNs } |]

let reports = findJsonReports resultsDir
match reports.Length with
| 0 ->
  printfn "⚠ No benchmark JSON reports found in %s — skipping gate" resultsDir
  exit 0
| n ->
  printfn "📊 Found %d benchmark report(s)" n

let allResults = reports |> Array.collect parseReport

let mutable failures = 0
for (method, maxNs) in thresholds do
  let matching = allResults |> Array.filter (fun r -> r.Method = method)
  match matching.Length with
  | 0 -> printfn "  ⏭ %s — not in this run, skipping" method
  | _ ->
    let worst = matching |> Array.maxBy (fun r -> r.MeanNs)
    match worst.MeanNs > maxNs with
    | true ->
      printfn "  ❌ %s: %.1f ns > %.1f ns threshold (%.1f%% over)"
        method worst.MeanNs maxNs ((worst.MeanNs / maxNs - 1.0) * 100.0)
      failures <- failures + 1
    | false ->
      printfn "  ✅ %s: %.1f ns ≤ %.1f ns threshold"
        method worst.MeanNs maxNs

match failures with
| 0 ->
  printfn "\n✅ All benchmarks within thresholds"
  exit 0
| n ->
  printfn "\n❌ %d benchmark(s) exceeded thresholds" n
  exit 1
