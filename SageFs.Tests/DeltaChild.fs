/// Applies metadata deltas in a process of their own.
///
/// `MetadataUpdater.ApplyUpdate` is process-wide and cannot be taken back, and it needs
/// DOTNET_MODIFIABLE_ASSEMBLIES=debug when the process starts. So a delta is never applied in the test
/// host: the host starts this assembly again with `--delta-child <directory>`, the child runs the script
/// in `<directory>/script.txt` against the files beside it, prints what happened, and exits.
///
/// A script is one `Op` per line, and what the child prints is one `Fact` per line. Both are closed sets with one
/// place that spells them (`Op.toLine`, `Op.parse`, `FactKind.tag`), and a test round-trips every `Op`.
module SageFs.Tests.DeltaChild

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Threading
open System.Threading.Tasks
open SageFs.Features.MetadataDelta
open SageFs.Tests.DeltaProgram

/// The prefix `HarmonyPatch` puts on a method: it runs the original.
[<AbstractClass; Sealed>]
type HarmonyNoop =
  static member Prefix() : bool = true

/// Whether `bench` looks through the coverage probes of its baseline.
[<RequireQualifiedAccess>]
type BenchProbes =
  /// The baseline is a plain build.
  | Keep
  /// The baseline is an instrumented module and the probes are looked through.
  | LookThrough

/// What a script can ask of the child.
[<RequireQualifiedAccess>]
type Op =
  /// Load the assembly at that path into the default load context.
  | Load of file: string
  /// Run every method and print what each returned.
  | Eval
  /// The same, with the generic method at a type that has not run yet.
  | EvalLate
  /// Hand a delta to the runtime and print the outcome. The method tokens it wrote are in the file beside it.
  | Apply of meta: string * il: string
  /// Print what is known before an apply, without applying.
  | Check of meta: string * il: string
  /// Print what the process can do for a delta.
  | Capability
  /// Apply, and print how long the runtime took in milliseconds.
  | Time of meta: string * il: string
  /// Read both builds, diff and write the delta, `passes` times from nothing, printing each.
  | Bench of baseline: string * next: string * passes: int * probes: BenchProbes
  /// The next save after the last bench: commit its delta, prepare one for this build.
  | Second of next: string
  /// Apply the delta the last bench wrote, and print how long the runtime and the handlers took.
  | ApplyLast
  /// Call a static method of the loaded assembly that takes nothing, and print what it returned (a task is awaited).
  | Invoke of typeName: string * methodName: string
  /// Allocate this many entry probes in the process's registry, so a delta written with probes 1 to n has them.
  | AllocateProbes of count: int
  /// Print each probe's status: `Entered`, `Superseded` or `NotEntered`.
  | ReadProbes
  /// Read and clear the coverage tracker the instrumenter put in the loaded assembly, and print how many probes it has
  /// and which of them ran.
  | ReadCoverage
  /// Put a Harmony prefix that changes nothing on a static method of the loaded assembly, the way a guard or a detour does.
  | HarmonyPatch of typeName: string * methodName: string

