/// Builds the isolated FSI host (SageFs.FsiHost/) with the PROJECT'S OWN SDK and caches the result.
///
/// The host is compiled by that SDK's fsc against that SDK's FSharp.Compiler.Service and FSharp.Core, so the
/// FSI session that runs a user's code is exactly the SDK's FSI and shares no assembly with SageFs. A host is
/// built once per (SDK version, exact host sources) and cached under `<cacheRoot>/<key>/`; building takes a
/// couple of seconds. The sources are embedded in this assembly, so there is no path to find or package.
module SageFs.FsiHostBuild

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Threading
open SageFs.ProcessEnvironment

/// The files that make up the host project, in the order they are written.
let hostSourceNames =
  [ "FsiHost.fsproj"
    "Measures.fs"
    "Utils.fs"
    "FsiNaming.fs"
    "Timeouts.fs"
    "Instrumentation.fs"
    "DirectoryConfigTypes.fs"
    "ConfigDsl.fs"
    "LiveValueTree.fs"
    "LiveTestingTypes.fs"
    "CoverageProbes.fs"
    "LiveTestingInstrumentation.fs"
    "ReflectionDiscovery.fs"
    "LiveTestingExecutors.fs"
    "DevReload.fs"
    "ValueReads.fs"
    "ValueReadTracking.fs"
    "HotReloadCore.fs"
    "HostAgent.fs"
    "FsiProtocol.fs"
    "FcsQueries.fs"
    "Program.fs" ]

/// Whether a host had to be built now or was already in the cache. The path is the host's entry assembly.
type HostBuild =
  | Built of dll: string
  | Reused of dll: string

/// Why running an external process failed.
type ProcessFailure =
  | CouldNotStart of file: string * detail: string
  | TimedOut of command: string * timeoutMs: int
  | ExitedWith of command: string * exitCode: int * output: string

let describeProcessFailure (failure: ProcessFailure) : string =
  match failure with
  | CouldNotStart(file, detail) -> sprintf "could not run `%s`: %s" file detail
  | TimedOut(command, timeoutMs) -> sprintf "`%s` did not finish within %d ms" command timeoutMs
  | ExitedWith(command, exitCode, output) -> sprintf "`%s` exited with %d:\n%s" command exitCode output

/// Why a host could not be built or found.
type HostBuildError =
  | EmbeddedSourceMissing of name: string
  | SdkUnavailable of workingDir: string * failure: ProcessFailure
  | BuildLockUnavailable of path: string * detail: string
  | BuildFailed of sdkVersion: string * failure: ProcessFailure
  | BuildOutputMissing of dll: string
  | HarmonyUnavailable of path: string * detail: string

/// The one place a build error becomes text; every case says what to do about it where the user can act.
let describeBuildError (error: HostBuildError) : string =
  match error with
  | EmbeddedSourceMissing name -> sprintf "the FSI host source '%s' is not embedded in SageFs.Core (a broken SageFs build)" name
  | SdkUnavailable(workingDir, failure) ->
    sprintf
      "Could not determine the .NET SDK for %s: %s\nInstall the .NET SDK from https://dotnet.microsoft.com/download or fix the SDK version in global.json."
      workingDir
      (describeProcessFailure failure)
  | BuildLockUnavailable(path, detail) -> sprintf "could not take the FSI host build lock %s: %s" path detail
  | BuildFailed(sdkVersion, failure) ->
    sprintf "Building the FSI host with .NET SDK %s failed. Make sure that SDK is installed.\n%s" sdkVersion (describeProcessFailure failure)
  | BuildOutputMissing dll -> sprintf "the FSI host build succeeded but %s is missing" dll
  | HarmonyUnavailable(path, detail) ->
    sprintf "the FSI host's agent needs SageFs's Harmony (%s), which could not be prepared: %s (a broken SageFs install: reinstall the tool)" path detail

