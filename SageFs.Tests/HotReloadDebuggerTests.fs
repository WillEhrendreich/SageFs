/// Hot reload with a managed debugger attached to the process the app runs in.
///
/// Microsoft's pages say Hot Reload and Edit and Continue are not supported for F# while you debug it, and
/// that attaching to a running process is unsupported unless the runtime was started for it. Ours has no such
/// condition written down and no test, so this is the test. A real debugger (Samsung's netcoredbg, the one
/// that ships with no Visual Studio licence) is attached to the FSI host, the process the route table lives
/// in, and the answer to "is a debugger attached" is asked of that process, not assumed. Then real saves go
/// through, and the same process has to serve the new code and say Patched.
///
/// What it does not claim: stepping, breakpoints in patched code, or edits to a method that is stopped at a
/// breakpoint. Nothing here sets a breakpoint. It claims the narrower thing that was untested: a save lands
/// and runs in a process a debugger is attached to.
///
/// The debugger is a pinned release, downloaded once into the fixture's `.runs` folder and checked against its
/// SHA-256 before it is run. A machine that cannot fetch it, or is not Linux x64, fails the row by saying so.
module SageFs.Tests.HotReloadDebuggerTests

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.WorkerProtocol
open SageFs.Tests.HotReloadStateHarness
open SageFs.Tests.HotReloadParityTests

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// A debugger release we run, pinned so the row is the same row next month.
type private DebuggerRelease = {
  Version: string
  Url: string
  /// Of the downloaded archive, lower-case hex.
  Sha256: string
}

let private netcoredbg =
  { Version = "3.2.0-1092"
    Url = "https://github.com/Samsung/netcoredbg/releases/download/3.2.0-1092/netcoredbg-linux-amd64.tar.gz"
    Sha256 = "080eb3b2d2152465f599d3b33d1ee6e747794e11cc0a3773ec689f5e5f2c5afa" }

/// The rows the debugger is attached for: a lambda, an instance member that keeps state, a named task, and a
/// signature change. One of each mechanism, not the whole table: the table is HotReloadParityTests' job.
let private rowNames = [ "inlineLambda"; "instanceState"; "taskNamed"; "signature" ]

let private toolDirectory (release: DebuggerRelease) =
  Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "HotReloadParityFixture", ".runs", "tools", release.Version)

/// The debugger's executable, fetched and verified on first use.
let private ensureDebugger (release: DebuggerRelease) : Task<string> = task {
  match OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture = Architecture.X64 with
  | false ->
    return failwithf "This row attaches netcoredbg-linux-amd64 and reads /proc to find the process, so it needs Linux on x64. This machine is %O %O." RuntimeInformation.OSDescription RuntimeInformation.ProcessArchitecture
  | true ->
    let directory = toolDirectory release
    let executable = Path.Combine(directory, "netcoredbg", "netcoredbg")
    match File.Exists executable with
    | true -> return executable
    | false ->
      Directory.CreateDirectory directory |> ignore
      use client = new HttpClient(Timeout = TestTimeouts.processStartPatience)
      let! archive =
        task {
          try
            return! client.GetByteArrayAsync release.Url
          with ex ->
            return failwithf "Could not download the debugger this row attaches (%s): %s. Download it once from that address into %s and rerun." release.Url ex.Message directory
        }
      let actual = Convert.ToHexString(SHA256.HashData archive).ToLowerInvariant()
      actual |> Expect.equal "the downloaded debugger is the pinned release, byte for byte" release.Sha256
      use stream = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress)
      System.Formats.Tar.TarFile.ExtractToDirectory(stream, directory, true)
      return executable
}

/// Fetched once per run, however many runtimes ask.
let private debugger = lazy (ensureDebugger netcoredbg)

/// The process the app runs in: a child of the host that runs `FsiHost.dll`.
let private fsiHostPid (hostPid: int) : int =
  let readProcess (directory: string) =
    match Int32.TryParse(Path.GetFileName directory) with
    | true, pid ->
      try
        let stat = File.ReadAllText(Path.Combine(directory, "stat"))
        // "pid (name) state ppid ...": the name can hold spaces and brackets, so count from the LAST ")".
        let parent = int (stat.Substring(stat.LastIndexOf ')' + 2).Split(' ').[1])
        Some(pid, parent, File.ReadAllText(Path.Combine(directory, "cmdline")))
      with _ -> None
    | false, _ -> None
  Directory.GetDirectories "/proc"
  |> Array.choose readProcess
  |> Array.filter (fun (_, parent, command) -> parent = hostPid && command.Contains "FsiHost.dll")
  |> Array.map (fun (pid, _, _) -> pid)
  |> function
    | [| pid |] -> pid
    | found -> failwithf "Expected exactly one FSI host under host pid %d, found %A" hostPid found

/// An attached debugger and what it has said.
type private Attached = {
  Debugger: Process
  Said: StringBuilder
}

