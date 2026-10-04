// scripts/ratchets-source.fsx [<repo root>]    run with: dotnet fsi scripts/ratchets-source.fsx [-- <repo root>]
//
// The budget ratchets that need nothing compiled: file-size budgets and blocking-call budgets. They read source
// text and compare counts, so they run in seconds against the working tree BEFORE any build.
//
// Why this exists: "SageFs/Mcp.fs is 3299 lines, over its 3292 budget" used to be reported only after a full
// Release build of SageFs.Tests (minutes), because the ratchet lane lives inside the test assembly. The numbers and
// the counting now live in SageFs.Tests/RatchetBudgets.fs, which depends on nothing, and this script loads that
// same file. The compiled lane (`SageFs.Tests.dll --ratchets`) reads the very same table, so nothing is weakened
// and the two can never disagree; this is the same check, run earlier.
//
// Exit codes: 0 every budget holds; 1 something is over budget (named, with the fix); 64 no repo found.
#load "../SageFs.Tests/RatchetBudgets.fs"

open System
open System.IO

/// The repo root: the argument when given, else the directory above this script's own.
let repoRoot =
  match fsi.CommandLineArgs |> Array.skip 1 |> Array.filter (fun a -> a <> "--") |> Array.tryHead with
  | Some root -> root
  | None -> Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

if not (File.Exists(Path.Combine(repoRoot, "SageFs.slnx"))) then
  eprintfn "ratchets-source: no SageFs.slnx under %s" repoRoot
  exit 64

let clock = Diagnostics.Stopwatch.StartNew()
let budgets = SageFs.Tests.RatchetBudgets.fileSize.Length + SageFs.Tests.RatchetBudgets.blockingCalls.Length

match SageFs.Tests.RatchetBudgets.violations repoRoot with
| [] ->
  printfn "ratchets-source: all %d file-size and blocking-call budgets hold (%.1fs, nothing built)" budgets clock.Elapsed.TotalSeconds
  exit 0
| over ->
  for line in over do
    eprintfn "RATCHET OVER BUDGET: %s" line
  eprintfn "ratchets-source: %d of %d budgets are over. Split the file or convert the blocking call; a budget is never raised." over.Length budgets
  exit 1