/// Pure: the cache directory name for an SDK version and the exact host sources. Any change to either changes it.
let cacheKey (sdkVersion: string) (sources: (string * string) list) : string =
  let material =
    sources
    |> List.sortBy fst
    |> List.map (fun (name, content) -> name + "\n" + content)
    |> String.concat "\u0000"
  let hash = SHA256.HashData(Encoding.UTF8.GetBytes(sdkVersion + "\u0001" + material))
  sprintf "sdk-%s-%s" sdkVersion (Convert.ToHexString(hash).Substring(0, 12).ToLowerInvariant())

/// Pure: the global.json that pins the build to exactly one SDK (no roll-forward, previews allowed).
let globalJson (sdkVersion: string) : string =
  sprintf """{"sdk":{"version":"%s","rollForward":"disable","allowPrerelease":true}}""" sdkVersion

/// The assembly identity the host's Harmony carries. It is NOT `0Harmony`: a project that references Lib.Harmony must never
/// collide with the agent's own copy, so the agent's is a differently-named assembly of the same build.
[<Literal>]
let HostHarmonyName = "SageFs.HostHarmony"

/// Pure over bytes: the same assembly under another simple name. Types and namespaces are untouched (`HarmonyLib.*`), so
/// source compiled against it is identical; only the identity the loader binds by changes.
let renameAssembly (newName: string) (source: byte[]) : byte[] =
  use input = new MemoryStream(source)
  use assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly input
  assembly.Name.Name <- newName
  assembly.MainModule.Name <- newName + ".dll"
  use output = new MemoryStream()
  assembly.Write output
  output.ToArray()

/// SageFs's Harmony, renamed for the host: (its bytes, a hash that identifies them for the cache key).
let hostHarmony () : Result<byte[] * string, HostBuildError> =
  let path = typeof<HarmonyLib.Harmony>.Assembly.Location
  try
    let renamed = renameAssembly HostHarmonyName (File.ReadAllBytes path)
    Ok(renamed, Convert.ToHexString(SHA256.HashData renamed).ToLowerInvariant())
  with ex ->
    Error(HarmonyUnavailable(path, ex.Message))

let private readEmbedded (name: string) : Result<string, HostBuildError> =
  match Assembly.GetExecutingAssembly().GetManifestResourceStream("FsiHost/" + name) with
  | null -> Error(EmbeddedSourceMissing name)
  | stream ->
    use stream = stream
    use reader = new StreamReader(stream, Encoding.UTF8)
    Ok(reader.ReadToEnd())

/// The embedded host sources as (file name, content).
let embeddedSources () : Result<(string * string) list, HostBuildError> =
  hostSourceNames
  |> List.fold
       (fun acc name ->
         acc |> Result.bind (fun sources -> readEmbedded name |> Result.map (fun content -> (name, content) :: sources)))
       (Ok [])
  |> Result.map List.rev

let private runCaptureWith
  (environment: (string * string) list)
  (file: string)
  (arguments: string list)
  (workingDir: string)
  (timeoutMs: int)
  : Result<string, ProcessFailure> =
  let command = file + " " + String.concat " " arguments
  let psi = ProcessStartInfo(file)
  for argument in arguments do
    psi.ArgumentList.Add argument
  psi.WorkingDirectory <- workingDir
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  // See SageFs.ProcessEnvironment for why this scrub exists and what it strips
  // (Ionide.ProjInfo pins MSBuild's own resolution variables on this process;
  // every child would otherwise inherit them and load the wrong SDK's MSBuild).
  applyTo psi (("DOTNET_NOLOGO", "1") :: ("DOTNET_CLI_TELEMETRY_OPTOUT", "1") :: environment)
  try
    use proc = Process.Start psi
    // Drain both pipes concurrently or a chatty child deadlocks before it exits.
    let stdout' = proc.StandardOutput.ReadToEndAsync()
    let stderr' = proc.StandardError.ReadToEndAsync()
    match proc.WaitForExit timeoutMs with
    | false ->
      (try proc.Kill true with _ -> ())
      Error(TimedOut(command, timeoutMs))
    | true ->
      let output = stdout'.Result + stderr'.Result
      match proc.ExitCode with
      | 0 -> Ok output
      | code -> Error(ExitedWith(command, code, output))
  with ex ->
    Error(CouldNotStart(file, ex.Message))

