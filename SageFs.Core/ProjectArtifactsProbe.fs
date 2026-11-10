namespace SageFs

open System
open System.IO

/// Why MSBuild could not be asked whether a project sets `ArtifactsPath`: no SDK, a timeout, an evaluation that failed.
type ProbeFailure = ProbeFailure of reason: string

/// Does a project's own build files set where its output goes? Asked of MSBuild, so props files, imports and
/// `UseArtifactsOutput` all count, with nothing of the session's own artifacts path in the way. Before a session is created
/// with an artifacts path, this decides who wins (`SessionOptions.reconcileArtifacts`).
module ProjectArtifactsProbe =

  /// MSBuild's answer to `-getProperty:ArtifactsPath`: the value on a line of its own, empty when nothing sets it.
  let parseAnswer (output: string) : SessionOptions.ProjectClaim =
    match (match output with null -> "" | text -> text).Trim() with
    | "" -> SessionOptions.ProjectLeavesArtifacts
    | path -> SessionOptions.ProjectSetsArtifacts path

  /// The environment variable that would answer for the project: the session's own request must not be what is measured.
  [<Literal>]
  let private ArtifactsVariable = "ArtifactsPath"

  /// Asks MSBuild in the project's folder, so a global.json picks the project's own SDK. Never throws for a process that cannot
  /// be started or read. An answer that did not come (no SDK, a timeout, a failing evaluation) is `Error` with why, and the
  /// caller treats the project as leaving the choice alone, which is what it did before the question was asked.
  let ask (projectPath: string) : Result<SessionOptions.ProjectClaim, ProbeFailure> =
    try
      let muxer =
        match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
        | null
        | "" -> "dotnet"
        | path -> path
      let psi = Diagnostics.ProcessStartInfo(muxer)
      for argument in [ "msbuild"; projectPath; "-getProperty:ArtifactsPath"; "-nologo" ] do
        psi.ArgumentList.Add argument
      psi.WorkingDirectory <- Path.GetDirectoryName(Path.GetFullPath projectPath)
      psi.RedirectStandardOutput <- true
      psi.RedirectStandardError <- true
      psi.UseShellExecute <- false
      psi.CreateNoWindow <- true
      psi.Environment.Remove ArtifactsVariable |> ignore
      use proc = Diagnostics.Process.Start psi
      // One short line, far below a pipe's buffer, so the process can finish before anything reads it.
      match proc.WaitForExit(int Timeouts.targetFrameworkEvaluation.TotalMilliseconds) with
      | false ->
        // The process may have exited between the wait and the kill.
        (try proc.Kill true with :? InvalidOperationException -> ())
        Error(ProbeFailure(sprintf "`dotnet msbuild` did not answer within %O" Timeouts.targetFrameworkEvaluation))
      | true when proc.ExitCode = 0 -> Ok(parseAnswer (proc.StandardOutput.ReadToEnd()))
      | true -> Error(ProbeFailure(sprintf "`dotnet msbuild` exited with %d: %s" proc.ExitCode (proc.StandardError.ReadToEnd().Trim())))
    with
    | :? System.ComponentModel.Win32Exception as ex -> Error(ProbeFailure ex.Message)
    | :? InvalidOperationException as ex -> Error(ProbeFailure ex.Message)
    | :? IOException as ex -> Error(ProbeFailure ex.Message)

  /// The claim of a session's projects: the claim of the first project file, and no claim when none can be asked. Asked of the
  /// first project only, since a solution's projects share the props above them, and an answer that could not be had is returned
  /// as a note for the caller to log.
  let claimOf (askProject: string -> Result<SessionOptions.ProjectClaim, ProbeFailure>) (projects: string list) : SessionOptions.ProjectClaim * string list =
    match projects |> List.filter (fun p -> p.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) with
    | [] -> SessionOptions.ProjectLeavesArtifacts, []
    | first :: _ ->
      match askProject first with
      | Ok claim -> claim, []
      | Error(ProbeFailure why) -> SessionOptions.ProjectLeavesArtifacts, [ sprintf "Could not ask MSBuild whether %s sets ArtifactsPath: %s" first why ]
