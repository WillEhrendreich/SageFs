/// The one place that knows which environment variables poison a spawned process.
///
/// Loading a project in THIS process (Ionide.ProjInfo, used to classify a session's
/// projects) sets MSBuild's own resolution variables on the process itself —
/// `MSBUILD_EXE_PATH`, `MSBuildSDKsPath`, `MSBuildExtensionsPath*`,
/// `DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR`, `DOTNET_HOST_PATH`, `DOTNET_ROOT`,
/// `DOTNET_ROOT(x86)`. Every child process inherits them by default, and a child
/// `dotnet` then loads THAT MSBuild and THOSE targets no matter which `dotnet` it
/// was told to run — a repo-local SDK silently loses to whichever one loaded a
/// project first. Two real failures from this: a user's own `dotnet build` of a
/// small fixture, run from inside a session, failed with MSB4242 (SDK 11's
/// MSBuild loaded on a net10 runtime); and SageFs's own FSI host build ignored a
/// repo-local SDK pin for the same reason.
///
/// `FsiHostBuild.fs` carried its own private copy of this list before this module
/// existed. Every process SageFs spawns — the worker, the isolated FSI host the
/// user's own code runs in, a session's `dotnet build` — uses this one instead.
module SageFs.ProcessEnvironment

open System
open System.Collections
open System.Diagnostics

/// The variable names to strip from every spawned process's environment, unless
/// the caller deliberately wants one back (an explicit override wins — see
/// `sanitize`). Add a newly-discovered poisoner here, not at a call site.
let poisonedVariables : string list =
  [ "MSBUILD_EXE_PATH"
    "MSBuildSDKsPath"
    "MSBuildExtensionsPath"
    "MSBuildExtensionsPath32"
    "MSBuildExtensionsPath64"
    "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"
    "DOTNET_HOST_PATH"
    "DOTNET_ROOT"
    "DOTNET_ROOT(x86)" ]

/// Pure: what a child process's environment should be, given the parent's own
/// environment and whatever explicit overrides this particular child needs.
/// Poisoned keys are dropped first, then overrides are applied on top — so a
/// caller that genuinely wants one of them back for this one child (the FSI
/// host build pointing `DOTNET_ROOT` at a repo-local SDK) still can.
let sanitize (parentEnvironment: Map<string, string>) (overrides: (string * string) list) : Map<string, string> =
  let cleaned = poisonedVariables |> List.fold (fun env key -> Map.remove key env) parentEnvironment
  overrides |> List.fold (fun env (key, value) -> Map.add key value env) cleaned

/// Apply `sanitize` to a real `ProcessStartInfo`: remove every poisoned key the
/// process would otherwise inherit, then set `overrides`. Every spawn site
/// should call this instead of writing straight to `psi.Environment`.
let applyTo (psi: ProcessStartInfo) (overrides: (string * string) seq) : unit =
  for key in poisonedVariables do
    psi.Environment.Remove key |> ignore
  for key, value in overrides do
    psi.Environment[key] <- value

/// The environment variables an external tool has asked to have forwarded into
/// every process SageFs spawns.
///
/// A tool that needs to affect a worker it does not launch — a profiler, a
/// tracer, a deterministic execution or fault-injection agent — has no seam
/// today: it cannot reach `psi.Environment`, and the worker's variables are a
/// closed, hand-maintained list in `Args.buildWorkerSpawnConfig`. This is that
/// seam, and it is deliberately the smallest thing that can work: forward the
/// variables whose name starts with a prefix a tool declares, and nothing else.
///
/// The prefixes are read from the SageFs process's own environment, so a tool
/// opts in by being on the machine when the daemon starts, and opts out by not
/// being there. SageFs does not interpret, validate, or own any of it: the
/// variables are opaque strings, and a tool that wants its own is the only
/// thing that can add one.
///
/// Declared as `SAGEFS_FORWARD_PREFIXES`, a `;`-separated (Windows) or `:`
/// -separated (POSIX) list. Both separators are always accepted so a single
/// declaration works on either platform.
let forwardPrefixesEnvVar = "SAGEFS_FORWARD_PREFIXES"

let splitPrefixes (raw: string) : string list =
  if String.IsNullOrWhiteSpace raw then
    []
  else
  raw
    .Split([| ';'; ':' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun p -> p.Trim())
    |> Array.filter (fun p -> p.Length > 0)
    |> Array.toList

/// The prefixes the current process was asked to forward.
let forwardPrefixes () : string list =
  Environment.GetEnvironmentVariable forwardPrefixesEnvVar |> splitPrefixes

/// From a parent environment, the variables a tool asked to forward. A variable
/// is forwarded only if its name starts with one of `prefixes` AND it is not a
/// poisoned key — the poison list stays authoritative, because a forwarded
/// `DOTNET_ROOT` would reintroduce exactly the SDK-pinning bug above.
///
/// Pure, so a caller can reason about the result without spawning anything.
let forwarded
  (parentEnvironment: Map<string, string>)
  (overrides: (string * string) list)
  (prefixes: string list)
  : (string * string) list =
  if List.isEmpty prefixes then
    []
  else
  let poisoned = poisonedVariables |> Set.ofList
  let wanted =
    parentEnvironment
    |> Map.toList
    |> List.filter (fun (key, _) ->
      not (Set.contains key poisoned)
      && prefixes |> List.exists (fun p -> key.StartsWith(p, StringComparison.Ordinal)))

  // An explicit override wins over an inherited value, exactly as in `sanitize`.
  let explicitKeys = overrides |> List.map fst |> Set.ofList
  let inherited = wanted |> List.filter (fun (key, _) -> not (Set.contains key explicitKeys))
  inherited @ List.filter (fun (k, _) -> prefixes |> List.exists (fun p -> k.StartsWith(p, StringComparison.Ordinal))) overrides

/// Read the forwarding prefixes from the environment and return the variables to
/// add to a spawn. Kept separate from `forwarded` so the pure part stays pure.
let forwardingFor (parentEnvironment: Map<string, string>) (overrides: (string * string) list) : (string * string) list =
  forwarded parentEnvironment overrides (forwardPrefixes ())

/// `applyTo`, plus anything a tool asked to forward. The environment a child
/// already inherits is not read back out of `psi` (which would be lossy on some
/// platforms); the caller's own process environment is the parent of record.
let applyToWithForwarding
  (psi: ProcessStartInfo)
  (overrides: (string * string) seq)
  : unit =
  applyTo psi overrides
  let parent =
    Environment.GetEnvironmentVariables()
    |> Seq.cast<DictionaryEntry>
    |> Seq.map (fun (e: DictionaryEntry) -> string e.Key, string e.Value)
    |> Map.ofSeq
  forwardingFor parent (List.ofSeq overrides)
  |> List.iter (fun (key, value) -> psi.Environment[key] <- value)
