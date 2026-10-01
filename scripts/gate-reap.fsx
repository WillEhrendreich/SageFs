// Reap the local gate's state dir: checkouts and tier clones whose repo is gone or that nobody has used for the
// retention, pass records past the newest few, and old logs. Run by scripts/local-gate before it makes its own
// checkout.
//
//   dotnet fsi scripts/gate-reap.fsx <gate-state-dir> <invoking-repo> <invoking-pid>
//
// The decisions are SageFs.Core/GateReaper.fs and the pure hygiene domain it loads, so the gate and the daemon's
// workspace hygiene plan cannot disagree about what is safe to remove, and the logic is tested there. This file only
// reads its arguments and says what happened. The checkout for <invoking-repo> is never touched.

#load "../SageFs.Core/Timeouts.fs"
#load "../SageFs.Core/WorkspaceHygiene.fs"
#load "../SageFs.Core/HygieneFs.fs"
#load "../SageFs.Core/GateReaper.fs"

open System
open System.Diagnostics
open System.IO
open SageFs
open SageFs.WorkspaceHygiene

let args = fsi.CommandLineArgs

match args.Length with
| n when n < 4 ->
  eprintfn "usage: dotnet fsi scripts/gate-reap.fsx <gate-state-dir> <invoking-repo> <invoking-pid>"
  exit 2
| _ -> ()

let gateDir = args.[1]
let invokingRepo = args.[2]
// The gate script has already written its own pid into `current` by now; it must not make everything look busy.
let invokingPid = int args.[3]

let isAlive (pid: int) : bool =
  try
    use p = Process.GetProcessById pid
    not p.HasExited
  with _ -> false

let result =
  GateReaper.reap (GateReaper.realFs isAlive) DateTime.UtcNow gateDir [ invokingRepo ] (Some({ Repo = invokingRepo; Pid = invokingPid } : GateReaper.Invoker)) File.Delete

let removed =
  result.Report.Executed
  |> List.filter (fun e -> match e.Result with | StepResult.Ran(Outcome.Done _) -> true | _ -> false)
  |> List.length

let failed =
  result.Report.Executed
  |> List.choose (fun e ->
    match e.Result with
    | StepResult.Ran(Outcome.Failed why) -> Some why
    | StepResult.Skipped _ -> None
    | _ -> None)

printfn "reap: removed %d entries (%d MB), %d logs; %d failed" removed (result.Report.ReclaimedBytes / 1048576L) result.LogsDeleted (List.length failed)
for why in failed do
  printfn "reap: could not remove one: %s" why
