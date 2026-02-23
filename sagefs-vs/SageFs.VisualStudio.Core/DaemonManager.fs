namespace SageFs.VisualStudio.Core

open System
open System.Diagnostics

/// Manages the SageFs daemon process lifecycle.
module DaemonManager =

  /// Find the SageFs executable on PATH.
  let findSageFs () =
    let psi =
      ProcessStartInfo(
        "where", "SageFs",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true)
    try
      use p = Process.Start(psi)
      let line = p.StandardOutput.ReadLine()
      p.WaitForExit(3000) |> ignore
      if String.IsNullOrEmpty line then None
      else Some line
    with _ -> None

  /// Start the SageFs daemon with a project or solution.
  let startDaemon (projectOrSln: string) =
    match findSageFs () with
    | None -> Error "SageFs not found on PATH. Install with: dotnet tool install --global SageFs"
    | Some exe ->
      let flag =
        if projectOrSln.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
           || projectOrSln.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) then
          "--sln"
        else
          "--proj"
      let psi =
        ProcessStartInfo(
          exe,
          sprintf "%s \"%s\"" flag projectOrSln,
          UseShellExecute = true)
      try
        let proc = Process.Start(psi)
        Ok proc.Id
      with ex ->
        Error (sprintf "Failed to start SageFs: %s" ex.Message)

  /// Open the SageFs dashboard in the default browser.
  let OpenDashboard (port: int) =
    let url = sprintf "http://localhost:%d/dashboard" port
    Process.Start(ProcessStartInfo(url, UseShellExecute = true)) |> ignore
