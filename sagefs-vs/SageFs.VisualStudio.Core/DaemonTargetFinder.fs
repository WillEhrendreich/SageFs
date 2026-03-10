namespace SageFs.VisualStudio.Core

open System
open System.IO

/// Finds the best SageFs startup target (solution or project file) in a directory.
/// Preference order: .slnx (top-level) > .sln (top-level) > single .fsproj > test .fsproj among many > first .fsproj.
module DaemonTargetFinder =

  /// Find the best SageFs startup target in the given directory.
  /// Returns Ok(path) if a target is found, Error(message) if not.
  /// Results are sorted alphabetically within each category for deterministic output.
  let findTarget (searchDir: string) : Result<string, string> =
    let slnx =
      Directory.GetFiles(searchDir, "*.slnx", SearchOption.TopDirectoryOnly)
      |> Array.sort
    let sln =
      Directory.GetFiles(searchDir, "*.sln", SearchOption.TopDirectoryOnly)
      |> Array.sort
    let fsproj =
      Directory.GetFiles(searchDir, "*.fsproj", SearchOption.AllDirectories)
      |> Array.filter (fun f ->
        not (f.Contains("\\bin\\", StringComparison.Ordinal)) &&
        not (f.Contains("\\obj\\", StringComparison.Ordinal)))
      |> Array.sort
    if slnx.Length > 0 then
      Ok slnx.[0]
    elif sln.Length > 0 then
      Ok sln.[0]
    elif fsproj.Length = 0 then
      Error "No F# projects found. Open a folder with .fsproj files first."
    elif fsproj.Length = 1 then
      Ok fsproj.[0]
    else
      let testProj =
        fsproj
        |> Array.tryFind (fun f -> f.Contains("Test", StringComparison.OrdinalIgnoreCase))
      Ok (testProj |> Option.defaultValue fsproj.[0])
