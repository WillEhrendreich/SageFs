/// How an MCP tool call went, read from what its body handed back. Pure: no context, no IO, so the reading
/// the MCP seam does (`McpTools.withEcho`) is tested on its own.
module SageFs.McpEcho

/// How a tool call went, as the tool body says it: an answer, or a failure with its typed error. A body that
/// returns this is never read for an "Error:" prefix.
[<RequireQualifiedAccess>]
type EchoResult =
  | Answered of text: string
  | Failed of SageFs.SageFsError

/// The shapes of failure text that tool bodies still build by hand. A closed set: each says where the reason
/// starts.
[<RequireQualifiedAccess>]
type EchoFailureText =
  /// "Error: <reason>": the label is not part of the reason.
  | ErrorLabel
  /// "Error switching workflow: <reason>": the prefix starts the sentence. `Mcp.switchWorkflow` wrote it before it
  /// returned its typed error; a body that still writes it is read as a failure.
  | ErrorSentence
  /// "<what> → Next: <next>" with no prefix: `SageFsError.describeForAgent`, returned bare (`Mcp.createSession` did, before
  /// it returned its typed error; `withSession` still ends its refusal this way).
  | AgentDescription

module EchoFailureText =
  let prefix = function
    | EchoFailureText.ErrorLabel -> "Error: "
    | EchoFailureText.ErrorSentence -> "Error switching workflow: "
    | EchoFailureText.AgentDescription -> ""

  /// The arrow `SageFsError.describeForAgent` puts between the what and the next step.
  let nextMarker = " → Next: "

  /// The next step when the text names none.
  let noNextStep = "Fix what the message names, then call the tool again"

  let startsWith (p: string) (text: string) = text.StartsWith(p, System.StringComparison.Ordinal)

  /// Which failure shape a reply has, if any. A JSON reply is an answer whatever it quotes, and an arrow counts
  /// only on the reply's last line, where `describeForAgent` puts it.
  let ofText (text: string) : EchoFailureText option =
    match text with
    | t when startsWith (prefix EchoFailureText.ErrorLabel) t -> Some EchoFailureText.ErrorLabel
    | t when startsWith (prefix EchoFailureText.ErrorSentence) t -> Some EchoFailureText.ErrorSentence
    | t when startsWith "{" (t.TrimStart()) || startsWith "[" (t.TrimStart()) -> None
    | t ->
      let lastLine = t.TrimEnd().Split('\n') |> Array.last
      match lastLine.Contains(nextMarker, System.StringComparison.Ordinal) with
      | true -> Some EchoFailureText.AgentDescription
      | false -> None

/// Read a tool body's display text once, at the MCP seam: a reply in one of the failure shapes becomes
/// `SageFsError.ToolFailed` with its reason and next step, everything else is an answer. This exists for the
/// bodies in Mcp.fs that still return a bare string; a body that has its error returns `EchoResult.Failed`.
let classifyEcho (text: string) : EchoResult =
  match EchoFailureText.ofText text with
  | None -> EchoResult.Answered text
  | Some shape ->
    let body =
      match shape with
      | EchoFailureText.ErrorSentence -> text.TrimEnd()
      | EchoFailureText.ErrorLabel
      | EchoFailureText.AgentDescription -> text.Substring((EchoFailureText.prefix shape).Length).TrimEnd()
    match body.LastIndexOf(EchoFailureText.nextMarker, System.StringComparison.Ordinal) with
    | -1 -> EchoResult.Failed (SageFs.SageFsError.ToolFailed(body, EchoFailureText.noNextStep))
    | at ->
      EchoResult.Failed (SageFs.SageFsError.ToolFailed(body.Substring(0, at), body.Substring(at + EchoFailureText.nextMarker.Length)))

module EchoResult =
  /// A reply's text changed after the fact (a line appended to a session reply), a failure left as it was.
  let mapAnswered (change: string -> string) (echo: EchoResult) : EchoResult =
    match echo with
    | EchoResult.Answered text -> EchoResult.Answered (change text)
    | EchoResult.Failed err -> EchoResult.Failed err

  /// A body that already says how it went with a `Result`.
  let ofResult (outcome: Result<string, SageFs.SageFsError>) : EchoResult =
    match outcome with
    | Ok text -> EchoResult.Answered text
    | Error err -> EchoResult.Failed err
