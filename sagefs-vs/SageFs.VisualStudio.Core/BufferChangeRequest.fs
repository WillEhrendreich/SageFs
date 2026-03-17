namespace SageFs.VisualStudio.Core

open System
open System.IO
open System.Text.Json

type SessionOwnershipResolution =
  | NoMatch
  | UniqueMatch of sessionId: string
  | AmbiguousMatch of sessionIds: string list

type BufferChangeRequest =
  { SessionId: string
    FilePath: string
    Content: string }

[<RequireQualifiedAccess>]
module BufferChangeRequest =
  let private trimTrailingSeparators (path: string) =
    let root = Path.GetPathRoot(path)

    match String.Equals(path, root, StringComparison.OrdinalIgnoreCase) with
    | true -> path
    | false -> path.TrimEnd('\\', '/')

  let private normalizePath (path: string) =
    match String.IsNullOrWhiteSpace path with
    | true -> ""
    | false ->
        path.Replace('/', '\\')
        |> Path.GetFullPath
        |> trimTrailingSeparators

  let private withTrailingSeparator (path: string) =
    match path.EndsWith("\\", StringComparison.Ordinal) with
    | true -> path
    | false -> path + "\\"

  let private pathContains (directory: string) (filePath: string) =
    let normalizedDirectory = normalizePath directory
    let normalizedFilePath = normalizePath filePath

    match String.IsNullOrWhiteSpace normalizedDirectory, String.IsNullOrWhiteSpace normalizedFilePath with
    | true, _
    | _, true -> false
    | _ when String.Equals(normalizedDirectory, normalizedFilePath, StringComparison.OrdinalIgnoreCase) -> true
    | _ ->
        normalizedFilePath.StartsWith(
          withTrailingSeparator normalizedDirectory,
          StringComparison.OrdinalIgnoreCase)

  let private isCompiledSourceFile (filePath: string) =
    match String.IsNullOrWhiteSpace filePath with
    | true -> false
    | false ->
        match Path.GetExtension(filePath).ToLowerInvariant() with
        | ".fs"
        | ".fsi" -> true
        | _ -> false

  let resolveSessionOwnership (sessions: SessionInfo list) (filePath: string) =
    let owners =
      sessions
      |> List.filter (fun session -> pathContains session.WorkingDirectory filePath)
      |> List.map (fun session -> session.Id)

    match owners with
    | [] -> NoMatch
    | [ sessionId ] -> UniqueMatch sessionId
    | sessionIds -> AmbiguousMatch sessionIds

  let tryCreate (sessions: SessionInfo list) (filePath: string) (content: string) =
    match isCompiledSourceFile filePath, resolveSessionOwnership sessions filePath with
    | true, UniqueMatch sessionId ->
        Some
          { SessionId = sessionId
            FilePath = filePath
            Content = content }
    | _ -> None

  let toJson (request: BufferChangeRequest) =
    JsonSerializer.Serialize
      {| filePath = request.FilePath
         content = request.Content |}

type BufferChangeRequestInterop =
  static member TryCreate(
    sessions: System.Collections.Generic.IEnumerable<SessionInfo>,
    filePath: string,
    content: string) =
      let sessionList = sessions |> Seq.toList
      BufferChangeRequest.tryCreate sessionList filePath content
