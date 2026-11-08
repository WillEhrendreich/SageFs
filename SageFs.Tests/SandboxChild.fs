/// What a real seccomp filter does to a whole process, shown in a process of its own. A young-generation collection has to bring
/// every thread of the process to a safe point, so a case that collects inside a sandbox call took as long as the slowest
/// thread of every other case running in the same test process: 12 ms alone, 1 s at a load of 22, and more than 30 s in the
/// default suite with 184 pool threads and 264 queued items (the release gate, 2026-11-08). The claim is about the sandbox, so
/// the case runs where no other case's threads are: the test host starts this assembly again with
/// `--sandbox-child <scenario> <directory>`, the child runs one scenario and writes one line, and the parent waits for the
/// process to END. What the parent asserts is facts the child read (which thread it ran on, which threads carry a filter, what
/// the work returned), never how long anything took.
module SageFs.Tests.SandboxChild

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open SageFs

/// The scenarios a child can run. A closed set, with one way to write each as the argument.
[<RequireQualifiedAccess>]
type SandboxScenario =
  /// The sandbox call is made while every pool thread is blocked.
  | BlockedPool
  /// The work allocates, collects, makes a thread, JITs fresh code and runs a method hot enough to be promoted to the optimizing
  /// JIT, which the runtime does from a background thread it makes when the promotion is asked for.
  | RuntimeWork
  /// The control for the probe `BlockedPool` reads: a pool thread says it is one.
  | ProbeSeesAPoolThread

module SandboxScenario =

  let all : SandboxScenario list =
    [ SandboxScenario.BlockedPool; SandboxScenario.RuntimeWork; SandboxScenario.ProbeSeesAPoolThread ]

  let toArgument (scenario: SandboxScenario) : string =
    match scenario with
    | SandboxScenario.BlockedPool -> "blocked-pool"
    | SandboxScenario.RuntimeWork -> "runtime-work"
    | SandboxScenario.ProbeSeesAPoolThread -> "probe-sees-a-pool-thread"

  let parse (argument: string) : Result<SandboxScenario, string> =
    match all |> List.tryFind (fun scenario -> toArgument scenario = argument) with
    | Some scenario -> Result.Ok scenario
    | None -> Result.Error (sprintf "unknown sandbox scenario %s" argument)

/// The work on the filtered thread adds 1 up to this, and the answer is n(n+1)/2.
let private summedUpTo = 10000

/// What `runtimeWork` returns for the sum, so the parent can name it without running the work.
let expectedSum = summedUpTo * (summedUpTo + 1) / 2

/// Generation 0, the cheapest collection.
let private youngestGeneration = 0

/// How many times the work calls one method. Far past the runtime's call-count threshold (30), so the method is promoted to the
/// optimizing JIT, which the runtime does from a background worker it makes when the promotion is asked for. The parent starts
/// the child with the tiering delay at 0, so the promotion is asked for by the filtered thread and not by a timer later.
let hotCalls = 2000

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private addOne (x: int) : int = x + 1

/// What the work on the filtered thread brought back.
type RuntimeResult =
  { Sum: int
    /// What a thread the work started computed.
    FromThread: int
    /// What the method called `hotCalls` times answered.
    Hot: int
    Compiled: bool }

/// What the runtime does that a getter takes for granted: allocate, collect, make a thread, JIT fresh code, get a hot method
/// promoted.
let private runtimeWork () : RuntimeResult =
  let sum = Seq.sum (seq { for i in 1 .. summedUpTo -> i })
  // A young-generation collection still suspends and resumes every thread, which is what the filter must not break.
  GC.Collect youngestGeneration
  let inner = ref 0
  let t = Thread((fun () -> inner.Value <- sum), IsBackground = true)
  t.Start()
  t.Join()
  let mutable hot = 0
  for _ in 1 .. hotCalls do
    hot <- addOne hot
  let compiled = Regex("a+b", RegexOptions.Compiled).IsMatch "caaab"
  { Sum = sum; FromThread = inner.Value; Hot = hot; Compiled = compiled }

let private describeOutcome (outcome: SandboxOutcome<RuntimeResult>) : (string * string) list =
  match outcome with
  | Completed result ->
    [ "outcome", "completed"
      "sum", string result.Sum
      "fromThread", string result.FromThread
      "hot", string result.Hot
      "compiled", sprintf "%b" result.Compiled ]
  | Threw e -> [ "outcome", "threw"; "detail", e.GetType().Name + ":" + e.Message ]
  | Unavailable reason -> [ "outcome", "unavailable"; "detail", SandboxUnavailable.describe reason ]