let private attach (executable: string) (pid: int) : Attached =
  let psi = ProcessStartInfo(executable, sprintf "--interpreter=cli --attach %d" pid)
  psi.UseShellExecute <- false
  psi.RedirectStandardInput <- true
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  let said = StringBuilder()
  let proc = Process.Start psi
  let drain (reader: StreamReader) =
    Task.Run(fun () ->
      task {
        let mutable line = ""
        let! first = reader.ReadLineAsync()
        line <- first
        while not (isNull line) do
          lock said (fun () -> said.AppendLine line |> ignore)
          let! next = reader.ReadLineAsync()
          line <- next
      } :> Task)
    |> ignore
  drain proc.StandardOutput
  drain proc.StandardError
  { Debugger = proc; Said = said }

/// Ask the app's own process whether a debugger is attached to it. FSI evaluates in that process, so the
/// answer is about the process the route table lives in. A stopped process cannot answer, hence the bound.
let private isAttached (app: RunningApp) : Task<bool> = task {
  let ask = app.Proxy (WorkerMessage.EvalCode("System.Diagnostics.Debugger.IsAttached", Guid.NewGuid().ToString("N"))) |> Async.StartAsTask
  let! winner = Task.WhenAny(ask, Task.Delay TestTimeouts.patienceBrief)
  match obj.ReferenceEquals(winner, ask) with
  | false -> return false
  | true ->
    match ask.Result with
    | WorkerResponse.EvalResult(_, Ok result, _, _) -> return result.Contains "true"
    | _ -> return false
}

/// Wait for the debugger to be attached to the app's process. Attaching stops the process until the debugger
/// is told to carry on, so each poll tells it to, and then asks the process.
let private waitUntilAttached (app: RunningApp) (attached: Attached) : Task<unit> = task {
  let sw = Stopwatch.StartNew()
  let mutable attachedNow = false
  while not attachedNow && sw.Elapsed < TestTimeouts.readyBudget do
    match attached.Debugger.HasExited with
    | true ->
      failwithf "The debugger exited before it attached (exit %d). It said:\n%s" attached.Debugger.ExitCode (lock attached.Said (fun () -> attached.Said.ToString()))
    | false ->
      attached.Debugger.StandardInput.WriteLine "continue"
      let! answer = isAttached app
      attachedNow <- answer
      match attachedNow with
      | true -> ()
      | false -> do! Task.Delay TestTimeouts.pollService
  match attachedNow with
  | true -> ()
  | false ->
    failwithf "No debugger showed as attached to the app's process within %.0fs. The debugger said:\n%s" TestTimeouts.readyBudget.TotalSeconds (lock attached.Said (fun () -> attached.Said.ToString()))
}

let private withDebugger (runtime: HostRuntime) =
  testTask (sprintf "[%s] saves land and are Patched in a process a managed debugger is attached to" (HostRuntime.moniker runtime)) {
    let! executable = debugger.Value
    let! app = startFixture parityFixture runtime ignore
    let attached = attach executable (fsiHostPid app.Host.Id)
    try
      do! waitUntilAttached app attached
      let selected = rows |> List.filter (fun r -> List.contains r.Name rowNames)
      selected |> List.map _.Name |> List.sort |> Expect.equal "every row this test names exists" (List.sort rowNames)
      let observed = ResizeArray<Observed>()
      for row in selected do
        let! seen = exerciseRow app row
        observed.Add seen
      let! stillAttached = isAttached app
      stillAttached |> Expect.isTrue "the debugger was attached for every save, not detached by one"
      let table =
        observed
        |> Seq.map (fun o ->
          match o.Problem with
          | "" -> sprintf "  held  %-14s %s, serves %A" o.Row.Name o.Said o.Served
          | problem -> sprintf "  RED   %-14s %s, serves %A. %s" o.Row.Name o.Said o.Served problem)
        |> String.concat "\n"
      observed
      |> Seq.filter (fun o -> o.Problem <> "")
      |> Seq.length
      |> Expect.equal (sprintf "every save lands with a debugger attached to the process:\n%s" table) 0
    finally
      (try attached.Debugger.Kill(entireProcessTree = true) with _ -> ())
      stop app
  }

/// The twin: the probe the row above trusts has to be able to say "no". Without a debugger the same
/// question about the same process is answered false, so a row that passed did not pass because the
/// probe says yes to everything.
let private withoutDebugger (runtime: HostRuntime) =
  testTask (sprintf "[%s] twin: with no debugger attached, the app's process says so" (HostRuntime.moniker runtime)) {
    let! app = startFixture parityFixture runtime ignore
    try
      let! answer = isAttached app
      answer |> Expect.isFalse "nothing is attached to this process"
    finally
      stop app
  }

[<Tests>]
let hotReloadDebuggerTests =
  Integration.hostList "hot reload with a managed debugger attached" [
    for runtime in HostRuntime.all do
      withDebugger runtime
      withoutDebugger runtime
  ]
