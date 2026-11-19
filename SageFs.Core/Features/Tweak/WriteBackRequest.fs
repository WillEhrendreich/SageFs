namespace SageFs.Features.Tweak

open System

/// The pure front door to the edit tool. Everything that can be refused before
/// an eval starts is decided here, with no IO, no MCP types and no strings that
/// a caller has to parse. The write decision itself stays in `EvalWriteBack`,
/// which is also pure; the only impure work left is the eval and the journaled
/// file write at the edge.
module WriteBackRequest =

  /// What a caller asked for, exactly as it arrived on the wire.
  type Request =
    { Code: string
      FilePath: string
      BlockStartLine: int }

  /// A validated request. The same record today, a distinct type so the edge
  /// cannot confuse "the caller sent something" with "the caller sent
  /// something we can act on".
  type Plan =
    { Code: string
      FilePath: string
      BlockStartLine: int }

  /// Every way a request can be refused before the session is touched.
  /// Exhaustive, so a new guard is a compile error at every consumer.
  [<RequireQualifiedAccess>]
  type Refusal =
    | EmptyCode
    | MissingFilePath
    | InvalidBlockStartLine of line: int

  /// The only judge of a request. Three guards, in the order a caller fixes
  /// them, each with one answer. Nothing here reads a file or a session.
  let plan (r: Request) : Result<Plan, Refusal> =
    if isNull r.Code || String.IsNullOrWhiteSpace r.Code then Error Refusal.EmptyCode
    elif String.IsNullOrWhiteSpace r.FilePath then Error Refusal.MissingFilePath
    elif r.BlockStartLine < 1 then Error(Refusal.InvalidBlockStartLine r.BlockStartLine)
    else
      Ok
        { Code = r.Code
          FilePath = r.FilePath
          BlockStartLine = r.BlockStartLine }

  module Refusal =
    /// What the caller did and did not ask for. The same fact in both surfaces
    /// so an agent and a human cannot be told different things.
    let describe (r: Refusal) : string =
      match r with
      | Refusal.EmptyCode -> "write_back: no code was supplied. Nothing was evaluated and no file was changed."
      | Refusal.MissingFilePath -> "write_back: file_path is required. Nothing was evaluated and no file was changed."
      | Refusal.InvalidBlockStartLine line ->
        sprintf "write_back: block_start_line must be the 1-based line the declaration starts at, got %d. Nothing was evaluated and no file was changed." line

    let suggestedAction (r: Refusal) : string =
      match r with
      | Refusal.EmptyCode -> "Pass the replacement declaration as code."
      | Refusal.MissingFilePath -> "Pass the source file the declaration came from as file_path."
      | Refusal.InvalidBlockStartLine _ -> "Find the declaration's first line in the file and pass it as block_start_line."
