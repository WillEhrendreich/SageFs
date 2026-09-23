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