/// The same, with nothing added to the environment.
let private runCapture (file: string) (arguments: string list) (workingDir: string) (timeoutMs: int) : Result<string, ProcessFailure> =
  runCaptureWith [] file arguments workingDir timeoutMs

/// Which SDK builds the host, and which dotnet owns it. They are not always the
/// same install: an Arcade repo (dotnet/fsharp, dotnet/runtime, most of
/// dotnet/*) ships its SDK inside the checkout and points global.json at it
/// with `"paths": [".dotnet", "$host$"]`. Resolving the version from the
/// project's directory finds that SDK, but the host is built somewhere else,
/// where the repo's .dotnet isn't on the search path. Carrying the root along
/// is what makes the build use the SDK the project actually asked for.
type SdkSelection = { Version: string; DotnetRoot: string option }

module SdkSelection =
  /// The muxer to build with: the SDK's own, when it lives outside the default
  /// install, and the one we already have otherwise.
  let muxer (fallback: string) (selection: SdkSelection) : string =
    match selection.DotnetRoot with
    | None -> fallback
    | Some root ->
      let candidates = [ Path.Combine(root, "dotnet.exe"); Path.Combine(root, "dotnet") ]
      match candidates |> List.tryFind File.Exists with
      | Some muxer -> muxer
      | None -> fallback

/// Pure: the dotnet root that owns `version`, from `dotnet --list-sdks` output
/// ("11.0.100 [/path/to/.dotnet/sdk]"). The root is the sdk directory's parent.
let sdkRootOf (version: string) (listSdksOutput: string) : string option =
  listSdksOutput.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
  |> Array.tryPick (fun line ->
    let line = line.Trim()
    let openAt = line.IndexOf '['
    let closeAt = line.LastIndexOf ']'
    match openAt > 0 && closeAt > openAt with
    | false -> None
    | true ->
      match line.Substring(0, openAt).Trim() = version with
      | false -> None
      | true -> line.Substring(openAt + 1, closeAt - openAt - 1).Trim() |> Path.GetDirectoryName |> Option.ofObj)

/// The SDK version `dotnet` would use in `workingDir` (honouring global.json).
let resolveSdkVersion (dotnet: string) (workingDir: string) : Result<string, HostBuildError> =
  runCapture dotnet [ "--version" ] workingDir 30_000
  |> Result.map (fun output -> output.Trim())
  |> Result.mapError (fun failure -> SdkUnavailable(workingDir, failure))

/// The SDK for `workingDir`, and where it lives. Listing from the project's own
/// directory is what makes a repo-local SDK visible at all.
let resolveSdk (dotnet: string) (workingDir: string) : Result<SdkSelection, HostBuildError> =
  resolveSdkVersion dotnet workingDir
  |> Result.map (fun version ->
    let root =
      runCapture dotnet [ "--list-sdks" ] workingDir 30_000
      |> Result.toOption
      |> Option.bind (sdkRootOf version)
    { Version = version; DotnetRoot = root })

/// Pure: the SDK versions in `dotnet --list-sdks` output ("10.0.401 [/home/x/.dotnet/sdk]" per line).
let parseSdkList (output: string) : string list =
  output.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
  |> Array.choose (fun line ->
    let version = line.Trim().Split(' ').[0]
    match version.Length > 0 && Char.IsDigit version.[0] with
    | true -> Some version
    | false -> None)
  |> Array.toList

