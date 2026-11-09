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
  /// In a fresh process, a getter queues pool work and waits for it. After the call no thread carries the filter, and a pool
  /// thread can still write a file and start a process.
  | PoolWorkNoLeak
  /// The twin of `PoolWorkNoLeak`: the program as it was before thread creation was refused, with no preparation of the pool.
  /// The filter stays on the pool workers the call made.
  | PoolWorkNoLeakBefore
  /// A getter queues 300 pool items and waits for all of them (far more than any idle worker count). Nothing is left under the
  /// filter, the pool still serves, and it still grows when every worker is blocked (so its gate thread is intact).
  | PoolExhausted
  /// The work starts a thread and is refused, and the call still completes.
  | ThreadCreationRefused
  /// Rounds of: a getter queues pool work under the filter, then a burst of pool items does the delta emitter test's own file
  /// cycle (create a directory, write facts.txt, read it back). Counts every exception, with its type and its inner message.
  | PoolFileCycles
  /// The same rounds with the filter as it was before thread creation was refused and no preparation of the pool.
  | PoolFileCyclesBefore
  /// The work takes every existing pool worker and then asks the pool for one more: the moment the pool would make a thread on
  /// the filtered one. The call completes, the pool's limits were held during it and are back after it, and the pool still serves.
  | PoolBusy
  /// The twin of `PoolBusy`: the pool is prepared and the thread refused, but its limits are not held. The pool tries to make the
  /// thread on the filtered one, is refused, and throws into the work.
  | PoolBusyBefore
  /// A hold whose call never ends is let go by its ceiling, and the call's late leave does nothing.
  | PoolHoldExpires

module SandboxScenario =

  let all : SandboxScenario list =
    [ SandboxScenario.BlockedPool; SandboxScenario.RuntimeWork; SandboxScenario.ProbeSeesAPoolThread
      SandboxScenario.PoolWorkNoLeak; SandboxScenario.PoolWorkNoLeakBefore; SandboxScenario.PoolExhausted
      SandboxScenario.ThreadCreationRefused; SandboxScenario.PoolFileCycles; SandboxScenario.PoolFileCyclesBefore
      SandboxScenario.PoolBusy; SandboxScenario.PoolBusyBefore; SandboxScenario.PoolHoldExpires ]

  let toArgument (scenario: SandboxScenario) : string =
    match scenario with
    | SandboxScenario.BlockedPool -> "blocked-pool"
    | SandboxScenario.RuntimeWork -> "runtime-work"
    | SandboxScenario.ProbeSeesAPoolThread -> "probe-sees-a-pool-thread"
    | SandboxScenario.PoolWorkNoLeak -> "pool-work-no-leak"
    | SandboxScenario.PoolWorkNoLeakBefore -> "pool-work-no-leak-before"
    | SandboxScenario.PoolExhausted -> "pool-exhausted"
    | SandboxScenario.ThreadCreationRefused -> "thread-creation-refused"
    | SandboxScenario.PoolFileCycles -> "pool-file-cycles"
    | SandboxScenario.PoolFileCyclesBefore -> "pool-file-cycles-before"
    | SandboxScenario.PoolBusy -> "pool-busy"
    | SandboxScenario.PoolBusyBefore -> "pool-busy-before"
    | SandboxScenario.PoolHoldExpires -> "pool-hold-expires"

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
    /// What the method called `hotCalls` times answered.
    Hot: int
    Compiled: bool }

/// What the runtime does that a getter takes for granted: allocate, collect, JIT fresh code, get a hot method promoted. (Not
/// make a thread: the filter refuses that, see `ThreadCreationRefused`.)
let private runtimeWork () : RuntimeResult =
  let sum = Seq.sum (seq { for i in 1 .. summedUpTo -> i })
  // A young-generation collection still suspends and resumes every thread, which is what the filter must not break.
  GC.Collect youngestGeneration
  let mutable hot = 0
  for _ in 1 .. hotCalls do
    hot <- addOne hot
  let compiled = Regex("a+b", RegexOptions.Compiled).IsMatch "caaab"
  { Sum = sum; Hot = hot; Compiled = compiled }

