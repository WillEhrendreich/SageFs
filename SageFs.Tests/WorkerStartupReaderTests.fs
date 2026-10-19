/// A worker's stderr is read by ONE thread for the life of the worker. Two threads reading the same StreamReader is a data
/// race: lines go to one reader or the other, an exception in either ends it silently, and a pipe nobody drains fills and
/// blocks the worker in write(2). That is how a live-testing eval stalled forever: the worker's own debug logging blocked,
/// and with it the thread that reads the FSI host's reply.
module SageFs.Tests.WorkerStartupReaderTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs

/// Far more than a pipe buffer holds (64 KB), so a reader that stops reading blocks the writer.
let private stderrLines = 20000

let private endMarker = "END"

/// Start the fake worker and report what reached the app output, once both streams have said they are done (or the
/// wait for them runs out).
let private runFakeWorker (script: string) (goFile: string) : Task<(AppOutput.Stream * string) list> =
  task {
    // Run it THROUGH /bin/sh rather than exec'ing the file. Executing a file this test just wrote races with every other
    // test in the parallel suite that forks: a child can inherit the still-open write handle and the exec then fails with
    // ETXTBSY ("Text file busy"). The script's `$1` is still the go file.
    let psi = ProcessStartInfo("/bin/sh")
    psi.ArgumentList.Add script
    psi.ArgumentList.Add goFile
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    let proc = Process.Start psi
    try
      let received = ConcurrentQueue<AppOutput.Stream * string>()
      let sawStdoutEnd = TaskCompletionSource()
      let sawStderrEnd = TaskCompletionSource()
      let events : WorkerStartupEvents =
        { OnProgress = ignore
          // The worker is told it is ready by a file, and floods stderr only after that: lines written before the start
          // was seen to finish are start-up output, which is kept for the tail and is not an app's.
          OnReady = fun _ _ -> File.WriteAllText(goFile, "go")
          OnSpawnFailed = fun _ reason -> failwithf "the fake worker did not start: %s" reason
          OnStartTimedOut = fun _ _ _ -> failwith "the fake worker timed out starting"
          OnAppOutput =
            fun stream line ->
              received.Enqueue(stream, line)
              match stream, line = endMarker with
              | AppOutput.Stream.Stdout, true -> sawStdoutEnd.TrySetResult() |> ignore
              | AppOutput.Stream.Stderr, true -> sawStderrEnd.TrySetResult() |> ignore
              | _ -> () }
      let budget : StartBudget =
        { Attempt = 1
          Inactivity = TestTimeouts.patience
          Absolute = TestTimeouts.readyBudget
          Earlier = [] }
      WorkerStartup.await StartLedger.closed proc CancellationToken.None budget events
      let! _ = Task.WhenAny(Task.WhenAll(sawStdoutEnd.Task, sawStderrEnd.Task), Task.Delay TestTimeouts.patience)
      return List.ofSeq received
    finally
      try if not proc.HasExited then proc.Kill(true) with _ -> ()
      try proc.Dispose() with _ -> ()
  }

[<Tests>]
let tests =
  testList "Worker startup: one reader per stream" [
    testTask "every stderr line a worker writes after its port arrives, once and in order" {
      match OperatingSystem.IsWindows() with
      | true -> skiptest "the fake worker is a shell script"
      | false ->
        let script = Path.Combine(Path.GetTempPath(), sprintf "fake-worker-%s.sh" (Guid.NewGuid().ToString "N"))
        File.WriteAllText(
          script,
          sprintf
            "#!/bin/sh\necho WORKER_PORT=http://localhost:1\nwhile [ ! -f \"$1\" ]; do sleep 0.02; done\ni=0\nwhile [ $i -lt %d ]; do echo \"APP_ERROR=line $i\" >&2; i=$((i+1)); done\necho APP_ERROR=%s >&2\necho APP_OUTPUT=%s\n"
            stderrLines endMarker endMarker)
        let goFile = script + ".go"
        try
          let! received = runFakeWorker script goFile
          let stderr =
            received
            |> List.choose (fun (stream, line) -> match stream with | AppOutput.Stream.Stderr -> Some line | AppOutput.Stream.Stdout -> None)
          let expected = [ for i in 0 .. stderrLines - 1 -> sprintf "line %d" i ] @ [ endMarker ]
          // The counts first: a diff of twenty thousand lines says nothing a number does not.
          stderr.Length |> Expect.equal "every stderr line reached the app output" expected.Length
          (stderr = expected) |> Expect.isTrue "in the order the worker wrote them, each once"
        finally
          try File.Delete script with _ -> ()
          try File.Delete goFile with _ -> ()
    }
  ]