[<RequireQualifiedAccess>]
module Op =

  let toLine (op: Op) : string =
    match op with
    | Op.Load file -> "load " + file
    | Op.Eval -> "eval"
    | Op.EvalLate -> "evallate"
    | Op.Apply (meta, il) -> sprintf "apply %s %s" meta il
    | Op.Check (meta, il) -> sprintf "check %s %s" meta il
    | Op.Capability -> "capability"
    | Op.Time (meta, il) -> sprintf "time %s %s" meta il
    | Op.Bench (baseline, next, passes, probes) ->
      sprintf "bench %s %s %d %s" baseline next passes (match probes with | BenchProbes.Keep -> "keep" | BenchProbes.LookThrough -> "look-through")
    | Op.Second next -> "second " + next
    | Op.ApplyLast -> "applylast"
    | Op.Invoke (typeName, methodName) -> sprintf "invoke %s %s" typeName methodName
    | Op.AllocateProbes count -> sprintf "allocateprobes %d" count
    | Op.ReadProbes -> "readprobes"
    | Op.ReadCoverage -> "readcoverage"
    | Op.HarmonyPatch (typeName, methodName) -> sprintf "harmonypatch %s %s" typeName methodName

  let parse (line: string) : Op voption =
    match line.Split(' ', StringSplitOptions.RemoveEmptyEntries) with
    | [| "load"; file |] -> ValueSome (Op.Load file)
    | [| "eval" |] -> ValueSome Op.Eval
    | [| "evallate" |] -> ValueSome Op.EvalLate
    | [| "apply"; meta; il |] -> ValueSome (Op.Apply (meta, il))
    | [| "check"; meta; il |] -> ValueSome (Op.Check (meta, il))
    | [| "capability" |] -> ValueSome Op.Capability
    | [| "time"; meta; il |] -> ValueSome (Op.Time (meta, il))
    | [| "bench"; baseline; next; passes; probes |] ->
      match Int32.TryParse passes, probes with
      | (true, n), "keep" -> ValueSome (Op.Bench (baseline, next, n, BenchProbes.Keep))
      | (true, n), "look-through" -> ValueSome (Op.Bench (baseline, next, n, BenchProbes.LookThrough))
      | _ -> ValueNone
    | [| "second"; next |] -> ValueSome (Op.Second next)
    | [| "applylast" |] -> ValueSome Op.ApplyLast
    | [| "invoke"; typeName; methodName |] -> ValueSome (Op.Invoke (typeName, methodName))
    | [| "allocateprobes"; count |] ->
      match Int32.TryParse count with
      | true, n -> ValueSome (Op.AllocateProbes n)
      | false, _ -> ValueNone
    | [| "readprobes" |] -> ValueSome Op.ReadProbes
    | [| "readcoverage" |] -> ValueSome Op.ReadCoverage
    | [| "harmonypatch"; typeName; methodName |] -> ValueSome (Op.HarmonyPatch (typeName, methodName))
    | _ -> ValueNone

/// What a line the child prints is about.
[<RequireQualifiedAccess>]
type FactKind =
  | EvalBegin
  | EvalEnd
  | EvalLine
  | Apply
  | Check
  | Capability
  | Time
  | Bench
  | Second
  | Handler
  /// What a method called by `Invoke` returned, or the name of the exception it threw.
  | Invoked
  /// The status of the probes, from `ReadProbes`: `1=Entered 2=NotEntered`.
  | Probes
  /// The coverage tracker, from `ReadCoverage`: `total=N hit=a,b,c`.
  | Coverage
  /// The script had a line the child does not know.
  | Unknown
  /// The last fact of a script that ran to its end, with the number of facts before it. A child that exits 0 without it
  /// did not finish, whatever else it said.
  | Done

[<RequireQualifiedAccess>]
module FactKind =
  let all : FactKind list =
    [ FactKind.EvalBegin; FactKind.EvalEnd; FactKind.EvalLine; FactKind.Apply; FactKind.Check; FactKind.Capability
      FactKind.Time; FactKind.Bench; FactKind.Second; FactKind.Handler; FactKind.Invoked; FactKind.Probes; FactKind.Coverage; FactKind.Unknown
      FactKind.Done ]

  let tag (kind: FactKind) : string =
    match kind with
    | FactKind.EvalBegin -> "EVAL-BEGIN"
    | FactKind.EvalEnd -> "EVAL-END"
    | FactKind.EvalLine -> "EVAL-LINE"
    | FactKind.Apply -> "APPLY"
    | FactKind.Check -> "CHECK"
    | FactKind.Capability -> "CAPABILITY"
    | FactKind.Time -> "TIME"
    | FactKind.Bench -> "BENCH"
    | FactKind.Second -> "SECOND"
    | FactKind.Handler -> "HANDLER"
    | FactKind.Invoked -> "INVOKED"
    | FactKind.Probes -> "PROBES"
    | FactKind.Coverage -> "COVERAGE"
    | FactKind.Unknown -> "UNKNOWN"
    | FactKind.Done -> "DONE"