/// Every SDK installed on this machine. The host must build with each of them: FCS's API differs between SDKs.
let installedSdkVersions (dotnet: string) : Result<string list, HostBuildError> =
  runCapture dotnet [ "--list-sdks" ] (Path.GetTempPath()) 30_000
  |> Result.map parseSdkList
  |> Result.mapError (fun failure -> SdkUnavailable(Path.GetTempPath(), failure))

/// Cross-process lock so two sessions starting together build a given host once. Held for the whole build.
let private withBuildLock (lockPath: string) (timeoutMs: int) (work: unit -> Result<'a, HostBuildError>) : Result<'a, HostBuildError> =
  let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
  let rec acquire () =
    try
      Ok(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    with
    | :? IOException when DateTime.UtcNow < deadline ->
      Thread.Sleep 200
      acquire ()
    | ex -> Error(BuildLockUnavailable(lockPath, ex.Message))
  match acquire () with
  | Error reason -> Error reason
  | Ok handle ->
    use _ = handle
    work ()

/// Ensure a host built by `selection`'s SDK exists under `cacheRoot`, building it if not.
let ensureBuiltWith (dotnet: string) (selection: SdkSelection) (cacheRoot: string) : Result<HostBuild, HostBuildError> =
  let sdkVersion = selection.Version
  let dotnet = SdkSelection.muxer dotnet selection
  embeddedSources ()
  |> Result.bind (fun sources ->
    hostHarmony ()
    |> Result.bind (fun (harmonyBytes, harmonyHash) ->
    // The renamed Harmony is part of what the host is built from, so it is part of the key.
    let directory = Path.Combine(cacheRoot, cacheKey sdkVersion ((HostHarmonyName + ".dll", harmonyHash) :: sources))
    let dll = Path.Combine(directory, "bin", "FsiHost.dll")
    let stamp = Path.Combine(directory, ".built")
    let isBuilt () = File.Exists stamp && File.Exists dll
    match isBuilt () with
    | true -> Ok(Reused dll)
    | false ->
      Directory.CreateDirectory directory |> ignore
      withBuildLock (Path.Combine(directory, ".lock")) 300_000 (fun () ->
        // Another session may have finished the build while we waited for the lock.
        match isBuilt () with
        | true -> Ok(Reused dll)
        | false ->
          let source = Path.Combine(directory, "src")
          Directory.CreateDirectory source |> ignore
          for name, content in sources do
            File.WriteAllText(Path.Combine(source, name), content)
          File.WriteAllBytes(Path.Combine(source, HostHarmonyName + ".dll"), harmonyBytes)
          File.WriteAllText(Path.Combine(source, "global.json"), globalJson sdkVersion)
          // DOTNET_ROOT is inherited, and it points at whichever install
          // started us. A repo-local SDK loses to it: the muxer runs, then
          // MSBuild loads its targets from the inherited root instead. Point
          // the whole build at the SDK the project asked for.
          let buildEnvironment =
            match selection.DotnetRoot with
            | None -> []
            | Some root -> [ "DOTNET_ROOT", root; "DOTNET_HOST_PATH", SdkSelection.muxer dotnet selection ]
          runCaptureWith
            buildEnvironment
            dotnet
            [ "build"; "FsiHost.fsproj"; "-c"; "Release"; "-o"; Path.Combine(directory, "bin"); "--nologo"; "-v"; "q" ]
            source
            300_000
          |> Result.mapError (fun failure -> BuildFailed(sdkVersion, failure))
          |> Result.bind (fun _ ->
            match File.Exists dll with
            | false -> Error(BuildOutputMissing dll)
            | true ->
              File.WriteAllText(stamp, sdkVersion)
              Ok(Built dll)))))

/// Kept for callers that already know the version and nothing about where it lives.
let ensureBuilt (dotnet: string) (sdkVersion: string) (cacheRoot: string) : Result<HostBuild, HostBuildError> =
  ensureBuiltWith dotnet { Version = sdkVersion; DotnetRoot = None } cacheRoot
