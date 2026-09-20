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
let hostSourceNames = [ "FsiHost.fsproj"; "FsiProtocol.fs"; "Program.fs" ]

/// Whether a host had to be built now or was already in the cache. The path is the host's entry assembly.
type HostBuild =
  | Built of dll: string
  | Reused of dll: string

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

let private readEmbedded (name: string) : Result<string, string> =
  let assembly = Assembly.GetExecutingAssembly()
  match assembly.GetManifestResourceStream("FsiHost/" + name) with
  | null -> Result.Error(sprintf "the FSI host source '%s' is not embedded in %s" name (assembly.GetName().Name |> string))
  | stream ->
    use stream = stream
    use reader = new StreamReader(stream, Encoding.UTF8)
    Result.Ok(reader.ReadToEnd())

/// The embedded host sources as (file name, content).
let embeddedSources () : Result<(string * string) list, string> =
  hostSourceNames
  |> List.fold
       (fun acc name ->
         acc |> Result.bind (fun sources -> readEmbedded name |> Result.map (fun content -> (name, content) :: sources)))
       (Result.Ok [])
  |> Result.map List.rev

let private runCapture (file: string) (arguments: string list) (workingDir: string) (timeoutMs: int) : Result<string, string> =
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
      Result.Error(sprintf "`%s %s` did not finish within %d ms" file (String.concat " " arguments) timeoutMs)
    | true ->
      let output = stdout'.Result + stderr'.Result
      match proc.ExitCode with
      | 0 -> Result.Ok output
      | code -> Result.Error(sprintf "`%s %s` exited with %d:\n%s" file (String.concat " " arguments) code output)
  with ex ->
    Result.Error(sprintf "could not run `%s`: %s" file ex.Message)

/// The SDK version `dotnet` would use in `workingDir` (honouring global.json).
let resolveSdkVersion (dotnet: string) (workingDir: string) : Result<string, string> =
  runCapture dotnet [ "--version" ] workingDir 30_000
  |> Result.map (fun output -> output.Trim())
  |> Result.mapError (fun reason ->
    sprintf
      "Could not determine the .NET SDK for %s: %s\nInstall the .NET SDK from https://dotnet.microsoft.com/download or fix the SDK version in global.json."
      workingDir
      reason)

/// Cross-process lock so two sessions starting together build a given host once. Held for the whole build.
let private withBuildLock (lockPath: string) (timeoutMs: int) (work: unit -> Result<'a, string>) : Result<'a, string> =
  let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
  let rec acquire () =
    try
      Result.Ok(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    with
    | :? IOException when DateTime.UtcNow < deadline ->
      Thread.Sleep 200
      acquire ()
    | ex -> Result.Error(sprintf "could not take the FSI host build lock %s: %s" lockPath ex.Message)
  match acquire () with
  | Result.Error reason -> Result.Error reason
  | Result.Ok handle ->
    use _ = handle
    work ()

/// Ensure a host built by the SDK `sdkVersion` exists under `cacheRoot`, building it if not.
let ensureBuilt (dotnet: string) (sdkVersion: string) (cacheRoot: string) : Result<HostBuild, string> =
  embeddedSources ()
  |> Result.bind (fun sources ->
    let directory = Path.Combine(cacheRoot, cacheKey sdkVersion sources)
    let dll = Path.Combine(directory, "bin", "FsiHost.dll")
    let stamp = Path.Combine(directory, ".built")
    let isBuilt () = File.Exists stamp && File.Exists dll
    match isBuilt () with
    | true -> Result.Ok(Reused dll)
    | false ->
      Directory.CreateDirectory directory |> ignore
      withBuildLock (Path.Combine(directory, ".lock")) 300_000 (fun () ->
        // Another session may have finished the build while we waited for the lock.
        match isBuilt () with
        | true -> Result.Ok(Reused dll)
        | false ->
          let source = Path.Combine(directory, "src")
          Directory.CreateDirectory source |> ignore
          for name, content in sources do
            File.WriteAllText(Path.Combine(source, name), content)
          File.WriteAllText(Path.Combine(source, "global.json"), globalJson sdkVersion)
          runCapture dotnet [ "build"; "FsiHost.fsproj"; "-c"; "Release"; "-o"; Path.Combine(directory, "bin"); "--nologo"; "-v"; "q" ] source 300_000
          |> Result.mapError (fun reason ->
            sprintf "Building the FSI host with .NET SDK %s failed. Make sure that SDK is installed.\n%s" sdkVersion reason)
          |> Result.bind (fun _ ->
            match File.Exists dll with
            | false -> Result.Error(sprintf "the FSI host build succeeded but %s is missing" dll)
            | true ->
              File.WriteAllText(stamp, sdkVersion)
              Result.Ok(Built dll))))
