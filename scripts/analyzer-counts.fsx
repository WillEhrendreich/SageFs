// scripts/analyzer-counts.fsx [<configuration>]   run as: dotnet fsi scripts/analyzer-counts.fsx [-- Debug]
//
// The custom analyzer pack (analyzers/sageAnalyzers.dll) over the product projects, counted per rule and judged against
// RatchetBudgets.analyzerFindings. Thin glue: the logic (parse the CLI's output, judge, describe, the CLI's arguments and
// environment) is build/AnalyzerBudget.fs, which the tests cover.
//
// The gate's "analyzers" stage runs this script, and so does anyone who wants to see where the counts stand. It writes
// test-results/analyzer-counts.txt BEFORE it judges, so a red verdict still leaves the numbers behind. That matters most
// when the verdict is red because a budget is now too high (the pack got better, or a finding was fixed): the counts are
// what `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets --tighten` lowers the table to.
//
// Needs the product projects built in the configuration asked for (Release by default), which the gate's build stage and
// `dotnet build -c Release` both do. About two and a half minutes for the three projects.

#load "../build/AnalyzerBudget.fs"
#load "../SageFs.Tests/RatchetBudgets.fs"

open System
open System.Diagnostics
open System.IO
open SageFs.Build

let rootDir = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let outputDir = Path.Combine(rootDir, "test-results", "analyzers")
let reportFile = Path.Combine(rootDir, "test-results", AnalyzerBudget.reportFileName)

let configuration =
  match fsi.CommandLineArgs |> Array.tryItem 1 with
  | Some value -> value
  | None -> "Release"

/// Run `dotnet <argv>` from the repo root with the CLI's environment. The exit code and everything it printed.
let dotnet (argv: string list) : int * string =
  let psi = ProcessStartInfo("dotnet")
  argv |> List.iter psi.ArgumentList.Add
  psi.WorkingDirectory <- rootDir
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  for (name, value) in AnalyzerBudget.environment do psi.Environment[name] <- value
  use p = Process.Start psi
  let stdout = p.StandardOutput.ReadToEndAsync()
  let stderr = p.StandardError.ReadToEndAsync()
  p.WaitForExit()
  p.ExitCode, stdout.Result + stderr.Result

let die (lines: string list) : 'a =
  lines |> List.iter (eprintfn "%s")
  exit 2

let tail (text: string) =
  text.Split('\n') |> Array.rev |> Array.truncate 25 |> Array.rev |> String.concat "\n"

Directory.CreateDirectory outputDir |> ignore

match dotnet [ "tool"; "restore" ] with
| 0, _ -> ()
| code, text -> die [ sprintf "analyzers: `dotnet tool restore` exited %d, so the CLI in dotnet-tools.json is not installed:" code; tail text ]

let counts =
  AnalyzerBudget.projects
  |> List.collect (fun project ->
    let clock = Stopwatch.StartNew()
    let code, text = dotnet (AnalyzerBudget.cliArguments "analyzers" configuration "net11.0" project)
    File.WriteAllText(Path.Combine(outputDir, project + ".txt"), text)
    match code with
    | 0 ->
      let found = AnalyzerBudget.keyed project (AnalyzerBudget.countByRule text)
      printfn "analyzers: %-12s %3.0fs, %d findings (%s)" project clock.Elapsed.TotalSeconds (found |> List.sumBy snd) (Path.Combine(outputDir, project + ".txt"))
      found
    | failed -> die [ sprintf "analyzers on %s: the CLI exited %d. The last of its output:" project failed; tail text ])

File.WriteAllText(reportFile, AnalyzerBudget.renderReport counts)

let failing =
  AnalyzerBudget.judge SageFs.Tests.RatchetBudgets.analyzerFindings counts
  |> List.filter (fun (_, standing) -> AnalyzerBudget.fails standing)

match failing with
| [] ->
  printfn "analyzers: %d rule counts over %d projects, every one within its budget" counts.Length AnalyzerBudget.projects.Length
  exit 0
| _ ->
  failing |> List.iter (fun (key, standing) -> eprintfn "%s" (AnalyzerBudget.describe key standing))
  eprintfn "analyzers: the counts are in %s" reportFile
  exit 1
