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

/// The files that make up the host project, in the order they are written.
let hostSourceNames =
  [ "FsiHost.fsproj"
    "DirectoryConfigTypes.fs"
    "ConfigDsl.fs"
    "LiveValueTree.fs"
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

let private runCapture (file: string) (arguments: string list) (workingDir: string) (timeoutMs: int) : Result<string, ProcessFailure> =
  let command = file + " " + String.concat " " arguments
  let psi = ProcessStartInfo(file)
  for argument in arguments do
    psi.ArgumentList.Add argument
  psi.WorkingDirectory <- workingDir
  psi.RedirectStandardOutput <- true
  psi.RedirectStandardError <- true
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.Environment["DOTNET_NOLOGO"] <- "1"
  psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
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

/// The SDK version `dotnet` would use in `workingDir` (honouring global.json).
let resolveSdkVersion (dotnet: string) (workingDir: string) : Result<string, HostBuildError> =
  runCapture dotnet [ "--version" ] workingDir 30_000
  |> Result.map (fun output -> output.Trim())
  |> Result.mapError (fun failure -> SdkUnavailable(workingDir, failure))

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

/// Ensure a host built by the SDK `sdkVersion` exists under `cacheRoot`, building it if not.
let ensureBuilt (dotnet: string) (sdkVersion: string) (cacheRoot: string) : Result<HostBuild, HostBuildError> =
  embeddedSources ()
  |> Result.bind (fun sources ->
    let directory = Path.Combine(cacheRoot, cacheKey sdkVersion sources)
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
          File.WriteAllText(Path.Combine(source, "global.json"), globalJson sdkVersion)
          runCapture dotnet [ "build"; "FsiHost.fsproj"; "-c"; "Release"; "-o"; Path.Combine(directory, "bin"); "--nologo"; "-v"; "q" ] source 300_000
          |> Result.mapError (fun failure -> BuildFailed(sdkVersion, failure))
          |> Result.bind (fun _ ->
            match File.Exists dll with
            | false -> Error(BuildOutputMissing dll)
            | true ->
              File.WriteAllText(stamp, sdkVersion)
              Ok(Built dll))))
