// How long does SageFs take on THIS machine, stage by stage?
//
//   dotnet fsi scripts/machine-bench.fsx --sagefs <dir holding SageFs.dll> [options]
//
// It prints a machine profile, then runs a throwaway daemon (its own data dir, its own
// port pair, its own FSI host cache) against a small two-project fixture it writes
// itself, and prints one row per stage with n, p50, min and max in milliseconds. It
// never touches ~/.SageFs, the default ports, or the installed tool: every path it
// writes is under --work, and it removes only that on exit (unless --keep).
//
// Options
//   --sagefs DIR     the directory that holds SageFs.dll (a build output, or an
//                    unzipped nupkg's tools/net10.0/any). Required.
//   --runs N         warm runs, host cache already built (default 5)
//   --cold-runs N    cold runs, each one builds the FSI host from scratch (default 3)
//   --port P         MCP port; the dashboard takes P+1 (default 47811)
//   --work DIR       scratch directory (default: a new directory under the temp dir)
//   --label TEXT     a name for this run in the output (a tier, a machine)
//   --env K=V        extra environment for the daemon, repeatable (a timeout override)
//   --nice           run the daemon at nice 19 (Linux/macOS)
//   --ready-cap S    give a session this many seconds to reach Ready (default 600)
//   --out FILE       also write the raw samples as JSON
//   --keep           do not delete --work at the end
//   --skip-lt        skip the live-testing stages
//   --skip-reset     skip the hard-reset stages
//
// To get a tier on one machine, wrap THIS command, for example
//   systemd-run --user --scope -p AllowedCPUs=0-1 -p CPUQuota=100% -p MemoryMax=2G \
//     dotnet fsi scripts/machine-bench.fsx --sagefs ... --label "2 cores 2 GB"
// Everything the script starts inherits the limits.
//
// The numbers it prints come from three places: the harness's own stopwatch (what a
// client sees), the worker's log lines (what the worker says it spent), and /proc
// (peak memory). A stage with no number says so; nothing is invented.

#load "../SageFs.Core/MachineCalibration.fs"

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

// ---- options ---------------------------------------------------------------------

type Options =
  { SageFsDir: string
    Runs: int
    ColdRuns: int
    Port: int
    Work: string
    Label: string
    Env: (string * string) list
    Nice: bool
    ReadyCap: TimeSpan
    Out: string
    Keep: bool
    SkipLt: bool
    SkipReset: bool }

let defaultOptions =
  { SageFsDir = ""
    Runs = 5
    ColdRuns = 3
    Port = 47811
    Work = ""
    Label = ""
    Env = []
    Nice = false
    ReadyCap = TimeSpan.FromSeconds 600.0
    Out = ""
    Keep = false
    SkipLt = false
    SkipReset = false }

let rec parseArgs (o: Options) (rest: string list) : Options =
  match rest with
  | [] -> o
  | "--sagefs" :: v :: tl -> parseArgs { o with SageFsDir = Path.GetFullPath v } tl
  | "--runs" :: v :: tl -> parseArgs { o with Runs = int v } tl
  | "--cold-runs" :: v :: tl -> parseArgs { o with ColdRuns = int v } tl
  | "--port" :: v :: tl -> parseArgs { o with Port = int v } tl
  | "--work" :: v :: tl -> parseArgs { o with Work = Path.GetFullPath v } tl
  | "--label" :: v :: tl -> parseArgs { o with Label = v } tl
  | "--env" :: v :: tl ->
    let i = v.IndexOf '='
    if i <= 0 then failwithf "--env wants K=V, got %s" v
    parseArgs { o with Env = o.Env @ [ (v.Substring(0, i), v.Substring(i + 1)) ] } tl
  | "--nice" :: tl -> parseArgs { o with Nice = true } tl
  | "--ready-cap" :: v :: tl -> parseArgs { o with ReadyCap = TimeSpan.FromSeconds(float v) } tl
  | "--out" :: v :: tl -> parseArgs { o with Out = Path.GetFullPath v } tl
  | "--keep" :: tl -> parseArgs { o with Keep = true } tl
  | "--skip-lt" :: tl -> parseArgs { o with SkipLt = true } tl
  | "--skip-reset" :: tl -> parseArgs { o with SkipReset = true } tl
  | other :: _ -> failwithf "unknown option %s (see the top of this file)" other

