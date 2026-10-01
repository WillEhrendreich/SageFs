/// Applies metadata deltas in a process of their own.
///
/// `MetadataUpdater.ApplyUpdate` is process-wide and cannot be taken back, and it needs
/// DOTNET_MODIFIABLE_ASSEMBLIES=debug when the process starts. So a delta is never applied in the test
/// host: the host starts this assembly again with `--delta-child <directory>`, the child runs the script
/// in `<directory>/script.txt` against the files beside it, prints what happened, and exits.
///
/// The script is one operation per line:
///
///   load <file>              load the assembly at that path into the default load context
///   eval | evallate          run every method and print what each returned. `evallate` adds the generic
///                            method at a type that has not run yet
///   apply <meta> <il>        hand a delta to the runtime and print the outcome
///   check <meta> <il>        print what is known before an apply, without applying
///   capability               print what the process can do for a delta
///   time <meta> <il>         apply, and print how long the runtime took in milliseconds
///
/// Everything it prints starts with `DELTACHILD `, one fact to a line.
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

let private prefix = "DELTACHILD "

let private say (text: string) = printfn "%s%s" prefix text

/// What the delta was, as far as applying it needs to know.
let private payloadOf (meta: byte array) (il: byte array) (requires: RequiredFeature list) : DeltaPayload =
  { Generation = 0
    Metadata = meta
    Il = il
    Updated = []
    AddedMethods = []
    Requires = requires }

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
  let path (name: string) = Path.Combine(directory, name)
  for line in File.ReadAllLines(path "script.txt") do
    match line.Split(' ', StringSplitOptions.RemoveEmptyEntries) with
    | [| "load"; file |] -> assembly <- AssemblyLoadContext.Default.LoadFromAssemblyPath(path file)
    | [| "eval" |]
    | [| "evallate" |] as op ->
      say "EVAL begin"
      for l in evaluateLines assembly (op[0] = "evallate") do
        say ("L " + l)
      say "EVAL end"
    | [| "apply"; meta; il |] ->
      say ("APPLY " + describeOutcome (DeltaApply.apply assembly (payloadOf (File.ReadAllBytes(path meta)) (File.ReadAllBytes(path il)) [])))
    | [| "check"; meta; il |] ->
      let payload = payloadOf (File.ReadAllBytes(path meta)) (File.ReadAllBytes(path il)) [ RequiredFeature.Baseline ]
      match DeltaApply.check (DeltaApply.capability ()) assembly payload with
      | CapabilityCheck.Capable -> say "CHECK Capable"
      | CapabilityCheck.Incapable gaps ->
        for gap in gaps do
          say ("CHECK " + DeltaApply.describeGap gap)
    | [| "capability" |] ->
      let c = DeltaApply.capability ()
      say (sprintf "CAPABILITY support=%A environment=%A debugger=%A features=%s" c.Support c.Environment c.Debugger (String.Join(",", c.Features)))
    | [| "time"; meta; il |] ->
      let m = File.ReadAllBytes(path meta)
      let i = File.ReadAllBytes(path il)
      let watch = Stopwatch.StartNew()
      let outcome = DeltaApply.apply assembly (payloadOf m i [])
      watch.Stop()
      say (sprintf "TIME %.3f %s" watch.Elapsed.TotalMilliseconds (describeOutcome outcome))
    | [||] -> ()
    | other -> say ("UNKNOWN " + String.Join(" ", other))
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
    /// The `DELTACHILD` lines, without the prefix.
    Facts: string list
    Stderr: string }

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
  match modifiable with
  | ModifiableAssemblies.Debug -> info.Environment["DOTNET_MODIFIABLE_ASSEMBLIES"] <- "debug"
  | ModifiableAssemblies.NotSet -> info.Environment.Remove "DOTNET_MODIFIABLE_ASSEMBLIES" |> ignore
  info

/// Run a script in a child. Both streams are drained into files while it runs, because a redirected pipe nobody reads
/// fills and stops the child before it says anything.
let run (directory: string) (modifiable: ModifiableAssemblies) (script: string list) : Task<ChildResult> =
  task {
    File.WriteAllLines(Path.Combine(directory, "script.txt"), script)
    let stdoutPath = Path.Combine(directory, "stdout.txt")
    let stderrPath = Path.Combine(directory, "stderr.txt")
    use stdoutFile = new StreamWriter(stdoutPath)
    use stderrFile = new StreamWriter(stderrPath)
    use child = new Process(StartInfo = startInfo directory modifiable)
    child.OutputDataReceived.Add(fun e -> lock stdoutFile (fun () -> match e.Data with | null -> () | line -> stdoutFile.WriteLine line))
    child.ErrorDataReceived.Add(fun e -> lock stderrFile (fun () -> match e.Data with | null -> () | line -> stderrFile.WriteLine line))
    child.Start() |> ignore
    child.BeginOutputReadLine()
    child.BeginErrorReadLine()
    use cts = new CancellationTokenSource(TestTimeouts.patience)
    try
      do! child.WaitForExitAsync cts.Token
    with :? OperationCanceledException ->
      child.Kill true
      failwithf "the delta child did not end within %O (directory %s)" TestTimeouts.patience directory
    // The no-argument wait returns once the redirected streams reached end of file.
    child.WaitForExit()
    stdoutFile.Flush()
    stderrFile.Flush()
    let facts =
      File.ReadAllLines stdoutPath
      |> Array.filter (fun l -> l.StartsWith(prefix, StringComparison.Ordinal))
      |> Array.map (fun l -> l.Substring prefix.Length)
      |> Array.toList
    return
      { ExitCode = child.ExitCode
        Facts = facts
        Stderr = File.ReadAllText stderrPath }
  }

/// The lines of each `eval`, in order.
let evalBlocks (result: ChildResult) : string list list =
  let rec go (facts: string list) (current: string list option) (acc: string list list) =
    match facts, current with
    | [], _ -> List.rev acc
    | "EVAL begin" :: rest, _ -> go rest (Some []) acc
    | "EVAL end" :: rest, Some lines -> go rest None (List.rev lines :: acc)
    | fact :: rest, Some lines when fact.StartsWith("L ", StringComparison.Ordinal) -> go rest (Some (fact.Substring 2 :: lines)) acc
    | _ :: rest, _ -> go rest current acc
  go result.Facts None []

/// What each `apply` said, in order.
let applyOutcomes (result: ChildResult) : string list =
  result.Facts
  |> List.filter (fun f -> f.StartsWith("APPLY ", StringComparison.Ordinal))
  |> List.map (fun f -> f.Substring "APPLY ".Length)
