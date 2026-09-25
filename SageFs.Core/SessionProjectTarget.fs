namespace SageFs

open System
open System.IO

/// What a new session is explicitly created to load.
///
/// This is deliberately closed: an empty project collection is not a fourth
/// meaning that silently triggers discovery. Project and solution paths remain
/// distinguished, while a genuinely project-free REPL must be spelled
/// `SessionProjectTarget.Bare`.
[<RequireQualifiedAccess>]
type SessionProjectTarget =
  | Project of path: string
  | Solution of path: string
  | Bare

module SessionProjectTarget =

  let private hasExtension (path: string) (extension: string) : bool =
    Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)

  let private isNonBlank (path: string) : bool =
    not (String.IsNullOrWhiteSpace(path))

  /// Validate and classify one explicit path. No filesystem access: a missing
  /// path is still a valid request and is reported later by the normal loading
  /// / readiness path with its actionable unresolved-project error.
  let tryCreate (path: string) : Result<SessionProjectTarget, string> =
    match isNonBlank path with
    | false -> Error "A session project target cannot be blank."
    | true when hasExtension path ".sln" || hasExtension path ".slnx" ->
      Ok(SessionProjectTarget.Solution path)
    | true when hasExtension path ".fsproj" ->
      Ok(SessionProjectTarget.Project path)
    | true ->
      Error (sprintf "A session target must be an .fsproj, .sln, or .slnx path: %s" path)

  /// Convert the existing external projects field into the closed domain.
  /// Empty input now has exactly one interpretation: an explicit bare REPL.
  /// It never means "discover whatever is nearby".
  let tryCreateMany (paths: string list) : Result<SessionProjectTarget list, string> =
    match paths with
    | [] -> Ok [ SessionProjectTarget.Bare ]
    | _ ->
      paths
      |> List.map tryCreate
      |> List.fold
        (fun state next ->
          match state, next with
          | Error err, _ -> Error err
          | _, Error err -> Error err
          | Ok targets, Ok target -> Ok (target :: targets))
        (Ok [])
      |> Result.map List.rev

  /// Validate a target collection used by internal session creation.
  /// Project combinations are supported; `Bare` is exclusive and must be the
  /// only target. This keeps the runtime representation total even though the
  /// type itself is public.
  let validate (targets: SessionProjectTarget list) : Result<unit, string> =
    match targets with
    | [] -> Error "Session creation requires at least one explicit target."
    | [ SessionProjectTarget.Bare ] -> Ok ()
    | SessionProjectTarget.Bare :: _ -> Error "Bare cannot be combined with a project or solution target."
    | _ :: SessionProjectTarget.Bare :: _ -> Error "Bare cannot be combined with a project or solution target."
    | _ ->
      targets
      |> List.tryFind (fun target ->
        match target with
        | SessionProjectTarget.Project path -> not (hasExtension path ".fsproj")
        | SessionProjectTarget.Solution path -> not (hasExtension path ".sln" || hasExtension path ".slnx")
        | SessionProjectTarget.Bare -> true)
      |> function
        | Some _ -> Error "Session project/solution target has an invalid extension."
        | None -> Ok ()

  let paths (targets: SessionProjectTarget list) : string list =
    targets
    |> List.choose (function
      | SessionProjectTarget.Project path
      | SessionProjectTarget.Solution path -> Some path
      | SessionProjectTarget.Bare -> None)

  let projects (targets: SessionProjectTarget list) : string list =
    targets
    |> List.choose (function
      | SessionProjectTarget.Project path -> Some path
      | SessionProjectTarget.Solution _
      | SessionProjectTarget.Bare -> None)

  let solutions (targets: SessionProjectTarget list) : string list =
    targets
    |> List.choose (function
      | SessionProjectTarget.Solution path -> Some path
      | SessionProjectTarget.Project _
      | SessionProjectTarget.Bare -> None)

  let isBare (targets: SessionProjectTarget list) : bool =
    targets = [ SessionProjectTarget.Bare ]

  let same
    (left: SessionProjectTarget list)
    (right: SessionProjectTarget list) : bool =
    match left, right with
    | [ SessionProjectTarget.Bare ], [ SessionProjectTarget.Bare ] -> true
    | SessionProjectTarget.Bare :: _, _
    | _, SessionProjectTarget.Bare :: _ -> false
    | _ -> List.sort (paths left) = List.sort (paths right)

  let label (target: SessionProjectTarget) : string =
    match target with
    | SessionProjectTarget.Project path -> sprintf "Project: %s" path
    | SessionProjectTarget.Solution path -> sprintf "Solution: %s" path
    | SessionProjectTarget.Bare -> "Bare REPL (no project discovery)"

  let describe (targets: SessionProjectTarget list) : string =
    match targets with
    | [ SessionProjectTarget.Bare ] -> "Bare REPL (no project discovery)"
    | _ -> targets |> List.map label |> String.concat ", "