/// The call is made from a thread of its own while every pool thread is blocked. The caller and the work each say whether they
/// are pool threads (a structural fact, the same on a loaded machine). The work itself reads how many blockers still hold the
/// pool once it has finished, and only then lets them go, so "finished while the pool was blocked" is the order the work ran in,
/// not a race between two threads. Nothing here waits on a deadline: a call that needed the pool would never finish, and the
/// parent's ceiling on the process is what says so.
let private blockedPool () : (string * string) list =
  let release = new ManualResetEventSlim(false)
  let minWorkers, _ = ThreadPool.GetMinThreads()
  let blockers = minWorkers + 12
  let running = ref 0
  let enoughRunning = new ManualResetEventSlim(false)
  for _ in 1 .. blockers do
    ThreadPool.UnsafeQueueUserWorkItem(
      (fun _ ->
        match Interlocked.Increment running >= minWorkers with
        | true -> enoughRunning.Set()
        | false -> ()
        release.WaitHandle.WaitOne() |> ignore),
      null,
      false)
    |> ignore
  // Every worker the pool starts at once is now inside a blocker.
  enoughRunning.WaitHandle.WaitOne() |> ignore
  let callerIsPool = ref true
  let workIsPool = ref true
  let blockersAtFinish = ref 0
  let outcome = ref (Threw (InvalidOperationException "the call thread ended without a result"))
  let caller =
    Thread(
      (fun () ->
        callerIsPool.Value <- Thread.CurrentThread.IsThreadPoolThread
        outcome.Value <-
          ThreadSandbox.run SandboxPolicy.NoNetworkNoWritesNoSpawn (fun () ->
            workIsPool.Value <- Thread.CurrentThread.IsThreadPoolThread
            let result = runtimeWork ()
            // The work is done and the pool has not been let go: this is the state the work finished in.
            blockersAtFinish.Value <- running.Value
            release.Set()
            result)),
      IsBackground = true)
  caller.Start()
  caller.Join()
  [ "callerIsPoolThread", sprintf "%b" callerIsPool.Value
    "workIsPoolThread", sprintf "%b" workIsPool.Value
    "poolMinWorkers", string minWorkers
    "blockersRunningAtFinish", string blockersAtFinish.Value ]
  @ describeOutcome outcome.Value

/// The work allocates, collects, makes a thread and JITs, and the child reports what it returned.
let private runtimeWorkScenario () : (string * string) list =
  describeOutcome (ThreadSandbox.run SandboxPolicy.NoNetworkNoWritesNoSpawn runtimeWork)

let private probeSeesAPoolThread () : (string * string) list =
  [ "isPoolThread", sprintf "%b" (Task.Run(fun () -> Thread.CurrentThread.IsThreadPoolThread)).Result ]

/// One line, `key=value` pairs, no value holds a space.
let private line (scenario: SandboxScenario) (fields: (string * string) list) : string =
  let clean (value: string) = Regex.Replace(value, @"\s+", "_")
  "SANDBOXCHILD scenario=" + SandboxScenario.toArgument scenario
  + (fields |> List.map (fun (key, value) -> sprintf " %s=%s" key (clean value)) |> String.concat "")

/// What running an argument list came to.
type ChildRun =
  /// This was a child: it ran its scenario and this is the exit code.
  | Ran of exitCode: int
  /// Not a child: the arguments are the test runner's.
  | NotAChild

/// The file in the run's directory that holds the line the child says. The parent reads it from there: the line also goes to
/// stdout, but a pipe is read by an event on the parent's pool, and a starved pool must not decide what the child said.
[<Literal>]
let ResultFileName = "result.txt"

let tryRun (argv: string[]) : ChildRun =
  match argv with
  | [| "--sandbox-child"; argument; directory |] ->
    match SandboxScenario.parse argument with
    | Result.Error message ->
      eprintfn "%s" message
      ChildRun.Ran 2
    | Result.Ok scenario ->
      try
        let fields =
          match scenario with
          | SandboxScenario.BlockedPool -> blockedPool ()
          | SandboxScenario.RuntimeWork -> runtimeWorkScenario ()
          | SandboxScenario.ProbeSeesAPoolThread -> probeSeesAPoolThread ()
        let said = line scenario fields
        File.WriteAllText(Path.Combine(directory, ResultFileName), said + "\n")
        printfn "%s" said
        stdout.Flush()
        ChildRun.Ran 0
      with e ->
        eprintfn "%s" (e.ToString())
        ChildRun.Ran 3
  | _ -> ChildRun.NotAChild

