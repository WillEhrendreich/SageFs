namespace SageFs.Features

open System

/// A single snippet in a scratch pad.
type ScratchSnippet = {
  Id: int
  Code: string
  CreatedAt: DateTimeOffset
  Result: Result<string, string> option
}

/// Ephemeral evaluation area that doesn't pollute session history.
type ScratchPadState = {
  Label: string
  Snippets: ScratchSnippet list
  NextId: int
}

module ScratchPad =

  /// Create a new scratch pad with a label.
  let create (label: string) : ScratchPadState =
    { Label = label; Snippets = []; NextId = 1 }

  /// Add a code snippet to the pad.
  let addSnippet (code: string) (pad: ScratchPadState) : ScratchPadState =
    let snippet = {
      Id = pad.NextId
      Code = code
      CreatedAt = DateTimeOffset.UtcNow
      Result = None
    }
    { pad with
        Snippets = snippet :: pad.Snippets
        NextId = pad.NextId + 1 }

  /// Number of snippets in the pad.
  let snippetCount (pad: ScratchPadState) : int =
    pad.Snippets.Length

  /// All snippets, newest first.
  let snippets (pad: ScratchPadState) : ScratchSnippet list =
    pad.Snippets

  /// Record a result for a snippet by ID.
  let recordResult (snippetId: int) (result: Result<string, string>) (pad: ScratchPadState) : ScratchPadState =
    let updated =
      pad.Snippets
      |> List.map (fun s ->
        match s.Id = snippetId with
        | true -> { s with Result = Some result }
        | false -> s)
    { pad with Snippets = updated }

  /// Clear all snippets.
  let clear (pad: ScratchPadState) : ScratchPadState =
    { pad with Snippets = []; NextId = 1 }

  /// Export all snippets as an .fsx script (oldest first for correct ordering).
  let exportFsx (pad: ScratchPadState) : string =
    let header = sprintf "// @sagefs-scratch pad: %s" pad.Label
    let body =
      pad.Snippets
      |> List.rev
      |> List.map (fun s -> s.Code)
      |> String.concat "\n\n"
    match pad.Snippets with
    | [] -> header
    | _ -> sprintf "%s\n\n%s\n" header body

  /// Get code from snippets that evaluated successfully.
  let promoteSuccessful (pad: ScratchPadState) : string list =
    pad.Snippets
    |> List.rev
    |> List.choose (fun s ->
      match s.Result with
      | Some (Ok _) -> Some s.Code
      | _ -> None)
