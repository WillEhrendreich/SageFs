namespace SageFs

open System.IO

/// Pure discovery and evaluation of startup init scripts.
/// Searches for .SageFs/init.fsx or .SageFsrc in the working directory.
module StartupProfile =

  /// Locations to search for init scripts, in priority order.
  let discoverInitScript (workingDir: string) : string option =
    let candidates = [
      Path.Combine(workingDir, ".SageFs", "init.fsx")
      Path.Combine(workingDir, ".SageFsrc")
    ]
    candidates |> List.tryFind File.Exists

  /// Evaluate an init script in the FSI session.
  /// Returns Ok with the script path, or Error with the failure message.
  let evalInitScript
    (eval: string -> unit)
    (logger: string -> unit)
    (scriptPath: string)
    : Result<string, string> =
    try
      let contents = File.ReadAllText scriptPath
      logger (sprintf "Loading startup profile: %s" scriptPath)
      eval contents
      Result.Ok scriptPath
    with ex ->
      Result.Error (sprintf "Startup profile %s failed: %s" scriptPath ex.Message)
