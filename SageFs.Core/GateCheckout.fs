/// How a local-gate checkout's build output was last left, and what the next run does about it.
///
/// `scripts/local-gate.fsx` keeps one persistent checkout per repo so a Release build stays warm, and its `git clean`
/// runs without -x, so ignored build output survives between runs. A run that is killed part way leaves that output
/// made for a commit the tree has since moved past, and the next run died in zero seconds on a FileNotFoundException
/// naming a SageFs.Core version the tree no longer was. The gate now records what it is doing beside the checkout,
/// and the next run reads the record.
///
/// It depends on nothing but System, so an `.fsx` can `#load` it, as `gate-reap.fsx` does `GateReaper.fs`.
module SageFs.GateCheckout

open System
open System.IO

/// What the last run in a checkout left its build output as.
type CheckoutBuild =
  /// Nothing readable says how the output was made.
  | NeverRecorded
  /// A run began for this commit and never wrote its end.
  | Interrupted of sha: string
  /// A run for this commit reached its end, passed or failed.
  | Finished of sha: string

/// Whether the build output in a checkout is worth keeping for the next run.
type OutputDecision =
  | Keep
  | Clear of reason: string

/// What the gate writes beside the checkout before it runs the pipeline.
let startedRecord (sha: string) : string = "started " + sha

/// What the gate writes in its place once the pipeline has ended on its own.
let finishedRecord (sha: string) : string = "finished " + sha

/// Reads a record back. Anything that is not exactly one of the two shapes is `NeverRecorded`, so a file that is
/// empty, torn or hand-edited can never read as a clean one.
let parseRecord (text: string) : CheckoutBuild =
  match text.Split([| ' '; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) with
  | [| "started"; sha |] -> CheckoutBuild.Interrupted sha
  | [| "finished"; sha |] -> CheckoutBuild.Finished sha
  | _ -> CheckoutBuild.NeverRecorded

/// The record that says a state back: the one `parseRecord` reads. `NeverRecorded` has nothing to say, so it writes
/// the empty record and reads back as itself.
let recordOf (state: CheckoutBuild) : string =
  match state with
  | CheckoutBuild.Interrupted sha -> startedRecord sha
  | CheckoutBuild.Finished sha -> finishedRecord sha
  | CheckoutBuild.NeverRecorded -> ""

/// What a pipeline's exit leaves the checkout as: a stop by the clock or a signal (`cancelled`) is an interrupted
/// run, and any other exit, including a red one, reached its end.
let afterPipeline (cancelled: bool) (sha: string) : CheckoutBuild =
  match cancelled with
  | true -> CheckoutBuild.Interrupted sha
  | false -> CheckoutBuild.Finished sha

/// Output left by a run that reached its end is kept, because the warm build is why the checkout persists.
/// Output of a run that did not, or of a checkout nobody recorded, is cleared: nothing says it is whole.
let decide (previous: CheckoutBuild) : OutputDecision =
  match previous with
  | CheckoutBuild.Finished finishedFor -> OutputDecision.Keep
  | CheckoutBuild.Interrupted sha ->
    OutputDecision.Clear(sprintf "the last run in this checkout, for %s, never finished" (sha.Substring(0, min 8 sha.Length)))
  | CheckoutBuild.NeverRecorded -> OutputDecision.Clear "nothing records how this checkout's build output was made"

/// The `bin` and `obj` directories of the checkout's projects: those that sit beside a project file, found without
/// going into node_modules or .git. A `bin` with no project beside it is somebody's folder and is left alone.
let rec outputDirs (dir: string) : string list =
  let hasProject = Directory.EnumerateFiles dir |> Seq.exists (fun f -> f.EndsWith ".fsproj" || f.EndsWith ".csproj")
  [ for child in Directory.EnumerateDirectories dir do
      match Path.GetFileName child with
      | "bin" | "obj" -> if hasProject then yield child
      | "node_modules" | ".git" -> ()
      | _ -> yield! outputDirs child ]

/// What `prepare` did: the decision it acted on, the directories it removed, and those it could not.
type Cleanup =
  { Decision: OutputDecision
    Cleared: string list
    Failed: (string * string) list }

/// The record file for one checkout, under `stateDir`, which is outside the checkout so `git clean` and a reset can
/// never touch it.
let recordPath (stateDir: string) (checkout: string) : string = Path.Combine(stateDir, Path.GetFileName(checkout.TrimEnd('/', '\\')))

/// What the last run recorded for `checkout`. A record that is missing or cannot be read is `NeverRecorded`, which
/// the decision clears on: only the two failures a file read can have are caught, so any other fault is not hidden.
let readRecord (stateDir: string) (checkout: string) : CheckoutBuild =
  let path = recordPath stateDir checkout
  match File.Exists path with
  | false -> CheckoutBuild.NeverRecorded
  | true ->
    try parseRecord (File.ReadAllText path)
    with
    | :? IOException -> CheckoutBuild.NeverRecorded
    | :? UnauthorizedAccessException -> CheckoutBuild.NeverRecorded

/// Read what the last run recorded for `checkout`, decide, and clear the build output when the decision says to. A
/// directory that cannot be removed is reported and left: the pipeline's own build then says what is wrong.
let prepare (stateDir: string) (checkout: string) : Cleanup =
  let previous = readRecord stateDir checkout
  match decide previous with
  | OutputDecision.Keep -> { Decision = OutputDecision.Keep; Cleared = []; Failed = [] }
  | OutputDecision.Clear reason ->
    let attempts =
      outputDirs checkout
      |> List.map (fun dir ->
        try
          Directory.Delete(dir, true)
          Ok dir
        with ex -> Error(dir, ex.Message))
    { Decision = OutputDecision.Clear reason
      Cleared = attempts |> List.choose (function Ok dir -> Some dir | Error _ -> None)
      Failed = attempts |> List.choose (function Error failure -> Some failure | Ok _ -> None) }

/// Why a record could not be written, and where.
type RecordError = RecordNotWritten of path: string * reason: string

/// Write what this run is doing, or what it left. The first goes down before the pipeline starts and the second once
/// it has ended on its own, so a run that is killed never writes the second, which is how the next one knows.
let record (stateDir: string) (checkout: string) (state: CheckoutBuild) : Result<unit, RecordError> =
  let path = recordPath stateDir checkout
  try
    Directory.CreateDirectory stateDir |> ignore
    File.WriteAllText(path, recordOf state)
    Ok()
  with ex -> Error(RecordNotWritten(path, ex.Message))