let opts =
  let parsed = parseArgs defaultOptions (fsi.CommandLineArgs |> Array.skip 1 |> Array.toList)
  match File.Exists(Path.Combine(parsed.SageFsDir, "SageFs.dll")) with
  | false -> failwith "pass --sagefs <the directory that holds SageFs.dll>"
  | true ->
    match parsed.Work with
    | "" -> { parsed with Work = Path.Combine(Path.GetTempPath(), sprintf "sagefs-bench-%s" (Guid.NewGuid().ToString("N").Substring(0, 8))) }
    | _ -> parsed

// ---- machine profile ---------------------------------------------------------------

let tryRead (path: string) =
  try (match File.Exists path with | true -> Some (File.ReadAllText(path).Trim()) | false -> None) with _ -> None

let cpuinfoField (name: string) =
  match tryRead "/proc/cpuinfo" with
  | None -> None
  | Some text ->
    text.Split('\n')
    |> Array.tryPick (fun l ->
      match l.StartsWith(name, StringComparison.Ordinal) with
      | true -> Some (l.Substring(l.IndexOf(':') + 1).Trim())
      | false -> None)

let meminfoKb (key: string) =
  match tryRead "/proc/meminfo" with
  | None -> None
  | Some text ->
    text.Split('\n')
    |> Array.tryPick (fun l ->
      match l.StartsWith(key + ":", StringComparison.Ordinal) with
      | true ->
        let digits = l.Substring(key.Length + 1).Replace("kB", "").Trim()
        Some (int64 digits)
      | false -> None)

let cgroupLimit (file: string) =
  try
    match tryRead "/proc/self/cgroup" with
    | None -> None
    | Some text ->
      let line = text.Split('\n') |> Array.tryFind (fun l -> l.StartsWith("0::", StringComparison.Ordinal))
      match line with
      | None -> None
      | Some l ->
        let rel = l.Substring(3).Trim('/')
        // Walk up: the limit may sit on a parent of the scope this process is in.
        let rec up (path: string) =
          match tryRead (Path.Combine("/sys/fs/cgroup", path, file)) with
          | Some v when v <> "max" && not (v.StartsWith("max ", StringComparison.Ordinal)) -> Some (sprintf "%s (%s)" v (if path = "" then "root" else path))
          | _ ->
            match path with
            | "" -> None
            | _ -> up (match path.LastIndexOf('/') with | -1 -> "" | i -> path.Substring(0, i))
        up rel
  with _ -> None

let runCapture (exe: string) (args: string) : string =
  try
    let psi = ProcessStartInfo(exe, args, RedirectStandardOutput = true, RedirectStandardError = true)
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd()
    p.WaitForExit(15000) |> ignore
    out.Trim()
  with _ -> ""

/// The product's own calibration workload (SageFs.Core/MachineCalibration.fs), so the number
/// printed here is the number SageFs measures at start. The first run includes the JIT, which a
/// cold SageFs start pays too; the steady figure is the lowest of the later runs.
let calibrate () : float * float =
  let m = SageFs.MachineCalibration.measure ()
  m.FirstMs, m.SteadyMs

type MachineProfile =
  { Label: string
    Cpu: string
    LogicalCores: int
    PhysicalMemoryMb: int64
    AvailableMemoryMb: int64
    SwapMb: int64
    MaxMhz: string
    Governor: string
    Load1: string
    CgroupCpu: string
    CgroupMemory: string
    Os: string
    DotnetSdk: string
    CalibFirstMs: float
    CalibSteadyMs: float }

let profile () : MachineProfile =
  let first, steady = calibrate ()
  { Label = opts.Label
    Cpu = (cpuinfoField "model name" |> Option.defaultValue "unknown")
    LogicalCores = Environment.ProcessorCount
    PhysicalMemoryMb = (meminfoKb "MemTotal" |> Option.map (fun k -> k / 1024L) |> Option.defaultValue 0L)
    AvailableMemoryMb = (meminfoKb "MemAvailable" |> Option.map (fun k -> k / 1024L) |> Option.defaultValue 0L)
    SwapMb = (meminfoKb "SwapTotal" |> Option.map (fun k -> k / 1024L) |> Option.defaultValue 0L)
    MaxMhz = (tryRead "/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq" |> Option.map (fun k -> sprintf "%d" (int k / 1000)) |> Option.defaultValue "unknown")
    Governor = (tryRead "/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor" |> Option.defaultValue "unknown")
    Load1 = (tryRead "/proc/loadavg" |> Option.map (fun s -> s.Split(' ').[0]) |> Option.defaultValue "unknown")
    CgroupCpu = (cgroupLimit "cpu.max" |> Option.defaultValue "none")
    CgroupMemory = (cgroupLimit "memory.max" |> Option.defaultValue "none")
    Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription
    DotnetSdk = runCapture "dotnet" "--version"
    CalibFirstMs = first
    CalibSteadyMs = steady }