/// One thing the child said.
type Fact =
  { Kind: FactKind
    Text: string }

let private prefix = "DELTACHILD "

/// The file the child's facts go to, in the script's directory.
[<Literal>]
let private factsFileName = "facts.txt"

/// Where a fact is written first. The facts do not travel over stdout: a pipe is read by an event on the parent's pool,
/// and a parent that lost it saw a child that ran its whole script as one that said nothing. A file the child writes
/// and flushes line by line is there when the child is gone.
let mutable private factSink : TextWriter = TextWriter.Null
let mutable private factsSaid = 0

let private say (kind: FactKind) (text: string) =
  let line = sprintf "%s%s %s" prefix (FactKind.tag kind) text
  factSink.WriteLine line
  factsSaid <- factsSaid + 1
  // Stdout still carries them, for a person watching and for the failure message's last lines.
  printfn "%s" line

let private parseFact (line: string) : Fact voption =
  match line.StartsWith(prefix, StringComparison.Ordinal) with
  | false -> ValueNone
  | true ->
    let rest = line.Substring prefix.Length
    let tag, text =
      match rest.IndexOf ' ' with
      | -1 -> rest, ""
      | i -> rest.Substring(0, i), rest.Substring(i + 1)
    FactKind.all
    |> List.tryFind (fun kind -> FactKind.tag kind = tag)
    |> function
      | Some kind -> ValueSome { Kind = kind; Text = text }
      | None -> ValueNone

/// What the delta was, as far as applying it needs to know.
let private payloadOf (meta: byte array) (il: byte array) (tokens: int list) (requires: RequiredFeature list) : DeltaPayload =
  { Generation = 0
    Metadata = meta
    Il = il
    Updated = []
    AddedMethods = []
    MethodTokens = tokens
    Probes = []
    Requires = requires }

