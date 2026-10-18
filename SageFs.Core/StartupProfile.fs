namespace SageFs

open System.IO

/// Pure discovery and evaluation of startup init scripts.
/// Searches for .SageFs/init.fsx or .SageFsrc in the session's working directory,
/// then in the primary project's folder.
module StartupProfile =

  type ApplyResult =
    | NotFound
    | Loaded of string
    | Failed of string * string

  /// The folders to look in, in priority order. The session's working directory comes
  /// first, because that is where its `.SageFs/config.fsx` is read from. The worker
  /// then moves into the primary project's folder before the script runs. That folder
  /// is the fallback, because it was the only place looked before, and an init.fsx
  /// kept there still loads.
  let searchDirectories (sessionDir: string) (projectDir: string option) : string list =
    match projectDir with
    | Some dir when dir <> sessionDir -> [ sessionDir; dir ]
    | Some _
    | None -> [ sessionDir ]

  /// The first init script found, by folder first and then by file name.
  let discoverInitScript (dirs: string list) : string option =
    dirs
    |> List.collect (fun dir -> [
      Path.Combine(dir, ".SageFs", "init.fsx")
      Path.Combine(dir, ".SageFsrc")
    ])
    |> List.tryFind File.Exists

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

  let applyIfPresent
    (dirs: string list)
    (eval: string -> unit)
    (logger: string -> unit)
    : ApplyResult =
    match discoverInitScript dirs with
    | None -> NotFound
    | Some scriptPath ->
      match evalInitScript eval logger scriptPath with
      | Result.Ok path -> Loaded path
      | Result.Error message -> Failed (scriptPath, message)

  let loadedPath = function
    | Loaded path -> Some path
    | NotFound
    | Failed _ -> None