// -- the parent side -------------------------------------------------------------------------------------------

/// What the child said and how it ended.
type ChildOutcome =
  { ExitCode: int
    /// The `SANDBOXCHILD` line as `key=value` pairs, empty when the child never wrote one (it died first).
    Fields: Map<string, string>
    Stdout: string
    Stderr: string }

let private parseFields (said: string) : Map<string, string> =
  said.Split([| ' '; '\n' |], StringSplitOptions.RemoveEmptyEntries)
  |> Array.choose (fun token ->
    match token.IndexOf '=' with
    | -1 -> None
    | i -> Some (token.Substring(0, i), token.Substring(i + 1)))
  |> Map.ofArray

/// How to start this assembly again: `dotnet SageFs.Tests.dll` when the host is the dotnet muxer, the executable itself when
/// it is the apphost.
let private startInfo (scenario: SandboxScenario) (directory: string) : ProcessStartInfo =
  let executable = Environment.ProcessPath
  let info =
    match Path.GetFileNameWithoutExtension executable with
    | "dotnet" ->
      let i = ProcessStartInfo(executable)
      i.ArgumentList.Add(Assembly.GetExecutingAssembly().Location)
      i
    | _ -> ProcessStartInfo(executable)
  info.ArgumentList.Add "--sandbox-child"
  info.ArgumentList.Add (SandboxScenario.toArgument scenario)
  info.ArgumentList.Add directory
  info.RedirectStandardOutput <- true
  info.RedirectStandardError <- true
  info.UseShellExecute <- false
  info.CreateNoWindow <- true
  info

/// Runs one scenario in a child process and waits for it to END. The only bound is the ceiling on the whole process
/// (`TestTimeouts.childScenarioCeiling`): reaching it means the child hung, and a child that hung is a failed case. Both streams
/// are drained into files while it runs, because a redirected pipe nobody reads fills and stops the child.
let run (scenario: SandboxScenario) : Task<ChildOutcome> =
  task {
    let directory = Path.Combine(Path.GetTempPath(), sprintf "sagefs-sandbox-child-%s" (Guid.NewGuid().ToString "N"))
    Directory.CreateDirectory directory |> ignore
    let stdoutPath = Path.Combine(directory, "stdout.txt")
    let stderrPath = Path.Combine(directory, "stderr.txt")
    try
      use stdoutFile = new StreamWriter(stdoutPath)
      use stderrFile = new StreamWriter(stderrPath)
      let info = startInfo scenario directory
      // The shared environment first (without the MSBuild pins another case's project load left on the process), then the one
      // setting this child is about: the promotion of a hot method is asked for at once, by the filtered thread, instead of by
      // a timer a tenth of a second later.
      SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply info
      info.Environment["DOTNET_TC_CallCountingDelayMs"] <- "0"
      use child = new Process(StartInfo = info)
      child.OutputDataReceived.Add(fun e -> lock stdoutFile (fun () -> match e.Data with | null -> () | text -> stdoutFile.WriteLine text))
      child.ErrorDataReceived.Add(fun e -> lock stderrFile (fun () -> match e.Data with | null -> () | text -> stderrFile.WriteLine text))
      child.Start() |> ignore
      child.BeginOutputReadLine()
      child.BeginErrorReadLine()
      use cts = new CancellationTokenSource(TestTimeouts.childScenarioCeiling)
      try
        do! child.WaitForExitAsync cts.Token
      with :? OperationCanceledException ->
        child.Kill true
        failwithf "the child for %s did not end within %O: it hung" (SandboxScenario.toArgument scenario) TestTimeouts.childScenarioCeiling
      // The no-argument wait returns once the redirected streams reached end of file.
      child.WaitForExit()
      stdoutFile.Flush()
      stderrFile.Flush()
      let resultPath = Path.Combine(directory, ResultFileName)
      let said = match File.Exists resultPath with | true -> File.ReadAllText resultPath | false -> ""
      return
        { ExitCode = child.ExitCode
          Fields = parseFields said
          Stdout = File.ReadAllText stdoutPath
          Stderr = File.ReadAllText stderrPath }
    finally
      try Directory.Delete(directory, true) with _ -> ()
  }