/// The method tokens a delta wrote, from the file the parent put beside it (empty when there is none).
let private tokensIn (file: string) : int list =
  match File.Exists file with
  | false -> []
  | true ->
    File.ReadAllText(file).Split(',', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map int
    |> Array.toList

let private evaluateLines (assembly: Assembly) (late: bool) : string list =
  let prog = assembly.GetType "Gen.Prog"
  let call (m: MethodInfo) (args: obj array) : string =
    try string (m.Invoke(null, args))
    with :? TargetInvocationException as e ->
      // The test compares the exception's name; the rest goes to stderr, which a red case prints.
      match e.InnerException with
      | :? DivideByZeroException | :? OverflowException -> ()
      | inner -> eprintfn "%s threw %s" m.Name (inner.ToString())
      "exc:" + e.InnerException.GetType().Name
  let methods =
    prog.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
    |> Array.filter (fun m -> m.Name.StartsWith("m", StringComparison.Ordinal) && not m.IsGenericMethodDefinition)
    |> Array.sortBy (fun m -> int (m.Name.Substring 1))
  let g = prog.GetMethod "G"
  [ for m in methods do
      for x in inputs do
        yield sprintf "%s(%d)=%s" m.Name x (call m [| box x |])
    for k in inputs do
      yield sprintf "G<int>(7,%d)=%s" k (call (g.MakeGenericMethod typeof<int>) [| box 7; box k |])
      yield sprintf "G<string>(abc,%d)=%s" k (call (g.MakeGenericMethod typeof<string>) [| box "abc"; box k |])
      match late with
      | true -> yield sprintf "G<double>(1.5,%d)=%s" k (call (g.MakeGenericMethod typeof<double>) [| box 1.5; box k |])
      | false -> () ]

let private describeOutcome (outcome: ApplyOutcome) : string =
  match outcome with
  | ApplyOutcome.Applied -> "Applied"
  | ApplyOutcome.AppliedHandlersFailed failures -> "AppliedHandlersFailed " + String.Join("; ", failures)
  | ApplyOutcome.RuntimeNotModifiable reason -> "RuntimeNotModifiable " + reason
  | ApplyOutcome.DebuggerAttached -> "DebuggerAttached"
  | ApplyOutcome.NotSupported reason -> "NotSupported " + reason
  | ApplyOutcome.Rejected reason -> "Rejected " + reason

/// The child's work. Returns the exit code.
let private runScript (directory: string) : int =
  let mutable assembly : Assembly = null
  // The delta the last `bench` produced, for `ApplyLast`, and the chain after it with the build it was made from, for `Second`.
  let mutable lastPayload : DeltaPayload voption = ValueNone
  let mutable lastChain : (DeltaChain * PeImage) voption = ValueNone
  let mutable allocatedProbes = 0
  let path (name: string) = Path.Combine(directory, name)
  factSink <- new StreamWriter(path factsFileName, false, Text.UTF8Encoding false, AutoFlush = true)
  for line in File.ReadAllLines(path "script.txt") do
    match Op.parse line with
    | ValueNone -> say FactKind.Unknown line
    | ValueSome (Op.Load file) -> assembly <- AssemblyLoadContext.Default.LoadFromAssemblyPath(path file)
    | ValueSome (Op.Eval | Op.EvalLate as op) ->
      say FactKind.EvalBegin ""
      for l in evaluateLines assembly (op = Op.EvalLate) do
        say FactKind.EvalLine l
      say FactKind.EvalEnd ""
    | ValueSome (Op.Apply (meta, il)) ->
      let tokens = tokensIn (path (Path.ChangeExtension(meta, "tokens")))
      say FactKind.Apply (describeOutcome (DeltaApply.apply assembly (payloadOf (File.ReadAllBytes(path meta)) (File.ReadAllBytes(path il)) tokens [])))
    | ValueSome (Op.Check (meta, il)) ->
      let payload = payloadOf (File.ReadAllBytes(path meta)) (File.ReadAllBytes(path il)) (tokensIn (path (Path.ChangeExtension(meta, "tokens")))) [ RequiredFeature.Baseline ]
      match DeltaApply.check (DeltaApply.capability ()) assembly payload with
      | CapabilityCheck.Capable -> say FactKind.Check "Capable"
      | CapabilityCheck.Incapable gaps ->
        for gap in gaps do
          say FactKind.Check (DeltaApply.describeGap gap)
    | ValueSome Op.Capability ->
      let c = DeltaApply.capability ()
      say FactKind.Capability (sprintf "support=%A environment=%A debugger=%A features=%s" c.Support c.Environment c.Debugger (String.Join(",", c.Features)))
    | ValueSome (Op.Time (meta, il)) ->
      let m = File.ReadAllBytes(path meta)
      let i = File.ReadAllBytes(path il)
      let watch = Stopwatch.StartNew()
      let outcome = DeltaApply.apply assembly (payloadOf m i [] [])
      watch.Stop()
      say FactKind.Time (sprintf "%.3f %s" watch.Elapsed.TotalMilliseconds (describeOutcome outcome))
    | ValueSome (Op.Bench (baseline, next, passes, benchProbes)) ->
      // Read both builds, diff them and write the delta, `passes` times from nothing. The first is cold (the JIT
      // has not seen this code); the rest are what a second save costs.
      let probes =
        match benchProbes with
        | BenchProbes.LookThrough -> ProbeStripping.StripCoverageProbes CoverageProbe.hitSymbol
        | BenchProbes.Keep -> ProbeStripping.KeepEveryInstruction
      for pass in 1 .. passes do
        let watch = Stopwatch.StartNew()
        let before = PeImage.OfFile(path baseline)
        let after = PeImage.OfFile(path next)
        let read = watch.Elapsed.TotalMilliseconds
        watch.Restart()
        let chain = DeltaChain.Start(before, probes)
        match chain.Prepare(Guid.NewGuid(), after) with
        | PrepareOutcome.Ready prepared ->
          lastPayload <- ValueSome prepared.Payload
          lastChain <- ValueSome (chain.Commit prepared, after)
          say FactKind.Bench
            (sprintf "%d read=%.1f prepare=%.1f metadata=%d il=%d updated=%d added=%d"
               pass read watch.Elapsed.TotalMilliseconds prepared.Payload.Metadata.Length prepared.Payload.Il.Length
               prepared.Payload.Updated.Length prepared.Payload.AddedMethods.Length)
        | PrepareOutcome.NothingChanged -> say FactKind.Bench (sprintf "%d nothing-changed" pass)
        | PrepareOutcome.Refused causes ->
          say FactKind.Bench (sprintf "%d refused %s" pass (String.Join("; ", causes |> List.map RudeCause.describe)))
    | ValueSome (Op.Second next) ->
      // The save after the one `Bench` made: the chain has committed it, so the previous build is the one it was
      // made from (already read), and only the new build is new.
      match lastChain with
      | ValueNone -> say FactKind.Second "no-chain"
      | ValueSome (chain, _) ->
        let watch = Stopwatch.StartNew()
        let after = PeImage.OfFile(path next)
        let read = watch.Elapsed.TotalMilliseconds
        watch.Restart()
        match chain.Prepare(Guid.NewGuid(), after) with
        | PrepareOutcome.Ready prepared ->
          say FactKind.Second
            (sprintf "read=%.1f prepare=%.1f metadata=%d il=%d updated=%d added=%d"
               read watch.Elapsed.TotalMilliseconds prepared.Payload.Metadata.Length prepared.Payload.Il.Length
               prepared.Payload.Updated.Length prepared.Payload.AddedMethods.Length)
        | PrepareOutcome.NothingChanged -> say FactKind.Second "nothing-changed"
        | PrepareOutcome.Refused causes -> say FactKind.Second (sprintf "refused %s" (String.Join("; ", causes |> List.map RudeCause.describe)))
    | ValueSome (Op.Invoke (typeName, methodName)) ->
      let result =
        try
          let m = assembly.GetType(typeName).GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Static, null, Type.EmptyTypes, null)
          match m.Invoke(null, [||]) with
          | :? System.Threading.Tasks.Task<string> as task -> task.Result
          | other -> string other
        with
        | :? TargetInvocationException as e -> "exc:" + e.InnerException.GetType().Name + " " + e.InnerException.Message
        | e -> "exc:" + e.GetType().Name + " " + e.Message
      say FactKind.Invoked (sprintf "%s.%s=%s" typeName methodName result)
    | ValueSome (Op.AllocateProbes count) ->
      for _ in 1 .. count do
        allocatedProbes <- allocatedProbes + 1
        SageFs.Middleware.EntryProbes.ProbeRegistry.Shared.Allocate(sprintf "probe-%d" allocatedProbes) |> ignore
    | ValueSome (Op.HarmonyPatch (typeName, methodName)) ->
      let target = assembly.GetType(typeName).GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Static)
      let prefix = HarmonyLib.HarmonyMethod(typeof<HarmonyNoop>.GetMethod("Prefix", BindingFlags.Public ||| BindingFlags.Static))
      HarmonyLib.Harmony("deltachild").Patch(target, prefix) |> ignore
      say FactKind.Invoked (sprintf "%s.%s patched" typeName methodName)
    | ValueSome Op.ReadCoverage ->
      match SageFs.Features.LiveTesting.CoverageProbes.readAndClear (SageFs.Features.LiveTesting.CoverageProbes.findTrackers [| assembly |]) with
      | None -> say FactKind.Coverage "total=0 hit="
      | Some hits ->
        let which = hits |> Array.indexed |> Array.filter snd |> Array.map (fst >> string)
        say FactKind.Coverage (sprintf "total=%d hit=%s" hits.Length (String.Join(",", which)))
    | ValueSome Op.ReadProbes ->
      let reading = SageFs.Middleware.EntryProbes.ProbeRegistry.Shared.Read [ 1L .. int64 allocatedProbes ]
      say FactKind.Probes
        (String.Join(" ", reading.Sightings |> List.map (fun s -> sprintf "%d=%A" s.Probe s.Status)))
    | ValueSome Op.ApplyLast ->
      match lastPayload with
      | ValueNone -> say FactKind.Time "no-delta"
      | ValueSome payload ->
        // The runtime's call and the handlers after it, timed apart: they are different costs.
        let watch = Stopwatch.StartNew()
        System.Reflection.Metadata.MetadataUpdater.ApplyUpdate(assembly, ReadOnlySpan<byte>(payload.Metadata), ReadOnlySpan<byte>(payload.Il), ReadOnlySpan<byte>.Empty)
        let applied = watch.Elapsed.TotalMilliseconds
        watch.Restart()
        let runs = DeltaApply.runUpdateHandlers (DeltaApply.updatedTypes assembly payload)
        let failures = DeltaApply.handlerFailures runs
        say FactKind.Time
          (sprintf "%.3f handlers=%.3f %s" applied watch.Elapsed.TotalMilliseconds (match failures with | [] -> "Applied" | f -> "handlers failed: " + String.Join("; ", f)))
        for run in runs |> List.sortByDescending (fun r -> r.Milliseconds) |> List.truncate 4 do
          say FactKind.Handler (sprintf "%.3f ms %s.%s" run.Milliseconds run.Handler run.Step)
  say FactKind.Done (string factsSaid)
  factSink.Dispose()
  0

