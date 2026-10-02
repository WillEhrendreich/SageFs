// scripts/machine-bench-tiers.fsx   run scripts/machine-bench.fsx once per emulated machine tier on THIS Linux machine
// Run with: dotnet fsi scripts/machine-bench-tiers.fsx -- <dir holding SageFs.dll> <output dir> [runs] [cold-runs] [mode]
//
// To get a curve rather than one point. Each tier is a systemd user scope (cpu quota, memory cap, no swap) plus
// a taskset CPU list. Nothing needs root. Everything the benchmark starts inherits the limits.
//
// mode is `defaults` (the daemon's own timeouts, the default) or `generous` (every wait for the machine raised
// far past any tier, to see what the machine can do when no timeout binds).
//
// What this CAN emulate: fewer cores, less CPU time per core (a quota, which stalls all threads together, so it
// is a harsher shape than a slower clock), less memory with a hard OOM instead of swap. What it CANNOT: a slower
// clock, a slower disk, a cold page cache (dropping caches needs root). The real slow machine is the benchmark
// run on that machine.
//
// One tier's output is <output dir>/<tier>-<mode>.txt (the table), .json (raw samples) and .err (stderr).
// SMT siblings are skipped: on a CPU whose logical cpu N+8 shares a core with N, "4 cores" means cpus 0-3.
// Check `lscpu -e` and edit `tiers` if yours is laid out differently.
//
// TIERS_ONLY="t1q50 t4m2g" in the environment runs just those (a rerun of the tiers that failed under the
// shipped timeouts).
//
// Exit codes: 0 every tier was attempted (a tier's own failure is in its .err file, as it always was);
// 64 bad arguments.
open System
open System.Diagnostics
open System.IO

// ── named values ─────────────────────────────────────────────────────────────

/// One emulated machine: a name, the CPUs it may use, and optionally a CPU quota and a memory cap.
type Tier =
  { Name: string
    Cpus: string
    CpuQuota: string option
    MemoryMax: string option }

let tiers : Tier list =
  [ { Name = "t16"; Cpus = "0-15"; CpuQuota = None; MemoryMax = None }
    { Name = "t8"; Cpus = "0-7"; CpuQuota = None; MemoryMax = None }
    { Name = "t4"; Cpus = "0-3"; CpuQuota = None; MemoryMax = None }
    { Name = "t4q50"; Cpus = "0-3"; CpuQuota = Some "200%"; MemoryMax = None }
    { Name = "t2"; Cpus = "0-1"; CpuQuota = None; MemoryMax = None }
    { Name = "t2q50"; Cpus = "0-1"; CpuQuota = Some "100%"; MemoryMax = None }
    { Name = "t1q50"; Cpus = "0"; CpuQuota = Some "50%"; MemoryMax = None }
    { Name = "t4m8g"; Cpus = "0-3"; CpuQuota = None; MemoryMax = Some "8G" }
    { Name = "t4m4g"; Cpus = "0-3"; CpuQuota = None; MemoryMax = Some "4G" }
    { Name = "t4m2g"; Cpus = "0-3"; CpuQuota = None; MemoryMax = Some "2G" }
    { Name = "t4m1500m"; Cpus = "0-3"; CpuQuota = None; MemoryMax = Some "1500M" }
    { Name = "t4m1g"; Cpus = "0-3"; CpuQuota = None; MemoryMax = Some "1G" } ]

/// How long a session may take to reach Ready on any tier, in seconds (machine-bench's --ready-cap).
let readyCapSeconds = 300
let defaultRuns = "5"
let defaultColdRuns = "3"

type Mode =
  | Defaults
  | Generous

let modeName = function
  | Defaults -> "defaults"
  | Generous -> "generous"

/// Every wait for the machine raised far past any tier, so no timeout binds in `generous` mode.
let generousEnvironment : (string * string) list =
  [ "SAGEFS_WARMUP_INACTIVITY_SECONDS", "1200"; "SAGEFS_WARMUP_MAX_MINUTES", "60"
    "SAGEFS_FSI_HOST_STARTUP_SECONDS", "1200"; "SAGEFS_DOTNET_SDK_QUERY_SECONDS", "300"
    "SAGEFS_REBUILD_READY_SECONDS", "1200"; "SAGEFS_WORKER_HTTP_READ_SECONDS", "600"
    "SAGEFS_STOP_GRACEFUL_SECONDS", "300"; "SAGEFS_SUPERVISOR_WEDGE_SECONDS", "600"
    "SAGEFS_HOST_BUILD_MINUTES", "30"; "SAGEFS_WARMUP_READY_POLL_SECONDS", "1200"
    "SAGEFS_STOP_SESSION_TIMEOUT_SECONDS", "300" ]

