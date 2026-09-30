module SageFs.Tests.WorkerStderrCaptureTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol

/// A child that writes `lines` numbered lines to stderr and then exits without
/// ever printing WORKER_PORT=, which is what a worker that crashes in startup
/// looks like from the daemon's side.
let private crashingWorker (lines: int) : Process =
  let psi = ProcessStartInfo("sh")
  psi.ArgumentList.Add "-c"
  psi.ArgumentList.Add(
    sprintf "i=1; while [ $i -le %d ]; do echo \"stderr line $i\" >&2; i=$((i+1)); done; exit 3" lines)
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  let proc = new Process()
  proc.StartInfo <- psi
  proc.Start() |> ignore
  proc

/// Run awaitWorkerPort against `proc` and return the reason the daemon's mailbox
/// is told the spawn failed with.
let private spawnFailureReason (proc: Process) : Task<string> =
  let reason = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
  let inbox =
    MailboxProcessor<SessionManager.SessionCommand>.Start(fun mb ->
      async {
        while true do
          let! msg = mb.Receive()
          match msg with
          | SessionManager.SessionCommand.WorkerSpawnFailed(_, _, why) -> reason.TrySetResult why |> ignore
          | _ -> ()
      })
  SessionManager.awaitWorkerPort (SessionId.newId ()) proc inbox CancellationToken.None
  reason.Task.WaitAsync(TimeSpan.FromSeconds 60.0)

let private requireUnix () =
  match OperatingSystem.IsWindows() with
  | true -> skiptest "the fake worker is a POSIX sh script"
  | false -> ()

[<Tests>]
let workerStderrCaptureTests =
  testList "SessionManager worker stderr capture" [

    testTask "WHY — awaitWorkerPort — a worker that dies reports the END of its stderr, because the crash reason is the last thing it writes" {
      requireUnix ()
      let! (reason: string) = spawnFailureReason (crashingWorker 1000)
      reason |> Expect.stringContains "newest line is in the reason" "stderr line 1000"
      reason |> Expect.stringContains "the tail starts summaryLineCount lines back"
        (sprintf "stderr line %d" (1000 - StderrTail.summaryLineCount + 1))
      reason.Contains "stderr line 1\n"
      |> Expect.isFalse "the oldest lines are not what is reported"
    }

    testTask "WHY — awaitWorkerPort — the reason carries at most summaryLineCount stderr lines, so a chatty worker cannot flood a status line" {
      requireUnix ()
      let! (reason: string) = spawnFailureReason (crashingWorker 1000)
      let stderrLines =
        reason.Split('\n')
        |> Array.filter (fun l -> l.StartsWith("stderr line ", StringComparison.Ordinal))
      stderrLines.Length |> Expect.equal "bounded" StderrTail.summaryLineCount
    }

    testTask "WHY — awaitWorkerPort — a worker that dies silent keeps the plain 'exited before reporting port' wording with no stderr section" {
      requireUnix ()
      let! (reason: string) = spawnFailureReason (crashingWorker 0)
      reason |> Expect.equal "no stderr, no stderr section" "Worker process exited before reporting port"
    }
  ]