/// What running an argument list came to.
type ChildRun =
  /// This was a child: it ran its script and this is the exit code.
  | Ran of exitCode: int
  /// Not a child: the arguments are the test runner's.
  | NotAChild

let tryRun (argv: string[]) : ChildRun =
  match argv with
  | [| "--delta-child"; directory |] ->
    try ChildRun.Ran (runScript directory)
    with e ->
      eprintfn "%s" (e.ToString())
      ChildRun.Ran 3
  | _ -> ChildRun.NotAChild

// -- the parent side -------------------------------------------------------------------------------------------

/// What a child printed and how it ended.
type ChildResult =
  { ExitCode: int
    /// The facts the child wrote to its file, in order.
    Facts: Fact list
    /// Whether the last fact is `Done` and it counts the facts before it, so the file is whole.
    Finished: bool
    Stderr: string
    /// Everything the child wrote to stdout, facts or not, so a run that parsed to nothing can show what it got.
    Stdout: string
    /// How long the child lived, from start to the last byte read.
    Elapsed: TimeSpan
    /// The child's process id, so a kernel log line about it can be matched.
    ProcessId: int
    /// The directory the script ran in.
    Directory: string
    /// The script, as the lines the child read.
    Script: string list }

/// Whether the child starts with the environment variable that makes modules editable.
[<RequireQualifiedAccess>]
type ModifiableAssemblies =
  | Debug
  | NotSet

