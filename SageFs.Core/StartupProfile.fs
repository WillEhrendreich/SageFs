namespace SageFs

open System.IO

/// Pure discovery and evaluation of startup init scripts.
/// Searches for .SageFs/init.fsx or .SageFsrc in the primary project's folder,
/// then in the session's working directory.
module StartupProfile =

  type ApplyResult =
    | NotFound
    | Loaded of string
    | Failed of string * string

  /// A startup script, and the folder it was found in: the one that holds its
  /// `.SageFs` folder or its `.SageFsrc`.
  type InitScript = { Folder: string; Path: string }

  /// The folders to look in, in priority order. The primary project's folder comes
  /// first, because it was the only place looked before, so an init.fsx kept there
  /// keeps running even when the session's working directory also has one (a
  /// monorepo root, say). The session's working directory, where its
  /// `.SageFs/config.fsx` is read from, is the fallback.
  let searchDirectories (projectDir: string option) (sessionDir: string) : string list =
    match projectDir with
    | Some dir when dir <> sessionDir -> [ dir; sessionDir ]
    | Some dir -> [ dir ]
    | None -> [ sessionDir ]

  /// The first init script found, by folder first and then by file name.
  let tryFindInitScript (dirs: string list) : InitScript option =
    dirs
    |> List.tryPick (fun dir ->
      [ Path.Combine(dir, ".SageFs", "init.fsx")
        Path.Combine(dir, ".SageFsrc") ]
      |> List.tryFind File.Exists
      |> Option.map (fun path -> { Folder = dir; Path = path }))

  let discoverInitScript (dirs: string list) : string option =
    tryFindInitScript dirs |> Option.map (fun script -> script.Path)

  /// The code to evaluate for a script. FSI resolves a relative `#load` or `#r` in
  /// evaluated text against the folder the session started in (`fsiBaseDir`, the
  /// primary project's folder), so a script from that folder is evaluated as written,
  /// exactly as before. A script found in another folder gets `#I` for that folder,
  /// which FSI searches after its own, so a path written relative to that folder
  /// resolves. A line directive does not move where FSI looks, so it is only there to
  /// report errors against the script's own file and line numbers.
  let source (fsiBaseDir: string) (script: InitScript) (contents: string) : string =
    match script.Folder = fsiBaseDir with
    | true -> contents
    | false ->
      String.concat "\n" [
        sprintf "#I @\"%s\"" script.Folder
        sprintf "# 1 \"%s\"" script.Path
        contents ]

  /// Evaluate an init script in the FSI session.
  /// Returns Ok with the script path, or Error with the failure message.
  let evalInitScript
    (eval: string -> unit)
    (logger: string -> unit)
    (fsiBaseDir: string)
    (script: InitScript)
    : Result<string, string> =
    try
      let contents = File.ReadAllText script.Path
      logger (sprintf "Loading startup profile: %s" script.Path)
      eval (source fsiBaseDir script contents)
      Result.Ok script.Path
    with ex ->
      Result.Error (sprintf "Startup profile %s failed: %s" script.Path ex.Message)

  let applyIfPresent
    (fsiBaseDir: string)
    (dirs: string list)
    (eval: string -> unit)
    (logger: string -> unit)
    : ApplyResult =
    match tryFindInitScript dirs with
    | None -> NotFound
    | Some script ->
      match evalInitScript eval logger fsiBaseDir script with
      | Result.Ok path -> Loaded path
      | Result.Error message -> Failed (script.Path, message)

  let loadedPath = function
    | Loaded path -> Some path
    | NotFound
    | Failed _ -> None
