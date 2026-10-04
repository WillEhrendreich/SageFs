/// What one tier's whole process tree cost, read from the cgroup its processes ran in. A tier spawns daemons,
/// workers, FSI hosts, MSBuild nodes and browsers, and a daemon outlives the process that started it, so a
/// per-process CPU reading (ps, `time`) misses most of it. The scope's own counters do not.
///
/// Pure, so the pipeline script and the tests read the same rules. Loaded by ci-pipeline.fsx (`#load`).
module SageFs.Build.TierCost

type Cost =
  { UserSeconds: float
    SystemSeconds: float
    PeakBytes: int64 }

/// Where the scope's epilogue writes the counters, named by the environment of the tier it wraps.
let statFileEnvironmentVariable = "SAGEFS_TIER_STAT"

let userField = "user_usec"
let systemField = "system_usec"
let peakField = "peak_bytes"
let microsecondsPerSecond = 1_000_000.0

let totalSeconds (cost: Cost) = cost.UserSeconds + cost.SystemSeconds

/// The counters file the epilogue writes: cpu.stat lines (`user_usec 123`) plus `peak_bytes N`.
let parse (text: string) : Result<Cost, string> =
  let fields =
    text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun line ->
      match line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) with
      | [| name; value |] ->
        match System.Int64.TryParse value with
        | true, n -> Some (name, n)
        | false, _ -> None
      | _ -> None)
    |> Map.ofArray
  match fields.TryFind userField, fields.TryFind systemField, fields.TryFind peakField with
  | Some user, Some system, Some peak ->
    Result.Ok
      { UserSeconds = float user / microsecondsPerSecond
        SystemSeconds = float system / microsecondsPerSecond
        PeakBytes = peak }
  | _ -> Result.Error (sprintf "the counters file is missing one of %s, %s, %s" userField systemField peakField)

/// The epilogue runs INSIDE the scope after the tier ends, because a transient scope disappears with its last
/// process and its counters go with it. The tier's exit code is passed through untouched.
let epilogue =
  "cg=/sys/fs/cgroup$(cut -d: -f3 /proc/self/cgroup); \"$@\"; rc=$?; "
  + "{ cat \"$cg/cpu.stat\"; echo \"peak_bytes $(cat \"$cg/memory.peak\")\"; } > \"$"
  + statFileEnvironmentVariable
  + "\"; exit $rc"

/// `argv` run in a transient systemd user scope named `unitName`, writing its counters to the file named by
/// SAGEFS_TIER_STAT when it ends. The whole isolated argv goes inside, so every descendant is counted,
/// orphaned daemons included.
let accountedArgv (unitName: string) (argv: string list) : string list =
  [ "systemd-run"; "--user"; "--scope"; "--quiet"; "--collect"; sprintf "--unit=%s" unitName; "--"; "sh"; "-c"; epilogue; "sh" ]
  @ argv