let private describeOutcome (outcome: SandboxOutcome<RuntimeResult>) : (string * string) list =
  match outcome with
  | Completed result ->
    [ "outcome", "completed"
      "sum", string result.Sum
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

/// The kernel thread ids the sandbox's own work ran on, each recorded by the work itself from /proc/thread-self (a readlink,
/// which the filter allows).
let private sandboxTids = System.Collections.Concurrent.ConcurrentBag<string>()

let private markSandboxThread () : unit =
  match File.ResolveLinkTarget("/proc/thread-self", false) with
  | null -> ()
  | target -> sandboxTids.Add (Path.GetFileName target.FullName)

/// How an outcome reads in the child's result line. A getter that threw says what and where, so a rare throw can be read.
let private describeThrown (e: exn) : string =
  let frames = (string e.StackTrace).Split('\n') |> Array.truncate 6 |> Array.map (fun l -> l.Trim())
  sprintf "threw:%s: %s @ %s" (e.GetType().Name) (e.Message.Replace('\n', ' ')) (String.Join(" | ", frames))

let private describeCount (outcome: SandboxOutcome<int>) : string =
  match outcome with
  | Completed total -> sprintf "completed:%d" total
  | Threw e -> describeThrown e
  | Unavailable reason -> "unavailable:" + SandboxUnavailable.describe reason

/// The threads of this process that carry a seccomp filter, from the kernel's own per-thread report (`Seccomp:` in
/// /proc/self/task/<tid>/status is 0 for none, 2 for a filter). Not a behaviour probe: it names the thread.
let private threadsUnderFilter () : string list =
  match Directory.Exists "/proc/self/task" with
  | false -> []
  | true ->
    [ for dir in Directory.GetDirectories "/proc/self/task" do
        let status = try File.ReadAllLines (Path.Combine(dir, "status")) with _ -> [||]
        let field (name: string) =
          status
          |> Array.tryFind (fun line -> line.StartsWith(name + ":", StringComparison.Ordinal))
          |> Option.map (fun line -> line.Substring(name.Length + 1).Trim())
          |> Option.defaultValue "?"
        match field "Seccomp", Seq.contains (Path.GetFileName dir) sandboxTids with
        // A thread that exited between the listing and the read has no status to report: it carries nothing.
        | "0", _ | "?", _ -> ()
        // The sandbox's own thread carries the filter by design, and the kernel keeps its entry listed for a moment after the
        // call has returned. Every other thread under the filter is the leak.
        | _, true -> ()
        | mode, false -> yield sprintf "%s(%s)=%s" (field "Name") (Path.GetFileName dir) mode ]

/// What a getter that uses the pool does: queues work and waits for it.
let private queuesPoolWork () : int =
  let queued = [| for i in 1 .. 8 -> Task.Run(fun () -> i * 2) |]
  Task.WaitAll(queued |> Array.map (fun t -> t :> Task))
  queued |> Array.sumBy (fun t -> t.Result)

/// In a process whose pool has not grown, a getter queues pool work and waits for it. A filter is inherited by every thread the
/// filtered one creates, and the pool makes workers (and its gate thread) from the thread that queued the work, so a worker made
/// for the getter used to carry the filter for the life of the process. After the call the kernel must name no thread under the
/// filter, and a pool thread must still be able to write a file and start a process.
let private poolWorkNoLeakWith (run: (unit -> int) -> SandboxOutcome<int>) (directory: string) : (string * string) list =
  let before = threadsUnderFilter ()
  let outcome = run (fun () -> markSandboxThread (); queuesPoolWork ())
  let after = threadsUnderFilter ()
  let fromAPoolThread (what: unit -> string) =
    (Task.Run(fun () -> try what () with e -> e.GetType().Name + ":" + e.Message)).Result
  let poolWrite =
    fromAPoolThread (fun () ->
      File.WriteAllText(Path.Combine(directory, "from-a-pool-thread.txt"), "x")
      "wrote")
  let poolSpawn =
    fromAPoolThread (fun () ->
      let info = ProcessStartInfo("true", UseShellExecute = false)
      SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply info
      use started = Process.Start info
      started.WaitForExit()
      "spawned")
  [ "filteredBefore", string before.Length
    "filteredAfter", string after.Length
    "filteredNames", (match after with | [] -> "none" | names -> String.Join(",", names))
    "poolWrite", poolWrite
    "poolSpawn", poolSpawn
    "outcome", describeCount outcome ]

let private poolWorkNoLeak (directory: string) : (string * string) list =
  poolWorkNoLeakWith (ThreadSandbox.run SandboxPolicy.NoNetworkNoWritesNoSpawn) directory

/// The product's call, except that the filter is the one it had before thread creation was refused and nothing prepares the pool.
let private poolWorkNoLeakBefore (directory: string) : (string * string) list =
  match ThreadSandbox.availability () with
  | Error reason -> [ "outcome", "unavailable:" + SandboxUnavailable.describe reason ]
  | Ok arch ->
    let install = ThreadSandbox.installerFor Seccomp.ThreadCreation.Allowed arch SandboxPolicy.NoNetworkNoWritesNoSpawn
    poolWorkNoLeakWith (fun work -> ThreadSandbox.runWithInstaller install work) directory

let private poolExhausted () : (string * string) list =
  let outcome =
    ThreadSandbox.run SandboxPolicy.NoNetworkNoWritesNoSpawn (fun () ->
      markSandboxThread ()
      let queued = [| for i in 1 .. 300 -> Task.Run(fun () -> i) |]
      Task.WaitAll(queued |> Array.map (fun t -> t :> Task))
      queued.Length)
  let after = threadsUnderFilter ()
  // The pool still serves work after a call that asked it for far more than it had.
  let served = (Task.Run(fun () -> queuesPoolWork ())).Result
  // And it still grows when every worker is blocked: the gate thread that does it was not left in a state it cannot leave. The
  // probe can only run on a worker the pool adds; the wait has no deadline, and the parent's ceiling on the process is what says
  // the pool did not.
  let release = new ManualResetEventSlim(false)
  for _ in 1 .. ThreadPool.ThreadCount + 2 do
    ThreadPool.UnsafeQueueUserWorkItem(WaitCallback(fun _ -> release.WaitHandle.WaitOne() |> ignore), null) |> ignore
  let grown = (Task.Run(fun () -> "ran")).Result
  release.Set()
  [ "outcome", describeCount outcome
    "filteredAfter", string after.Length
    "filteredNames", (match after with | [] -> "none" | names -> String.Join(",", names))
    "servedAfter", string served
    "grewWhenBlocked", grown ]

/// The work starts a thread. Under the filter the runtime cannot make one, and says so: the work sees the refusal and the call still
/// completes. A thread the filtered one made would inherit the filter for the life of the process.
let private threadCreationRefused () : (string * string) list =
  let outcome =
    ThreadSandbox.run SandboxPolicy.NoNetworkNoWritesNoSpawn (fun () ->
      markSandboxThread ()
      try
        let t = Thread((fun () -> ()), IsBackground = true)
        t.Start()
        t.Join()
        "started"
      with e -> "refused:" + e.GetType().Name)
  let after = threadsUnderFilter ()
  match outcome with
  | Completed what -> [ "outcome", "completed"; "thread", what; "filteredAfter", string after.Length ]
  | Threw e -> [ "outcome", "threw"; "detail", e.GetType().Name + ":" + e.Message ]
  | Unavailable reason -> [ "outcome", "unavailable"; "detail", SandboxUnavailable.describe reason ]

/// Rounds of a sandbox call that queues pool work, each followed by a burst of pool items doing a file cycle like the delta
/// emitter test's (a directory, facts.txt written and read back). A pool worker that carries the filter refuses the write, and the
/// exception says how: its type and its inner message are what the gate's `facts.txt` failure can be compared with.
let private poolFileCycles (run: (unit -> int) -> SandboxOutcome<int>) (directory: string) : (string * string) list =
  let rounds = 12
  let burst = 64
  let errors = System.Collections.Concurrent.ConcurrentQueue<string>()
  let cycle (id: int) =
    try
      let dir = Path.Combine(directory, sprintf "cycle-%d" id)
      Directory.CreateDirectory dir |> ignore
      let facts = Path.Combine(dir, "facts.txt")
      File.WriteAllLines(facts, [ "DELTACHILD one"; "DELTACHILD two" ])
      File.ReadAllLines facts |> ignore
    with e ->
      errors.Enqueue(e.GetType().Name + ": " + (match e.InnerException with | null -> e.Message | inner -> inner.Message))
  let mutable completedCalls = 0
  for round in 1 .. rounds do
    match run (fun () -> markSandboxThread (); queuesPoolWork ()) with
    | Completed _ -> completedCalls <- completedCalls + 1
    | Threw _
    | Unavailable _ -> ()
    let items = [| for i in 1 .. burst -> Task.Run(fun () -> cycle (round * 1000 + i)) |]
    Task.WaitAll items
  let seen = errors.ToArray()
  [ "rounds", string rounds
    "completedCalls", string completedCalls
    "cycles", string (rounds * burst)
    "errors", string seen.Length
    "firstError", (match seen with [||] -> "none" | all -> all.[0])
    "filteredAfter", string (threadsUnderFilter ()).Length ]

/// The pool's minimum and maximum worker counts, as the pool reports them now.
let private poolLimits () : string * string =
  let minWorkers, _ = ThreadPool.GetMinThreads()
  let maxWorkers, _ = ThreadPool.GetMaxThreads()
  string minWorkers, string maxWorkers

/// Runs on the filtered thread: takes every worker the pool has (each blocks until released), then asks the pool for one more and
/// lets the others go. With a worker free the request is served by waking it; with none free the pool would make a thread, on
/// this one. Returns what the limits were while this ran.
let private takeEveryWorkerThenAskForOneMore () : int * (string * string) =
  markSandboxThread ()
  let during = poolLimits ()
  let release = new ManualResetEventSlim(false)
  let blocked = ref 0
  // One blocker at a time, each waited for until it is running, so no request is left over to wake a worker later: when the
  // loop ends every worker the pool has is inside a blocker.
  while blocked.Value < ThreadPool.ThreadCount do
    let begun = new ManualResetEventSlim(false)
    ThreadPool.UnsafeQueueUserWorkItem(
      WaitCallback(fun _ ->
        Interlocked.Increment blocked |> ignore
        begun.Set()
        release.WaitHandle.WaitOne() |> ignore),
      null)
    |> ignore
    begun.WaitHandle.WaitOne() |> ignore
  let probe = Task.Run(fun () -> 1)
  release.Set()
  probe.Result, during

let private poolBusyWith (run: (unit -> int * (string * string)) -> SandboxOutcome<int * (string * string)>) (afterwards: unit -> (string * string) list) : (string * string) list =
  let minBefore, maxBefore = poolLimits ()
  let outcome = run takeEveryWorkerThenAskForOneMore
  let minAfter, maxAfter = poolLimits ()
  let said =
    match outcome with
    | Completed (answer, (minDuring, maxDuring)) ->
      [ "outcome", sprintf "completed:%d" answer; "minDuring", minDuring; "maxDuring", maxDuring ]
    | Threw e -> [ "outcome", describeThrown e ]
    | Unavailable reason -> [ "outcome", "unavailable:" + SandboxUnavailable.describe reason ]
  said
  @ [ "minBefore", minBefore; "maxBefore", maxBefore; "minAfter", minAfter; "maxAfter", maxAfter
      "filteredAfter", string (threadsUnderFilter ()).Length ]
  @ afterwards ()

let private poolBusy () : (string * string) list =
  poolBusyWith
    (fun work -> ThreadSandbox.run SandboxPolicy.NoNetworkNoWritesNoSpawn work)
    (fun () -> [ "servedAfter", string (Task.Run(fun () -> queuesPoolWork ())).Result ])

/// Blocks one more pool worker until `release` is set, and returns when it is running, so the next call finds the pool exactly one
/// worker busier and no request left over.
let private blockOneWorker (release: ManualResetEventSlim) : unit =
  let begun = new ManualResetEventSlim(false)
  ThreadPool.UnsafeQueueUserWorkItem(
    WaitCallback(fun _ ->
      begun.Set()
      release.WaitHandle.WaitOne() |> ignore),
    null)
  |> ignore
  begun.WaitHandle.WaitOne() |> ignore

/// The thread is refused, as the product does, but nothing holds the pool's limits. The work keeps one more worker blocked each
/// time, waiting for each to run so no request is left over to wake a worker later. The pool has a few workers and a goal of its
/// minimum, so past the workers it has, every request finds none free and the pool is under its goal: it makes a thread, on the
/// filtered one, and is refused. The loop is as long as the goal, so it ends in that refusal whatever the pool had to start with,
/// and it does not depend on timing.
let private poolBusyBefore () : (string * string) list =
  match ThreadSandbox.availability () with
  | Error reason -> [ "outcome", "unavailable:" + SandboxUnavailable.describe reason ]
  | Ok arch ->
    let release = new ManualResetEventSlim(false)
    let install = ThreadSandbox.installerFor Seccomp.ThreadCreation.Refused arch SandboxPolicy.NoNetworkNoWritesNoSpawn
    let outcome =
      ThreadSandbox.runWithInstaller install (fun () ->
        markSandboxThread ()
        try
          let minWorkers, _ = ThreadPool.GetMinThreads()
          for _ in 1 .. minWorkers + 1 do
            blockOneWorker release
          minWorkers
        finally
          release.Set())
    [ "outcome", describeCount outcome ]

/// A call that never ends: it enters the hold and nothing leaves. The hold's own ceiling ends it, and the guard says so through
/// `applied`, which this waits on (no poll, no clock of its own). The call's late leave must then change nothing.
let private poolHoldExpires () : (string * string) list =
  let restored = new ManualResetEventSlim(false)
  let moves = System.Collections.Concurrent.ConcurrentQueue<string>()
  let guard =
    SandboxPool.Guard(
      SageFs.Tests.TestTimeouts.abandonedHoldCeiling,
      fun move ->
        match move with
        | PoolMove.Restore _ ->
          moves.Enqueue "restore"
          restored.Set()
        | PoolMove.Pin _ -> moves.Enqueue "pin"
        | PoolMove.Keep -> ())
  match SandboxPool.prepare () with
  | Error reason -> [ "outcome", "unavailable:" + SandboxUnavailable.describe reason ]
  | Ok () ->
    let minBefore, maxBefore = poolLimits ()
    match guard.Enter() with
    | Error reason -> [ "outcome", "refused:" + SandboxUnavailable.describe reason ]
    | Ok epoch ->
      let minHeld, maxHeld = poolLimits ()
      restored.WaitHandle.WaitOne() |> ignore
      let minExpired, maxExpired = poolLimits ()
      guard.Leave epoch
      let minLate, maxLate = poolLimits ()
      [ "outcome", "expired"
        "minBefore", minBefore; "maxBefore", maxBefore
        "minHeld", minHeld; "maxHeld", maxHeld
        "minExpired", minExpired; "maxExpired", maxExpired
        "minLate", minLate; "maxLate", maxLate
        "moves", String.Join(",", moves.ToArray()) ]

let private poolFileCyclesProduct (directory: string) : (string * string) list =
  poolFileCycles (ThreadSandbox.run SandboxPolicy.NoNetworkNoWritesNoSpawn) directory

let private poolFileCyclesBefore (directory: string) : (string * string) list =
  match ThreadSandbox.availability () with
  | Error reason -> [ "outcome", "unavailable:" + SandboxUnavailable.describe reason ]
  | Ok arch ->
    let install = ThreadSandbox.installerFor Seccomp.ThreadCreation.Allowed arch SandboxPolicy.NoNetworkNoWritesNoSpawn
    poolFileCycles (fun work -> ThreadSandbox.runWithInstaller install work) directory

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
          | SandboxScenario.PoolWorkNoLeak -> poolWorkNoLeak directory
          | SandboxScenario.PoolWorkNoLeakBefore -> poolWorkNoLeakBefore directory
          | SandboxScenario.PoolExhausted -> poolExhausted ()
          | SandboxScenario.ThreadCreationRefused -> threadCreationRefused ()
          | SandboxScenario.PoolFileCycles -> poolFileCyclesProduct directory
          | SandboxScenario.PoolFileCyclesBefore -> poolFileCyclesBefore directory
          | SandboxScenario.PoolBusy -> poolBusy ()
          | SandboxScenario.PoolBusyBefore -> poolBusyBefore ()
          | SandboxScenario.PoolHoldExpires -> poolHoldExpires ()
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
