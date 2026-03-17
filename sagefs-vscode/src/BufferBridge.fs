module SageFs.Vscode.BufferBridge

open System

type BufferChangedRequest = {
  SessionId: string
  FilePath: string
  Content: string
}

type SessionOwnershipCandidate = {
  SessionId: string
  WorkingDirectory: string
}

type SessionOwnershipDecision =
  | NoMatch
  | UniqueMatch of string
  | AmbiguousMatch of string list

let bufferChangedPath (sessionId: string) =
  sprintf "/api/sessions/%s/buffer-changed" sessionId

let private hasSupportedExtension (filePath: string) =
  filePath.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
  || filePath.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase)

let private samePath (left: string) (right: string) =
  String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

let private normalizePath (path: string) =
  path.Replace('/', '\\').TrimEnd([| '\\' |])

let private isWithinDirectory (directoryPath: string) (filePath: string) =
  let normalizedDirectory = normalizePath directoryPath
  let normalizedFile = normalizePath filePath
  samePath normalizedDirectory normalizedFile
  || normalizedFile.StartsWith(normalizedDirectory + "\\", StringComparison.OrdinalIgnoreCase)

let resolveSessionOwnership
  (knownSessions: SessionOwnershipCandidate array)
  (documentFilePath: string)
  (uriScheme: string)
  =
  match not (String.IsNullOrWhiteSpace documentFilePath)
        && uriScheme = "file"
        && hasSupportedExtension documentFilePath with
  | false ->
    NoMatch
  | true ->
    let matches =
      knownSessions
      |> Array.choose (fun session ->
        match String.IsNullOrWhiteSpace session.WorkingDirectory with
        | true -> None
        | false ->
          match isWithinDirectory session.WorkingDirectory documentFilePath with
          | true -> Some session.SessionId
          | false -> None)
      |> Array.distinct
      |> Array.toList

    match matches with
    | [] -> NoMatch
    | [ sessionId ] -> UniqueMatch sessionId
    | sessionIds -> AmbiguousMatch sessionIds

let tryBuildBufferChangedRequest
  (activeSessionId: string option)
  (activeSessionWorkingDirectory: string option)
  (activeFilePath: string option)
  (knownSessions: SessionOwnershipCandidate array)
  (documentFilePath: string)
  (uriScheme: string)
  (content: string)
  =
  let makeRequest sessionId =
    Some {
      SessionId = sessionId
      FilePath = documentFilePath
      Content = content
    }

  match resolveSessionOwnership knownSessions documentFilePath uriScheme with
  | UniqueMatch sessionId ->
    makeRequest sessionId
  | AmbiguousMatch _ ->
    None
  | NoMatch ->
    match activeSessionId with
    | Some sessionId
        when not (Array.isEmpty knownSessions)
             && not (String.IsNullOrWhiteSpace documentFilePath)
             && uriScheme = "file"
             && hasSupportedExtension documentFilePath ->
      None
    | Some sessionId
        when not (String.IsNullOrWhiteSpace documentFilePath)
             && uriScheme = "file"
             && hasSupportedExtension documentFilePath ->
      let matchesActiveFile =
        activeFilePath
        |> Option.exists (fun activePath -> samePath activePath documentFilePath)

      let belongsToActiveSession =
        activeSessionWorkingDirectory
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.exists (fun workingDirectory -> isWithinDirectory workingDirectory documentFilePath)

      match matchesActiveFile || belongsToActiveSession with
      | true ->
        makeRequest sessionId
      | false ->
        None
    | _ ->
      None
