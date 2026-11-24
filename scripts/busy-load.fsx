// Synthetic CPU load for proving that a test suite passes because its waits are correct, not because the
// machine happened to be quiet. Lifted from Molina's research/BusyLoops.fsx.
//
//   dotnet fsi scripts/busy-load.fsx -- THREADS SECONDS [command args...]
//
// With no command, it just loads the box for SECONDS. With a command, it starts the load, runs the command,
// and reports the command's exit code after the load ends. It prints no verdict and writes no trust row:
// the caller's TRUST line is the result.
open System
open System.Diagnostics
open System.Threading

let args = fsi.CommandLineArgs |> Array.skip 1
let threadsAsked, secondsAsked =
  match args |> Array.take 2 |> Array.map Int32.TryParse with
  | [| (true, threads); (true, seconds) |] when threads > 0 && seconds > 0 -> threads, seconds
  | _ -> eprintfn "usage: dotnet fsi scripts/busy-load.fsx THREADS SECONDS [command args...]"; exit 2

let spin (until: Stopwatch) (limit: TimeSpan) () : unit =
  let mutable x = 1UL
  while until.Elapsed < limit do
    for _ in 1 .. 100_000 do x <- x * 6364136223846793005UL + 1442695040888963407UL
  if x = 0UL then printfn "unreachable"

let clock = Stopwatch.StartNew ()
let limit = TimeSpan.FromSeconds (float secondsAsked)
let threads = [ for _ in 1 .. threadsAsked -> Thread (spin clock limit, IsBackground = true) ]
printfn "BUSY-LOAD started: %d threads for %d seconds" threadsAsked secondsAsked
for t in threads do t.Start ()

let mutable code = 0
if args.Length > 2 then
  let rest = args |> Array.skip 2
  let psi = ProcessStartInfo(rest[0], RedirectStandardOutput = false, RedirectStandardError = false, UseShellExecute = false)
  for a in rest |> Array.skip 1 do psi.ArgumentList.Add a
  use proc = Process.Start psi
  proc.WaitForExit ()
  code <- proc.ExitCode

for t in threads do t.Join ()
printfn "BUSY-LOAD ended after %.0f seconds" clock.Elapsed.TotalSeconds
exit code
