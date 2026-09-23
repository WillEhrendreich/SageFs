namespace SageFs.Features

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open SageFs.Utils

/// Captures the evidence a `WorkerRss` `Broken` verdict needs before it's
/// gone. Both real incidents that motivated `HealthWatch` died with nothing
/// to look at: by the time anyone noticed the daemon was eating the machine,
/// the box had no memory left to safely take a dump with, and the process
/// got killed by hand before anyone thought to try. A gcdump taken the
/// MOMENT the detector first confirms `Broken` — not drifting, not every
/// sample after — is the difference between "reclaimable churn" (a gcdump
/// this repo already used once, on a healthier daemon, to rule out a leak)
/// and "here is the actual retained object graph" the next time this
/// happens.
///
/// No new NuGet dependency: this shells out to the `dotnet-gcdump` GLOBAL
/// TOOL exactly the way `SessionBuild.fs` already shells out to `dotnet` for
/// builds — a subprocess, not a package reference.
module GcDumpCapture =

  /// What happened when a capture was attempted (or not).
  [<RequireQualifiedAccess>]
  type CaptureOutcome =
    | Captured of path: string
    | Skipped of reason: string
    | Failed of reason: string

  /// Whether THIS observation should trigger a capture. Pure so the "once
  /// per daemon run" and "only for the signal a dump can actually explain"
  /// rules are provable without touching a process. `WorkerRss` is the only
  /// signal a heap dump has anything to say about — a slow `/health` or a
  /// deep mailbox queue needs a thread dump or a trace, not a gcdump, and
  /// firing one for those would just cost memory and time for no evidence.
  let shouldCapture (alreadyCaptured: bool) (signal: HealthAnomaly.SignalId) (verdict: HealthAnomaly.Verdict) : bool =
    match alreadyCaptured with
    | true -> false
    | false ->
      match signal, verdict with
      | HealthAnomaly.SignalId.WorkerRss, HealthAnomaly.Verdict.Broken _ -> true
      | _ -> false

  /// Enough machine memory left to safely attempt a dump without being the
  /// thing that finally starves the box. `dotnet-gcdump` walks the live
  /// managed heap of the target process, which in the worst case needs
  /// scratch space comparable to the process's own working set — demanding
  /// MORE available memory than the process's current RSS is a conservative
  /// floor that lets a genuinely huge process (the 50GB case, with only 1GB
  /// left) skip the dump rather than risk taking the machine down trying to
  /// prove why it's huge. A process with zero recorded RSS has nothing
  /// meaningful to gate on either way.
  let hasHeadroomToCapture (processRssBytes: int64) (machineAvailableBytes: int64) : bool =
    processRssBytes > 0L && machineAvailableBytes > processRssBytes

  /// `dotnet-gcdump collect` on a process this size can legitimately take
  /// tens of seconds; generous without being an unbounded hang on an already
  /// struggling machine.
  let captureTimeoutMs = 120_000

  /// Whether dotnet-gcdump is installed at all. Worth knowing before a
  /// capture is attempted (a skip for a missing tool is honest; a skip for
  /// anything else is a bug), and worth telling a user who wants the
  /// diagnostic: without the tool we can say the daemon is sick but not what
  /// is holding the memory. Checks PATH and the default global-tool location,
  /// which is where `dotnet tool install -g dotnet-gcdump` puts it.
  let isToolAvailable () : bool =
    let onPath =
      match Environment.GetEnvironmentVariable "PATH" with
      | null -> []
      | path -> path.Split(IO.Path.PathSeparator) |> Array.toList
    let names = [ "dotnet-gcdump"; "dotnet-gcdump.exe" ]
    let homeTools =
      match Environment.GetEnvironmentVariable "HOME" with
      | null -> []
      | home -> [ IO.Path.Combine(home, ".dotnet", "tools") ]
    (onPath @ homeTools)
    |> List.exists (fun dir ->
      names |> List.exists (fun name -> try IO.File.Exists(IO.Path.Combine(dir, name)) with _ -> false))

  /// The file name for one capture — pid plus a timestamp, so two daemons
  /// (or two runs of the same daemon) never collide in the same directory.
  let fileNameFor (pid: int) (at: DateTimeOffset) : string =
    sprintf "sagefs-daemon-%d-%s.gcdump" pid (at.ToString("yyyyMMdd-HHmmss", Globalization.CultureInfo.InvariantCulture))

  /// The real side effect, run OFF the hot path by the caller (see
  /// `GcDumpWatch.maybeCapture`) — this function itself never decides WHEN
  /// to run, only HOW. Best-effort: a tool that isn't installed, a
  /// subprocess that fails, or one that times out all come back as
  /// `Failed`/`Skipped` with a reason, never an exception — a failed
  /// capture attempt must never be the thing that adds insult to an
  /// already-degraded daemon.
  let captureAsync (pid: int) (outputDir: string) : Async<CaptureOutcome> =
    async {
      let! ct = Async.CancellationToken
      try
        Directory.CreateDirectory outputDir |> ignore
        let path = Path.Combine(outputDir, fileNameFor pid DateTimeOffset.UtcNow)
        let psi =
          ProcessStartInfo(
            "dotnet-gcdump",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
          )
        for arg in [ "collect"; "-p"; string pid; "-o"; path ] do
          psi.ArgumentList.Add(arg)
        use proc = Process.Start(psi)
        let stderrTask = proc.StandardError.ReadToEndAsync(ct)
        let stdoutTask = proc.StandardOutput.ReadToEndAsync(ct)
        let tcs = TaskCompletionSource<bool>()
        proc.EnableRaisingEvents <- true
        proc.Exited.Add(fun _ -> tcs.TrySetResult(true) |> ignore)
        match proc.HasExited with
        | true -> tcs.TrySetResult(true) |> ignore
        | false -> ()
        let timeoutTask = Task.Delay(captureTimeoutMs, ct)
        let! completed = Task.WhenAny(tcs.Task, timeoutTask) |> Async.AwaitTask
        match Object.ReferenceEquals(completed, timeoutTask) with
        | true ->
          try
            proc.Kill(entireProcessTree = true)
          with ex ->
            Log.warn "[GcDumpCapture] kill on timeout: %s" ex.Message
          return CaptureOutcome.Failed(sprintf "dotnet-gcdump did not finish within %dms" captureTimeoutMs)
        | false ->
          let! stderrText = stderrTask |> Async.AwaitTask
          let! _ = stdoutTask |> Async.AwaitTask
          match proc.ExitCode with
          | 0 when File.Exists path -> return CaptureOutcome.Captured path
          | 0 -> return CaptureOutcome.Failed "dotnet-gcdump exited 0 but wrote no file"
          | code -> return CaptureOutcome.Failed(sprintf "dotnet-gcdump exited %d: %s" code (stderrText.Trim()))
      with
      | :? System.ComponentModel.Win32Exception as ex ->
        return CaptureOutcome.Skipped(sprintf "dotnet-gcdump is not installed or not on PATH: %s" ex.Message)
      | ex -> return CaptureOutcome.Failed ex.Message
    }