/// How to start this assembly again: `dotnet SageFs.Tests.dll` when the host is the dotnet muxer, the executable
/// itself when it is the apphost.
let private startInfo (directory: string) (modifiable: ModifiableAssemblies) : ProcessStartInfo =
  let executable = Environment.ProcessPath
  let info =
    match Path.GetFileNameWithoutExtension executable with
    | "dotnet" ->
      let i = ProcessStartInfo(executable)
      i.ArgumentList.Add(Assembly.GetExecutingAssembly().Location)
      i
    | _ -> ProcessStartInfo(executable)
  info.ArgumentList.Add "--delta-child"
  info.ArgumentList.Add directory
  info.RedirectStandardOutput <- true
  info.RedirectStandardError <- true
  info.UseShellExecute <- false
  info.CreateNoWindow <- true
  // The shared environment first (without the MSBuild pins another case's project load left on the process), then
  // the one variable this child is about: DOTNET_MODIFIABLE_ASSEMBLIES is in the stripped set, so it is set AFTER.
  SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply info
  match modifiable with
  | ModifiableAssemblies.Debug -> info.Environment["DOTNET_MODIFIABLE_ASSEMBLIES"] <- "debug"
  | ModifiableAssemblies.NotSet -> info.Environment.Remove "DOTNET_MODIFIABLE_ASSEMBLIES" |> ignore
  info