type Request =
  { SageFsDir: string
    OutDir: string
    Runs: string
    ColdRuns: string
    Mode: Mode }

type Failure =
  | Usage of string

let exitCodeOf = function
  | Usage _ -> 64

let describe = function
  | Usage m -> m

exception Stop of Failure
let fail f = raise (Stop f)

let parse (argv: string list) : Request =
  let mode (text: string) =
    match text with
    | "defaults" -> Defaults
    | "generous" -> Generous
    | other -> fail (Usage (sprintf "mode is 'defaults' or 'generous', not '%s'" other))
  match argv with
  | [ dir; out ] -> { SageFsDir = dir; OutDir = out; Runs = defaultRuns; ColdRuns = defaultColdRuns; Mode = Defaults }
  | [ dir; out; runs ] -> { SageFsDir = dir; OutDir = out; Runs = runs; ColdRuns = defaultColdRuns; Mode = Defaults }
  | [ dir; out; runs; cold ] -> { SageFsDir = dir; OutDir = out; Runs = runs; ColdRuns = cold; Mode = Defaults }
  | [ dir; out; runs; cold; m ] -> { SageFsDir = dir; OutDir = out; Runs = runs; ColdRuns = cold; Mode = mode m }
  | _ -> fail (Usage "usage: machine-bench-tiers.fsx <dir holding SageFs.dll> <output dir> [runs] [cold-runs] [mode]")

/// The systemd-run arguments for one tier: a user scope with the quota and the memory cap (and no swap with a
/// cap), then taskset to pin the CPUs, then the benchmark.
let commandFor (request: Request) (tier: Tier) : string * string list =
  let label = sprintf "%s-%s" tier.Name (modeName request.Mode)
  let properties =
    [ yield! tier.CpuQuota |> Option.map (fun q -> [ "-p"; "CPUQuota=" + q ]) |> Option.toList |> List.concat
      yield! tier.MemoryMax |> Option.map (fun m -> [ "-p"; "MemoryMax=" + m; "-p"; "MemorySwapMax=0" ]) |> Option.toList |> List.concat ]
  let environment =
    match request.Mode with
    | Defaults -> []
    | Generous -> generousEnvironment |> List.collect (fun (k, v) -> [ "--env"; sprintf "%s=%s" k v ])
  "systemd-run",
  [ "--user"; "--scope"; "-q" ] @ properties
  @ [ "taskset"; "-c"; tier.Cpus; "dotnet"; "fsi"; Path.Combine(__SOURCE_DIRECTORY__, "machine-bench.fsx")
      "--sagefs"; request.SageFsDir; "--runs"; request.Runs; "--cold-runs"; request.ColdRuns
      "--ready-cap"; string readyCapSeconds; "--label"; label; "--out"; Path.Combine(request.OutDir, label + ".json") ]
  @ environment

/// The tiers to run: all of them, or the names in TIERS_ONLY (space-separated).
let selected () : Tier list =
  match Environment.GetEnvironmentVariable "TIERS_ONLY" with
  | null | "" -> tiers
  | only ->
    let names = only.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray
    tiers |> List.filter (fun t -> names.Contains t.Name)

/// Runs one tier to completion with its table in <label>.txt and its stderr in <label>.err.
let runTier (request: Request) (tier: Tier) : unit =
  let label = sprintf "%s-%s" tier.Name (modeName request.Mode)
  eprintfn "== %s: cpus %s quota '%s' memory '%s'" tier.Name tier.Cpus (defaultArg tier.CpuQuota "") (defaultArg tier.MemoryMax "")
  let file, args = commandFor request tier
  let psi = ProcessStartInfo(file)
  args |> List.iter psi.ArgumentList.Add
  psi.UseShellExecute <- false
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  use p = Process.Start psi
  let out = p.StandardOutput.ReadToEndAsync()
  let err = p.StandardError.ReadToEndAsync()
  p.WaitForExit()
  File.WriteAllText(Path.Combine(request.OutDir, label + ".txt"), out.Result)
  File.WriteAllText(Path.Combine(request.OutDir, label + ".err"), err.Result)

let argv = fsi.CommandLineArgs |> Array.toList |> List.tail |> List.filter (fun a -> a <> "--")

let code =
  try
    let request = parse argv
    Directory.CreateDirectory request.OutDir |> ignore
    for tier in selected () do
      runTier request tier
    0
  with Stop f ->
    eprintfn "machine-bench-tiers: %s" (describe f)
    exitCodeOf f

exit code
