// Measure a suite's flake rate by running it N times with independent seeds.
// A gating run tells you one result; a reliability run tells you whether that result was luck.
//
//   dotnet fsi scripts/reliability-run.fsx -- N <test dll> [expecto args...]
//
// Prints one line per run with its TRUST verdict and a final tally. Writes no
// trust row of its own: this is a measurement, not a gate.
open System
open System.Diagnostics
open System.Text.RegularExpressions

let args = fsi.CommandLineArgs |> Array.skip 1

let usage () =
  eprintfn "usage: dotnet fsi scripts/reliability-run.fsx N <test dll> [expecto args...]"
  exit 2

let parsedCount () =
  if args.Length = 0 then usage ()
  let ok, n = Int32.TryParse(args[0])
  if ok && n > 0 then n else usage ()

let count = parsedCount ()
let dll = if args.Length > 1 then args[1] else usage ()
let rest = if args.Length > 2 then args |> Array.skip 2 else [||]

let trustLine = Regex(@"TRUST tier=(\S+).*verdict=(\S+)")

let runOnce i =
  let psi = ProcessStartInfo("dotnet", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
  psi.ArgumentList.Add dll
  for a in rest do psi.ArgumentList.Add a
  use proc = Process.Start psi
  let out = proc.StandardOutput.ReadToEnd ()
  let err = proc.StandardError.ReadToEnd ()
  proc.WaitForExit ()
  let verdict =
    let m = trustLine.Match out
    if m.Success then m.Groups[2].Value
    elif proc.ExitCode = 0 then "PassedNoTrust"
    else "NoReport"
  printfn "RELIABILITY run %d/%d exit=%d verdict=%s" (i + 1) count proc.ExitCode verdict
  err.Split('\n')
  |> Array.filter (fun l -> l.Contains "TRUST")
  |> Array.iter (fun l -> printfn "%s" l)
  verdict

let verdicts = [ for i in 1 .. count -> runOnce i ]
let bad = verdicts |> List.filter (fun v -> v <> "Trusted")
printfn "RELIABILITY tally: %d/%d clean, %d with a non-Trusted verdict" (count - bad.Length) count bad.Length
if not bad.IsEmpty then exit 1