/// What the parent does with the read end of the child's stdout pipe.
[<RequireQualifiedAccess>]
type StdoutReading =
  /// It reads every line into `stdout.txt`, as a run always does.
  | Drained
  /// It closes the read end straight after the start, the way a parent that lost a descriptor would. The child's
  /// writes then fail with EPIPE, which the runtime's console ignores, so the child exits 0 and says nothing. This is
  /// the twin: a harness whose facts depend on the pipe goes red under it.
  | ClosedAtStart

/// Run a script in a child. Both streams are drained into files while it runs, because a redirected pipe nobody reads
/// fills and stops the child before it says anything.
let runReading (reading: StdoutReading) (budget: TimeSpan) (directory: string) (modifiable: ModifiableAssemblies) (script: Op list) : Task<ChildResult> =
  task {
    let scriptLines = script |> List.map Op.toLine
    File.WriteAllLines(Path.Combine(directory, "script.txt"), scriptLines)
    let watch = Stopwatch.StartNew()
    let stdoutPath = Path.Combine(directory, "stdout.txt")
    let stderrPath = Path.Combine(directory, "stderr.txt")
    use stdoutFile = new StreamWriter(stdoutPath)
    use stderrFile = new StreamWriter(stderrPath)
    // `startInfo` above calls SpawnEnvironment.apply before it sets the one variable this child is about.
    use child = new Process(StartInfo = startInfo directory modifiable)
    child.OutputDataReceived.Add(fun e -> lock stdoutFile (fun () -> match e.Data with | null -> () | line -> stdoutFile.WriteLine line))
    child.ErrorDataReceived.Add(fun e -> lock stderrFile (fun () -> match e.Data with | null -> () | line -> stderrFile.WriteLine line))
    child.Start() |> ignore
    match reading with
    | StdoutReading.Drained -> child.BeginOutputReadLine()
    | StdoutReading.ClosedAtStart -> child.StandardOutput.Dispose()
    child.BeginErrorReadLine()
    use cts = new CancellationTokenSource(budget)
    try
      do! child.WaitForExitAsync cts.Token
    with :? OperationCanceledException ->
      child.Kill true
      failwithf "the delta child did not end within %O (directory %s)" budget directory
    // The no-argument wait returns once the redirected streams reached end of file.
    child.WaitForExit()
    stdoutFile.Flush()
    stderrFile.Flush()
    let stdout = File.ReadAllText stdoutPath
    // The facts are the child's own file, not what came over the pipe.
    let factsPath = Path.Combine(directory, factsFileName)
    let facts =
      match File.Exists factsPath with
      | false -> []
      | true ->
        File.ReadAllLines factsPath
        |> Array.choose (fun l -> match parseFact l with | ValueSome fact -> Some fact | ValueNone -> None)
        |> Array.toList
    let finished =
      match List.tryLast facts with
      | Some { Kind = FactKind.Done; Text = count } -> count = string (facts.Length - 1)
      | _ -> false
    return
      { ExitCode = child.ExitCode
        Facts = facts
        Finished = finished
        Stderr = File.ReadAllText stderrPath
        Stdout = stdout
        Elapsed = watch.Elapsed
        ProcessId = child.Id
        Directory = directory
        Script = scriptLines }
  }

/// `runReading` with the pipe read the way a run always reads it.
let runWithin (budget: TimeSpan) (directory: string) (modifiable: ModifiableAssemblies) (script: Op list) : Task<ChildResult> =
  runReading StdoutReading.Drained budget directory modifiable script

/// Run a script in a child that is expected to end within `TestTimeouts.patience`.
let run (directory: string) (modifiable: ModifiableAssemblies) (script: Op list) : Task<ChildResult> =
  runWithin TestTimeouts.patience directory modifiable script