// ---- samples -----------------------------------------------------------------------

type RunKind =
  | Cold
  | Warm

let kindName = function Cold -> "cold" | Warm -> "warm"

/// stage -> kind -> samples
let samples = Dictionary<string * string, ResizeArray<float>>()
let failures = ResizeArray<string>()
let notes = ResizeArray<string>()

let record (kind: RunKind) (stage: string) (ms: float) =
  let key = kindName kind, stage
  match samples.TryGetValue key with
  | true, l -> l.Add ms
  | false, _ ->
    let l = ResizeArray<float>()
    l.Add ms
    samples.[key] <- l

let say (fmt: Printf.TextWriterFormat<'a>) = fprintfn stderr fmt

// ---- fixture -----------------------------------------------------------------------

let fixtureFiles : (string * string) list =
  [ // The fixture targets net10.0 and says so to the SDK too: on a machine that also has a newer
    // SDK, an unpinned build would load that SDK's MSBuild into a net10 SageFs, which refuses.
    "global.json",
    """{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }
"""
    "Bench/Bench.fsproj",
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Lib.fs" />
  </ItemGroup>
</Project>
"""
    "Bench/Lib.fs",
    """module Bench.Lib

let add (a: int) (b: int) = a + b

let greet (name: string) = sprintf "hello %s" name
"""
    "Bench.Tests/Bench.Tests.fsproj",
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Tests.fs" />
    <Compile Include="Program.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Expecto" Version="10.2.3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Bench/Bench.fsproj" />
  </ItemGroup>
</Project>
"""
    "Bench.Tests/Tests.fs",
    """module Bench.Tests.Tests

open Expecto

[<Tests>]
let tests =
  testList "Lib" [
    testCase "add adds" <| fun _ ->
      Expect.equal (Bench.Lib.add 2 3) 5 "2 + 3"
    testCase "greet greets" <| fun _ ->
      Expect.equal (Bench.Lib.greet "x") "hello x" "greeting"
  ]
"""
    "Bench.Tests/Program.fs",
    """module Bench.Tests.Program

open Expecto

[<EntryPoint>]
let main argv = runTestsInAssemblyWithCLIArgs [] argv
""" ]

let fixtureDir = Path.Combine(opts.Work, "fixture")
let testsProject = Path.Combine(fixtureDir, "Bench.Tests", "Bench.Tests.fsproj")

let writeFixture () =
  for rel, text in fixtureFiles do
    let path = Path.Combine(fixtureDir, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

/// `dotnet build` of the fixture, timed. It is not a SageFs stage; it is the cost of the user's
/// own build on this machine, which every other number sits beside.
let buildFixture () : float =
  let sw = Stopwatch.StartNew()
  let psi = ProcessStartInfo("dotnet", sprintf "build \"%s\" -nologo -v q" testsProject, WorkingDirectory = fixtureDir, RedirectStandardOutput = true, RedirectStandardError = true)
  psi.Environment.["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
  use p = Process.Start psi
  let out = p.StandardOutput.ReadToEndAsync()
  let err = p.StandardError.ReadToEndAsync()
  p.WaitForExit()
  match p.ExitCode with
  | 0 -> sw.Elapsed.TotalMilliseconds
  | code -> failwithf "fixture build failed (%d):\n%s\n%s" code out.Result err.Result

// ---- daemon ------------------------------------------------------------------------

let http = new HttpClient(Timeout = TimeSpan.FromMinutes 15.0)

let baseUrl = sprintf "http://localhost:%d" opts.Port

let getJson (path: string) : JsonDocument option =
  try
    let r = http.GetAsync(baseUrl + path).Result
    match r.IsSuccessStatusCode with
    | true -> Some (JsonDocument.Parse(r.Content.ReadAsStringAsync().Result))
    | false -> None
  with _ -> None

let postJson (path: string) (body: string) : int * string =
  try
    let r = http.PostAsync(baseUrl + path, new StringContent(body, Encoding.UTF8, "application/json")).Result
    int r.StatusCode, r.Content.ReadAsStringAsync().Result
  with ex -> -1, ex.Message

type Daemon =
  { Proc: Process
    DataDir: string
    OutFile: string
    ErrFile: string }

let startDaemon (dataDir: string) (hostCache: string) : Daemon =
  Directory.CreateDirectory dataDir |> ignore
  Directory.CreateDirectory hostCache |> ignore
  let dll = Path.Combine(opts.SageFsDir, "SageFs.dll")
  let exe, args =
    match opts.Nice with
    | true -> "nice", sprintf "-n 19 dotnet \"%s\" --mcp-port %d --ttl 30m" dll opts.Port
    | false -> "dotnet", sprintf "\"%s\" --mcp-port %d --ttl 30m" dll opts.Port
  let psi = ProcessStartInfo(exe, args, WorkingDirectory = fixtureDir, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true)
  // Nothing of the caller's SageFs configuration may leak in: it would change what is measured.
  for k in psi.Environment.Keys |> Seq.toList do
    if k.StartsWith("SAGEFS_", StringComparison.Ordinal) then psi.Environment.Remove k |> ignore
  psi.Environment.["SAGEFS_DATA_DIR"] <- dataDir
  psi.Environment.["SAGEFS_HOST_CACHE_DIR"] <- hostCache
  psi.Environment.["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
  for k, v in opts.Env do psi.Environment.[k] <- v
  let outFile = Path.Combine(dataDir, "daemon.stdout.txt")
  let errFile = Path.Combine(dataDir, "daemon.stderr.txt")
  let outW = new StreamWriter(outFile, false, AutoFlush = true)
  let errW = new StreamWriter(errFile, false, AutoFlush = true)
  let p = new Process(StartInfo = psi)
  p.OutputDataReceived.Add(fun e -> match e.Data with | null -> () | d -> lock outW (fun () -> outW.WriteLine d))
  p.ErrorDataReceived.Add(fun e -> match e.Data with | null -> () | d -> lock errW (fun () -> errW.WriteLine d))
  p.Start() |> ignore
  p.BeginOutputReadLine()
  p.BeginErrorReadLine()
  { Proc = p; DataDir = dataDir; OutFile = outFile; ErrFile = errFile }

/// Ask the daemon to end (SIGTERM, so it flushes its log and writes its manifest, which is what
/// the evidence is read from), then kill the whole tree if it has not ended in 20 seconds.
let stopDaemon (d: Daemon) =
  try
    match d.Proc.HasExited with
    | true -> ()
    | false ->
      (match OperatingSystem.IsWindows() with
       | true -> ()
       | false ->
         // `nice` execs dotnet, so the pid is the daemon's either way.
         use k = Process.Start(ProcessStartInfo("kill", sprintf "-TERM %d" d.Proc.Id))
         k.WaitForExit())
      match d.Proc.WaitForExit(20000) with
      | true -> ()
      | false ->
        d.Proc.Kill(true)
        d.Proc.WaitForExit(15000) |> ignore
  with _ -> ()

// ---- peak memory (Linux) -------------------------------------------------------------

/// The pids under `root`, from /proc, with their command lines. Linux only; elsewhere empty.
let processTree (root: int) : (int * string) list =
  match Directory.Exists "/proc/self" with
  | false -> []
  | true ->
    let children = Dictionary<int, ResizeArray<int>>()
    let cmd = Dictionary<int, string>()
    for dir in Directory.GetDirectories "/proc" do
      match Int32.TryParse(Path.GetFileName dir) with
      | true, pid ->
        match tryRead (Path.Combine(dir, "stat")) with
        | Some stat ->
          let close = stat.LastIndexOf ')'
          let after = stat.Substring(close + 2).Split(' ')
          let ppid = int after.[1]
          (match children.TryGetValue ppid with
           | true, l -> l.Add pid
           | false, _ ->
             let l = ResizeArray<int>()
             l.Add pid
             children.[ppid] <- l)
          cmd.[pid] <- (try File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\000', ' ') with _ -> "")
        | None -> ()
      | _ -> ()
    let rec collect pid =
      pid
      :: (match children.TryGetValue pid with
          | true, l -> l |> Seq.toList |> List.collect collect
          | false, _ -> [])
    collect root |> List.map (fun pid -> pid, (match cmd.TryGetValue pid with | true, c -> c | false, _ -> ""))

let peakRssMb (pid: int) : float option =
  match tryRead (sprintf "/proc/%d/status" pid) with
  | None -> None
  | Some text ->
    text.Split('\n')
    |> Array.tryPick (fun l ->
      match l.StartsWith("VmHWM:", StringComparison.Ordinal) with
      | true -> Some (float (l.Substring(6).Replace("kB", "").Trim() |> int64) / 1024.0)
      | false -> None)

// ---- worker log --------------------------------------------------------------------

let tsRegex = Regex(@"^(\d{4}-\d\d-\d\dT[\d:.]+Z) \[(\w+)\] (.*)$", RegexOptions.Compiled)

type LogLine = { At: DateTimeOffset; Text: string }

let readWorkerLog (dataDir: string) (sid: string) : LogLine list =
  let path = Path.Combine(dataDir, "workers", sid + ".log")
  match File.Exists path with
  | false -> []
  | true ->
    use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
    use r = new StreamReader(fs)
    r.ReadToEnd().Split('\n')
    |> Array.choose (fun l ->
      let m = tsRegex.Match(l.TrimEnd('\r'))
      match m.Success with
      | true -> Some { At = DateTimeOffset.Parse(m.Groups.[1].Value, null, Globalization.DateTimeStyles.AssumeUniversal); Text = m.Groups.[3].Value }
      | false -> None)
    |> Array.toList

let findLog (lines: LogLine list) (needle: string) =
  lines |> List.tryFind (fun l -> l.Text.Contains(needle, StringComparison.Ordinal))

let msBetween (a: DateTimeOffset) (b: DateTimeOffset) = (b - a).TotalMilliseconds

// ---- one run -----------------------------------------------------------------------

let jsonString (el: JsonElement) (name: string) =
  match el.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
  | _ -> ""

let sessionsSnapshot () : (string * string * string) list =
  match getJson "/api/sessions" with
  | None -> []
  | Some doc ->
    use d = doc
    match d.RootElement.TryGetProperty "sessions" with
    | true, arr ->
      [ for s in arr.EnumerateArray() -> jsonString s "id", jsonString s "status", jsonString s "faultReason" ]
    | _ -> []

let awaitHealth (sw: Stopwatch) (cap: TimeSpan) : bool =
  let start = sw.Elapsed
  let mutable up = false
  while not up && sw.Elapsed - start < cap do
    match getJson "/health" with
    | Some _ -> up <- true
    | None -> Threading.Thread.Sleep 100
  up

type ReadyOutcome =
  | ReachedReady of sid: string
  | NeverReady of status: string * reason: string

/// Poll the daemon's session list until the one session is Ready, Faulted, or the cap passes.
let awaitReady (cap: TimeSpan) : ReadyOutcome =
  let sw = Stopwatch.StartNew()
  let mutable outcome = None
  while outcome.IsNone && sw.Elapsed < cap do
    match sessionsSnapshot () with
    | [ sid, "Ready", _ ] -> outcome <- Some (ReachedReady sid)
    | [ _, ("Faulted" as s), reason ] -> outcome <- Some (NeverReady (s, reason))
    | _ -> Threading.Thread.Sleep 200
  match outcome with
  | Some o -> o
  | None ->
    let last = sessionsSnapshot () |> List.tryHead |> Option.map (fun (_, s, r) -> s, r) |> Option.defaultValue ("no session", "")
    NeverReady (fst last, sprintf "still %s after %.0fs: %s" (fst last) cap.TotalSeconds (snd last))

/// After a request that restarts the session: wait for it to leave Ready (it may never be seen
/// leaving, if the restart was quicker than the poll), then for it to be Ready again.
let awaitCycle (cap: TimeSpan) : ReadyOutcome =
  let sw = Stopwatch.StartNew()
  let mutable left = false
  while not left && sw.Elapsed < TimeSpan.FromSeconds 15.0 do
    match sessionsSnapshot () with
    | [ _, "Ready", _ ] -> Threading.Thread.Sleep 50
    | _ -> left <- true
  awaitReady cap

let timed (f: unit -> 'a) : 'a * float =
  let sw = Stopwatch.StartNew()
  let r = f ()
  r, sw.Elapsed.TotalMilliseconds

let evalCode (code: string) : int * string =
  let body = JsonSerializer.Serialize({| code = code; working_directory = fixtureDir |})
  postJson "/exec" body

/// What /api/live-testing/status says: how many tests it has found, and whether discovery has
/// finished (so a project with no tests ends the wait instead of running out the cap).
let ltStatus () : int * bool =
  match getJson "/api/live-testing/status" with
  | None -> 0, false
  | Some doc ->
    use d = doc
    let root = d.RootElement
    let total =
      match root.TryGetProperty "Summary" with
      | true, s ->
        match s.TryGetProperty "Total" with
        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
        | _ -> 0
      | _ -> 0
    let state = jsonString root "DiscoveryState"
    total, (state = "ready_zero_tests" || state = "ready_with_tests")

let runOnce (kind: RunKind) (index: int) (hostCache: string) =
  let tag = sprintf "%s-%d" (kindName kind) index
  say "[%s] daemon start" tag
  let dataDir = Path.Combine(opts.Work, "data-" + tag)
  let swAll = Stopwatch.StartNew()
  let daemon = startDaemon dataDir hostCache
  try
    let up = awaitHealth swAll (TimeSpan.FromSeconds 120.0)
    match up with
    | false -> failures.Add(sprintf "[%s] daemon never answered /health within 120s" tag)
    | true ->
      record kind "daemon_up" swAll.Elapsed.TotalMilliseconds
      let createdUtc = DateTimeOffset.UtcNow
      let body = JsonSerializer.Serialize({| workingDirectory = fixtureDir; projects = [ testsProject ] |})
      let (code, resp), createMs = timed (fun () -> postJson "/api/sessions/create" body)
      record kind "create_request" createMs
      match code with
      | 200 ->
        let swReady = Stopwatch.StartNew()
        let outcome = awaitReady opts.ReadyCap
        let readyMs = createMs + swReady.Elapsed.TotalMilliseconds
        match outcome with
        | NeverReady (status, reason) ->
          failures.Add(sprintf "[%s] session did not reach Ready: %s (%s)" tag status reason)
          say "[%s] NOT READY: %s %s" tag status reason
        | ReachedReady sid ->
          record kind "ready_total" readyMs
          // What the worker says it spent, from its own log.
          let log = readWorkerLog dataDir sid
          let attempts = log |> List.filter (fun l -> l.Text.Contains("Creating FSI session", StringComparison.Ordinal)) |> List.length
          record kind "fsi_creation_attempts" (float attempts)
          let first = log |> List.tryHead
          let creating = findLog log "Creating FSI session"
          let created = findLog log "FSI session created in"
          let warm = findLog log "Warm-up complete in"
          (match first with
           | Some f -> record kind "worker_boot" (max 0.0 (msBetween createdUtc f.At))
           | None -> ())
          // The worker logs nothing between the coverage step and its welcome line, and that is where
          // a cold start builds the FSI host, so the gap is its own stage.
          let coverage = findLog log "IL coverage:"
          let welcome = findLog log "Welcome to SageFs!"
          (match first, coverage with
           | Some f, Some c -> record kind "project_load" (msBetween f.At c.At)
           | _ -> ())
          (match coverage, welcome with
           | Some c, Some w -> record kind "host_prep" (msBetween c.At w.At)
           | _ -> ())
          (match created with
           | Some c ->
             let m = Regex.Match(c.Text, @"created in (\d+)ms")
             if m.Success then record kind "fsi_session_create" (float m.Groups.[1].Value)
           | None -> ())
          (match warm with
           | Some w ->
             let m = Regex.Match(w.Text, @"Warm-up complete in (\d+)ms")
             (match created, m.Success with
              | Some c, true -> record kind "warmup_after_session" (msBetween c.At w.At)
              | _ -> ())
           | None -> ())
          // Evals, as a client sees them.
          let (_, e1) = timed (fun () -> evalCode "1 + 1;;")
          record kind "eval_first" e1
          let (_, e2) = timed (fun () -> evalCode "2 + 2;;")
          record kind "eval_second" e2
          let (_, e3) = timed (fun () -> evalCode "let (x: int) = \"a\";;")
          record kind "eval_type_error" e3
          let (r4, e4) = timed (fun () -> evalCode "Bench.Lib.add 1 2;;")
          record kind "eval_project_call" e4
          (match r4 with
           | 200, _ -> ()
           | c, t -> notes.Add(sprintf "[%s] eval of Bench.Lib.add answered %d: %s" tag c (t.Substring(0, min 120 t.Length))))
          // Live testing: first discovery, first run.
          (match opts.SkipLt with
           | true -> ()
           | false ->
             let swLt = Stopwatch.StartNew()
             postJson "/api/live-testing/enable" "{}" |> ignore
             let mutable found = 0
             let mutable finished = false
             while not finished && swLt.Elapsed < opts.ReadyCap do
               let total, done' = ltStatus ()
               found <- total
               finished <- done'
               if not finished then Threading.Thread.Sleep 200
             (match found with
              | 0 -> notes.Add(sprintf "[%s] live testing found no tests within the cap" tag)
              | _ ->
                record kind "lt_first_discovery" swLt.Elapsed.TotalMilliseconds
                let swRun = Stopwatch.StartNew()
                let rc, rt = postJson "/api/live-testing/run" "{}"
                (match rc with
                 | 200 | 202 ->
                   // The request returns at once; the run is done when every discovered test has a verdict.
                   let mutable verdicts = false
                   while not verdicts && swRun.Elapsed < opts.ReadyCap do
                     (match getJson "/api/live-testing/status" with
                      | Some doc ->
                        use d = doc
                        (match d.RootElement.TryGetProperty "Summary" with
                         | true, s ->
                           let n (name: string) = match s.TryGetProperty name with | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32() | _ -> 0
                           verdicts <- n "Passed" + n "Failed" >= found && n "Running" = 0
                         | _ -> ())
                      | None -> ())
                     if not verdicts then Threading.Thread.Sleep 100
                   (match verdicts with
                    | true -> record kind "lt_first_run" swRun.Elapsed.TotalMilliseconds
                    | false -> notes.Add(sprintf "[%s] live-testing run produced no verdicts within the cap" tag))
                 | c -> notes.Add(sprintf "[%s] live-testing run answered %d: %s" tag c (rt.Substring(0, min 120 rt.Length))))))
          // Peak memory of the daemon, worker and host, while the session is up.
          let tree = processTree daemon.Proc.Id
          let pick (needle: string) =
            tree |> List.filter (fun (_, c) -> c.Contains(needle, StringComparison.Ordinal)) |> List.choose (fst >> peakRssMb) |> List.sortDescending |> List.tryHead
          (match pick "SageFs.dll" with Some v -> record kind "peak_rss_mb_daemon" v | None -> ())
          (match pick "SageFs.Host.dll" with Some v -> record kind "peak_rss_mb_worker" v | None -> ())
          (match pick "FsiHost.dll" with Some v -> record kind "peak_rss_mb_fsihost" v | None -> ())
          // Restart: a hard reset keeps the host; a rebuild reset also builds.
          (match opts.SkipReset with
           | true -> ()
           | false ->
             let swReset = Stopwatch.StartNew()
             let rc, rbody = postJson "/hard-reset" "{\"rebuild\":false}"
             (match rc with
              | 200 ->
                (match awaitCycle opts.ReadyCap with
                 | ReachedReady _ -> record kind "hard_reset" swReset.Elapsed.TotalMilliseconds
                 | NeverReady (s, r) -> failures.Add(sprintf "[%s] hard reset never returned to Ready: %s %s" tag s r))
              | c -> notes.Add(sprintf "[%s] hard-reset answered %d: %s" tag c (rbody.Substring(0, min 160 rbody.Length))))
             let swRebuild = Stopwatch.StartNew()
             let rc2, rbody2 = postJson "/hard-reset" "{\"rebuild\":true}"
             (match rc2 with
              | 200 ->
                (match awaitCycle opts.ReadyCap with
                 | ReachedReady _ -> record kind "hard_reset_rebuild" swRebuild.Elapsed.TotalMilliseconds
                 | NeverReady (s, r) -> failures.Add(sprintf "[%s] rebuild reset never returned to Ready: %s %s" tag s r))
              | c -> notes.Add(sprintf "[%s] hard-reset rebuild answered %d: %s" tag c (rbody2.Substring(0, min 160 rbody2.Length)))))
          let (_, stopMs) = timed (fun () -> postJson "/api/sessions/stop" (JsonSerializer.Serialize({| sessionId = sid |})))
          record kind "session_stop" stopMs
      | c -> failures.Add(sprintf "[%s] create answered %d: %s" tag c resp)
  finally
    stopDaemon daemon
    say "[%s] done in %.0fs" tag swAll.Elapsed.TotalSeconds

// ---- report ------------------------------------------------------------------------

let median (xs: float list) =
  let s = List.sort xs
  let n = List.length s
  match n with
  | 0 -> nan
  | _ when n % 2 = 1 -> s.[n / 2]
  | _ -> (s.[n / 2 - 1] + s.[n / 2]) / 2.0

let stageOrder =
  [ "daemon_up", "daemon start to /health"
    "create_request", "POST create returns"
    "worker_boot", "create to the worker's first log line"
    "project_load", "worker first line to the end of the coverage step (MSBuild project load, shadow copies)"
    "host_prep", "coverage step to the worker's welcome line (a cold start builds the FSI host here)"
    "fsi_session_create", "FSI session creation (the host process start), as the worker logs it"
    "warmup_after_session", "FSI session created to warm-up complete"
    "ready_total", "create to Ready (what the user waits for)"
    "fsi_creation_attempts", "times the worker logged 'Creating FSI session' (more than 1 is a retry)"
    "eval_first", "first eval"
    "eval_second", "second eval"
    "eval_type_error", "eval that fails type checking"
    "eval_project_call", "eval that calls the loaded project"
    "lt_first_discovery", "live testing enabled to first discovered test"
    "lt_first_run", "first live-testing run, request to every test having a verdict"
    "hard_reset", "hard reset to Ready"
    "hard_reset_rebuild", "hard reset with rebuild to Ready"
    "session_stop", "stop session"
    "peak_rss_mb_daemon", "peak RSS MB, daemon"
    "peak_rss_mb_worker", "peak RSS MB, worker"
    "peak_rss_mb_fsihost", "peak RSS MB, FSI host" ]

let printReport (p: MachineProfile) (fixtureBuildMs: float) =
  printfn ""
  printfn "== machine profile =="
  printfn "label            %s" (match p.Label with "" -> "(none)" | l -> l)
  printfn "cpu              %s" p.Cpu
  printfn "logical cores    %d (what .NET sees: affinity and cgroup applied)" p.LogicalCores
  printfn "max MHz          %s   governor %s   load(1m) at start %s" p.MaxMhz p.Governor p.Load1
  printfn "memory           %d MB total, %d MB available, %d MB swap" p.PhysicalMemoryMb p.AvailableMemoryMb p.SwapMb
  printfn "cgroup cpu.max   %s" p.CgroupCpu
  printfn "cgroup memory    %s" p.CgroupMemory
  printfn "os               %s" p.Os
  printfn "dotnet sdk       %s" p.DotnetSdk
  printfn "calibration      first run %.0f ms (includes JIT), steady %.0f ms (fixed single-thread workload)" p.CalibFirstMs p.CalibSteadyMs
  printfn "fixture build    %.0f ms (dotnet build of the two-project fixture)" fixtureBuildMs
  printfn "sagefs           %s" opts.SageFsDir
  printfn "daemon env       %s" (match opts.Env with [] -> "(defaults)" | e -> e |> List.map (fun (k, v) -> k + "=" + v) |> String.concat " ")
  for kind in [ Cold; Warm ] do
    printfn ""
    printfn "== %s runs (milliseconds) ==" (kindName kind)
    printfn "%-22s %3s %9s %9s %9s   %s" "stage" "n" "p50" "min" "max" "what"
    for stage, what in stageOrder do
      match samples.TryGetValue((kindName kind, stage)) with
      | true, l ->
        let xs = l |> Seq.toList
        printfn "%-22s %3d %9.0f %9.0f %9.0f   %s" stage xs.Length (median xs) (List.min xs) (List.max xs) what
      | false, _ -> printfn "%-22s %3d %9s %9s %9s   %s" stage 0 "-" "-" "-" what
  printfn ""
  printfn "== failures (%d) ==" failures.Count
  for f in failures do printfn "%s" f
  printfn "== notes (%d) ==" notes.Count
  for n in notes do printfn "%s" n

// ---- main --------------------------------------------------------------------------

let main () =
  Directory.CreateDirectory opts.Work |> ignore
  say "work dir: %s" opts.Work
  let p = profile ()
  writeFixture ()
  say "building the fixture"
  let fixtureBuildMs = buildFixture ()
  // Cold runs: every one starts with an empty FSI host cache, so the host is built from scratch.
  for i in 1 .. opts.ColdRuns do
    runOnce Cold i (Path.Combine(opts.Work, sprintf "hosts-cold-%d" i))
  // Warm runs share one host cache, the one the first cold run left behind (or a fresh one).
  let warmHosts =
    match opts.ColdRuns with
    | 0 -> Path.Combine(opts.Work, "hosts-warm")
    | _ -> Path.Combine(opts.Work, "hosts-cold-1")
  for i in 1 .. opts.Runs do
    runOnce Warm i warmHosts
  printReport p fixtureBuildMs
  match opts.Out with
  | "" -> ()
  | path ->
    let payload =
      {| profile = p
         samples = samples |> Seq.map (fun kv -> {| kind = fst kv.Key; stage = snd kv.Key; ms = kv.Value |> Seq.toList |}) |> Seq.toList
         failures = failures |> Seq.toList
         notes = notes |> Seq.toList |}
    File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonSerializerOptions(WriteIndented = true)))
    say "raw samples written to %s" path

try
  main ()
finally
  match opts.Keep with
  | true -> say "kept %s" opts.Work
  | false ->
    try Directory.Delete(opts.Work, true) with _ -> ()