/// The text of each fact of one kind, in order.
let factsOf (kind: FactKind) (result: ChildResult) : string list =
  result.Facts |> List.filter (fun f -> f.Kind = kind) |> List.map (fun f -> f.Text)

/// The lines of each `Eval`, in order.
let evalBlocks (result: ChildResult) : string list list =
  let blocks = ResizeArray<string list>()
  let current = ResizeArray<string>()
  for fact in result.Facts do
    match fact.Kind with
    | FactKind.EvalBegin -> current.Clear()
    | FactKind.EvalLine -> current.Add fact.Text
    | FactKind.EvalEnd -> blocks.Add(List.ofSeq current)
    | _ -> ()
  List.ofSeq blocks

/// What each `Apply` said, in order.
let applyOutcomes (result: ChildResult) : string list = factsOf FactKind.Apply result

/// How many of the last lines of a stream `describe` quotes.
let private tailLines = 8

let private tailOf (text: string) : string =
  match text.Split('\n', StringSplitOptions.RemoveEmptyEntries) with
  | [||] -> "(nothing)"
  | lines -> String.Join(" // ", lines |> Array.skip (max 0 (lines.Length - tailLines)) |> Array.map (fun l -> l.TrimEnd '\r'))

/// What the host's descriptor table holds, by kind (`file`, `pipe`, `socket`, `anon_inode`, `other`), from the links
/// under /proc/self/fd. A table that lost or swapped a descriptor shows up as a kind that is out of proportion, and a
/// link that cannot be read is counted as `gone` (it was closed while this looked).
let private descriptorKinds () : string =
  try
    Directory.GetFileSystemEntries "/proc/self/fd"
    |> Array.map (fun entry ->
      match FileInfo(entry).LinkTarget with
      | null -> "gone"
      | target when target.StartsWith("pipe:", StringComparison.Ordinal) -> "pipe"
      | target when target.StartsWith("socket:", StringComparison.Ordinal) -> "socket"
      | target when target.StartsWith("anon_inode:", StringComparison.Ordinal) -> "anon_inode"
      | target when target.StartsWith("/", StringComparison.Ordinal) -> "file"
      | _ -> "other")
    |> Array.countBy id
    |> Array.sortBy fst
    |> Array.map (fun (kind, n) -> sprintf "%s=%d" kind n)
    |> String.concat " "
  with _ -> "unknown"

/// What the machine looked like when the failure was read: the load average, the descriptors the host process holds
/// and its thread count. The first two are on Linux only; elsewhere the text says so.
let machineNow () : string =
  let load = try File.ReadAllText("/proc/loadavg").Trim() with _ -> "no /proc/loadavg"
  sprintf "load %s, host descriptors [%s], host threads %d" load (descriptorKinds ()) (Process.GetCurrentProcess().Threads.Count)

/// Everything a reader needs to tell a child that said nothing from one that said something the parent did not
/// parse: how it ended, how long it lived, how much it wrote to each stream and the end of both, and where it ran.
let describeRun (result: ChildResult) : string =
  let stdoutBytes = System.Text.Encoding.UTF8.GetByteCount result.Stdout
  let stderrBytes = System.Text.Encoding.UTF8.GetByteCount result.Stderr
  let scriptFile =
    let path = Path.Combine(result.Directory, "script.txt")
    match File.Exists path with
    | true -> sprintf "script.txt %d bytes" (FileInfo(path).Length)
    | false -> "script.txt missing"
  sprintf
    "child pid %d exit %d after %.1f s in %s (%s) | facts file: %d facts, finished %b | stdout %d bytes, ends: %s | stderr %d bytes, ends: %s | script: %s | %s"
    result.ProcessId result.ExitCode result.Elapsed.TotalSeconds result.Directory scriptFile
    result.Facts.Length result.Finished stdoutBytes (tailOf result.Stdout)
    stderrBytes (tailOf result.Stderr)
    (String.Join("; ", result.Script)) (machineNow ())
